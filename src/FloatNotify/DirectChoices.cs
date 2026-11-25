using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBlock = System.Windows.Controls.TextBlock;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace FloatNotify;

internal sealed class DirectChoices : IDisposable
{
    readonly Card card;
    readonly string chat;
    readonly Func<bool> canBind;
    readonly bool preview;
    readonly DispatcherTimer retry = new(){Interval=TimeSpan.FromMilliseconds(500)};
    Notice notice;
    FormBridge.View? view;
    bool reading,sending,disposed,rendering;
    bool questionUnconfirmed,openOnSend;
    string? selected;
    FormBridge.MenuSession? menu;
    FormBridge.View? menuView;
    int generation;
    public DirectChoices(Card card,Notice n,string chat,Func<bool> canBind)
    {
        this.card=card; notice=n; this.chat=chat; this.canBind=canBind;
        preview=n.Source=="preview";
        if(preview)
        {
            var example=new Prompt("审阅结果按什么顺序展示？",["按优先级排列","按文件顺序排列"]);
            view=n.Kind==Kind.Question
                ? new("preview",example.Title,example,[],true,0)
                : new("preview","允许列出工作区里的文件吗？\nPowerShell\nGet-ChildItem -Name\n工作目录   示例项目",null,
                    [new("审批选项","more",true,true),new("拒绝","deny",true,false),new("允许一次","allow",true,false)],false,0);
        }
        retry.Tick+=(_,_)=>RefreshOriginal();
        card.Send.Click+=(_,_)=>Commit(selected??card.Editor.Text,selected==null,openQuestion:openOnSend);
        card.Editor.TextChanged+=(_,_)=>{
            if(rendering)return;
            if(card.Editor.Text.Length>0)ClearSelectedOption();
            UpdateEditor(); EnableSend();
        };
        card.Editor.PreviewMouseDown+=(_,_)=>{
            if(selected==null)return;
            ClearSelectedOption();
            EnableSend();
        };
        card.Editor.GotKeyboardFocus+=(_,_)=>card.EditorFrame.BorderBrush=Card.Paint("#4780C1");
        card.Editor.LostKeyboardFocus+=(_,_)=>card.EditorFrame.BorderBrush=(System.Windows.Media.Brush)card.FindResource("LensEdge");
        Render();
    }
    public bool Busy => sending;
    public void PauseReading(){retry.Stop();}
    public void StartReading()
    {
        if(retry.IsEnabled||preview||disposed||notice.Approval!=null)return;
        retry.Start(); RefreshOriginal();
    }
    public void UpdateNotice(Notice updated)
    {
        bool sameQuestion=updated.Prompts is [var prompt] && prompt.Title==card.Headline.Text;
        generation++; notice=updated; view=null;
        if(!sameQuestion){questionUnconfirmed=false;selected=null;card.Editor.Clear();}
        Render(); if(retry.IsEnabled)RefreshOriginal();
    }
    public void Dispose(){disposed=true;retry.Stop();}
    void Info(string text)
    {
        card.StatusText.Text=text;
        card.StatusText.Visibility=text.Length==0?Visibility.Collapsed:Visibility.Visible;
    }
    void ClearSelectedOption()
    {
        if(selected==null)return;
        selected=null;
        foreach(var option in card.Options.Children.OfType<RadioButton>())option.IsChecked=false;
    }
    void UpdateEditor() => card.Placeholder.Visibility=card.Editor.Text.Length==0?Visibility.Visible:Visibility.Hidden;
    void EnableSend(bool? identified=null)
    {
        if(notice.Kind!=Kind.Question)return;
        bool draft=selected!=null || card.EditorBox.Visibility==Visibility.Visible && !string.IsNullOrWhiteSpace(card.Editor.Text);
        card.Send.IsEnabled=!sending && draft && (preview || (identified ?? canBind()));
        // Keep the progress label while sending, and the advertised action throughout a press.
        if(sending || card.Send.IsPressed)return;
        openOnSend=card.Send.IsEnabled && !preview && (view==null || questionUnconfirmed);
        card.SendLabel.Text=openOnSend?"打开原题并发送":"发送";
    }
    void EnableInput(bool enabled)
    {
        card.Options.IsEnabled=card.Editor.IsEnabled=card.ApprovalStrip.IsEnabled=enabled;
        EnableSend();
    }
    static TextBlock Wrap(string text) => new(){Text=text,TextWrapping=TextWrapping.Wrap};
    void Render()
    {
        rendering=true;
        try
        {
            if(notice.Kind==Kind.Permission)
            {
                RenderApproval();
                RenderMenu();
            }
            else
            {
                RenderQuestion();
                UpdateEditor();
                EnableSend();
            }
            if(!questionUnconfirmed)Info(view==null && notice.Approval==null?"正在核对 ChatGPT 中的原请求。":"");
        }
        finally{rendering=false;}
    }
    void RenderApproval()
    {
        card.MoreActions.Children.Clear();
        card.AllowActions.Children.Clear();
        card.Decisions.Children.Clear();
        card.ApprovalStrip.Visibility=card.ContextBox.Visibility=Visibility.Collapsed;
        FormBridge.ActionItem[] actions;
        if(notice.Approval is {} official){
            card.ShowOfficialApproval(official.Details);
            actions=[new("拒绝","deny",true,false),new("允许一次","allow",true,false)];
        }else{
            card.Headline.Text="请确认这项权限申请";
            string details=(view?.Context??notice.Body).Trim();
            int newline=details.IndexOf('\n');
            string first=(newline<0?details:details[..newline]).Trim();
            if(first.Length is >0 and <=100 && (first.EndsWith('?')||first.EndsWith('？'))){
                card.Headline.Text=first;
                details=newline<0?"":details[(newline+1)..].Trim();
            }
            card.ShowContext(details);
            if(view==null)return;
            actions=view.Actions;
        }
        card.ApprovalStrip.Visibility=Visibility.Visible;
        foreach(var action in actions)
        {
            bool more=action.Menu;
            bool primary=FormBridge.IsAllowLabel(action.Label) && !more;
            var button=new Button {
                Content=more ? new System.Windows.Shapes.Path{Data=System.Windows.Media.Geometry.Parse("M1,3 L5,7 L9,3"),Width=10,Height=10,Stroke=Card.Paint("#415461"),StrokeThickness=1.4} : Wrap(action.Label),
                IsEnabled=action.Enabled,Style=(Style)card.FindResource(primary||more?"SplitPart":"Action"),
                ToolTip=action.Label,Padding=new Thickness(more?7:8,0,more?7:8,0)
            };
            AutomationProperties.SetName(button,action.Label);
            button.Click+=(_,_)=>{if(more)ToggleMenu(action.Key); else Commit(action.Key,false);};
            var group=more ? card.MoreActions : primary ? card.AllowActions : card.Decisions;
            group.Children.Add(button);
        }
        bool hasAllow=card.AllowActions.Children.Count>0, hasMenu=card.MoreActions.Children.Count>0;
        card.ApprovalGroup.Visibility=hasAllow||hasMenu?Visibility.Visible:Visibility.Collapsed;
        card.MenuDivider.Visibility=hasAllow&&hasMenu?Visibility.Visible:Visibility.Collapsed;
    }
    void RenderMenu()
    {
        card.MenuItems.Children.Clear();
        card.MenuBox.Visibility=menu==null?Visibility.Collapsed:Visibility.Visible;
        if(menu==null)return;
        var source=menuView;
        if(source==null)
        {
            var hint=Wrap("正在等待 ChatGPT 的审批选项。也可打开 ChatGPT 查看。");
            hint.Margin=new Thickness(7,4,7,4);
            card.MenuItems.Children.Add(hint);
            return;
        }
        foreach(var action in source.Actions)
        {
            var button=new Button {Content=Wrap(action.Label),Style=(Style)card.FindResource("Action"),
                HorizontalContentAlignment=HorizontalAlignment.Left,IsEnabled=action.Enabled,Margin=new Thickness(1,2,1,2)};
            AutomationProperties.SetName(button,action.Label);
            button.Click+=(_,_)=>Commit(action.Key,false,source);card.MenuItems.Children.Add(button);
        }
    }
    async void ToggleMenu(string key)
    {
        if(preview){Info("这是本机预览。");return;}
        if(sending || disposed || view==null || !canBind())return;
        int version=++generation;
        var original=view;var request=notice;
        sending=true;EnableInput(false);
        try
        {
            if(menu!=null)
            {
                var active=menu;
                menu=null;menuView=null;RenderMenu();
                await Task.Run(()=>FormBridge.CollapseMenu(request,chat,active));
                return;
            }
            Info("正在展开审批选项…");
            var opened=await Task.Run(()=>FormBridge.OpenMenu(request,chat,original,key));
            if(disposed || generation!=version || !canBind())return;
            menu=opened.Session;menuView=opened.Choices;
            Info(menu==null?"原菜单暂不可用，可以打开 ChatGPT 查看。":"");
            RenderMenu();
            if(menu!=null)Audit.Write($"permission-menu-requested request={request.Id}");
            if(menuView!=null)Audit.Write($"permission-menu-items-loaded request={request.Id} items={menuView.Actions.Length}");
        }
        finally
        {
            if(!disposed){sending=false;EnableInput(true);retry.Interval=TimeSpan.FromMilliseconds(350);RefreshOriginal();}
        }
    }
    void RenderQuestion()
    {
        card.Options.Children.Clear();
        card.Send.Visibility=card.EditorBox.Visibility=Visibility.Collapsed;
        var prompts=notice.Prompts??[];
        Prompt? question=view?.Question ?? (prompts.Length==1?prompts[0]:null);
        card.Headline.Text=question?.Title ?? (notice.Body.Length>0?notice.Body:"Codex 有一个问题需要你回答");
        if(question==null)
        {
            foreach(var p in prompts)
            {
                if(p.Title!=card.Headline.Text)card.Options.Children.Add(Wrap(p.Title));
                foreach(string label in p.Options)card.Options.Children.Add(Wrap("• "+label));
            }
            return;
        }
        if(selected!=null && !question.Options.Contains(selected))selected=null;
        foreach(string label in question.Options)
        {
            var radio=new RadioButton{Content=Wrap(label),Style=(Style)card.FindResource("Option"),IsChecked=label==selected};
            AutomationProperties.SetName(radio,label);
            radio.Checked+=(_,_)=>{selected=label; card.Editor.Clear(); EnableSend();};
            card.Options.Children.Add(radio);
        }
        if(view?.Editable==true || view==null)
        {
            card.EditorBox.Visibility=Visibility.Visible;
            card.Placeholder.Text=question.Options.Length>0?"或输入自己的回复":"填写回答";
            AutomationProperties.SetName(card.Editor,card.Placeholder.Text);
        }
        card.Send.Visibility=Visibility.Visible;
    }
    static bool Equivalent(FormBridge.View? a,FormBridge.View? b)=>a==null ? b==null :
        b!=null && a.MatchesForm(b) && a.Editable==b.Editable && a.Actions.SequenceEqual(b.Actions);
    async void RefreshOriginal()
    {
        if(disposed||reading||sending||!card.IsVisible)return;
        bool identified=canBind();
        EnableSend(identified);
        if(!identified)
        {
            if(view!=null){view=null;Render();}
            Info("无法唯一定位待处理请求。可打开 ChatGPT 查看。");return;
        }
        int version=generation;var request=notice;reading=true;
        try
        {
            var activeMenu=menu;
            var result=await Task.Run(()=>FormBridge.Read(request,chat));
            if(disposed||version!=generation||sending)return;
            if(!canBind())result=null;
            if(!Equivalent(view,result))
            {
                if(result?.Question is {} next && next.Title!=card.Headline.Text){questionUnconfirmed=false;selected=null;card.Editor.Clear();}
                view=result;Render();
                if(result!=null && request.Kind==Kind.Permission)
                    Audit.Write($"permission-options-loaded request={request.Id} buttons={result.Actions.Length}");
            }
            retry.Interval=TimeSpan.FromMilliseconds(result==null?600:2500);
            if(activeMenu!=null && result!=null && menu==activeMenu)
            {
                var child=await Task.Run(()=>FormBridge.Read(request,chat,activeMenu));
                if(disposed || version!=generation || sending || menu!=activeMenu || !canBind())return;
                if(child!=null && !Equivalent(menuView,child))
                {
                    menuView=child;RenderMenu();
                    Audit.Write($"permission-menu-items-loaded request={request.Id} items={child.Actions.Length}");
                }
                retry.Interval=TimeSpan.FromMilliseconds(child==null?500:2000);
            }
            if(result==null && menu!=null)
            {
                menu=null;menuView=null;RenderMenu();
            }
        }
        finally{reading=false;}
    }
    async Task<FormBridge.View?> OpenQuestion(Notice request,int version)
    {
        Program.OpenThread(request.Chat);
        var lookup=System.Diagnostics.Stopwatch.StartNew();
        var limit=TimeSpan.FromSeconds(6);
        while(!disposed && generation==version && canBind())
        {
            var remaining=limit-lookup.Elapsed;
            if(remaining<=TimeSpan.Zero)break;
            try
            {
                var form=await Task.Run(()=>FormBridge.Read(request,chat)).WaitAsync(remaining);
                if(form!=null)return form;
            }
            catch(TimeoutException){break;}
            await Task.Delay(200);
        }
        return null;
    }
    async void Commit(string value,bool custom,FormBridge.View? menuSource=null,bool openQuestion=false)
    {
        if(sending||disposed)return;
        if(preview){Info("这是本机预览。");return;}
        if(notice.Approval is {} official)
        {
            if(value is "allow" or "deny" && official.Respond(value)){
                sending=true; EnableInput(false); Info("正在把决定交回 Codex…");
            }
            return;
        }
        if(!canBind()){Info("无法唯一定位这条请求。请打开 ChatGPT 查看。");return;}
        sending=true;int version=++generation;EnableInput(false);
        var current=menuSource??view;var request=notice;
        string error;
        bool question=request.Kind==Kind.Question;
        if(question)
        {
            card.SendLabel.Text="发送中…";
            Info("正在把答案提交给 ChatGPT…");
        }
        if(openQuestion && question)
        {
            Info("正在打开原题并核对答案…");
            try { current=await OpenQuestion(request,version); }
            catch(System.ComponentModel.Win32Exception){Info("未能打开原题。你的答案已保留。");sending=false;EnableInput(true);return;}
        }
        if(disposed)return;
        if(current==null || version!=generation || !canBind())error=request.Kind==Kind.Question
            ? "原题尚未就绪。答案已保留，可点「打开原题并发送」。" : "原审批还未就绪。可点「打开 ChatGPT 查看」。";
        else if(menuSource!=null && view is {} parent)
        {
            error=await Task.Run(()=>FormBridge.SubmitMenu(request,chat,parent,menuSource,value));
        }
        else error=await Task.Run(()=>FormBridge.Submit(request,chat,current,value,custom));
        if(error=="")Audit.Write($"original-control-invoked kind={request.Kind.ToString().ToLowerInvariant()} request={request.Id} menu={menuSource!=null}");
        if(disposed)return;
        if(error=="" && question)
        {
            // The accessibility provider accepts the action before the app confirms delivery.
            // The matching session reply closes the card. Keep its draft while waiting.
            card.SendLabel.Text="等待确认…";
            Info("已操作原题，正在等待 ChatGPT 确认。");
            await Task.Delay(request.Prompts is {Length:>1} ? 500 : 5000);
            if(disposed)return;
            if(request.Prompts is not {Length:>1})
            {
                error="还未收到 ChatGPT 的回执。答案已保留，点「打开原题并发送」可到原题重试。";
                Audit.Write($"question-awaiting-receipt request={request.Id}");
            }
        }
        else if(error=="")
        {
            // Some original buttons reveal details without deciding the request.
            Info("已操作原控件，等待 ChatGPT 更新。");
            await Task.Delay(900);
            if(disposed)return;
        }
        if(question && error!="")questionUnconfirmed=true;
        sending=false;EnableInput(true);
        if(error!=""){Info(error);return;}
        RefreshOriginal();
    }
}
