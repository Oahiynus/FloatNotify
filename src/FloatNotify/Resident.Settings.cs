using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FloatNotify;

internal sealed partial class Resident
{
    readonly RecentHistory history=new();
    Preferences prefs=LocalData.Read<Preferences>("settings")??new();
    SettingsWindow? settings;
    string? settingsTrial;
    readonly System.Windows.Forms.Timer trialTimer=new(){Interval=3000};
    Kind trialKind;
    bool TrialInProgress=>trialTimer.Enabled || settingsTrial!=null;
    int publishedRevision=-1;
    readonly ConnectionVerification verification=new(MonitorFiles.Home,Audit.Folder);
    ConnectionState? connection;
    bool? changingCodex,retryCodex;
    string connectionError="";
    DateTime nextConnectionCheck;
    (DateTime Hooks,DateTime Config)? lastFiles;
    DiagnosticReport.Hook? stopHook,approvalHook;
    bool RefreshConnection(ConnectionVerification.Signal? signal=null)
    {
        // An intentional disconnect suspends verification. Compare the restored definitions on reconnect.
        if(!prefs.Codex || changingCodex!=null)return false;
        if(signal==null && DateTime.UtcNow<nextConnectionCheck)return false;
        nextConnectionCheck=DateTime.UtcNow.AddSeconds(3);
        string home=MonitorFiles.Home,exe=Path.Combine(About.InstallDir,"FloatNotify.exe");
        try{
            var files=(File.GetLastWriteTimeUtc(Path.Combine(home,"hooks.json")),File.GetLastWriteTimeUtc(Path.Combine(home,"config.toml")));
            // Timestamps only save repeat parsing. Evidence is bound to the effective definition.
            if(lastFiles!=files || signal!=null || stopHook?.Ready==null || approvalHook?.Ready==null){
                stopHook=DiagnosticReport.InspectHook(home,exe,"Stop");
                approvalHook=DiagnosticReport.InspectHook(home,exe,"PermissionRequest");
                lastFiles=files;
            }
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
            stopHook=approvalHook=new(null,"无法读取 Hook 配置（"+ex.GetType().Name+"）");
            lastFiles=null;
        }
        var next=verification.Update(approvalHook!,stopHook!,monitor.HasListener,signal);
        if(connection==next)return false;
        connection=next;
        return true;
    }
    void VerifyEvent(ConnectionVerification.Signal signal)
    {
        if(RefreshConnection(signal))settings?.RefreshState();
    }
    public void OpenSettings()
    {
        if(settings==null||settings.IsDisposed){
            settings=new SettingsWindow(this,OpenUpdatedAbout);
            OpenUpdatedAbout=false;
            settings.FormClosed+=(_,_)=>{settings=null;EndSettingsTrial();};
            settings.Show();
        }else{
            if(settings.WindowState==FormWindowState.Minimized)settings.WindowState=FormWindowState.Normal;
            settings.Show();settings.Activate();
        }
    }
    static string TypeOf(Kind kind)=>kind switch{Kind.Permission=>"approval",Kind.Question=>"question",_=>"done"};
    static string KindName(Kind kind)=>kind switch{Kind.Permission=>"请求审批",Kind.Question=>"需要你的回答",_=>"回复已完成"};
    static string FocusText(string value)=>value switch{"unchanged"=>"保持不变","changed"=>"已变化",_=>"未记录"};
    static string SignalName(Kind kind)=>kind switch{
        Kind.CheckApproval=>"核对原生审批",Kind.Answered or Kind.RequestAnswered=>"收到响应",Kind.TurnEnded=>"回合结束",_=>KindName(kind)
    };
    readonly ThreadWorkspaces workspaces=new(MonitorFiles.Home);
    string RowTitle(HistoryRow row)=>row.Title!=""?row.Title:row.Kind==Kind.Done && row.Chat!=Guid.Empty?ChatTitle(row.Chat):KindName(row.Kind);
    object ViewRow(HistoryRow row)
    {
        var at=row.At.ToLocalTime();
        var workspace=workspaces.For(row.Chat);
        return new{id=row.Id,type=TypeOf(row.Kind),client="codex",date=at.ToString("yyyy-MM-dd"),time=at.ToString("HH:mm:ss"),title=RowTitle(row),result=row.Status,pending=row.Pending,
            canOpen=row.Chat!=Guid.Empty,detail=new string[][]{
                ["所属对话",row.Chat==Guid.Empty?"旧记录未提供":ChatTitle(row.Chat)],
                ["项目 / 工作区",workspace.Label,workspace.Path]}};
    }
    sealed record AppDetails(string Version, string Windows, string Runtime, string InstallDir, string LogDir);
    internal static readonly string AppVersion = Application.ProductVersion.Split('+')[0];
    static readonly AppDetails About = new(
        AppVersion,
        $"Windows · 内部版本 {Environment.OSVersion.Version.Build} · {RuntimeInformation.OSArchitecture}",
        RuntimeInformation.FrameworkDescription,
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Audit.Folder);

