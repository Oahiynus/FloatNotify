using Microsoft.Win32;
using System.Text.Json;

namespace FloatNotify;

internal static class LocalData
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static string FilePath(string name) => Path.Combine(Audit.Folder,name+".json");
    public static T? Read<T>(string name) where T:class
    {
        var path=FilePath(name);
        if(!File.Exists(path))return null;
        try {return JsonSerializer.Deserialize<T>(File.ReadAllText(path),Json);}
        catch(JsonException){File.Copy(path,path+"."+DateTime.UtcNow.Ticks+".invalid");return null;}
    }
    public static void Write<T>(string name,T value)
    {
        Directory.CreateDirectory(Audit.Folder);
        var path=FilePath(name);
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(value,Json));
        File.Move(path+".tmp",path,true);
    }
}

internal sealed record Preferences
{
    public bool Codex {get;init;}=true;
    public bool Approval {get;init;}=true;
    public bool Question {get;init;}=true;
    public bool Done {get;init;}=true;
    public bool History {get;init;}=true;
    public Dictionary<string,bool> Visible {get;init;}=new(){{"codex",true},{"claude",false},{"deepseek",false},{"gemini",false}};
    public bool Accept(Kind kind) => Codex && (kind switch {Kind.Permission or Kind.CheckApproval=>Approval,Kind.Question=>Question,Kind.Done=>Done,_=>true});
    const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run", Value="FloatNotify";
    static string Command=>"\""+Environment.ProcessPath+"\" --background";
    public static bool StartEnabled(){using var key=Registry.CurrentUser.OpenSubKey(RunKey);return string.Equals(key?.GetValue(Value) as string,Command,StringComparison.OrdinalIgnoreCase);}
    public static void SetStartup(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(RunKey);
        if(enabled)key.SetValue(Value,Command);else key.DeleteValue(Value,false);
    }
}

internal sealed record HistoryRow
{
    public Guid Id {get;init;}=Guid.NewGuid();
    public Guid Chat {get;init;}
    public DateTimeOffset At {get;init;}=DateTimeOffset.Now;
    public Kind Kind {get;init;}
    public string Title {get;init;}="";
    public string Status {get;init;}="待处理";
    public bool Pending {get;init;}
    public RequestRef[] Refs {get;init;}=[];
    public string Geometry {get;init;}="";
    public string Focus {get;init;}="";
}

internal sealed class RecentHistory
{
    public const int Days=30, Limit=200;
    List<HistoryRow> rows;
    public int Revision {get;private set;}
    public RecentHistory()
    {
        var saved=LocalData.Read<List<HistoryRow>>("history");
        rows=saved??ImportAudit(Audit.Folder);
        // While the listener was stopped, the original may have been handled.
        Commit(rows.Select(r=>r.Pending?r with{Pending=false,Status="待核对"}:r),force:saved==null);
    }
    public IEnumerable<HistoryRow> Rows=>rows.Where(r=>r.At>=DateTimeOffset.Now.AddDays(-Days));
    public HistoryRow Find(Guid id)=>Rows.FirstOrDefault(r=>r.Id==id)??throw new InvalidOperationException("这条提醒已不在最近记录中。");
    void Commit(IEnumerable<HistoryRow> values,bool force=false)
    {
        var next=values.Where(r=>r.At>=DateTimeOffset.Now.AddDays(-Days)).OrderByDescending(r=>r.At).Take(Limit).ToList();
        if(!force && rows.SequenceEqual(next))return;
        LocalData.Write("history",next);rows=next;Revision++;
    }
    public void Clear()=>Commit([]);
    public Guid Add(Notice n,string title)
    {
        var prior=rows.FirstOrDefault(r=>r.Chat==n.Chat&&r.Kind==n.Kind&&r.Refs.Any(a=>a.Id==n.Id));
        if(prior!=null)return prior.Id;
        var row=new HistoryRow{Chat=n.Chat,Kind=n.Kind,Title=title,Refs=[n.Reference],Pending=n.Urgent,Status=n.Urgent?"待处理":"已收到"};
        Commit(rows.Prepend(row));return row.Id;
    }
    public void Update(Guid? id,Func<HistoryRow,HistoryRow> edit)
    {
        if(id==null||!rows.Any(r=>r.Id==id))return;
        Commit(rows.Select(r=>r.Id==id?edit(r):r));
    }
    public void LinkQuestions(Guid? id,List<RequestRef> references,Notice detail,List<Guid> remove)
    {
        if(id==null)return;
        Commit(rows.Where(r=>!remove.Contains(r.Id)).Select(r=>r.Id==id?
            r with{Refs=[..references],Title=string.IsNullOrWhiteSpace(detail.Body)?r.Title:Summary(detail)}:r));
    }
    public static string Summary(Notice n)
    {
        var line=n.Body.Split('\n')[0].Trim();return line.Length>180?line[..180]+"…":line;
    }
    public void Resolve(Notice n)
    {
        bool Match(HistoryRow r)=>(r.Pending || r.Status=="待核对") && n.Resolves(r.Chat,r.Refs);
        if(rows.Any(Match))Commit(rows.Select(r=>Match(r)?r with{Pending=false,Status=n.Kind==Kind.TurnEnded?"已结束":n.Kind==Kind.Answered&&n.Body=="reply"?"已回答":"已处理"}:r));
    }
    internal static List<HistoryRow> ImportAudit(string folder)
    {
        var result=new List<HistoryRow>();var seen=new HashSet<(Guid,Kind,string)>();
        foreach(var name in new[]{"events.log.1","events.log"})
        {
            var path=Path.Combine(folder,name);if(!File.Exists(path))continue;
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var reader=new StreamReader(stream);
            while(reader.ReadLine() is {} line)
            {
                var parts=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);if(parts.Length<3||!DateTimeOffset.TryParse(parts[0],out var stamp)||stamp<DateTimeOffset.Now.AddDays(-Days))continue;
                Kind? kind=parts[1] switch{"done-shown"=>Kind.Done,"question-shown"=>Kind.Question,"permission-shown"=>Kind.Permission,_=>(Kind?)null};if(kind==null)continue;
                string Field(string key)=>parts.FirstOrDefault(p=>p.StartsWith(key+"=",StringComparison.Ordinal))?[(key.Length+1)..]??"";
                var source=Field("source");if(source is not ("session" or "desktop" or "notification" or "native" or "hook"))continue;
                Guid.TryParse(Field("chat"),out var chat);string turn=Field("turn");
                string id=kind==Kind.Done?turn:Field("request");
                if(id!=""&&!seen.Add((chat,kind.Value,id)))continue;
                result.Add(new(){At=stamp,Kind=kind.Value,Chat=chat,Status="已显示",Refs=[new(source,id,turn,false)],Geometry=Field("centered"),Focus=Field("keyboardWindow")});
            }
        }
        return result;
    }
}
