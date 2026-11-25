using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace FloatNotify;

internal static class Audit
{
    static readonly object gate=new();
    public static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FloatNotify");
    public static void Write(string message)
    {
        try{lock(gate){Directory.CreateDirectory(Folder);var p=Path.Combine(Folder,"events.log");if(File.Exists(p)&&new FileInfo(p).Length>192*1024)File.Move(p,p+".1",true);File.AppendAllText(p,$"{DateTimeOffset.Now:O} {message}\n");}}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}

internal static class Program
{
    public static readonly string Pipe="FloatNotify-"+WindowsIdentity.GetCurrent().User!.Value+"-"+Process.GetCurrentProcess().SessionId;
    public static void OpenThread(Guid chat)=>Process.Start(new ProcessStartInfo("codex://threads/"+chat){UseShellExecute=true});
    [STAThread]
    static int Main(string[] args)
    {
        if(args is ["--hook"])
        {
            try
            {
                using var j=JsonDocument.Parse(Console.OpenStandardInput());
                if(j.RootElement.Str("hook_event_name")=="PermissionRequest" &&
                    ApprovalDetails.Parse(j.RootElement,MonitorFiles.Sessions,reason=>Audit.Write("permission-hook-deferred reason="+reason)) is {} approval)
                {
                    OfficialApproval.RunHook(approval).GetAwaiter().GetResult();
                    return 0;
                }
                if(j.RootElement.Str("hook_event_name")=="Stop")
                    Send(new {action="hint",path=j.RootElement.Str("transcript_path")});
            }
            catch(Exception e) when(e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or
                ArgumentException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception){}
            Console.WriteLine("{}");return 0;
        }
        if(args is ["--self-test"]){try{Checks.Run();Console.WriteLine("PASS");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
        if(args is ["--exit"])return Send(new {action="exit"})?0:1;
        if(args is ["--preview",var kind] && kind is "done" or "question" or "permission")return Send(new {action="preview",kind})?0:1;
        if(args is ["--preview","done",var target] && Guid.TryParse(target,out var chat))
            return Send(new {action="preview",kind="done",chat})?0:1;
        if(args is ["--dismiss-preview"])return Send(new {action="dismiss-preview"})?0:1;
        bool background=args is ["--background"];
        if(args.Length!=0 && !background && args is not ["--settings"])return 2;
        using var singleton=new Mutex(true,@"Local\"+Pipe,out bool first);
        if(!first)return background||Send(new {action="settings"})?0:1;
        ApplicationConfiguration.Initialize();
        using var app=new Resident();if(!background)app.OpenSettings();Application.Run(app);return 0;
    }
    static bool Send(object message)
    {
        try{using var pipe=new NamedPipeClientStream(".",Pipe,PipeDirection.Out,PipeOptions.None);pipe.Connect(350);using var writer=new StreamWriter(pipe,new UTF8Encoding(false));writer.WriteLine(JsonSerializer.Serialize(message));return true;}
        catch(Exception e) when(e is IOException or TimeoutException or UnauthorizedAccessException){return false;}
    }
}

internal static class Native
{
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h,out int id);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h,IntPtr insert,int x,int y,int w,int height,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h,uint flags,StringBuilder name,ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    public static bool IsCodexPath(string path)
    {
        string file=Path.GetFileName(path);
        return (file.Equals("Codex.exe",StringComparison.OrdinalIgnoreCase)||file.Equals("ChatGPT.exe",StringComparison.OrdinalIgnoreCase))
            && (path.Contains(@"\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Programs\Codex\",StringComparison.OrdinalIgnoreCase));
    }
    public static bool CodexInFront(IntPtr h)=>h!=IntPtr.Zero&&!IsIconic(h)&&IsCodexWindow(h);
    internal delegate bool WindowVisitor(IntPtr window,IntPtr state);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(WindowVisitor visit,IntPtr state);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h,int part);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] internal static extern int WindowStyle(IntPtr h,int index);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder name,int size);
    static bool MainChatWindow(IntPtr window)
    {
        if(!IsWindowVisible(window) || GetWindow(window,4)!=IntPtr.Zero ||
           (WindowStyle(window,-16)&0x20000)==0 || (WindowStyle(window,-20)&0x80)!=0)return false;
        var name=new StringBuilder(64);
        return GetClassName(window,name,name.Capacity)>0 && name.ToString()=="Chrome_WidgetWin_1";
    }
    public static Guid? ForegroundRoute(IntPtr foreground,DesktopChatRoutes routes)
    {
        if(!MainChatWindow(foreground))return null;
        GetWindowThreadProcessId(foreground,out int pid);
        bool ambiguous=false;
        EnumWindows((window,_)=>{
            GetWindowThreadProcessId(window,out int owner);
            if(window!=foreground && owner==pid && MainChatWindow(window))ambiguous=true;
            return !ambiguous;
        },IntPtr.Zero);
        if(ambiguous)return null;
        try
        {
            using var process=Process.GetProcessById(pid);
            return routes.ForPrimary(pid,process.StartTime.ToUniversalTime());
        }
        catch(Exception e) when(e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception){return null;}
    }
    [StructLayout(LayoutKind.Sequential)] struct GuiThreadInfo
    {
        public int Size,Flags;
        public IntPtr Active,Focus,Capture,Menu,Move,Caret;
        public int Left,Top,Right,Bottom;
    }
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
    public static IntPtr KeyboardWindow(IntPtr window)
    {
        var info=new GuiThreadInfo{Size=Marshal.SizeOf<GuiThreadInfo>()};
        uint thread=GetWindowThreadProcessId(window,out _);
        return thread!=0 && GetGUIThreadInfo(thread,ref info) ? info.Focus : IntPtr.Zero;
    }
    public static bool IsCodexWindow(IntPtr h)
    {
        if(h==IntPtr.Zero)return false;
        GetWindowThreadProcessId(h,out int id);
        var p=OpenProcess(0x1000,false,id);if(p==IntPtr.Zero)return false;
        try{var name=new StringBuilder(2048);int n=name.Capacity;return QueryFullProcessImageName(p,0,name,ref n)&&IsCodexPath(name.ToString());}
        finally{CloseHandle(p);}
    }
}
