using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FloatNotify;

internal sealed partial class Resident : ApplicationContext
{
    internal sealed class Entry(Notice notice)
    {
        public Notice Notice = notice;
        public Guid? HistoryId;
        public readonly List<RequestRef> Aliases = [notice.Reference];
        public bool MatchesQuestion(Notice n)
        {
            if(Notice.Kind!=Kind.Question || n.Kind!=Kind.Question || Notice.Chat!=n.Chat)return false;
            var incoming=n.Reference;
            if(Aliases.Any(a=>a.Turn!="" && n.Turn!="" && a.Turn!=n.Turn ||
                a.QuestionCall!=null && incoming.QuestionCall!=null && a.QuestionCall!=incoming.QuestionCall))return false;
            return Aliases.Any(a=>a.Source==n.Source && a.Id==n.Id ||
                a.QuestionCall!=null && a.QuestionCall==incoming.QuestionCall);
        }
        public bool Attach(RequestRef reference)
        {
            int i=Aliases.FindIndex(r=>r.Source==reference.Source && r.Id==reference.Id);
            if(i<0){Aliases.Add(reference);return true;}
            var prior=Aliases[i];
            var next=reference with{Turn=reference.Turn!=""?reference.Turn:prior.Turn,
                CallId=reference.CallId??prior.CallId,Async=reference.Async||prior.Async};
            if(next==prior)return false;
            Aliases[i]=next;return true;
        }
        public string MonitorName = "";
        public Card? Window;
        public bool KeepFaceClear, InOverflow;
        public Size Measured;
        public Rectangle MeasuredArea;
        public double MeasuredScale;
        public bool Visible => Window?.IsVisible == true;
        public bool CheckingView;
        bool seenAway;
        public bool ReturnedToTask(CompletionView view)
        {
            if(view is CompletionView.OtherApp or CompletionView.OtherChat)seenAway=true;
            return seenAway && view==CompletionView.SameChat;
        }
    }
    GlassMaterial material=GlassPreference.Read();
    readonly Control dispatcher=new();
    readonly NotifyIcon tray;
    readonly MonitorFiles monitor;
    readonly CancellationTokenSource stop=new();
    readonly List<Entry> cards=[];
    readonly HashSet<Entry> activeRequests=[];
    string lastSignal="本次启动尚未收到";
    readonly Dictionary<(Guid,Kind,string),DateTime> seen=new();
    DateTime nextSeenCleanup;
    readonly Dictionary<Guid,string> names=new();
    readonly ForegroundChat foregroundChat=new();
    sealed class ApprovalScan(string turn)
    {
        public readonly string Turn=turn;
        public DateTime Until=DateTime.UtcNow.AddSeconds(20);
    }
    readonly Dictionary<Guid,ApprovalScan> approvalChecks=[];
    DateTime titlesModified;

