using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace FloatNotify;

internal sealed class SettingsWindow : Form
{
    const string Address="https://fujian.local/index.html";
    readonly WebView2 web=new(){Dock=DockStyle.Fill};
    readonly Resident resident;
    bool ready, copyingDiagnostics;
    FormWindowState lastReportedState;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp=base.CreateParams;
            const int WS_CAPTION=0x00C00000;
            cp.Style &= ~WS_CAPTION; // Keep the native sizing frame and system menu.
            return cp;
        }
    }
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

    protected override void WndProc(ref Message m)
    {
        const int WM_NCCALCSIZE=0x0083;
        if(m.Msg==WM_NCCALCSIZE && m.LParam!=IntPtr.Zero && !IsZoomed(m.HWnd) && !IsIconic(m.HWnd))
        {
            // Both NCCALCSIZE layouts begin with a RECT. Keep the native sides
            // and bottom, but let the WebView paint from the actual window top.
            const int rectTopOffset=sizeof(int);
            int top=Marshal.ReadInt32(m.LParam,rectTopOffset);
            base.WndProc(ref m);
            Marshal.WriteInt32(m.LParam,rectTopOffset,top);
            m.Result=IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    void BeginTopResize(int hit)
    {
        const int WM_NCLBUTTONDOWN=0x00A1, VK_LBUTTON=1;
        if(WindowState!=FormWindowState.Normal || GetAsyncKeyState(VK_LBUTTON)>=0)return;
        var cursor=System.Windows.Forms.Cursor.Position;
        var coordinates=unchecked((IntPtr)((cursor.X & 0xffff) | (cursor.Y << 16)));
        ReleaseCapture();
        var message=Message.Create(Handle,WM_NCLBUTTONDOWN,(IntPtr)hit,coordinates);
        DefWndProc(ref message);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if(!ready || WindowState==lastReportedState)return;
        if(lastReportedState==FormWindowState.Minimized)resident.CancelTrialOnRestore();
        PublishWindowState();
    }
    void PublishWindowState()
    {
        lastReportedState=WindowState;
        Send(new {maximized=WindowState==FormWindowState.Maximized});
    }
    public void MinimizeForTrial()
    {
        // SW_MINIMIZE transfers activation to the next window in the desktop z-order.
        const int SW_MINIMIZE=6;
        ShowWindow(Handle,SW_MINIMIZE);
    }
    void WindowCommand(string command)
    {
        switch(command){
            // HTTOP, HTTOPLEFT and HTTOPRIGHT use the normal Windows sizing loop.
            case "resize-top":BeginInvoke(()=>BeginTopResize(12));break;
            case "resize-top-left":BeginInvoke(()=>BeginTopResize(13));break;
            case "resize-top-right":BeginInvoke(()=>BeginTopResize(14));break;
            case "minimize":WindowState=FormWindowState.Minimized;break;
            case "maximize":WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;break;
            case "close":BeginInvoke(Close);break;
        }
    }
    public SettingsWindow(Resident resident)
    {
        this.resident=resident;Text="浮笺";Icon=System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        StartPosition=FormStartPosition.CenterScreen;ClientSize=new Size(1040,710);MinimumSize=new Size(620,470);
        Controls.Add(web);Shown+=async (_,_)=>await Initialize();
    }
    static bool Trusted(string url)=>Uri.TryCreate(url,UriKind.Absolute,out var u)&&u.GetLeftPart(UriPartial.Path)==Address;
    async Task Initialize()
    {
        try {
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Audit.Folder,"WebView2"));
            if(IsDisposed)return;
            await web.EnsureCoreWebView2Async(env);
            if(IsDisposed)return;
            var core=web.CoreWebView2;
            core.Settings.IsNonClientRegionSupportEnabled=true;
            core.Settings.AreHostObjectsAllowed=false;core.Settings.AreDefaultContextMenusEnabled=false;
            core.Settings.IsStatusBarEnabled=false;core.Settings.AreDevToolsEnabled=false;
            core.SetVirtualHostNameToFolderMapping("fujian.local",Path.Combine(AppContext.BaseDirectory,"SettingsUI"),CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting+=(_,e)=>{if(!Trusted(e.Uri))e.Cancel=true;};
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.WebMessageReceived+=async (_,e)=>{
                if(!Trusted(e.Source))return;
                try {
                    using var msg=JsonDocument.Parse(e.WebMessageAsJson);
                    var root=msg.RootElement;
                    string action=root.Str("action");
                    if(action=="init"){ready=true;PublishWindowState();}
                    if(action=="window"){WindowCommand(root.Str("key"));return;}
                    if(!ready)return;
                    if(action=="copyDiagnostics")
                    {
                        if(copyingDiagnostics)return;
                        copyingDiagnostics=true;
                        try
                        {
                            string report=await resident.AppDiagnosticsAsync();
                            if(!IsDisposed&&!Disposing){Clipboard.SetText(report);Copied("application");}
                        }
                        finally{copyingDiagnostics=false;}
                        return;
                    }
                    await resident.SettingsCommandAsync(root);RefreshState();
                }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException){
                    Send(new{error=ex.Message});RefreshState();
                }
            };
            core.Navigate(Address);
        }catch(Exception ex){if(!IsDisposed)MessageBox.Show(this,"设置窗口打开失败。\n"+ex.Message,"浮笺");}
    }
    void Send(object value)
    {
        if(ready&&!IsDisposed&&web.CoreWebView2 is {} core&&Trusted(core.Source))core.PostWebMessageAsJson(JsonSerializer.Serialize(value,LocalData.Json));
    }
    public void Copied(string id)=>Send(new{copied=id});
    public void RefreshState(){if(ready)Send(resident.SettingsSnapshot());}
}
