using System.Text.Json;

namespace FloatNotify;

internal static class Checks
{
    public static void Run()
    {
        int count=0;
        void Ok(bool test,string why){if(!test)throw new InvalidOperationException(why);count++;}
        Guid chat=Guid.NewGuid(), turn=Guid.NewGuid();
        string log="2026-11-24T01:02:03.456Z info [electron-message-handler] [desktop-notifications] ";
        Ok(Events.Desktop(log+$"received approval conversationId={chat} kind=commandExecution requestId=12")?.Kind==Kind.Permission,"actual approval");
        Ok(Events.Desktop(log+$"received question conversationId={chat} requestId=17")?.Kind==Kind.Question,"actual question");
        Ok(Events.Desktop(log+$"received turn-complete conversationId={chat} turnId={turn}")?.Id==turn.ToString(),"completion id");
        Ok(Events.Desktop(log+$"show notification conversationId={chat} kind=question requestId=undefined")==null &&
            Events.Desktop(log+$"received question conversationId={chat} requestId=null")==null,"placeholder IDs do not merge unrelated questions");
        Ok(Events.Desktop(log+$"received approval conversationId={chat} requestId=0 turnId={turn}") is {Id:"0"} approval &&
            approval.Turn==turn.ToString(),"desktop request preserves its turn and valid zero ID");
        var ended=new Notice(Kind.TurnEnded,chat,"new","task_complete","new");
        var desktop=new RequestRef("desktop","server/0","",false);
        var sessionRef=new RequestRef("session","call","new",false);
        RequestRef[] aliases=[desktop,sessionRef];
        Ok(ended.Resolves(chat,aliases) && !ended.Resolves(Guid.NewGuid(),aliases),"end belongs to the same chat and turn");
        Ok(!(ended with{Turn="older",Body="turn_aborted"}).Resolves(chat,aliases),"a missing desktop turn cannot override a known session turn");
        Ok(!ended.Resolves(chat,[desktop,sessionRef with{Async=true}]) &&
            (ended with{Body="turn_aborted"}).Resolves(chat,[desktop,sessionRef with{Async=true}]),"async questions survive completion until answered or aborted");
        Ok(!ended.Resolves(chat,[desktop]) && !(ended with{Body="turn_aborted"}).Resolves(chat,[desktop]),"a desktop-only approval without a turn waits for its own receipt");
        var response=new Notice(Kind.RequestAnswered,Guid.Empty,"server/0");
        Ok(response.Resolves(chat,[desktop]) && !response.Resolves(chat,[sessionRef with{Id="server/0"}]) &&
            !response.Resolves(chat,[desktop with{Id="other/0"}]),"desktop replies require the matching desktop instance and request");
        Ok(!response.Resolves(chat,aliases) && response.Resolves(chat,[desktop with{CallId="call"},sessionRef]),"legacy time-linked history cannot close a structured question on an unverified receipt");
        CheckQuestionIdentity(chat,turn,Ok);
        var duplicate=new FormBridge.QuestionScope(new DesktopChatRoutes(),false);
        Ok(duplicate.Matches(chat,chat,"同名对话","同名对话"),"same-name question follows the matching window route");
        Ok(duplicate.Matches(chat,chat,"Codex","同名对话"),"the identified main window may use the application title");
        Ok(!duplicate.Matches(chat,Guid.NewGuid(),"同名对话","同名对话"),"a different same-name chat cannot receive the answer");
        Ok(!duplicate.Matches(chat,null,"同名对话","同名对话"),"same-name questions require a known route");
        var unique=duplicate with{UniqueTitle=true};
        Ok(unique.Matches(chat,null,"唯一对话","唯一对话") && !unique.Matches(chat,null,"Codex","唯一对话"),
            "only an exact unique title can stand in for a missing route");
        Ok(!unique.Matches(chat,Guid.NewGuid(),"唯一对话","唯一对话"),"a different known route overrides a matching title");
        Ok(Events.Desktop($"2026-11-24T00:00:00.000Z info [electron-message-handler] [windows-review-request] reveal requested conversationId={chat} hostId=local method=mcpServer/elicitation/request requestId=21 wasMinimized=true")?.Kind==Kind.Permission,"MCP request");
        Ok(Events.Desktop(log+$"received approval conversationId=invalid requestId=23")==null,"invalid chat");
        Ok(Events.Desktop("Tool requested elevated permissions: require_escalated")==null,"automatic escalation");
        Ok(Events.Desktop(log+$"suppressed approval conversationId={chat} requestId=1")==null,"suppressed approval");
        Ok(Events.Desktop("2026-11-24T00:00:00.000Z info [host-app-server-manager] Sending server response id=21 method=mcpServer/elicitation/request response={}")?.Kind==Kind.RequestAnswered,"request cleared");
        var session=new Session();
        List<Notice> Row(string type,object payload){var row=JsonSerializer.SerializeToElement(new{type,payload});return session.Read(row).ToList();}
        Row("session_meta",new{id=chat,originator="Codex Desktop",thread_source="user",source="vscode"});
        Row("event_msg",new{type="task_started",turn_id=turn});
        Ok(Row("response_item",new{type="custom_tool_call",name="exec",call_id="c1",input="require_escalated"}).Single().Kind==Kind.CheckApproval,"tool hint only requests a read of the native form");
        Ok(Row("response_item",new{type="custom_tool_call",name="exec",call_id="c2",input="Get-Date"}).Count==0,"ordinary commands do not trigger approval scans");
        var args=JsonSerializer.Serialize(new{questions=new[]{new{title="选择 A 或 B",options=new[]{"先看","再决定"}},new{title="再选一个",options=new[]{"可以"}}}});
        var q=Row("response_item",new{type="function_call",name="request_user_input_async",call_id="call_A",arguments=args});
        Ok(q.Single().Body=="选择 A 或 B","async question title");
        Ok(q.Single().Prompts is {Length:2} ps && ps[0].Options.SequenceEqual(new[]{"先看","再决定"}) && ps[1].Options.Single()=="可以","full native option order and multiple questions");
        var planArgs=JsonSerializer.Serialize(new{questions=new[]{new{id="a",question="同步提问",options=new[]{new{label="选择 A",description="详情"}}}}});
        var plan=Row("response_item",new{type="function_call",name="request_user_input",call_id="call_plan",arguments=planArgs});
        Ok(plan.Single().Prompts!.Single().Options.Single()=="选择 A","plan options have label fields");
        Ok(Row("response_item",new{type="function_call_output",call_id="call_plan",output="{}"}).Single().Kind==Kind.Answered,"sync question finalized");
        Ok(Row("response_item",new{type="function_call_output",call_id="call_A",output="{\"accepted\":true}"}).Count==0,"async ack keeps popup");
        List<Notice> Answer(int i){
            var item=JsonSerializer.Serialize(new object[]{"request_user_input_async","call_A",i});
            var text="<send_user_message_question_reply>\n"+JsonSerializer.Serialize(new[]{new{questionItemId=item,answer="A"}})+"\n</send_user_message_question_reply>";
            return Row("response_item",new{type="message",role="user",content=new[]{new{type="input_text",text}}});
        }
        Ok(Answer(0).Count==0,"partial answer");
        Row("event_msg",new{type="task_complete",turn_id=turn});
        Ok(Answer(1).Single().Kind==Kind.Answered,"async answer after end of turn");
        Ok(Row("event_msg",new{type="turn_aborted",turn_id=turn}).All(x=>x.Kind!=Kind.Done),"cancelled turn");
        Ok(Row("event_msg",new{type="task_complete",turn_id=turn}).Count(x=>x.Kind==Kind.Done)==1,"normal turn");
        Row("session_meta",new{id=chat,originator="Codex Desktop",thread_source="agent_created_thread",source="vscode"});
        Ok(Row("event_msg",new{type="task_complete",turn_id=turn}).Count(x=>x.Kind==Kind.Done)==1,"user-visible chat created from the desktop");
        Row("session_meta",new{id=chat,originator="Codex Desktop",thread_source="agent_created_thread",source=new{subagent="worker"}});
        Ok(Row("event_msg",new{type="task_complete",turn_id=turn}).Count==0,"background worker is not a user-visible desktop chat");
        Row("session_meta",new{id=chat,originator="Codex Desktop",thread_source="subagent"});
        Ok(Row("event_msg",new{type="task_complete",turn_id=turn}).Count==0,"subagent filtered");
        Ok(Native.IsCodexPath(@"C:\Program Files\WindowsApps\OpenAI.Codex_1\app\ChatGPT.exe"),"desktop package");
        Ok(!Native.IsCodexPath(@"D:\Tools\codex.exe"),"unrelated foreground");
        var titles=new Dictionary<Guid,string>{{chat,"本轮任务"},{Guid.NewGuid(),"另一条对话"}};
        Ok(ForegroundChat.Match(chat,"本轮任务",titles)==CompletionView.SameChat,"unique source title identifies the foreground task");
        Ok(ForegroundChat.Match(chat,"另一条对话",titles)==CompletionView.OtherChat,"unique other title identifies a different task");
        titles[Guid.NewGuid()]="本轮任务";
        Ok(ForegroundChat.Match(chat,"本轮任务",titles)==CompletionView.Unknown,"duplicate titles leave the current task unresolved");
        Ok(ForegroundChat.Match(chat,null,titles)==CompletionView.Unknown && ForegroundChat.Match(chat,"未知标题",titles)==CompletionView.Unknown,"unavailable and unindexed titles leave the task unresolved");
        titles[chat]="ChatGPT";
        Ok(ForegroundChat.Match(chat,"ChatGPT",titles)==CompletionView.Unknown,"generic activity and home titles leave the task unresolved");
        CheckOfficialApprovals(Ok);
        CheckRoutes(Ok);
        CheckFileDelivery(Ok);
        CheckMonitorRetention(Ok);
        CheckSettingsDetails(Ok);
        CheckCardLayout(Ok);
        CheckPlacement(Ok);
        var menu=new FormBridge.MenuSession("opener","doc","form",[]);
        var original=new FormBridge.View("form","只读",null,[new("允许一次","one",true,false),new("允许类似命令","similar",true,false)],false,1,menu);
        var reopened=original with {Actions=[original.Actions[0] with {Key="new-one"},original.Actions[1] with {Key="new-similar"}]};
        Ok(FormBridge.ReopenedAction(original,reopened,"允许一次")?.Key=="new-one","recreated menu keeps explicit one-time scope");
        Ok(FormBridge.ReopenedAction(original,reopened with {Menu=menu with {Owner="another"}},"允许一次")==null,"different menu opener rejected");
        Ok(FormBridge.ReopenedAction(original,reopened with {Actions=[..reopened.Actions.Reverse()]},"允许一次")==null,"changed menu rejected");
        Ok(FormBridge.ReopenedAction(original,reopened with {Context="不同的操作"},"允许一次")==null,"changed request details rejected");
        Console.WriteLine($"{count} checks passed.");
    }
    static void CheckQuestionIdentity(Guid chat,Guid turn,Action<bool,string> check)
    {
        string Forward(string call,int index,string host="local"){
            var item=JsonSerializer.Serialize(new object[]{"request_user_input_async",call,index});
            var outer=JsonSerializer.Serialize(new[]{Guid.Empty.ToString(),"example",host,chat.ToString(),turn.ToString(),item});
            return "2026-11-24T01:02:03.456Z info [notifications-service] [desktop-notifications] forward show kind=question notificationId=async-question-"+outer;
        }
        var a=Events.Desktop(Forward("call_A",0))!;
        var b=Events.Desktop(Forward("call_B",0))!;
        check(a is {Source:"notification",Async:true,Id:"call_A"} && a.Chat==chat && a.Turn==turn.ToString(),"recover actual async notification identity");
        check(Events.Desktop(Forward("call_A",1))==a && Events.Desktop(Forward("call_A",0,"remote"))==null &&
            Events.Desktop(Forward("call_A",0)[..^3])==null,"question indices share their call; ignore incomplete or nonlocal notifications");
        var sessionA=new Notice(Kind.Question,chat,"call_A","A",Source:"session",Async:true,Prompts:[new("A",[])]);
        var sessionB=sessionA with{Id="call_B",Body="B",Prompts=[new("B",[])]};
        var first=new Resident.Entry(a);var second=new Resident.Entry(sessionB);
        check(!first.MatchesQuestion(sessionB) && first.MatchesQuestion(sessionA) && second.MatchesQuestion(b),"interleaved A/B questions pair by call across sources");
        var opaque=new Notice(Kind.Question,chat,"server/A",Source:"desktop");
        check(!first.MatchesQuestion(opaque) && !second.MatchesQuestion(opaque),"an opaque server number cannot claim a structured question");
        first.Attach(sessionA.Reference);first.Attach((opaque with{CallId="call_A"}).Reference);second.Attach(b.Reference);
        var receipt=new Notice(Kind.RequestAnswered,Guid.Empty,"server/A");
        check(receipt.Resolves(chat,first.Aliases) && !receipt.Resolves(chat,second.Aliases),"A's verified receipt leaves B actionable");
        check(!(new Notice(Kind.TurnEnded,chat,"old","turn_aborted","old")).Resolves(chat,first.Aliases),"the full live request survives an unrelated abort even if its session has no turn");
        check((new Notice(Kind.Answered,chat,"call_B")).Resolves(chat,second.Aliases),"session receipt also closes a desktop-first question");
        check(Resident.Connection.From(false,false,true,true).Label=="待配置" && Resident.Connection.From(true,false,true,true).Label=="部分配置","missing registration wins over earlier traffic");
        check(Resident.Connection.From(true,true,false,false).Kind!="on" && Resident.Connection.From(true,true,true,false).Kind!="on" &&
            Resident.Connection.From(true,true,true,true).Kind=="on","green connection requires both hooks to have delivered");
    }
    static void CheckOfficialApprovals(Action<bool,string> check)
    {
        var folder=Directory.CreateTempSubdirectory("floatnotify-permission-test-");
        var path=Path.Combine(folder.FullName,"rollout-test.jsonl");
        var chat=Guid.NewGuid();var turn=Guid.NewGuid();
        string row(string type,object payload)=>JsonSerializer.Serialize(new{type,payload})+"\n";
        string header=row("session_meta",new{id=chat,originator="Codex Desktop",thread_source="user",source="vscode"});
        string context(string reviewer,string policy="on-request")=>row("turn_context",new{turn_id=turn,approvals_reviewer=reviewer,approval_policy=policy});
        ApprovalDetails? parse(string? file=null,string? root=null)=>ApprovalDetails.Parse(JsonSerializer.SerializeToElement(new{
            hook_event_name="PermissionRequest",session_id=chat,turn_id=turn,transcript_path=file??path,tool_name="Bash",cwd="test",
            tool_input=new{command="Get-Date",description="查看时间？"}}),root??folder.FullName);
        string extended(string file)=>file.StartsWith(@"\\") ? @"\\?\UNC\"+file[2..] : @"\\?\"+file;
        try{
            File.WriteAllText(path,header+context("auto_review"));
            check(parse()==null,"auto-review is returned to Codex");
            check(parse(extended(path))==null,"extended transcript keeps automatic review with Codex");
            File.WriteAllText(path,header+context("user","never"));
            check(parse()==null,"never policy is not actionable");
            File.WriteAllText(path,header);
            check(parse()==null,"unknown reviewer goes to the original");
            File.WriteAllText(path,header+context("user")+row("event_msg",new{type="turn_aborted",turn_id=turn}));
            check(parse()==null,"aborted turn is not actionable");
            File.WriteAllText(path,header+context("user")+row("turn_context",new{turn_id=Guid.NewGuid(),approvals_reviewer="user",approval_policy="on-request"}));
            check(parse()==null,"wrong turn cannot borrow reviewer");
            File.WriteAllText(path,header+context("user")+row("test_padding",new{text=new string('x',2*1024*1024)}));
            var detail=parse()??throw new InvalidOperationException("missing human approval");
            check(parse(extended(path))==detail && parse(path,extended(folder.FullName))==detail,"ordinary and extended paths open the same verified transcript");
            check(MonitorFiles.SessionFile(@"\\server\share\sessions\",@"\\?\UNC\server\share\sessions\one.jsonl")==@"\\server\share\sessions\one.jsonl",
                "extended UNC and ordinary UNC identify the same session file");
            check(MonitorFiles.SessionFile(folder.FullName,extended(Path.Combine(folder.FullName,"..","outside.jsonl")))==null &&
                MonitorFiles.SessionFile(folder.FullName,extended(folder.FullName+"-sibling\\one.jsonl"))==null &&
                MonitorFiles.SessionFile(folder.FullName,@"\\?\GLOBALROOT\Device\Anything")==null,
                "normalization keeps session directory boundaries");
            check(detail.Command=="Get-Date" && detail.Arguments=="Get-Date" && detail.Reason=="查看时间？","complete command in long active turn");
            var n=new Notice(Kind.Permission,chat,"native","查看时间？\nGet-Date",Turn:turn.ToString());
            check(detail.MatchesOriginal(n,n.Body) && !detail.MatchesOriginal(n,"查看时间？\nGet-Date -AsUTC")
                && !detail.MatchesOriginal(n with{Chat=Guid.NewGuid()},n.Body),"native handoff requires exact command and same chat");
            var extra=JsonSerializer.SerializeToElement(new{hook_event_name="PermissionRequest",session_id=chat,turn_id=turn,transcript_path=path,
                tool_name="Bash",tool_input=new{command="Get-Date",network="example.invalid"}});
            check(ApprovalDetails.Parse(extra,folder.FullName)!.Arguments.Contains("example.invalid"),"scope arguments remain visible");
            var sample=new OfficialApproval(detail);
            using var card=new Card(n with{Source="hook",Approval=sample},"测试",()=>{},()=>{},()=>false);
            check(!card.ShowActivated && card.ContextText.Text=="Get-Date" && card.AllowActions.Children.Count==1 &&
                card.Decisions.Children.Count==1 && card.MoreActions.Children.Count==0,"official card has the two supported actions without a native form");
            ((System.Windows.Controls.Button)card.AllowActions.Children[0]).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            check(!sample.Active && !sample.Respond("deny"),"click resolves precisely once");
            CheckApprovalPipe(detail,check).GetAwaiter().GetResult();
        }finally{if(File.Exists(path))File.Delete(path);folder.Delete();}
    }
    static Task CheckApprovalPipe(ApprovalDetails sample,Action<bool,string> check)=>Task.Run(async ()=>{
        // An isolated pipe and in-memory output exercise the actual wire protocol without a Codex request.
        var name="FloatNotify-test-"+Guid.NewGuid().ToString("N");
        using var stop=new CancellationTokenSource();
        var offered=System.Threading.Channels.Channel.CreateUnbounded<OfficialApproval>();
        var finished=System.Threading.Channels.Channel.CreateUnbounded<(string,string)>();
        var loop=OfficialApproval.Listen(a=>offered.Writer.TryWrite(a),(a,d)=>finished.Writer.TryWrite((a.Details.Command,d)),stop.Token,name);
        try{
            async Task<string> send(string label){
                using var output=new StringWriter();
                await OfficialApproval.RunHook(sample with{Command=label},name,output);
                return output.ToString().Trim();
            }
            async Task<OfficialApproval> next()=>await offered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
            var first=send("synthetic-one");var one=await next();
            var second=send("synthetic-two");var two=await next();
            check(!first.IsCompleted && !second.IsCompleted && one.Id!=two.Id,"pending approvals wait independently");
            two.Respond("deny");one.Respond("allow");
            check(await first==OfficialApproval.Output("allow") && await second==OfficialApproval.Output("deny"),"reverse-order choices reach their own callers");
            var handled=new Dictionary<string,string>();
            for(int i=0;i<2;i++){var (label,decision)=await finished.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));handled[label]=decision;}
            check(handled["synthetic-one"]=="allow" && handled["synthetic-two"]=="deny","hook stdout receipt matches the card");
            var third=send("synthetic-ignore");var ignored=await next();ignored.Respond("pass");
            check(await third=="{}","ignore resumes native approval");
            var fourth=send("synthetic-shutdown");await next();stop.Cancel();
            check(await fourth=="{}","resident shutdown resumes native approval");
        }finally{stop.Cancel();await loop.WaitAsync(TimeSpan.FromSeconds(4));}
    });

    static void CheckSettingsDetails(Action<bool,string> check)
    {
        var temp=Directory.CreateTempSubdirectory("floatnotify-settings-");
        try
        {
            string dir=temp.FullName;
            Guid projectChat=Guid.NewGuid(), archivedChat=Guid.NewGuid(), unknown=Guid.NewGuid();
            string actualRoot=@"D:\Work\Project", cwd=@"\\?\D:\Archived\Workspace";
            var state=new Dictionary<string,object>{
                ["thread-project-assignments"]=new Dictionary<Guid,object>{{projectChat,new{projectKind="local",projectId="p"}}},
                ["local-projects"]=new{p=new{name="项目别名",rootPaths=new[]{actualRoot}}},
                ["electron-owner-value:thread-workspace-root-hints:test"]=new Dictionary<Guid,string>{{projectChat,@"E:\Old"}},
                ["projectless-thread-ids"]=new[]{archivedChat}
            };
            File.WriteAllText(Path.Combine(dir,".codex-global-state.json"),JsonSerializer.Serialize(state));
            string archive=temp.CreateSubdirectory("archived_sessions").FullName;
            File.WriteAllText(Path.Combine(archive,$"rollout-old-{archivedChat:D}.jsonl"),JsonSerializer.Serialize(new{
                type="session_meta",payload=new{id=archivedChat,cwd}
            })+"\nignored");
            File.Copy(Path.Combine(archive,$"rollout-old-{archivedChat:D}.jsonl"),Path.Combine(archive,$"rollout-old-{unknown:D}.jsonl"));
            var workspaces=new ThreadWorkspaces(dir);
            workspaces.Refresh();
            check(workspaces.For(projectChat)==new WorkspaceInfo("项目别名",actualRoot),"explicit project beats older workspace hint");
            check(workspaces.For(archivedChat)==new WorkspaceInfo("Workspace",@"D:\Archived\Workspace"),"archived row recovers its verified working directory");
            check(workspaces.For(unknown)==ThreadWorkspaces.Unknown && workspaces.For(Guid.Empty)==ThreadWorkspaces.Unknown,"missing and mismatched chat ids do not borrow a project");
            string hookPath=Path.Combine(dir,"hooks.json"), exe=Path.Combine(dir,"FloatNotify.exe");
            var handler=new{type="command",commandWindows=$"& '{exe.Replace("'","''")}' --hook"};
            File.WriteAllText(hookPath,JsonSerializer.Serialize(new{hooks=new{Stop=new[]{new{hooks=new[]{handler}}}}}));
            File.WriteAllText(Path.Combine(dir,"config.toml"),$"[hooks.state.'{hookPath}:stop:4:0']\ntrusted_hash='sha256:{new string('a',64)}'\n");
            check(DiagnosticReport.HookRegistration(dir,exe).Contains("未找到此条目的信任记录"),"another hook's trust is excluded");
            check(DiagnosticReport.HookRegistration(dir,Path.Combine(dir,"old","FloatNotify.exe")).Contains("未匹配"),"stale installation path is reported");
            File.AppendAllText(Path.Combine(dir,"config.toml"),$"[hooks.state.'{hookPath}:stop:0:0']\ntrusted_hash='sha256:{new string('b',64)}'\n");
            check(DiagnosticReport.HookRegistration(dir,exe).Contains("有信任记录"),"current hook has a saved trust record");
            var hookFlags=JsonSerializer.SerializeToElement(new{data=new[]{new{name="hooks",enabled=true}}});
            var unset=JsonSerializer.SerializeToElement(new{config=new{features=new{}}});
            var disabled=JsonSerializer.SerializeToElement(new{config=new{features=new{hooks=false}}});
            check(CodexHookReport.FeatureSetting(unset,hookFlags).Contains("unset） · 当前已开启"),"unset and effective hook states are independent");
            check(CodexHookReport.FeatureSetting(disabled,default).StartsWith("显式关闭"),"disabled is not reported as unset");
            object Hook(string eventName,string command,string trustStatus,bool enabled=true,string source="user")=>new{eventName,command,trustStatus,enabled,source};
            var inventory=JsonSerializer.SerializeToElement(new{data=new[]{new{cwd=dir,hooks=new[]{
                Hook("stop",handler.commandWindows,"modified"),Hook("stop","other","trusted",false),
                Hook("permissionRequest","other","trusted"),Hook("preToolUse","managed","managed",true,"system")
            }}}});
            string report=CodexHookReport.FormatInventory(inventory,dir,exe)??"";
            check(report.Contains("Stop=modified / trusted") && report.Contains("浮笺 Stop：本安装路径匹配 · 已注册 / modified"),"mixed trust for the same event is attributed to the correct executable");
            check(report.Contains("条目已停用") && report.Contains("PermissionRequest=trusted") && !report.Contains("PreToolUse"),"user registrations and trust stay separate from policy hooks");
            check(CodexHookReport.FormatInventory(inventory,Path.Combine(dir,"unrelated"),exe)==null,"another host directory never supplies this inventory");
            var empty=JsonSerializer.SerializeToElement(new{data=new[]{new{cwd=dir,hooks=Array.Empty<object>(),errors=new[]{"invalid configuration"}}}});
            check(CodexHookReport.FormatInventory(empty,dir,exe)!.Contains("清单不完整"),"a failed load is not reported as an empty healthy inventory");
            string stamp="2026-01-01T02:03:04Z ";
            File.WriteAllText(Path.Combine(dir,"events.log.1"),stamp+"done-shown source=session centered=True renderer=system material=Liquid keyboardWindow=unchanged named=True\n");
            File.WriteAllText(Path.Combine(dir,"events.log"),stamp+"question-shown source=preview\n"+stamp+"history-write-failed\n");
            var audit=DiagnosticReport.ReadAudit(dir);
            check(audit.Popup.Contains("回复已完成") && audit.Popup.Contains("实际渲染：系统合成") && audit.Popup.Contains("保持不变") && audit.Popup.Contains("匹配"),"rotated real popup retains observed renderer and focus despite later trial");
            check(audit.Issue.Contains("写入提醒历史"),"last history failure is separately identified");
            string prefix=$"{DateTimeOffset.Now:O} ";
            string shown=$"permission-shown source=hook chat={projectChat} turn=t request=r";
            File.WriteAllLines(Path.Combine(dir,"events.log.1"),[prefix+shown]);
            File.WriteAllLines(Path.Combine(dir,"events.log"),[prefix+shown,prefix+shown+"2",
                prefix+$"done-shown source=session chat={projectChat} turn=t",prefix+"question-shown source=preview"]);
            var restored=RecentHistory.ImportAudit(dir);
            check(restored.Count==3 && restored.Count(r=>r.Chat==projectChat && r.Refs[0].Source=="hook")==2,
                "audit recovery keeps distinct approvals in the same turn and excludes previews and repeated records");
        }
        finally { temp.Delete(true); }
    }

    static void CheckRoutes(Action<bool,string> check)
    {
        Guid local=Guid.NewGuid(), cloud=Guid.NewGuid(), project=Guid.NewGuid();
        var routes=new DesktopChatRoutes();
        var start=DateTimeOffset.UtcNow;
        void Row(int seconds,string body,int pid=10)=>routes.Observe(pid,"run",$"{start.AddSeconds(seconds):O} info [electron-message-handler] "+body);
        void Route(int seconds,string path,int id=1,int pid=10)=>Row(seconds,
            $"IAB_LIFECYCLE received browser sidebar owner sync browserTabId=null conversationId={local} originWebContentsId=1 ownerRoutePath={path} windowId={id}",pid);
        void Appearance(int seconds,int id,string role,int pid=10)=>Row(seconds,
            $"[desktop-notifications] received turn-complete rendererWindowAppearance={role} rendererWindowId={id}",pid);
        check(DesktopChatRoutes.ParsePath($"/c/{cloud}?x=1")==cloud && DesktopChatRoutes.ParsePath($"/local/{local}")==local,"plain cloud and local route IDs");
        check(DesktopChatRoutes.ParsePath($"/g/{project}/c/{cloud}")==cloud && DesktopChatRoutes.ParsePath($"/g/{project}")==null,"project ID is never used as the chat ID");
        Appearance(1,1,"primary"); Route(2,$"/local/{local}");
        Route(3,$"/g/project-slug/c/{cloud}"); Route(2,$"/local/{local}");
        Appearance(4,2,"avatarOverlay"); Route(4,$"/local/{local}",2);
        check(routes.ForPrimary(10,start)==cloud,"newest primary route wins despite older records, overlays and unrelated conversationId");
        check(routes.ForPrimary(11,start)==null && routes.ForPrimary(10,start.AddSeconds(6))==null,"ignore another process or a recycled PID");
        Row(5,"client.performance.span name=thread_navigation outcome=success rendererWindowAppearance=primary rendererWindowId=1 wallDurationMs=1900");
        check(routes.ForPrimary(10,start)==cloud,"normal delayed navigation span retains matching owner route");
        Row(9,"client.performance.span name=thread_navigation outcome=success rendererWindowAppearance=primary rendererWindowId=1 wallDurationMs=300");
        check(routes.ForPrimary(10,start)==null,"navigation without a corresponding new route clears stale identity");
        Route(10,$"/local/{local}"); Route(11,"/settings");
        check(routes.ForPrimary(10,start)==null,"leaving chat clears the previous route");
        Route(12,$"/local/{local}"); Appearance(12,3,"primary"); Route(12,$"/c/{cloud}",3);
        check(routes.ForPrimary(10,start)==null,"ambiguous primary windows never share an ID");
    }
    static void CheckPlacement(Action<bool,string> check)
    {
        foreach(double scale in new[]{1d,1.25,1.5,2}){
            int P(int n)=>(int)Math.Round(n*scale);
            var area=new Rectangle(-P(1440),P(40),P(1440),P(960));
            var size=new Size(P(480),P(185));
            var blocked=new List<CardPlacement.Block>();
            Rectangle? Find(bool done=false)=>CardPlacement.Find(area,done?new Size(size.Width,P(60)):size,scale,done,blocked);
            var first=Find()!.Value;blocked.Add(new(first,false));
            check(first.Top==area.Top+(area.Height-size.Height)/2,"first centered at target DPI");
            var second=Find()!.Value;blocked.Add(new(second,false));
            check(second.Top>first.Bottom && first.Left==second.Left,"second below first");
            var third=Find()!.Value;
            check(third.Left>second.Left && third.Top>=second.Top+P(45) && third.IntersectsWith(second),"cascade preserves title");
            blocked.Clear();var hud=Find(true)!.Value;blocked.Add(new(hud,true));
            for(int i=0;i<24 && Find() is {} face;i++){check(!face.IntersectsWith(hud),"completion always entirely clear");blocked.Add(new(face,false));}
            check(Find()==null,"finite space becomes overflow");
            blocked.Clear();blocked.Add(new(new Rectangle(area.Left,area.Top,P(250),P(100)),true,true));
            check(Find()==first,"detached side card releases center");
            blocked.Clear();blocked.Add(new(first,true,true));
            check(!Find()!.Value.IntersectsWith(first),"slightly moved or editing face remains protected");
        }
    }
    static void CheckCardLayout(Action<bool,string> check)
    {
        using var permission=new Card(new(Kind.Permission,Guid.Empty,"preview",Source:"preview"),"预览",()=>{},()=>{},()=>false);
        using var question=new Card(new(Kind.Question,Guid.Empty,"preview",Source:"preview"),"预览",()=>{},()=>{},()=>false);
        using var done=new Card(new(Kind.Done,Guid.Empty,"preview",Source:"preview"),"预览",()=>{},()=>{},()=>false);
        bool Visible(System.Windows.UIElement c)=>c.Visibility==System.Windows.Visibility.Visible;
        foreach (var card in new[] { permission, question })
        {
            check(!card.ShowActivated && card.Headline.Focusable &&
                !System.Windows.Input.KeyboardNavigation.GetIsTabStop(card.Headline),"interactive card shows without activation and supports a neutral click target");
        }
        check(permission.MoreActions.Children.Count==1 && permission.Decisions.Children.Count==1 && permission.AllowActions.Children.Count==1,"original approval, refusal and optional split menu keep separate actions");
        check(permission.ContextText.Text.Contains("Get-ChildItem") && Visible(permission.ContextBox),"approval context shown");
        check(!question.Send.IsEnabled,"no answer preselected or sent");
        var pendingNotice=new Notice(Kind.Question,Guid.NewGuid(),"pending","测试草稿",Source:"session",Prompts:[new("测试草稿",["选项"])]);
        bool identified=true;
        using var pending=new Card(pendingNotice,"测试",()=>{},()=>{},()=>identified);
        check(Visible(pending.Send) && !pending.Send.IsEnabled && pending.SendLabel.Text=="发送","empty question has one disabled submit button");
        pending.Editor.Text="自定义回答";
        pending.UpdateNotice(pendingNotice);
        check(pending.Editor.Text=="自定义回答" && pending.SendLabel.Text=="打开原题并发送" && pending.Send.IsEnabled,"the same submit button offers to open the original and retains the draft");
        var footerRoot=(System.Windows.FrameworkElement)pending.Content;
        var size=new System.Windows.Size(pending.Width,480);
        footerRoot.Measure(size);footerRoot.Arrange(new System.Windows.Rect(size));footerRoot.UpdateLayout();
        var labelStart=pending.SendLabel.TranslatePoint(new System.Windows.Point(),pending.Send);
        check(labelStart.X>=0 && labelStart.X+pending.SendLabel.ActualWidth<=pending.Send.ActualWidth &&
            System.Windows.Automation.AutomationProperties.GetName(pending.Send)==pending.SendLabel.Text,
            "the full open-and-send label fits and is also the accessible button name");
        identified=false;
        pending.UpdateNotice(pendingNotice);
        check(!pending.Send.IsEnabled && pending.Editor.Text=="自定义回答","unidentified requests cannot send the draft");
        identified=true;
        pending.UpdateNotice(pendingNotice);
        check(pending.Send.IsEnabled && pending.SendLabel.Text=="打开原题并发送","recovering the request restores the advertised fallback");
        pending.Editor.Clear();
        check(!pending.Send.IsEnabled && pending.SendLabel.Text=="发送","clearing the draft resets the primary action");
        pending.UpdateNotice(pendingNotice with {Prompts=[new("第一题",["甲"]),new("第二题",["乙"])]});
        check(!Visible(pending.EditorBox) && !Visible(pending.Send) &&
            !Visible(pending.ApprovalStrip) && pending.Options.Children.Count==4,
            "a question with multiple unresolved prompts discards the previous single-question controls");
        pending.UpdateNotice(pendingNotice);
        check(Visible(pending.EditorBox) && Visible(pending.Send) && pending.Options.Children.Count==1,
            "returning to one question restores its controls exactly once");
        var options=question.Options.Children.OfType<System.Windows.Controls.RadioButton>().ToArray();
        options[0].IsChecked=true; options[1].IsChecked=true;
        check(options[0].IsChecked==false && options[1].IsChecked==true && question.Send.IsEnabled && question.SendLabel.Text=="发送","available original is sent directly after changing the answer");
        question.Editor.Text="自己的回答";
        check(options.All(o=>o.IsChecked==false) && question.Send.IsEnabled,"custom text replaces selected option");
        options[0].IsChecked=true;
        check(question.Editor.Text.Length==0 && question.Send.IsEnabled,"switch from free text to original option");
        check(!done.ShowActivated && Visible(done.Hud) && !Visible(done.DialogFace),"completion has a passive compact surface");
        check(done.DisplayedTitle=="预览" && done.Width==516 && done.DoneLabel.Text=="ChatGPT丨回复已完成","completion shows the supplied chat title at standard width");
        done.SetChatTitle(new string('宽',60));
        check(done.Width==626 && done.DisplayedTitle.Length==60,"long completion title retains its full text and caps width");
        check(question.Editor.AcceptsReturn && question.Editor.FontSize==14 && question.Placeholder.Text=="或输入自己的回复","multiline question uses the approved typography and placeholder");
        check(!done.HudClose.Focusable && !done.HudOpen.Focusable,"HUD actions leave keyboard focus alone");
        var title=string.Concat(Enumerable.Repeat("需要完整显示的长问题。",15));
        var labels=Enumerable.Range(1,12).Select(i=>"选项 "+i+" "+title).ToArray();
        question.UpdateNotice(new(Kind.Question,Guid.Empty,"sample",title,Prompts:[new(title,labels)]));
        var root=(System.Windows.FrameworkElement)question.Content;
        root.Measure(new System.Windows.Size(480,445));root.Arrange(new System.Windows.Rect(0,0,480,445));root.UpdateLayout();
        var scroll=(System.Windows.Controls.ScrollViewer)((System.Windows.FrameworkElement)question.Headline.Parent).Parent;
        check(scroll.ScrollableHeight>0 && question.Options.Children.Count==12 && question.Headline.Text==title,"long questions wrap and all choices scroll");
        check(question.Send.ActualWidth>0 && question.Ignore.ActualHeight>0,"footer stays available for long content");
    }
    static void CheckMonitorRetention(Action<bool,string> check)
    {
        var root=Directory.CreateTempSubdirectory("floatnotify-retention-");
        var sessions=root.CreateSubdirectory("sessions").FullName;
        var logs=root.CreateSubdirectory("logs").FullName;
        var received=new System.Collections.Concurrent.ConcurrentQueue<Notice>();
        var monitor=new MonitorFiles(received.Enqueue,sessions,logs);
        try{
            check(monitor.Ready.Wait(5000),"retention watcher ready");
            var stamp=DateTimeOffset.UtcNow;
            var chat=Guid.NewGuid();var first=Guid.NewGuid();var second=Guid.NewGuid();
            string Row(string type,object payload)=>JsonSerializer.Serialize(new{type,payload,timestamp=stamp})+"\n";
            string SessionText(Guid id)=>Row("session_meta",new{id=chat,originator="Codex Desktop",thread_source="user"})+
                Row("response_item",new{text=new string('x',800000)})+Row("event_msg",new{type="task_complete",turn_id=id});
            string file=Path.Combine(sessions,"rollout-reused.jsonl");
            bool Delivered(Guid id)=>SpinWait.SpinUntil(()=>received.Any(n=>n.Kind==Kind.Done && n.Id==id.ToString()),5000);
            File.WriteAllText(file,SessionText(first));
            check(Delivered(first),"large session delivers before archive");
            File.Move(file,Path.Combine(root.FullName,"archived-first"));
            File.WriteAllText(file,SessionText(second));
            check(Delivered(second),"replacement of the same length and filename starts at its own header");
            File.Move(file,Path.Combine(root.FullName,"archived-second"));
            Thread.Sleep(550);
            File.WriteAllText(Path.Combine(logs,"codex-desktop-barrier.log"),$"{DateTime.UtcNow:O} info [electron-message-handler] [desktop-notifications] received approval conversationId={chat} requestId=barrier\n");
            check(SpinWait.SpinUntil(()=>received.Any(n=>n.Id=="barrier"),5000),"watcher processed the archive batch");
            monitor.Dispose();monitor.Finished.GetAwaiter().GetResult();
            check(monitor.RetainedAfterStop==(1,0),"only the small desktop log remains and large buffers have been released");
        }finally{monitor.Dispose();monitor.Finished.GetAwaiter().GetResult();root.Delete(true);}
    }
    static void CheckFileDelivery(Action<bool,string> check)
    {
        var root=Directory.CreateTempSubdirectory("floatnotify-monitor-");
        var sessions=root.CreateSubdirectory("sessions").FullName;
        var logs=root.CreateSubdirectory("logs").FullName;
        var q=new System.Collections.Concurrent.ConcurrentQueue<Notice>();
        Guid chat=Guid.NewGuid(),turn=Guid.NewGuid();
        string file=Path.Combine(sessions,"rollout-test.jsonl");
        string splitFile=Path.Combine(sessions,"rollout-split.jsonl");
        string Row(string t,object p)=>JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,type=t,payload=p})+"\n";
        File.WriteAllText(file,Row("session_meta",new{id=chat,originator="Codex Desktop",thread_source="user"})+
            Row("event_msg",new{type="task_complete",turn_id=Guid.NewGuid()}));
        var m=new MonitorFiles(q.Enqueue,sessions,logs);
        try
        {
            check(m.Ready.Wait(4000),"monitor startup");
            string end=Row("event_msg",new{type="task_complete",turn_id=turn});
            File.AppendAllText(file,end[..^3]);m.Hint(file);Thread.Sleep(450);
            check(q.IsEmpty,"old and partial records stay silent");
            File.AppendAllText(file,end[^3..]);
            check(SpinWait.SpinUntil(()=>q.Any(e=>e.Kind==Kind.Done&&e.Id==turn.ToString()),4000),"resumed session append");
            string logFile=Path.Combine(logs,"codex-desktop-test.log");
            string body=$" info [electron-message-handler] [desktop-notifications] received approval conversationId={chat} requestId=";
            File.WriteAllText(logFile,"2020-01-01T00:00:00.000Z"+body+"old\n"+DateTime.UtcNow.ToString("O")+body+"24\n");
            check(SpinWait.SpinUntil(()=>q.Any(e=>e.Kind==Kind.Permission&&e.Id=="24"),4000),"new desktop log rotation");
            check(q.Count(e=>e.Kind==Kind.Done)==1,"no duplicate file delivery");
            check(!q.Any(e=>e.Id=="old"),"stale records in a new log are ignored");
            var splitTurn=Guid.NewGuid();
            string header=Row("session_meta",new{instructions=new string('x',80000),id=chat,originator="Codex Desktop",thread_source="user"});
            File.WriteAllText(splitFile,header[..70000]);m.Hint(splitFile);Thread.Sleep(550);
            File.AppendAllText(splitFile,header[70000..]+Row("event_msg",new{type="task_complete",turn_id=splitTurn}));m.Hint(splitFile);
            check(SpinWait.SpinUntil(()=>q.Any(e=>e.Kind==Kind.Done && e.Id==splitTurn.ToString()),4000),"large partially written session header resumes after its newline");
            Guid firstRun=Guid.NewGuid(),secondRun=Guid.NewGuid();
            string DesktopFile(Guid run,int pid)=>Path.Combine(logs,$"codex-desktop-{run}-{pid}-t0-i1-test.log");
            string DesktopRow(string text)=>DateTime.UtcNow.ToString("O")+text+"\n";
            string response=" info [host-app-server-manager] Sending server response id=1 method=item/commandExecution/requestApproval response={}";
            File.WriteAllText(DesktopFile(firstRun,42),DesktopRow(response));
            check(SpinWait.SpinUntil(()=>q.Any(e=>e.Kind==Kind.RequestAnswered && e.Id==firstRun+"/1"),3000),"desktop response from separate process");
            File.WriteAllText(DesktopFile(firstRun,43),DesktopRow(body+"1")+DesktopRow(body+"2"));
            check(SpinWait.SpinUntil(()=>q.Any(e=>e.Id==firstRun+"/2"),3000),"second request still pending");
            check(!q.Any(e=>e.Id==firstRun+"/1" && e.Urgent),"late record after real answer suppressed");
            using var handle=new FileStream(DesktopFile(secondRun,43),FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite);
            using var writer=new StreamWriter(handle){AutoFlush=true};
            writer.Write(DesktopRow(body+"2"));
            check(SpinWait.SpinUntil(()=>q.Any(e=>e.Id==secondRun+"/2"),3000),"discover log while writer keeps it open");
            writer.Write(DesktopRow(body+"1"));
            check(SpinWait.SpinUntil(()=>q.Any(e=>e.Id==secondRun+"/1" && e.Urgent),2500),"prompt from an open log; request ID reused after restart");
        }
        finally
        {
            m.Dispose();m.Finished.Wait(5000);
            File.Delete(file);File.Delete(splitFile);foreach(string path in Directory.GetFiles(logs))File.Delete(path);
            Directory.Delete(sessions);Directory.Delete(logs);root.Delete();
        }
    }
}
