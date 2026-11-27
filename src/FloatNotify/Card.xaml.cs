using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Media;
using Keys = System.Windows.Input.Key;
using Brush = System.Windows.Media.Brush;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace FloatNotify;

internal partial class Card : Window, IDisposable
{
    readonly bool urgent;
    readonly DirectChoices? choices;
    readonly Action dismiss;
    Rectangle workArea;
    bool closed, positioning, expanded;
    public bool Detached { get; private set; }
    public bool Protected => Detached || IsKeyboardFocusWithin || choices?.Busy == true;
    public event Action? LayoutRequested, HeaderClicked;
    Point? dragStart;
    bool dragging;
    Point dragOffset;
    readonly GlassSurface backdrop=new();
    readonly LiquidLens lens=new();
    readonly System.Windows.Threading.DispatcherTimer glassTimer=new(System.Windows.Threading.DispatcherPriority.Background);
    bool lensFailed;
    int idleFrames;
    bool covered;
    int liveFaces=1;
    public void SetExposure(int faces,bool obscured)
    {
        liveFaces=Math.Clamp(faces,1,10);
        if(covered==obscured)return;
        covered=obscured;
        if(covered){
            glassTimer.Stop();
            lens.PauseCapture();
            choices?.PauseReading();
        }else if(IsVisible){
            choices?.StartReading();SyncGlass();
        }
    }
    public string Renderer => lens.Brush!=null?"pixels":LiveBackdrop?"system":"tint";
    GlassMaterial material;
    public bool LiveBackdrop {get;private set;}
    public bool Refracting => lens.Brush!=null || backdrop.RimActive;
    public string DisplayedTitle => DoneText.Text;
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    const double OuterMargin = 36;
    public Card(Notice notice, string chat, Action open, Action dismiss, Func<bool> canBind, GlassMaterial material=GlassMaterial.Liquid, Func<FormBridge.QuestionScope>? questionScope=null)
    {
        InitializeComponent();
        this.dismiss = dismiss;
        string client=notice.Client switch {"DeepSeek Harness"=>"DeepSeek","Claude Code"=>"Claude","Antigravity"=>"Gemini", ""=>"ChatGPT",var value=>value};
        OpenLabel.Text=HudOpenLabel.Text="打开 "+client+" 查看";
        System.Windows.Automation.AutomationProperties.SetName(Open,OpenLabel.Text);
        System.Windows.Automation.AutomationProperties.SetName(HudOpen,HudOpenLabel.Text);
        CodeFold.Click+=(_,_)=>{expanded=!expanded;FitCode();};
        ContextText.SizeChanged+=(_,e)=>{if(e.WidthChanged)FitCode();};
        Width=480+OuterMargin;
        SizeToContent=SizeToContent.Height;
        urgent = notice.Urgent;
        ShowActivated = false;
        
        Ignore.Click += (_,_) => dismiss();
        Open.Click += (_,_) => open();
        HudOpen.Click += (_,_) => open();
        HudClose.Click += (_,_) => dismiss();
        Open.IsEnabled = HudOpen.IsEnabled = notice.Chat != Guid.Empty;
        if (urgent)
        {
            bool permission = notice.Kind == Kind.Permission;
            KindLabel.Text = client+"丨"+(permission ? "请求审批" : "需要你的回答");
            KindLabel.ToolTip = chat;
            ChatLabel.Text = chat; ChatLabel.ToolTip = chat;
            BadgeIcon.Data = Geometry.Parse(permission
                ? "M12,2 L20,5.5 V11 Q20,17 12,21 Q4,17 4,11 V5.5 Z M8,11 L11,14 16,8.5"
                : "M6,3 H18 Q21,3 21,6 V15 Q21,18 18,18 H10 L4,21 5,17 Q3,16 3,14 V6 Q3,3 6,3 Z M7,8 H17 M7,12 H14");

            choices = new DirectChoices(this, notice, chat, canBind, questionScope);
            IsVisibleChanged += (_,_) => { if(IsVisible) choices.StartReading(); else choices.PauseReading(); };
            PreviewKeyDown += OnUrgentKeyDown;
        }
        else
        {
            DoneLabel.Text=client+"丨回复已完成";
            SetChatTitle(chat);
            DialogFace.Visibility = Visibility.Collapsed;
            Hud.Visibility = Visibility.Visible;
            Hud.ToolTip = chat;
        }
        glassTimer.Tick+=(_,_)=>SyncGlass();
        LayoutUpdated+=(_,_)=>lens.InvalidateLayout();
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)ReleaseGlass();else RestoreShell();};
        ApplyMaterial(material);
        LocationChanged+=(_,_)=>SyncGlass();
        SourceInitialized += (_,_) =>
        {
            var window = HwndSource.FromHwnd(Handle)!;
            int flags = Native.WindowStyle(Handle, -20) | 0x80;
            if (!urgent) flags |= 0x08000000; // The completion bar also stays passive on mouse clicks.
            SetWindowLong(Handle, -20, flags);
            window.AddHook(WindowProc);
        };
        SizeChanged += (_,_) => { if (IsVisible && !positioning) LayoutRequested?.Invoke(); };
        DpiChanged += (_,_) => LayoutRequested?.Invoke();
        IsKeyboardFocusWithinChanged += (_,_) => { if(IsVisible)LayoutRequested?.Invoke(); };
        Closing += (_,e) =>
        {
            if (closed) return;
            e.Cancel=true;
            Dispatcher.InvokeAsync(() => { if (!closed) this.dismiss(); });
        };
    }
    IntPtr WindowProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int mouseActivate=0x21, leftDown=0x201, mouseMove=0x200, leftUp=0x202;
        if(msg==mouseActivate && (!urgent || IsDragArea(System.Windows.Forms.Cursor.Position))){
            handled=true;return (IntPtr)3; // MA_NOACTIVATE; keep typing in the original window.
        }
        if(msg==0x215){if(dragStart.HasValue){dragStart=null;LayoutRequested?.Invoke();}return IntPtr.Zero;}
        if(msg==0x1F){EndDrag(h);return IntPtr.Zero;}      // WM_CANCELMODE
        if(msg!=leftDown && (dragStart==null || (msg!=mouseMove && msg!=leftUp)))return IntPtr.Zero;

        // Mouse messages carry signed client pixels, including when capture moves outside the window.
        int coordinates=unchecked((int)lParam.ToInt64());
        var cursor=new Point((short)coordinates,(short)(coordinates>>16));
        if(!ClientToScreen(h,ref cursor))return IntPtr.Zero;
        if(msg==leftDown){
            if(!IsDragArea(cursor) || !GetWindowRect(h,out var window))return IntPtr.Zero;
            SetCapture(h);
            if(GetCapture()!=h)return IntPtr.Zero;
            dragStart=cursor;
            dragging=false;
            dragOffset=new Point(cursor.X-window.Left,cursor.Y-window.Top);
        }else{
            if(msg==leftUp || (wParam.ToInt64()&1)!=0){
                if(!dragging){
                    var start=dragStart!.Value;
                    var threshold=System.Windows.Forms.SystemInformation.DragSize;
                    dragging=Math.Abs(cursor.X-start.X)>=Math.Max(1,threshold.Width/2) ||
                        Math.Abs(cursor.Y-start.Y)>=Math.Max(1,threshold.Height/2);
                }
                if(dragging){
                    Detached=true;
                    Native.SetWindowPos(h,IntPtr.Zero,cursor.X-dragOffset.X,cursor.Y-dragOffset.Y,0,0,0x15);
                }
            }
            if(msg==leftUp || (wParam.ToInt64()&1)==0){
                bool click=!dragging && msg==leftUp;
                EndDrag(h);
                if(click)HeaderClicked?.Invoke();else LayoutRequested?.Invoke();
            }
        }
        handled=true;
        return IntPtr.Zero;
    }
    bool IsDragArea(Point screen)
    {
        var face=urgent?DialogFace:Hud;
        if(!face.IsVisible)return false;
        var point=face.PointFromScreen(new System.Windows.Point(screen.X,screen.Y));
        var hit=VisualTreeHelper.HitTest(face,point)?.VisualHit;
        for(var node=hit;node!=null;node=VisualTreeHelper.GetParent(node)){
            if(node is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar ||
               node==EditorBox || node==MenuBox || node==ApprovalGroup)return false;
            if(node==face)return true;
        }
        return false;
    }
    void EndDrag(IntPtr window)
    {
        if(!dragStart.HasValue)return;
        dragStart=null;
        if(GetCapture()==window)ReleaseCapture();
    }
    internal static Brush Paint(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    public void SetChatTitle(string title)
    {
        DoneText.Text=title; DoneText.ToolTip=title;
        var measured=new System.Windows.Controls.TextBlock{Text=title,FontSize=16,FontWeight=FontWeights.SemiBold,FontFamily=FontFamily};
        measured.Measure(new System.Windows.Size(double.PositiveInfinity,double.PositiveInfinity));
        Width=OuterMargin+Math.Clamp(measured.DesiredSize.Width+256,480,590);
    }
    public void ApplyMaterial(GlassMaterial value)
    {
        ReleaseGlass();
        material=value;lensFailed=false;
        RestoreShell();
        SyncGlass();
    }
    void ReleaseGlass()
    {
        glassTimer.Stop();
        lens.Dispose();backdrop.Dispose();LiveBackdrop=false;
        GlassShell.Background=null;
    }
    bool UsesPixels => material is GlassMaterial.Liquid or GlassMaterial.Clear;
    void RestoreShell()
    {
        GlassShell.Background=(Brush)FindResource(material switch {GlassMaterial.Frosted=>"FrostedFace",GlassMaterial.Clear=>"ClearFace",_=>"LiquidFace"});
        GlassShell.BorderThickness=new Thickness(1.3);
        GlassShell.Child.Visibility=Visibility.Visible;
    }
    void SyncGlass()
    {
        if(closed || !IsVisible || !IsLoaded || GlassShell.ActualWidth<1 || GlassShell.ActualHeight<1 || workArea.IsEmpty || positioning)return;
        if(UsesPixels && !lensFailed){
            if(covered && lens.Brush!=null)return;
            int changed=lens.Draw(this,!urgent,material==GlassMaterial.Clear);
            if(changed>=0){
                if(lens.Brush!=null && GlassShell.Background!=lens.Brush){
                    GlassShell.BorderThickness=new Thickness(0);
                    GlassShell.Child.Visibility=Visibility.Collapsed;
                    GlassShell.Background=lens.Brush;
                }
                LiveBackdrop=true;
                idleFrames=changed>0?0:Math.Min(6,idleFrames+1);
                int activeDelay=Math.Min(430,70*liveFaces);
                int idleStep=liveFaces==1?35:Math.Min(180,32*liveFaces);
                glassTimer.Interval=TimeSpan.FromMilliseconds(activeDelay+idleFrames*idleStep);
                if(covered)lens.PauseCapture();else if(!glassTimer.IsEnabled)glassTimer.Start();
                return;
            }
            lensFailed=true;glassTimer.Stop();lens.Dispose();RestoreShell();
            Audit.Write($"glass-pixels-unavailable reason={changed} fallback=system");
        }
        LiveBackdrop=backdrop.Place(Handle,GlassShell,material,!urgent);
        if(!LiveBackdrop)GlassShell.Background=Paint(material switch {GlassMaterial.Frosted=>"#EBF1F5F8",GlassMaterial.Clear=>"#CEDDEAF3",_=>"#DDF1F5F8"});
    }
    public void ShowOfficialApproval(ApprovalDetails request)
    {
        Headline.Text=request.Reason.Length is >0 and <=100 ? request.Reason : "请确认这项权限申请";
        Purpose.Text=request.Reason.Length>100 ? request.Reason : "";
        Purpose.Visibility=Purpose.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
        CodeLabel.Text=request.Tool;
        ContextText.Text=request.Arguments;
        ContextBox.Visibility=Visibility.Visible;
        LocationText.Text=request.Cwd.Length>0?"工作目录   "+request.Cwd:"";
        LocationText.Visibility=request.Cwd.Length>0?Visibility.Visible:Visibility.Collapsed;
        FitCode();
    }
    public void ShowContext(string details)
    {
        Purpose.Visibility=LocationText.Visibility=Visibility.Collapsed;
        ContextBox.Visibility=details.Length==0?Visibility.Collapsed:Visibility.Visible;
        if(details.Length==0)return;
        var lines=details.Replace("\r\n","\n").Split('\n');
        int shell=Array.FindIndex(lines,l=>l.Trim().ToLowerInvariant() is "powershell" or "pwsh" or "bash" or "cmd" or "shell" or "command" or "命令");
        int start=shell+1;
        int directory=Array.FindIndex(lines,start,l=>l.TrimStart().StartsWith("工作目录",StringComparison.Ordinal) || l.TrimStart().StartsWith("Working directory",StringComparison.OrdinalIgnoreCase));
        int end=directory>=0?directory:lines.Length;
        string code=string.Join("\n",lines[start..end]).Trim();
        if(code.Length==0){shell=-1;directory=-1;code=details;}
        if(shell>0){Purpose.Text=string.Join("\n",lines[..shell]).Trim();Purpose.Visibility=Visibility.Visible;}
        if(directory>=0){LocationText.Text=string.Join(" ",lines[directory..]);LocationText.Visibility=Visibility.Visible;}
        CodeLabel.Text=shell>=0?lines[shell].Trim():"请求详情";
        if(ContextText.Text!=code){ContextText.Text=code;expanded=false;}
        FitCode();
    }
    void FitCode()
    {
        if(ContextText.ActualWidth<1)return;
        var probe=new System.Windows.Controls.TextBlock{Text=ContextText.Text,FontSize=ContextText.FontSize,FontFamily=ContextText.FontFamily,
            LineHeight=19,LineStackingStrategy=LineStackingStrategy.BlockLineHeight,TextWrapping=TextWrapping.Wrap};
        probe.Measure(new System.Windows.Size(ContextText.ActualWidth,double.PositiveInfinity));
        bool longBlock=probe.DesiredSize.Height>39;
        CodeFold.Visibility=longBlock?Visibility.Visible:Visibility.Collapsed;
        ContextText.MaxHeight=longBlock&&!expanded?38:double.PositiveInfinity;
        CodeFold.Content=expanded?"收起代码":"展开代码";
    }
    public void SetBody(string text)
    {
        if (urgent) { StatusText.Text=text; StatusText.Visibility=Visibility.Visible; }
        else { Hud.ToolTip=text; HudOpen.ToolTip=text; }
    }
    public void UpdateNotice(Notice n) => choices?.UpdateNotice(n);
    public static double ScaleFor(Rectangle area)
    {
        var target = Screen.FromRectangle(area);
        var point = new Point(target.Bounds.Left+target.Bounds.Width/2, target.Bounds.Top+target.Bounds.Height/2);
        uint dpi = 96;
        if (GetDpiForMonitor(MonitorFromPoint(point,2),0,out uint dx,out _) == 0 && dx > 0) dpi=dx;
        return dpi/96.0;
    }
    public Size MeasureFace(Rectangle area, double scale)
    {
        workArea = area;
        MaxWidth = Math.Max(1,area.Width/scale);
        MaxHeight = Math.Clamp(area.Height/scale-55,1,580);

        double width=Math.Min(Width,MaxWidth);
        var root=(FrameworkElement)Content;
        root.Measure(new System.Windows.Size(width,MaxHeight));
        return new Size(Math.Max(1,(int)Math.Round((width-OuterMargin)*scale)),
            Math.Max(1,(int)Math.Round((Math.Min(root.DesiredSize.Height,MaxHeight)-OuterMargin)*scale)));
    }
    public Rectangle FaceBounds {
        get {
            if(Handle==IntPtr.Zero || !GetWindowRect(Handle,out var r))return Rectangle.Empty;
            int margin=(int)Math.Round(OuterMargin/2*VisualTreeHelper.GetDpi(this).DpiScaleX);
            return Rectangle.Inflate(Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom),-margin,-margin);
        }
    }
    public void Place(Rectangle face,double scale)
    {
        int m=(int)Math.Round(OuterMargin/2*scale);
        bool first=Handle==IntPtr.Zero;
        var hwnd=new WindowInteropHelper(this).EnsureHandle();
        positioning=true;
        try{
            if(!IsVisible || FaceBounds.Location!=face.Location)
                Native.SetWindowPos(hwnd,IntPtr.Zero,face.X-m,face.Y-m,face.Width+2*m,face.Height+2*m,0x14);
            if(first && urgent)ElementHost.EnableModelessKeyboardInterop(this);
            if(!IsVisible){Show();UpdateLayout();}
        }finally{positioning=false;}
        SyncGlass();
    }
    public void RaisePassive(){
        Native.SetWindowPos(Handle,(IntPtr)(-1),0,0,0,0,0x13);
        if(!UsesPixels || lensFailed)SyncGlass();
    }
    void OnUrgentKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Headline.IsKeyboardFocused || IsKeyboardFocused)
        {
            // A deliberate click on the title leaves Tab available without arming a button.
            if (e.Key is Keys.Enter or Keys.Space or Keys.Escape or Keys.Left or Keys.Right or Keys.Up or Keys.Down)
                e.Handled = true;
            return;
        }
        if (e.Key == Keys.Escape)
        {
            e.Handled = true;
            dismiss();
        }
        else if (e.Key == Keys.Enter && e.OriginalSource is System.Windows.Controls.RadioButton option)
        {
            e.Handled = true;
            option.IsChecked = true;
        }
    }
    public bool Centered(Rectangle a) => GetWindowRect(Handle,out var r) &&
        Math.Abs(r.Left+r.Right-a.Left-a.Right)<=4 && Math.Abs(r.Top+r.Bottom-a.Top-a.Bottom)<=4;
    public void Dispose()
    {
        if (closed) return;
        closed=true;EndDrag(Handle); choices?.Dispose();ReleaseGlass();Close();
    }
    [StructLayout(LayoutKind.Sequential)] struct WindowRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window,ref Point point);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetCapture();
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle,out WindowRect rect);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW")] static extern int SetWindowLong(IntPtr h,int index,int value);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point,uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
}