    bool shuttingDown;
    readonly System.Windows.Forms.Timer pulse=new(){Interval=400};
    public Resident()
    {
        _=dispatcher.Handle;
        var menu=new ContextMenuStrip();
        menu.Items.Add("打开浮笺",null,(_,_)=>OpenSettings());
        menu.Items.Add("退出浮笺",null,(_,_)=>ExitThread());
        tray=new NotifyIcon{Icon=System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),Text="浮笺",Visible=true,ContextMenuStrip=menu};
        tray.DoubleClick+=(_,_)=>OpenSettings();
        monitor=new MonitorFiles(n=>Post(()=>Observe(n)));
        _=Listen();
        _=OfficialApproval.Listen(a=>Post(()=>OfferApproval(a)),(a,result)=>Post(()=>FinishApproval(a,result)),stop.Token);
        trialTimer.Tick+=(_,_)=>ShowScheduledTrial();
        pulse.Tick+=(_,_)=>Tick();pulse.Start();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged+=DisplaysChanged;
        Audit.Write("resident-start");
        if(prefs.PendingCodex!=null || !prefs.Codex)Post(()=>_ = ChangeCodexAsync(prefs.PendingCodex??false));
    }
    void Post(Action action){try{if(!shuttingDown)dispatcher.BeginInvoke(action);}catch(InvalidOperationException){}}
    async Task Listen()
    {
        while(!stop.IsCancellationRequested)
        {
            try{
                using var pipe=new NamedPipeServerStream(Program.Pipe,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(1800);
                using var rd=new StreamReader(pipe,Encoding.UTF8);
                var line=await rd.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if(line==null||line.Length>32768)continue;
                using var j=JsonDocument.Parse(line);var root=j.RootElement;
                switch(root.Str("action"))
                {
                    case "settings":Post(OpenSettings);break;
                    case "hint":
                        if(monitor.Hint(root.Str("path")))Post(()=>VerifyEvent(ConnectionVerification.Signal.Stop));
                        break;
                    case "exit":Post(ExitThread);break;
                    case "dismiss-preview":Post(()=>{EndSettingsTrial();RemoveCards(e=>e.Notice.Source=="preview");});break;
                    case "preview":
                        var kind=root.Str("kind") switch{"done"=>Kind.Done,"question"=>Kind.Question,"permission"=>Kind.Permission,_=>(Kind?)null};
                        var chat=kind==Kind.Done && Guid.TryParse(root.Str("chat"),out var target) ? target : Guid.Empty;
                        if(kind!=null)Post(()=>Receive(new(kind.Value,chat,Guid.NewGuid().ToString(),Source:"preview")));
                        break;
                }
            }catch(OperationCanceledException){}catch(Exception e) when(e is IOException or JsonException or ArgumentException){Audit.Write("pipe-message-skipped");}
        }
    }
    readonly Dictionary<OfficialApproval,Entry> officialEntries=[];
    readonly List<(OfficialApproval Request, HashSet<string> Sources,DateTime Until)> handedOff=[];
    DateTime lastOfficialUtc;

    void OfferApproval(OfficialApproval request)
    {
        lastOfficialUtc=DateTime.UtcNow;
        if(!request.Active || !prefs.Accept(Kind.Permission)){request.Respond("pass");return;}
        var d=request.Details;
        Receive(new(Kind.Permission,d.Chat,request.Id,"请求审批 · "+d.Tool,d.Turn.ToString(),"hook",Approval:request));
    }
    void FinishApproval(OfficialApproval request,string result)
    {
        if(!officialEntries.Remove(request,out var entry))return;
        activeRequests.Remove(entry);
        RemoveCard(entry);
        string status=result switch{"allow"=>"已回传允许一次","deny"=>"已回传拒绝",_=>"已交回 Codex"};
        Record(()=>history.Update(entry.HistoryId,r=>r with{Pending=false,Status=status}));
        Audit.Write($"permission-hook-return request={request.Id} result={result}");

    }
    void HandBack(OfficialApproval approval)
    {
        if(approval.Respond("pass"))
            handedOff.Add((approval,new(StringComparer.Ordinal),DateTime.UtcNow.AddMinutes(2)));
    }
    bool HandledInOriginal(Notice notice,string context)
    {
        var matches=handedOff.Where(h=>h.Request.Details.MatchesOriginal(notice,context) && !h.Sources.Contains(notice.Source)).ToArray();
        if(matches.Length!=1)return false;
        matches[0].Sources.Add(notice.Source);
        return true;
    }
    // A desktop notification may carry only an unrelated server request number.
    // Reconcile it only if the original form can be identified; otherwise keep the reminder.
    async void Observe(Notice notice)
    {
        handedOff.RemoveAll(h=>h.Until<=DateTime.UtcNow);
        if(notice.Kind==Kind.Permission && notice.Source=="desktop" &&
            handedOff.Any(h=>h.Request.Details.Chat==notice.Chat))
        {
            var title=ChatTitle(notice.Chat);
            if(HasUniqueTitle(notice.Chat,title))
            {
                var started=DateTime.UtcNow;
                var form=await Task.Run(()=>FormBridge.Read(notice,title));
                if(shuttingDown || seen.TryGetValue((Guid.Empty,Kind.RequestAnswered,notice.Id),out var resolved) && resolved>=started)return;
                if(form!=null && HandledInOriginal(notice,form.Context)){
                    seen[(notice.Chat,notice.Kind,notice.Id)]=DateTime.UtcNow;return;
                }
            }
        }
        Receive(notice);
    }
    void Receive(Notice n)
    {
        if(shuttingDown)return;
        if(n.Source is "session" or "desktop" or "notification" or "native" or "hook")
            lastSignal=$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {DiagnosticReport.SourceName(n.Source)} · {SignalName(n.Kind)}";
        if(n.Source!="preview" && n.Kind is (Kind.Done or Kind.Permission or Kind.Question or Kind.CheckApproval) && !prefs.Accept(n.Kind))return;
        if(n.Kind==Kind.CheckApproval){CheckNativeApproval(n);return;}
        if(n.Kind is Kind.Answered or Kind.RequestAnswered or Kind.TurnEnded)
        {
            if(n.Kind==Kind.RequestAnswered)seen[(Guid.Empty,Kind.RequestAnswered,n.Id)]=DateTime.UtcNow;
            if(n.Kind==Kind.Answered)seen[(n.Chat,Kind.Answered,n.Id)]=DateTime.UtcNow;
            if(n.Kind==Kind.TurnEnded){
                if(n.Turn!="")seen[(n.Chat,Kind.TurnEnded,n.Body+"/"+n.Turn)]=DateTime.UtcNow;
                foreach(var approval in officialEntries.Keys.Where(a=>a.Details.Chat==n.Chat && a.Details.Turn.ToString()==n.Turn))
                    approval.Respond("pass");
                handedOff.RemoveAll(h=>h.Request.Details.Chat==n.Chat && h.Request.Details.Turn.ToString()==n.Turn);
            }
            Record(()=>history.Resolve(n));
            if(n.Kind==Kind.Answered && n.Body=="reply")Audit.Write($"question-reply-received request={n.Id}");
            if(n.Kind==Kind.TurnEnded && approvalChecks.TryGetValue(n.Chat,out var scan) && scan.Turn==n.Turn)
                approvalChecks.Remove(n.Chat);
            bool Matches(Entry e)=>e.Notice.Urgent && n.Resolves(e.Notice.Chat,e.Aliases);
            foreach(var resolved in activeRequests.Where(Matches).ToArray()){
                foreach(var alias in resolved.Aliases)
                    if(resolved.Notice.Kind==Kind.Question && alias.QuestionCall is {} call)
                        seen[(resolved.Notice.Chat,Kind.Answered,call)]=DateTime.UtcNow;
                activeRequests.Remove(resolved);
            }
            RemoveCards(Matches);
            return;
        }
        var now=DateTime.UtcNow;
        var cutoff=now.AddHours(-6);
        if(now>=nextSeenCleanup)
        {
            foreach(var expired in seen.Where(p=>p.Value<cutoff).Select(p=>p.Key).ToArray())seen.Remove(expired);
            nextSeenCleanup=now.AddMinutes(1);
        }
        var key=(n.Chat,n.Kind,n.Id);
        bool RecentEnd(string reason)=>seen.TryGetValue((n.Chat,Kind.TurnEnded,reason+"/"+n.Turn),out var time) && time>=cutoff;
        if(n.Urgent && n.Turn!="" && (RecentEnd("turn_aborted") ||
            (n.Kind==Kind.Permission || n.Source=="session" && !n.Async) && RecentEnd("task_complete"))){
            n.Approval?.Respond("pass");return;
        }
        if(n.Kind==Kind.Question && n.Source!="preview"){
            if(n.Reference.QuestionCall is {} call && seen.TryGetValue((n.Chat,Kind.Answered,call),out var ended) && ended>=cutoff)return;
            if(MergeQuestion(n)){seen[key]=now;return;}
        }
        if(seen.TryGetValue(key,out var firstSeen) && firstSeen>=cutoff)return;
        seen[key]=now;
        if(n.Kind==Kind.Permission && n.Source is not ("preview" or "hook"))
        {
            if(n.Source=="native" && HandledInOriginal(n,n.Body))return;
        }
        if(ConnectionVerification.FromNotice(n) is {} signal)VerifyEvent(signal);
        var entry=new Entry(n);
        if(n.Urgent && n.Source!="preview")activeRequests.Add(entry);
        if(n.Approval!=null)officialEntries.Add(n.Approval,entry);
        if(n.Source!="preview" && prefs.History)Record(()=>entry.HistoryId=history.Add(n,RecentHistory.Summary(n)));
        if(n.Urgent && TrialInProgress)EndSettingsTrial();
        AddCard(entry);
    }
    bool MergeQuestion(Notice n)
    {
        var matches=activeRequests.Where(e=>e.MatchesQuestion(n)).ToArray();
        if(matches.Length==0)return false;
        var keep=matches.FirstOrDefault(e=>e.Window?.Protected==true)??matches.FirstOrDefault(e=>e.Visible)??matches[0];
        var before=keep.Notice;
        var remove=new List<Guid>();
        foreach(var other in matches.Where(e=>e!=keep)){
            foreach(var alias in other.Aliases)keep.Attach(alias);
            if(other.Notice.Prompts!=null)keep.Notice=other.Notice;
            activeRequests.Remove(other);RemoveCard(other);
            if(other.HistoryId is {} id){
                if(keep.HistoryId==null)keep.HistoryId=id;else if(keep.HistoryId!=id)remove.Add(id);
            }
        }
        if(keep.Attach(n.Reference) || matches.Length>1 || (n.Prompts!=null && keep.Notice.Prompts==null)){
            if(n.Prompts!=null)keep.Notice=n;
            Record(()=>history.LinkQuestions(keep.HistoryId,keep.Aliases,keep.Notice,remove));
            if(keep.Notice!=before){
                keep.Measured=Size.Empty;
                keep.Window?.UpdateNotice(keep.Notice);
                ScheduleLayout();
            }
        }
        return true;
    }
    void RefreshTitles()
    {
        string p=Path.Combine(MonitorFiles.Home,"session_index.jsonl");
        try{
            var last=File.GetLastWriteTimeUtc(p);
            if(last!=titlesModified&&File.Exists(p))
            {
                using var s=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var rd=new StreamReader(s);string? line;
                while((line=rd.ReadLine())!=null){try{using var j=JsonDocument.Parse(line);var r=j.RootElement;if(Guid.TryParse(r.Str("id"),out var chat)&&r.Str("thread_name") is {Length:>0} v)names[chat]=v;}catch(JsonException){}}
                titlesModified=last;
            }
        }catch(IOException){}catch(UnauthorizedAccessException){}
    }
    string CachedTitle(Guid id)=>id==Guid.Empty?"本机预览":names.GetValueOrDefault(id,"对话 "+id.ToString()[..8]);
    string ChatTitle(Guid id){RefreshTitles();return CachedTitle(id);}
    bool HasUniqueTitle(Guid chat,string title) =>
        names.TryGetValue(chat,out var known) && known==title &&
        !names.Any(other=>other.Key!=chat && other.Value==title);
    async void CheckNativeApproval(Notice hint)
    {
        var chat=hint.Chat;
        if(approvalChecks.TryGetValue(chat,out var active) && active.Turn==hint.Turn){
            active.Until=DateTime.UtcNow.AddSeconds(20);return;
        }
        var scan=new ApprovalScan(hint.Turn);
        approvalChecks[chat]=scan;
        bool Current()=>approvalChecks.GetValueOrDefault(chat)==scan;
        bool HasDesktopApproval()=>activeRequests.Any(e=>e.Notice.Chat==chat && e.Notice.Kind==Kind.Permission && e.Notice.Source=="desktop");
        Notice? live=null;
        var probe=hint with{Kind=Kind.Permission};
        int missed=0;
        try
        {
            while(!shuttingDown && Current() && (DateTime.UtcNow<scan.Until || live!=null))
            {
                if(HasDesktopApproval())break;
                var title=ChatTitle(chat);
                if(!HasUniqueTitle(chat,title))break;
                var form=await Task.Run(()=>FormBridge.Read(probe,title));
                if(shuttingDown || !Current() || HasDesktopApproval())break;
                if(live!=null && form?.FormKey==live.NativeForm && form.Context==live.Body)missed=0;
                else if(live!=null && (form!=null || ++missed>=3))
                {
                    Receive(new(Kind.Answered,chat,live.Id));live=null;missed=0;
                }
                if(form!=null && live==null)
                {
                    live=probe with {Id="form/"+Guid.NewGuid().ToString("N"),Source="native",NativeForm=form.FormKey,Body=form.Context};
                    Receive(live);
                }
                await Task.Delay(650);
            }
        }
        finally
        {
            if(Current())approvalChecks.Remove(chat);
            if(!shuttingDown && live!=null)Receive(new(Kind.Answered,chat,live.Id));
        }
    }
    async Task<(CompletionView View,string Match)> ViewOf(Entry entry,IntPtr foreground)
    {
        if(foreground==IntPtr.Zero || foreground!=Native.GetForegroundWindow())return (CompletionView.Unknown,"unresolved");
        if(!Native.CodexInFront(foreground))return (CompletionView.OtherApp,"foreground-app");
        entry.CheckingView=true;
        try
        {
            if(entry.Notice.Chat!=Guid.Empty && Native.ForegroundRoute(foreground,monitor.Routes) is Guid visible)
                return (visible==entry.Notice.Chat ? CompletionView.SameChat : CompletionView.OtherChat,"route-id");
            var document=await foregroundChat.Read(foreground);
            if(foreground!=Native.GetForegroundWindow())return (CompletionView.Unknown,"unresolved");
            var result=ForegroundChat.Match(entry.Notice.Chat,document,names);
            return (result,result==CompletionView.Unknown?"unresolved":"title");
        }
        finally { entry.CheckingView=false; }
    }
    async void Tick()
    {
        bool connectionChanged=RefreshConnection();
        if(settings is {WindowState:not FormWindowState.Minimized} &&
            (publishedRevision!=history.Revision || connectionChanged))settings.RefreshState();
        if(pollingViews || cards.Count==0)return;
        var completions=cards.Where(e=>e.Visible && e.Notice.Kind==Kind.Done && e.Notice.Chat!=Guid.Empty && !e.CheckingView).ToArray();
        if(completions.Length==0)return;
        pollingViews=true;
        try{
            var fg=Native.GetForegroundWindow();
            RefreshTitles();
            foreach(var entry in completions){
                if(!entry.Visible)continue;
                string title=CachedTitle(entry.Notice.Chat);
                if(entry.Window!.DisplayedTitle!=title)entry.Window.SetChatTitle(title);
            }
            if(fg==IntPtr.Zero)return;
            bool inClient=Native.CodexInFront(fg);
            var route=inClient?Native.ForegroundRoute(fg,monitor.Routes):null;
            var document=inClient && route==null?await foregroundChat.Read(fg):null;
            if(shuttingDown || Native.GetForegroundWindow()!=fg)return;
            foreach(var e in completions){
                var view=!inClient?CompletionView.OtherApp:route is {} id?
                    (id==e.Notice.Chat?CompletionView.SameChat:CompletionView.OtherChat):ForegroundChat.Match(e.Notice.Chat,document,names);
                if(e.Visible && e.ReturnedToTask(view))RemoveCard(e);
            }
        }finally{pollingViews=false;}
    }
    bool pollingViews;
    protected override void ExitThreadCore()
    {
        shuttingDown=true;trialTimer.Stop();trialTimer.Dispose();stop.Cancel();monitor.Dispose();pulse.Stop();pulse.Dispose();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged-=DisplaysChanged;
        settings?.Close();RemoveCards(_=>true);foreach(var trayWindow in overflow.Values)trayWindow.Close();tray.Visible=false;tray.ContextMenuStrip?.Dispose();tray.Dispose();dispatcher.Dispose();
        Audit.Write("resident-stop");base.ExitThreadCore();
    }
}
