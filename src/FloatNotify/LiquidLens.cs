using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using Point = System.Windows.Point;
using PixelFormat = System.Windows.Media.PixelFormats;

namespace FloatNotify;

// Only an on-screen liquid card owns this surface. WPF still owns the controls and input.
internal sealed class LiquidLens : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Zone(float Left,float Top,float Right,float Bottom,int Kind);

    IntPtr lens;
    WriteableBitmap? image,inkMask;
    public ImageBrush? InkMask { get; private set; }
    Zone[] zones=[];
    bool layoutDirty=true;
    public ImageBrush? Brush { get; private set; }
    public void InvalidateLayout() => layoutDirty=true;

    public int Draw(Card card, bool hud, GlassMaterial material)
    {
        var face=card.GlassShell;
        var dpi=VisualTreeHelper.GetDpi(face);
        int width=(int)Math.Round(face.ActualWidth*dpi.DpiScaleX),height=(int)Math.Round(face.ActualHeight*dpi.DpiScaleY);
        if(lens==IntPtr.Zero){
            try{lens=LiquidCreate(card.Handle,width,height);}
            catch(Exception e) when(e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException){return -1;}
            if(lens==IntPtr.Zero)return -1;
        }
        if(layoutDirty){
            var shapes=new List<Zone>();
            Collect(hud?card.Hud:card.DialogFace,face,new Rect(0,0,face.ActualWidth,face.ActualHeight),shapes,card,material,false);
            zones=shapes.ToArray();layoutDirty=false;
        }
        var origin=face.PointToScreen(new Point());
        int changed=LiquidFrame(lens,(int)Math.Round(origin.X),(int)Math.Round(origin.Y),width,height,(float)dpi.DpiScaleX,
            (hud?1:0) | (material switch {GlassMaterial.Adaptive=>4,GlassMaterial.TonalLiquid=>8,GlassMaterial.Lustre=>16,GlassMaterial.TextRelief=>32,GlassMaterial.SoftFocus=>64,_=>0}),zones,zones.Length,out var pixels,out int w,out int h);
        if(changed<=0)return changed;
        if(image==null || image.PixelWidth!=w || image.PixelHeight!=h){
            image=new WriteableBitmap(w,h,96,96,PixelFormat.Bgra32,null);
            Brush=new ImageBrush(image){Stretch=Stretch.Fill};
        }
        image.WritePixels(new Int32Rect(0,0,w,h),pixels,checked(w*h*4),w*4);
        if(material is GlassMaterial.Adaptive or GlassMaterial.TonalLiquid or GlassMaterial.Lustre or GlassMaterial.TextRelief or GlassMaterial.SoftFocus){
            var mask=LiquidInkMask(lens,out int mw,out int mh);
            if(mask==IntPtr.Zero){InkMask=null;inkMask=null;return 1;}
            var format=material==GlassMaterial.SoftFocus?PixelFormat.Bgra32:PixelFormat.Gray8;
            int stride=mw*(format.BitsPerPixel/8);
            if(inkMask==null || inkMask.PixelWidth!=mw || inkMask.PixelHeight!=mh || inkMask.Format!=format){
                inkMask=new WriteableBitmap(mw,mh,96,96,format,null);
                InkMask=new ImageBrush(inkMask){Stretch=Stretch.Fill};
            }
            inkMask.WritePixels(new Int32Rect(0,0,mw,mh),mask,checked(stride*mh),stride);
        }
        return 1;
    }
    static void Collect(DependencyObject node,FrameworkElement face,Rect clip,List<Zone> zones,Card card,GlassMaterial material,bool option)
    {
        if(node is FrameworkElement element){
            if(!element.IsVisible || element.ActualWidth<=0 || element.ActualHeight<=0)return;
            var pos=element.TranslatePoint(new Point(),face);
            var box=new Rect(pos.X,pos.Y,element.ActualWidth,element.ActualHeight);
            if(!box.IntersectsWith(clip))return;
            void Add(Rect area,int kind){
                area.Intersect(clip);
                if(!area.IsEmpty && area.Width>0 && area.Height>0)zones.Add(new((float)area.Left,(float)area.Top,(float)area.Right,(float)area.Bottom,kind));
            }
            if(element is ScrollContentPresenter)clip.Intersect(box);
            if(element is RadioButton radio)Add(box,material==GlassMaterial.SoftFocus && radio.IsChecked==true?4:1);
            else if(element==card.EditorFrame)Add(box,1);
            if(element==card.ApprovalGroup || element is Button button && button.BorderThickness.Left>0)Add(box,2);
            if(element==card.Editor){
                box.Inflate(-12,-9);
                if(!box.IsEmpty)Add(box,0);
            }
            if(element is TextBlock text && text.Text.Length>0){
                var end=text.ContentEnd.GetCharacterRect(System.Windows.Documents.LogicalDirection.Backward);
                var start=text.ContentStart.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward);
                if((material is GlassMaterial.TextRelief or GlassMaterial.SoftFocus) && (option || text==card.Headline || text==card.DoneText)){
                    // Wrapped option labels can leave most of their last line empty.
                    var line=text.ContentStart;
                    while(line!=null && line.CompareTo(text.ContentEnd)<0){
                        var next=line.GetLineStartPosition(1);
                        var first=line.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward);
                        var last=(next??text.ContentEnd).GetCharacterRect(System.Windows.Documents.LogicalDirection.Backward);
                        if(!first.IsEmpty && !last.IsEmpty){
                            var span=Rect.Union(first,last);
                            span.Offset(pos.X,pos.Y);
                            span.Intersect(box);
                            Add(span,3);
                        }
                        if(next==null || next.CompareTo(line)<=0)break;
                        line=next;
                    }
                }
                if(!start.IsEmpty && !end.IsEmpty && Math.Abs(end.Top-start.Top)<1)
                    box=new Rect(pos.X+Math.Min(end.X,start.X),pos.Y+start.Y,Math.Abs(end.X-start.X)+2,Math.Max(start.Height,end.Height));
                box.Inflate(3,3);Add(box,0);
            }
        }
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);++i)Collect(VisualTreeHelper.GetChild(node,i),face,clip,zones,card,material,option || node is RadioButton);
    }
    public void PauseCapture(){
        if(lens!=IntPtr.Zero){LiquidDestroy(lens);lens=IntPtr.Zero;}
    }
    public void Dispose(){
        PauseCapture();
        Brush=null;image=null;InkMask=null;inkMask=null;zones=[];layoutDirty=true;
    }
    [DllImport("GlassBackdrop.dll")] static extern IntPtr LiquidCreate(IntPtr card,int width,int height);
    [DllImport("GlassBackdrop.dll")] static extern int LiquidFrame(IntPtr lens,int x,int y,int width,int height,float dpi,int flags,
        [In] Zone[] zones,int count,out IntPtr pixels,out int imageWidth,out int imageHeight);
    [DllImport("GlassBackdrop.dll")] static extern IntPtr LiquidInkMask(IntPtr lens,out int width,out int height);
    [DllImport("GlassBackdrop.dll")] static extern void LiquidDestroy(IntPtr lens);
}