    public async Task<string> AppDiagnosticsAsync()
    {
        nextConnectionCheck=DateTime.MinValue;
        RefreshConnection();
        string hooks=await CodexHookReport.ReadAsync(MonitorFiles.Home,Path.Combine(About.InstallDir,"FloatNotify.exe"));
        static string Setting(bool value) => value ? "开启" : "关闭";
        var queued=cards.Where(e=>!e.Visible && e.Notice.Source!="preview").ToArray();
        var pending=activeRequests;
        var log=DiagnosticReport.ReadAudit(Audit.Folder);
        string displayed=string.Join("、",cards.Where(e=>e.Visible).GroupBy(e=>e.Notice.Kind).Select(g=>$"{KindName(g.Key)} × {g.Count()}"));
        if(displayed.Length==0)displayed="无";
        var sessions=monitor.Listener(true);
        var desktop=monitor.Listener(false);
        return $"""
            浮笺 FloatNotify {About.Version} · 应用诊断
            运行环境：{About.Windows} · {About.Runtime}
            采集时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}
            登录 Windows 时启动：{Setting(Preferences.StartEnabled())}
            安装目录：{About.InstallDir}

            【提醒与历史】
            请求审批：{Setting(prefs.Approval)}
            等待回答：{Setting(prefs.Question)}
            回复完成：{Setting(prefs.Done)}
            当前卡片材质：{DiagnosticReport.MaterialName(material.ToString())}
            最近提醒：{Setting(prefs.History)} · 已保存 {history.Rows.Count()} 条
            历史存储：本地 JSON 明文 · 保留 {RecentHistory.Days} 天，最多 {RecentHistory.Limit} 条
            历史文件：{Path.Combine(About.LogDir,"history.json")}

            【客户端与待处理请求】
            Codex：已适配 · {(prefs.Codex?"提醒已开启":"提醒已暂停")}
            待适配：Claude Code、DeepSeek Harness、Antigravity
            本次运行待审批：{pending.Count(e=>e!.Notice.Kind==Kind.Permission)} 项
            本次运行待回答：{pending.Count(e=>e!.Notice.Kind==Kind.Question)} 项
            正在显示：{displayed}
            折叠在更多提醒中：{queued.Length} 张

            【三类提醒的接入验证】
            {ConnectionView()?.Detail}

            【Codex Hooks】
            {hooks}
            最近收到浮笺 PermissionRequest：{(lastOfficialUtc==DateTime.MinValue?"本次启动尚未收到":lastOfficialUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}
            最近收到浮笺 Stop 提示：{monitor.LastHook}

            【事件监听】
            会话监听：{sessions.Status}
            会话目录：{sessions.Path}
            桌面日志监听：{desktop.Status}
            桌面日志目录：{desktop.Path}
            最近收到事件：{lastSignal}

            【最近一次真实弹窗】
            {log.Popup}

            【浮笺日志】
            最近异常：{log.Issue}
            日志文件：{Path.Combine(About.LogDir,"events.log")}
            """.ReplaceLineEndings(Environment.NewLine);
    }
    ConnectionState? ConnectionView()
    {
        if(changingCodex is bool enabling){
            string label=enabling?"正在连接":"正在断开";
            return new("idle",label,label+" Codex…",label+" Codex…");
        }
        if(retryCodex is bool retry)return new("idle",retry?"连接失败":"断开失败",connectionError,connectionError);
        return prefs.Codex?connection:new("paused","未开启","Codex 提醒已关闭，已暂停验证。","");
    }
    public object SettingsSnapshot()
    {
        RefreshConnection();
        workspaces.Refresh();
        publishedRevision=history.Revision;
        return new {
            about=About,connection=ConnectionView(),changingCodex=changingCodex!=null,retryCodex,
            opts=new {prefs.Codex,prefs.Approval,prefs.Question,prefs.Done,prefs.History,start=Preferences.StartEnabled()},
            visible=prefs.Visible, material=material.ToString(),events=history.Rows.Select(ViewRow).ToArray(),revision=history.Revision,trial=TrialInProgress
        };
    }
    static bool Bool(JsonElement msg)=>msg.GetProperty("value").ValueKind switch{JsonValueKind.True=>true,JsonValueKind.False=>false,_=>throw new ArgumentException("无效的设置值。")};
    void Save(Preferences next){LocalData.Write("settings",next);prefs=next;}
    internal static async Task SetHooksAsync(string installDir,string home,bool enabled)
    {
        string script=Path.Combine(installDir,"Register.ps1");
        if(!File.Exists(script))throw new FileNotFoundException("安装目录缺少 Register.ps1，请重新安装浮笺。");
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe")){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,StandardErrorEncoding=System.Text.Encoding.UTF8
        };
        foreach(string arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-ForSettings","-ConfigDirectory",home})
            info.ArgumentList.Add(arg);
        if(!enabled)info.ArgumentList.Add("-Remove");
        using var process=Process.Start(info)??throw new InvalidOperationException("无法启动 Codex 连接脚本。");
        string error=(await process.StandardError.ReadToEndAsync().ConfigureAwait(false)).Trim();
        await process.WaitForExitAsync().ConfigureAwait(false);
        if(process.ExitCode!=0)throw new InvalidOperationException(error.Length==0?"请检查 Codex 配置目录后重试。":error);
    }

