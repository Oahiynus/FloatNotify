using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FloatNotify;

internal sealed class MonitorFiles : IDisposable
{
    public static readonly string Home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
    public static readonly string Sessions = Path.Combine(Home,"sessions");
    public static readonly string DesktopLogs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Packages","OpenAI.Codex_2p2nqsd0c76g0","LocalCache","Local","Codex","Logs");
    readonly Dictionary<string,Cursor> cursors = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string,byte> changed = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string,byte> removed = new(StringComparer.OrdinalIgnoreCase);
    readonly List<FileSystemWatcher> watchers = [];
    readonly HashSet<string> liveLogs = new(StringComparer.OrdinalIgnoreCase);
    DateTime fastDesktopPollUntil;
    readonly List<Notice> desktopBatch = [];
    readonly Dictionary<string,DateTime> resolved = new(StringComparer.Ordinal);
    readonly CancellationTokenSource cancellation = new();
    readonly Action<Notice> notify;
    readonly string sessionsRoot, logsRoot;
    readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready=>ready.Task;
    public Task Finished{get;}
    internal (int Files,long BufferBytes) RetainedAfterStop => Finished.IsCompleted
        ? (cursors.Count,cursors.Values.Sum(c=>(long)c.Partial.Capacity))
        : throw new InvalidOperationException("读取监听器最终状态前必须等待停止。");
    public DesktopChatRoutes Routes { get; }=new();
    readonly byte[] buffer = new byte[65536];
    readonly DateTimeOffset liveSince=DateTimeOffset.UtcNow;
    volatile bool rescan, sessionsAttached, logsAttached;
    readonly object issueLock=new();
    string lastIssue="";
    DateTime issueLogged;
    long lastHookUtcTicks;
    public DateTime LastStopUtc=>new(Volatile.Read(ref lastHookUtcTicks),DateTimeKind.Utc);
    public string LastHook=>LastStopUtc==DateTime.MinValue?"本次启动尚未收到":LastStopUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public (string Status,string Path) Listener(bool session)
    {
        string path=session?sessionsRoot:logsRoot;
        string status=!Directory.Exists(path)?"目录未找到":Finished.IsCompleted?"监听已停止":
            (session?sessionsAttached:logsAttached)?"监听已挂接":"等待挂接";
        return (status,path);
    }
    void ReportIssue(string stage, Exception ex)
    {
        string issue=$"stage={stage} error={ex.GetType().Name}";
        lock(issueLock)
        {
            if(lastIssue==issue && DateTime.UtcNow-issueLogged<TimeSpan.FromMinutes(1))return;
            lastIssue=issue;issueLogged=DateTime.UtcNow;
        }
        Audit.Write("monitor-error "+issue);
    }
    public MonitorFiles(Action<Notice> notify, string? sessionsRoot=null, string? logsRoot=null)
    {
        this.notify=notify;
        string root=sessionsRoot??Sessions;
        this.sessionsRoot=FullSessionPath(root)??root;
        this.logsRoot=logsRoot??DesktopLogs;
        Finished=Task.Run(Run);
    }
    sealed class Cursor
    {
        public long Offset;
        public string DesktopInstance = "";
        public int DesktopPid;
        public readonly MemoryStream Partial=new();
        public bool Oversize, HeaderRead, SkipFirst;
        public Session? Session;
        public void ResetLine(int retainedLimit)
        {
            Partial.SetLength(0);
            if(Partial.Capacity>retainedLimit)Partial.Capacity=0;
            SkipFirst=Oversize=false;
        }
    }
    static bool IsRollout(string p)=>p.EndsWith(".jsonl",StringComparison.OrdinalIgnoreCase);
    static bool Supported(string p)=> IsRollout(p)?Path.GetFileName(p).StartsWith("rollout-"):Path.GetFileName(p).StartsWith("codex-desktop-") && p.EndsWith(".log");
    static (string Instance, int Pid) LogOwner(string path)
    {
        string name=Path.GetFileName(path);
        const string prefix="codex-desktop-";
        int start=prefix.Length+37;
        if(!name.StartsWith(prefix) || name.Length<=start || name[start-1]!='-' ||
            !Guid.TryParseExact(name.AsSpan(prefix.Length,36),"D",out var instance))return ("",0);
        int end=name.IndexOf('-',start);
        return end>start && int.TryParse(name.AsSpan(start,end-start),out int pid)?(instance.ToString("D"),pid):("",0);
    }
    Cursor NewCursor(string p)
    {
        if(IsRollout(p))return new Cursor{Session=new Session()};
        liveLogs.Add(p);
        var owner=LogOwner(p);
        return new Cursor{DesktopInstance=owner.Instance,DesktopPid=owner.Pid};
    }
    // Compare and open the same canonical path. GetFullPath alone preserves the
    // extended Windows prefix and skips normalizing dot segments in that form.
    static string? FullSessionPath(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return null;
        string path=value.Replace('/','\\');
        if(path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))path=@"\\"+path[8..];
        else if(path.StartsWith(@"\\?\",StringComparison.Ordinal))
        {
            if(path.Length<7 || !char.IsAsciiLetter(path[4]) || path[5]!=':' || path[6]!='\\')return null;
            path=path[4..];
        }
        if(!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\.\",StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\",StringComparison.Ordinal))return null;
        return Path.GetFullPath(path);
    }
    internal static string? SessionFile(string root,string? path)
    {
        if(FullSessionPath(root) is not {} dir || FullSessionPath(path) is not {} file)return null;
        string prefix=Path.EndsInDirectorySeparator(dir)?dir:dir+Path.DirectorySeparatorChar;
        return file.Length>prefix.Length && file.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)?file:null;
    }
    public bool HasListener=>!Finished.IsCompleted && (sessionsAttached && Directory.Exists(sessionsRoot) || logsAttached && Directory.Exists(logsRoot));
    public bool Hint(string? path)
    {
        if (SessionFile(sessionsRoot,path) is {} p && IsRollout(p) && Supported(p))
        {
            Volatile.Write(ref lastHookUtcTicks,DateTime.UtcNow.Ticks);
            changed[p]=0;
            return File.Exists(p);
        }
        return false;
    }
    void Attach(string dir)
    {
        if(!Directory.Exists(dir)||watchers.Any(w=>w.Path==dir))return;
        var w=new FileSystemWatcher(dir){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.Size|NotifyFilters.LastWrite|NotifyFilters.FileName,InternalBufferSize=32768};
        void Mark(string p){if(Supported(p))changed[p]=0;}
        void Removed(string p){if(Supported(p))removed[p]=0;}
        w.Changed+=(_,e)=>Mark(e.FullPath);w.Created+=(_,e)=>Mark(e.FullPath);w.Deleted+=(_,e)=>Removed(e.FullPath);
        w.Renamed+=(_,e)=>{Removed(e.OldFullPath);Mark(e.FullPath);};
        w.Error+=(_,e)=>{ReportIssue("目录变动通知",e.GetException());rescan=true;};
        try {w.EnableRaisingEvents=true;} catch {w.Dispose();throw;}
        watchers.Add(w);
        if(dir==sessionsRoot)sessionsAttached=true;else logsAttached=true;
    }
    void Discover(bool initial, DateTime began)
    {
        var running=new HashSet<int>();
        foreach(string name in new[]{"ChatGPT","Codex"})
            foreach(var process in Process.GetProcessesByName(name))
                using(process)running.Add(process.Id);
        foreach(string id in resolved.Where(x=>x.Value<DateTime.UtcNow.AddHours(-6)).Select(x=>x.Key).ToArray())resolved.Remove(id);
        var missing=new HashSet<string>(cursors.Keys,StringComparer.OrdinalIgnoreCase);
        foreach(var dir in new[]{sessionsRoot,logsRoot})
        {
            if(!Directory.Exists(dir))continue;
            Attach(dir);
            foreach(var f in new DirectoryInfo(dir).EnumerateFiles(dir==sessionsRoot?"rollout-*.jsonl":"codex-desktop-*.log",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true}))
            {
                missing.Remove(f.FullName);
                if (!cursors.TryGetValue(f.FullName,out var c))
                {
                    c=NewCursor(f.FullName);cursors.Add(f.FullName,c);
                    if(initial && f.CreationTimeUtc<began)
                    {
                        // Capture the starting boundary. Never replay hours of old notifications.
                        using var reader=Open(f.FullName);c.Offset=reader.Length;
                        if(c.Session==null && running.Contains(c.DesktopPid))
                            SeedRoutes(reader,c);
                        if(c.Offset>0) {reader.Seek(c.Offset-1,SeekOrigin.Begin);c.SkipFirst=reader.ReadByte()!=10;}
                    }
                }
                if(c.Session==null)
                {
                    int pid=c.DesktopPid;
                    if(running.Contains(pid) || (pid==0 && f.LastWriteTimeUtc>DateTime.UtcNow.AddDays(-1)))liveLogs.Add(f.FullName);
                    else liveLogs.Remove(f.FullName);
                }
                if(f.Length!=c.Offset)changed[f.FullName]=0;
            }
        }
        foreach(var path in missing)Forget(path);
    }
    void Forget(string path)
    {
        if(cursors.Remove(path,out var cursor))cursor.Partial.Dispose();
        liveLogs.Remove(path);
    }
    static FileStream Open(string p)=>new(p,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
    async Task Run()
    {
        var began=DateTime.UtcNow;var nextScan=began;var nextDesktopPoll=began;bool first=true;
        while(!cancellation.IsCancellationRequested)
        {
            try
            {
                if(rescan||DateTime.UtcNow>=nextScan){rescan=false;nextScan=DateTime.UtcNow.AddSeconds(35);Discover(first,began);first=false;ready.TrySetResult();}
                foreach(var p in removed.Keys)
                    if(removed.TryRemove(p,out _)){Forget(p);changed[p]=0;}
                // Electron holds log files open. Windows directory sizes and change
                // notifications can lag behind bytes already readable on the handle.
                var now=DateTime.UtcNow;
                if(now>=nextDesktopPoll){
                    foreach(string p in liveLogs)changed[p]=0;
                    nextDesktopPoll=now.AddMilliseconds(now<fastDesktopPollUntil?260:1000);
                }
                foreach(string p in changed.Keys.OrderBy(IsRollout))
                {
                    if(!changed.TryRemove(p,out _))continue;
                    try{Read(p);}
                    catch(IOException ex){if(File.Exists(p)){ReportIssue(IsRollout(p)?"读取会话":"读取桌面日志",ex);changed[p]=0;}}
                    catch(UnauthorizedAccessException ex){ReportIssue(IsRollout(p)?"读取会话":"读取桌面日志",ex);}
                }
                if(desktopBatch.Count>0)
                {
                    foreach(var n in desktopBatch.Where(n=>n.Kind==Kind.RequestAnswered))resolved[n.Id]=DateTime.UtcNow;
                    foreach(var n in desktopBatch){
                        if(!n.Urgent || !resolved.ContainsKey(n.Id))notify(n);
                        else if(n.Kind==Kind.Question && n.CallId is {} call)
                            notify(new(Kind.Answered,n.Chat,call,Turn:n.Turn,Source:"desktop"));
                    }
                    desktopBatch.Clear();
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){ReportIssue("扫描监听目录",ex);}
            try{await Task.Delay(260,cancellation.Token);}catch(OperationCanceledException){break;}
        }
        foreach(var w in watchers)w.Dispose();
    }
    void Read(string p)
    {
        if(!File.Exists(p)){Forget(p);return;}
        if(!cursors.TryGetValue(p,out var c)){c=NewCursor(p);cursors[p]=c;}
        using var stream=Open(p);
        if(stream.Length==c.Offset)return;
        if(c.Session==null)fastDesktopPollUntil=DateTime.UtcNow.AddSeconds(4);
        if(c.Offset>stream.Length){c.Offset=0;c.ResetLine(0);c.HeaderRead=false;if(c.Session!=null)c.Session=new();}
        if(c.Session!=null && !c.HeaderRead)
        {
            // Existing sessions may be resumed days after their rollout was created.
            int count=stream.Read(buffer);
            int nl=Array.IndexOf(buffer,(byte)10,0,count);
            if(nl<0 && count<buffer.Length)return;
            // session_meta can contain very large instructions; identity fields may be near the end.
            if(nl<0){
                // A large first record may still be in the middle of being written.
                do { count=stream.Read(buffer); } while(count>0 && Array.IndexOf(buffer,(byte)10,0,count)<0);
                if(count==0)return;
                stream.Position=0;
                using var rd=new StreamReader(stream,Encoding.UTF8,true,8192,true);
                ParseHeader(c.Session,rd.ReadLine()??"");
            }
            else ParseHeader(c.Session,Encoding.UTF8.GetString(buffer,0,nl));
            c.HeaderRead=true;
        }
        if(c.Session!=null && !c.Session.DesktopUser){c.Offset=stream.Length;return;}
        stream.Position=c.Offset;
        // Bound each pass so a busy thread cannot hold up the others.
        long budget=4*1024*1024;int n;
        while(budget>0 && (n=stream.Read(buffer))>0)
        {
            for(int start=0;start<n;)
            {
                int nl=Array.IndexOf(buffer,(byte)10,start,n-start), end=nl<0?n:nl;
                if(nl>=0 && !c.SkipFirst && !c.Oversize && c.Partial.Length==0){
                    Line(c,Encoding.UTF8.GetString(buffer,start,end-start).TrimEnd('\r'));start=nl+1;continue;
                }
                if(!c.SkipFirst && !c.Oversize && c.Partial.Length+(end-start)<=2*1024*1024)c.Partial.Write(buffer,start,end-start);else c.Oversize=true;
                if(nl<0)break;
                if(!c.SkipFirst && !c.Oversize)Line(c,Encoding.UTF8.GetString(c.Partial.GetBuffer(),0,(int)c.Partial.Length).TrimEnd('\r'));
                c.ResetLine(buffer.Length);start=nl+1;
            }
            budget-=n;c.Offset+=n;
        }
        if(stream.Length>c.Offset)changed[p]=0;
    }
    static void ParseHeader(Session s,string line){try{using var j=JsonDocument.Parse(line);s.Identify(j.RootElement);}catch(JsonException){}}
    void SeedRoutes(FileStream stream,Cursor cursor)
    {
        // Recover recent window state on restart, without replaying old notices.
        var start=Math.Max(0,cursor.Offset-1024*1024);
        stream.Position=start;
        var bytes=new byte[(int)(cursor.Offset-start)];
        stream.ReadExactly(bytes);
        var lines=Encoding.UTF8.GetString(bytes).Split('\n');
        for(int i=start==0?0:1;i<lines.Length-1;i++)
            Routes.Observe(cursor.DesktopPid,cursor.DesktopInstance,lines[i].TrimEnd('\r'));
    }
    void Line(Cursor c,string line)
    {
        if(c.Session==null)
        {
            Routes.Observe(c.DesktopPid,c.DesktopInstance,line);
            var notice=Events.Desktop(line);
            if(notice!=null && DateTimeOffset.TryParse(line.AsSpan(0,line.IndexOf(' ')),out var stamp) && stamp>=liveSince)
            {
                if(c.DesktopInstance!="" && notice.Source=="desktop" && (notice.Urgent || notice.Kind==Kind.RequestAnswered))
                    notice=notice with{Id=c.DesktopInstance+"/"+notice.Id};
                desktopBatch.Add(notice);
            }
            return;
        }
        try
        {
            using var doc=JsonDocument.Parse(line);
            if(!DateTimeOffset.TryParse(doc.RootElement.Str("timestamp"),out var stamp)||stamp<liveSince)return;
            foreach(var n in c.Session.Read(doc.RootElement))notify(n);
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or FormatException){ }
    }
    public void Dispose()=>cancellation.Cancel();
}
