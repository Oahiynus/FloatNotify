using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace FloatNotify;

internal enum GlassMaterial { Liquid, LiquidSystem, Frosted, Adaptive, TonalLiquid }

internal static class GlassPreference
{
    static readonly string path=Path.Combine(Audit.Folder,"material.txt");
    public static GlassMaterial Read()
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path).Trim()) : GlassMaterial.Liquid; }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return GlassMaterial.Liquid;}
    }
    internal static GlassMaterial Parse(string value)=>value switch {
        "tonal-liquid"=>GlassMaterial.TonalLiquid,"adaptive"=>GlassMaterial.Adaptive,
        "frosted"=>GlassMaterial.Frosted,"liquid-system"=>GlassMaterial.LiquidSystem,
        _=>GlassMaterial.Liquid // Includes the retired "clear" preference.
    };
    public static void Write(GlassMaterial material)
    {
        Directory.CreateDirectory(Audit.Folder);
        File.WriteAllText(path+".tmp",material switch {GlassMaterial.Adaptive=>"adaptive",GlassMaterial.TonalLiquid=>"tonal-liquid",GlassMaterial.Frosted=>"frosted",GlassMaterial.LiquidSystem=>"liquid-system",_=>"liquid"});
        File.Move(path+".tmp",path,true);
    }
}

// The compositor paints a click-through, non-activating surface directly below the WPF controls.
internal sealed class GlassSurface : IDisposable
{
    IntPtr window,surface;
    (int Width,int Height,double Scale,GlassMaterial Material,bool Hud) last;
    bool failed;
    public bool RimActive {get;private set;}
    public bool Place(IntPtr card,FrameworkElement face,GlassMaterial material,bool hud)
    {
        if(failed || !face.IsVisible || face.ActualWidth<1 || face.ActualHeight<1)return false;
        var dpi=VisualTreeHelper.GetDpi(face);
        var pos=face.PointToScreen(new Point());
        var size=((int)Math.Round(face.ActualWidth*dpi.DpiScaleX),(int)Math.Round(face.ActualHeight*dpi.DpiScaleY),dpi.DpiScaleX,material,hud);
        try{
            if(window==IntPtr.Zero){
                window=CreateWindowEx(0x08000000|0x00200000|0x80|0x20|0x8,"STATIC","",0x80000000,
                    0,0,1,1,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
                if(window==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
                Marshal.ThrowExceptionForHR(GlassCreate(window,out surface));
            }
            if(last!=size){
                Marshal.ThrowExceptionForHR(GlassDraw(surface,size.Item1,size.Item2,(float)dpi.DpiScaleX,hud?29:30,material==GlassMaterial.Frosted));
                last=size;
            }
            int x=(int)Math.Round(pos.X), y=(int)Math.Round(pos.Y);
            bool placed=Native.SetWindowPos(window,card,x,y,size.Item1,size.Item2,0x50);
            if(placed && material!=GlassMaterial.Frosted)
                RimActive=GlassPlaceRim(surface,card,x,y,size.Item1,size.Item2,(float)dpi.DpiScaleX,hud?29:30);
            else RimActive=false;
            return placed;
        }
        catch(Exception e) when(e is COMException or ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotImplementedException or UnauthorizedAccessException or NotSupportedException){
            Dispose();failed=true;Audit.Write($"glass-fallback hr=0x{e.HResult:X8}");return false;
        }
    }
    public void Dispose(){
        last=default;RimActive=false;failed=false;
        if(surface!=IntPtr.Zero){GlassDestroy(surface);surface=IntPtr.Zero;}
        if(window!=IntPtr.Zero){DestroyWindow(window);window=IntPtr.Zero;}
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateWindowExW")]
    static extern IntPtr CreateWindowEx(uint ex,string cls,string caption,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr data);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("GlassBackdrop.dll")] static extern int GlassCreate(IntPtr window,out IntPtr surface);
    [DllImport("GlassBackdrop.dll")] static extern int GlassDraw(IntPtr surface,int width,int height,float scale,float radius,bool frosted);
    [DllImport("GlassBackdrop.dll")] static extern bool GlassPlaceRim(IntPtr surface,IntPtr card,int x,int y,int width,int height,float scale,float radius);
    [DllImport("GlassBackdrop.dll")] static extern void GlassDestroy(IntPtr surface);
}