    async Task ChangeCodexAsync(bool enabled)
    {
        if(changingCodex!=null)return;
        changingCodex=enabled;
        retryCodex=null;
        bool applied=false;
        try{
            // Persist intent first. An interrupted two-file change resumes on the next launch.
            if(enabled!=prefs.Codex && prefs.PendingCodex!=enabled)Save(prefs with{PendingCodex=enabled});
            settings?.RefreshState();
            await SetHooksAsync(About.InstallDir,MonitorFiles.Home,enabled);
            applied=true;
            prefs=prefs with{Codex=enabled};
            if(!enabled)DismissDisabledRequests();
            if(prefs.PendingCodex!=null)Save(prefs with{PendingCodex=null});
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception){
            retryCodex=enabled;
            connectionError=(applied?"Hook 已更新，保存开关状态失败。":"无法更新浮笺的 Codex Hook。")+ex.Message;
            Audit.Write("codex-connection-change-failed enabled="+enabled+" error="+ex.GetType().Name);
            if(!applied && prefs.PendingCodex!=null){
                try{Save(prefs with{PendingCodex=null});}
                catch(Exception write) when(write is IOException or UnauthorizedAccessException){connectionError+=" 下次启动会重试。";}
            }
        }finally{
            changingCodex=null;lastFiles=null;nextConnectionCheck=DateTime.MinValue;
            if(!shuttingDown)settings?.RefreshState();
        }
    }
    void DismissDisabledRequests()
    {
        if(!prefs.Accept(Kind.Permission))foreach(var approval in officialEntries.Keys)approval.Respond("pass");
        RemoveCards(e=>e.Notice.Source!="preview"&&!prefs.Accept(e.Notice.Kind));
        activeRequests.RemoveWhere(e=>!prefs.Accept(e.Notice.Kind));
    }
    public async Task SettingsCommandAsync(JsonElement msg)
    {
        string key=msg.Str("key");
        switch(msg.Str("action")){
            case "init":break;
            case "visible":{
                if(key is not ("codex" or "claude" or "deepseek" or "gemini"))throw new ArgumentException("未知客户端。");
                var visible=new Dictionary<string,bool>(prefs.Visible){[key]=Bool(msg)};Save(prefs with{Visible=visible});break;
            }
            case "toggle":{
                bool value=Bool(msg);
                if(key=="start"){Preferences.SetStartup(value);break;}
                if(key=="codex"){await ChangeCodexAsync(value);break;}
                Save(key switch{
                    "approval"=>prefs with{Approval=value},"question"=>prefs with{Question=value},
                    "done"=>prefs with{Done=value},"history"=>prefs with{History=value},_=>throw new ArgumentException("未知设置。")});
                if(!value && key!="history")DismissDisabledRequests();
                break;
            }
            case "material":{
                var next=key switch{"Adaptive"=>GlassMaterial.Adaptive,"TonalLiquid"=>GlassMaterial.TonalLiquid,"Liquid"=>GlassMaterial.Liquid,"LiquidSystem"=>GlassMaterial.LiquidSystem,"Frosted"=>GlassMaterial.Frosted,_=>throw new ArgumentException("未知材质。")};
                GlassPreference.Write(next);material=next;foreach(var e in cards)e.Window?.ApplyMaterial(next);Audit.Write($"glass-material selected={next}");break;
            }
            case "openLogs":
                Directory.CreateDirectory(About.LogDir);
                Process.Start(new ProcessStartInfo(About.LogDir) { UseShellExecute=true })?.Dispose();
                break;
            case "clear":history.Clear();break;
            case "open":case "copy":{
                if(!Guid.TryParse(key,out var id))throw new ArgumentException("提醒编号无效。");
                var r=history.Find(id);
                if(msg.Str("action")=="open"){OpenReminder(r);}
                else{
                    var a=r.Refs.FirstOrDefault();
                    var workspace=workspaces.For(r.Chat);
                    Clipboard.SetText($"浮笺 · 单条诊断\n时间：{r.At:O}\n客户端：Codex\n类型：{KindName(r.Kind)}\n状态：{r.Status}\n标题：{RowTitle(r)}\n项目 / 工作区：{workspace.Label}{(workspace.Path.Length>0?" · "+workspace.Path:"")}\n对话编号：{(r.Chat==Guid.Empty?"未记录":r.Chat)}\n请求或回合：{a?.Id}\n来源：{a?.Source}\n居中：{r.Geometry}\n键盘焦点：{FocusText(r.Focus)}");
                    settings?.Copied(key);
                }
                break;
            }
            case "trial":
                ScheduleSettingsTrial(key);break;
            case "endtrial":EndSettingsTrial();break;
            default:throw new ArgumentException("未知操作。");
        }
    }
    void OpenReminder(HistoryRow row)
    {
        if(row.Chat==Guid.Empty)return;
        Program.OpenThread(row.Chat);
        if(!row.Pending || !prefs.Accept(row.Kind))return;
        var entry=activeRequests.FirstOrDefault(e=>e.HistoryId==row.Id);
        if(entry==null)return;
        if(!cards.Contains(entry))AddCard(entry);
        Reveal(entry);
    }
    void ScheduleSettingsTrial(string key)
    {
        if(TrialInProgress || settings==null)return;
        trialKind=key switch{"approval"=>Kind.Permission,"question"=>Kind.Question,"done"=>Kind.Done,_=>throw new ArgumentException("未知卡片。")};
        settings.MinimizeForTrial();
        trialTimer.Start();
    }
    public void CancelTrialOnRestore()
    {
        if(trialTimer.Enabled)EndSettingsTrial();
    }
    void ShowScheduledTrial()
    {
        trialTimer.Stop();
        if(shuttingDown || settings==null)return;
        settingsTrial=Guid.NewGuid().ToString();
        var entry=new Entry(new(trialKind,Guid.Empty,settingsTrial,Source:"preview"));
        AddCard(entry);
        settings.RefreshState();
    }
    void EndSettingsTrial()
    {
        if(trialTimer.Enabled){trialTimer.Stop();settings?.RefreshState();}
        if(settingsTrial==null)return;
        var id=settingsTrial;settingsTrial=null;
        RemoveCards(e=>e.Notice.Id==id && e.Notice.Source=="preview");
        settings?.RefreshState();
    }
    void Record(Action write)
    {
        try{write();}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Audit.Write("history-write-failed error="+ex.GetType().Name);}
    }
}
