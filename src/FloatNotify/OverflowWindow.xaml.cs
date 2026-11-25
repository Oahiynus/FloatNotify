using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using MenuItem = System.Windows.Controls.MenuItem;

namespace FloatNotify;

internal partial class OverflowWindow : Window
{
    internal sealed record Item(Resident.Entry Entry,string Kind,string Chat);
    Item[] items=[];
    readonly Action<Resident.Entry> select;
    public OverflowWindow(Action<Resident.Entry> select)
    {
        InitializeComponent();
        this.select=select;
        Picker.PlacementTarget=More;
        More.Click+=(_,_)=>Picker.IsOpen=true;
        Picker.Opened+=(_,_)=>BuildMenu();
        Closed+=(_,_)=>Picker.IsOpen=false;
    }
    void BuildMenu()
    {
        Picker.Items.Clear();
        foreach(var item in items){
            var header=new StackPanel();
            header.Children.Add(new TextBlock{Text=item.Kind,FontSize=11,Foreground=Card.Paint("#526879")});
            header.Children.Add(new TextBlock{Text=item.Chat,FontSize=14,MaxWidth=285,TextTrimming=TextTrimming.CharacterEllipsis});
            var row=new MenuItem{Header=header,ToolTip=item.Chat};
            AutomationProperties.SetName(row,item.Kind+" · "+item.Chat);
            row.Click+=(_,_)=>select(item.Entry);
            Picker.Items.Add(row);
        }
    }
    public void ExplainBlocked()
    {
        Dispatcher.InvokeAsync(()=>{
            if(!IsVisible)return;
            Picker.IsOpen=true;
            BuildMenu();
            Picker.Items.Insert(0,new MenuItem{IsEnabled=false,Header="请移开一张桌面卡片，腾出展开空间"});
        });
    }
    public void Update(Item[] next,Rectangle area,double scale,IReadOnlyList<CardPlacement.Block> blocks)
    {
        if(!items.SequenceEqual(next)){
            items=next;Caption.Text=$"还有 {items.Length} 条";
            AutomationProperties.SetName(More,Caption.Text+"提醒");
            if(Picker.IsOpen)BuildMenu();
        }
        int w=(int)Math.Round(Width*scale),h=(int)Math.Round(Height*scale);
        int m=(int)Math.Round(4*scale);
        var sites=new[]{new System.Drawing.Point(area.Left+(area.Width-w)/2,area.Bottom-h-m),
            new System.Drawing.Point(area.Right-w-m,area.Bottom-h-m),new System.Drawing.Point(area.Left+m,area.Bottom-h-m),
            new System.Drawing.Point(area.Right-w-m,area.Top+m),new System.Drawing.Point(area.Left+m,area.Top+m)};
        var spot=sites.OrderBy(p=>blocks.Count(b=>b.Face.IntersectsWith(new Rectangle(p,new System.Drawing.Size(w,h))))).First();
        Picker.MaxHeight=Math.Max(50,Math.Min(360,area.Height/scale-64));
        var hwnd=new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(hwnd,IntPtr.Zero,spot.X,spot.Y,w,h,0x14);
        if(!IsVisible)Show();
    }
}

