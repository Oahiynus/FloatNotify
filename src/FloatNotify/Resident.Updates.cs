using System.Diagnostics;
using System.Net.Http;

namespace FloatNotify;

internal sealed partial class Resident
{
    ReleaseUpdate? release;
    string updatePhase = "idle";
    string updateMessage = "";
    internal bool OpenUpdatedAbout { get; private set; }

    void ReadUpdateResult()
    {
        if(Environment.GetEnvironmentVariable("FLOATNOTIFY_UPDATED_TO")!=AppVersion)return;
        Environment.SetEnvironmentVariable("FLOATNOTIFY_UPDATED_TO",null);
        OpenUpdatedAbout=true;
        updateMessage=$"浮笺已更新至 v{AppVersion}。";
    }

    internal object UpdateState()=>new {update=new {
        phase=updatePhase, available=release?.NewVersion!=null, message=updateMessage
    }};

    internal void UpdateAction(string action)
    {
        if(shuttingDown || updatePhase!="idle")return;
        if(action=="checkUpdate")TrackRead(CheckUpdateAsync());
        if(action=="installUpdate" && release?.NewVersion is {} version)TrackRead(InstallUpdateAsync(version));
        if(action=="openRelease" && release?.ReleasePage is {} page)
            Process.Start(new ProcessStartInfo(page.AbsoluteUri){UseShellExecute=true})?.Dispose();
    }

    void UpdateStatus(string phase,string message)
    {
        updatePhase=phase;updateMessage=message;
        if(!shuttingDown)settings?.PublishUpdate();
    }

    async Task CheckUpdateAsync()
    {
        release=null;
        UpdateStatus("check","正在连接 GitHub…");
        release=await ReleaseUpdates.CheckAsync(AppVersion,stop:stop.Token);
        UpdateStatus("idle",release.Message);
    }

    async Task InstallUpdateAsync(Version version)
    {
        string? directory=null;
        bool handedOff=false;
        UpdateStatus("download",$"正在下载 v{version}…");
        try
        {
            directory=Directory.CreateTempSubdirectory("FloatNotify-update-").FullName;
            void Progress(int percent)=>Post(()=>{
                if(updatePhase=="download")UpdateStatus("download",$"正在下载 v{version} · {percent}%");
            });
            string digest=await UpdateInstaller.DownloadAsync(version,directory,Progress,stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            using var helper=UpdateInstaller.Start(version,directory,digest);
            handedOff=true;
            UpdateStatus("install","校验通过，正在安装。浮笺将自动重新打开。");
            await helper.WaitForExitAsync(stop.Token);
            UpdateStatus("idle",helper.ExitCode==0?"更新已完成。":"更新未完成，可以重试。");
        }
        catch(OperationCanceledException) when(!stop.IsCancellationRequested){UpdateStatus("idle","下载超时，请重试。");}
        catch(HttpRequestException ex){
            UpdateStatus("idle",ex.StatusCode is {} status?$"下载失败（HTTP {(int)status}），请稍后重试。":"无法下载安装包，请检查网络后重试。");
        }
        catch(Exception ex) when(ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception){
            UpdateStatus("idle",ex.Message);
        }
        finally{if(!handedOff && directory!=null)UpdateInstaller.Discard(directory,version);}
    }
}
