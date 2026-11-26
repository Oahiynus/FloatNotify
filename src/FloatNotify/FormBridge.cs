using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace FloatNotify;

// Only controls from a live, identified ChatGPT request form are actionable.
// Runtime IDs belong to the current UI. Never replay a label against another form.
internal static class FormBridge
{
    internal sealed record ActionItem(string Label, string Key, bool Enabled, bool Menu);
    internal sealed record MenuSession(string Owner,string Document,string Form,HashSet<string> Before)
    {
        public string? OpenedMenu {get;set;}
        public string LastState="";
        public void Observe(string state)
        {
            if(state==LastState)return;
            LastState=state;Audit.Write("permission-menu-state "+state);
        }
    }
    internal sealed record View(string FormKey, string Context, Prompt? Question,
        ActionItem[] Actions, bool Editable, int ProcessId, MenuSession? Menu=null)
    {
        public bool MatchesForm(View other)=>ProcessId==other.ProcessId && FormKey==other.FormKey &&
            Context==other.Context && Question==other.Question && Menu==other.Menu;
    }
    sealed record Located(AutomationElement Document, View View, Dictionary<string,AutomationElement> Buttons,
        AutomationElement? Editor, AutomationElement? Submit, AutomationElement[] Nodes);
    const TreeScope Deep=TreeScope.Descendants;
    static readonly Condition All=Condition.TrueCondition;
    static readonly Condition Documents=new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Document);
    static readonly Condition TextType=new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text);
    static readonly HashSet<string> SendNames=new(StringComparer.OrdinalIgnoreCase){"发送","提交","下一步","下一题","确认答案","Send","Submit","Next","Continue"};
    static bool IsType(AutomationElement e,ControlType type)=>e.Current.ControlType==type;
    static string Id(AutomationElement e)=>string.Join(".",e.GetRuntimeId());
    static bool Has(AutomationElement e,AutomationPattern pattern)=>e.TryGetCurrentPattern(pattern,out _);
    static T? Pattern<T>(AutomationElement e,AutomationPattern pattern) where T:class =>
        e.TryGetCurrentPattern(pattern,out var p)?p as T:null;
    static bool ExpectedError(Exception e)=>e is ElementNotAvailableException or InvalidOperationException or UnauthorizedAccessException or COMException or ArgumentException or TimeoutException or System.ComponentModel.Win32Exception;
    static string Normal(string s)=>System.Text.RegularExpressions.Regex.Replace(s,@"\s+"," ").Trim();
    static bool Eq(string a,string b)=>Normal(a)==Normal(b);
    static bool MenuButton(AutomationElement e)
    {
        if(Has(e,ExpandCollapsePattern.Pattern))return true;
        var property=AutomationProperty.LookupById(30102); // UIA_AriaPropertiesPropertyId
        return property!=null && e.GetCurrentPropertyValue(property) is string s &&
            (s.Contains("haspopup=menu",StringComparison.OrdinalIgnoreCase)||s.Contains("haspopup=true",StringComparison.OrdinalIgnoreCase));
    }
    internal sealed record QuestionScope(DesktopChatRoutes Routes, bool UniqueTitle)
    {
        public bool Matches(Guid chat, Guid? route, string document, string title)
        {
            if (route is {} id)
                return id == chat && (document == title || document is "ChatGPT" or "Codex");
            return UniqueTitle && document == title;
        }
    }
    static IEnumerable<AutomationElement> DocumentsNamed(string title,bool includeActivity,Guid chat=default,QuestionScope? questionScope=null)
    {
        var windows=new List<IntPtr>();
        Native.EnumWindows((h,_)=>{if(Native.IsCodexWindow(h))windows.Add(h);return true;},IntPtr.Zero);
        foreach(var h in windows)
        {
            AutomationElement[] docs;
            try{docs=AutomationElement.FromHandle(h).FindAll(Deep,Documents).Cast<AutomationElement>().ToArray();}
            catch(Exception e) when(ExpectedError(e)){continue;}
            Guid? route=questionScope==null?null:Native.ForegroundRoute(h,questionScope.Routes);
            foreach(var doc in docs)
            {
                string name;
                try{name=doc.Current.Name;}catch(Exception e) when(ExpectedError(e)){continue;}
                bool matches=questionScope!=null ? questionScope.Matches(chat,route,name,title)
                    : name==title || includeActivity && name is "ChatGPT" or "Codex";
                if(matches)yield return doc;
            }
        }
    }
    static readonly string[] DenyLabels=["拒绝","不允许","Deny","Decline","Don't allow","Don’t allow"];
    static readonly string[] AllowLabels=["允许一次","允许本次","仅允许一次","本次允许","同意一次","Allow once","Allow for this request","允许","Allow","Approve"];
    static readonly string[] MoreLabels=["审批选项","批准选项","更多审批选项","Approval options","More approval options"];
    internal static bool IsAllowLabel(string label)=>AllowLabels.Contains(label.Trim(),StringComparer.OrdinalIgnoreCase);
    static bool LabelIn(AutomationElement e,string[] labels)=>labels.Contains(e.Current.Name.Trim(),StringComparer.OrdinalIgnoreCase);
    static bool DecisionButton(AutomationElement e)=>LabelIn(e,DenyLabels) || LabelIn(e,AllowLabels) || LabelIn(e,MoreLabels);
    static readonly Condition ButtonType=new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button);
    static readonly Condition DenyButton=new AndCondition(ButtonType,new OrCondition(DenyLabels.Select(label=>(Condition)
        new PropertyCondition(AutomationElement.NameProperty,label,PropertyConditionFlags.IgnoreCase)).ToArray()));
    static List<AutomationElement> QuestionForms(AutomationElement doc,Prompt[] prompts)
    {
        var forms=new List<AutomationElement>();
        foreach(var prompt in prompts)
        {
            var heading=new AndCondition(TextType,new PropertyCondition(AutomationElement.NameProperty,prompt.Title));
            foreach(AutomationElement text in doc.FindAll(Deep,heading))
            {
                // The same title occurs in the transcript. Only its local, live
                // request card owns the answer field, skip/dismiss and submit.
                for(var parent=TreeWalker.ControlViewWalker.GetParent(text);parent!=null;parent=TreeWalker.ControlViewWalker.GetParent(parent))
                {
                    var info=parent.Current;
                    if(info.ControlType==ControlType.Document || info.Name is "对话" or "Conversation" ||
                        info.ClassName.Contains("thread-scroll-container"))break;
                    var buttons=parent.FindAll(Deep,ButtonType).Cast<AutomationElement>().ToArray();
                    if(buttons.Count(b=>SendNames.Contains(b.Current.Name.Trim()))!=1)continue;
                    if(!buttons.Any(b=>b.Current.Name.Trim() is "忽略" or "跳过" or "Skip" or "Dismiss") &&
                        !Normal(info.Name).Contains(Normal(prompt.Title),StringComparison.Ordinal))continue;
                    forms.Add(parent);break;
                }
            }
        }
        return forms.DistinctBy(Id).ToList();
    }
    static List<AutomationElement> PermissionForms(AutomationElement doc,string title)
    {
        bool activity=doc.Current.Name!=title;
        var result=new List<AutomationElement>();
        var denies=doc.FindAll(Deep,DenyButton).Cast<AutomationElement>().Where(b=>Has(b,InvokePattern.Pattern)).ToArray();
        foreach(var deny in denies)
        {
            var parent=deny;
            for(int depth=0;depth<13;depth++)
            {
                parent=TreeWalker.RawViewWalker.GetParent(parent);
                if(parent==null || IsType(parent,ControlType.Document) || IsType(parent,ControlType.List))break;
                var info=parent.Current;
                if(info.Name is "对话" or "Conversation" || info.ClassName.Contains("thread-scroll-container"))break;
                var nodes=parent.FindAll(Deep,All).Cast<AutomationElement>().ToArray();
                if(nodes.Length>180)break;
                var buttons=nodes.Where(e=>IsType(e,ControlType.Button)).ToArray();
                if(buttons.Count(b=>LabelIn(b,DenyLabels))!=1 || !buttons.Any(b=>LabelIn(b,AllowLabels)))continue;
                if(activity && !(nodes.Any(e=>IsType(e,ControlType.Text)&&e.Current.Name==title) ||
                    buttons.Any(e=>e.Current.Name.StartsWith(title+". ",StringComparison.Ordinal) &&
                        (e.Current.Name.EndsWith("打开通知") || e.Current.Name.EndsWith("Open notification",StringComparison.OrdinalIgnoreCase)))))continue;
                var labels=buttons.Select(b=>b.Current.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool hasDetails=nodes.Any(e=>(IsType(e,ControlType.Text)||IsType(e,ControlType.Edit)) &&
                    e.Current.Name.Trim() is {Length:>=6} text && text!=title && !labels.Contains(text));
                if(!hasDetails)continue;
                result.Add(parent);break;
            }
        }
        return result.DistinctBy(Id).ToList();
    }
    static readonly Condition MenuType=new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Menu);
    static AutomationElement[] VisibleMenus(AutomationElement doc)=>doc.FindAll(Deep,MenuType).Cast<AutomationElement>()
        .Where(m=>!m.Current.IsOffscreen).ToArray();
    static AutomationElement[] MenuItems(AutomationElement doc,AutomationElement owner,MenuSession session,string[] allowLabels)
    {
        var visible=VisibleMenus(doc);
        var expanded=Pattern<ExpandCollapsePattern>(owner,ExpandCollapsePattern.Pattern);
        if(expanded?.Current.ExpandCollapseState==ExpandCollapseState.Collapsed){session.Observe("opener-collapsed");return [];}
        var property=AutomationProperty.LookupById(30104);
        System.Collections.IEnumerable? linked=null;
        try{if(property!=null)linked=owner.GetCurrentPropertyValue(property,true) as System.Collections.IEnumerable;}
        catch(ArgumentException){} // Provider does not implement ControllerFor.
        var ids=(linked?.OfType<AutomationElement>()??[]).Select(Id).ToHashSet();
        foreach(var child in owner.FindAll(Deep,MenuType).Cast<AutomationElement>())ids.Add(Id(child));
        foreach(var menu in visible)
            if(menu.Current.LabeledBy is {} label && Id(label)==session.Owner)ids.Add(Id(menu));
        var candidates=visible.Where(m=>ids.Contains(Id(m))).ToArray();
        if(candidates.Length==0)
        {
            // Chromium can expose a portal as a sibling of the form without a
            // ControllerFor relation. Only accept the newly opened menu of this
            // exact expanded opener, in the same document, with its original action.
            if(expanded?.Current.ExpandCollapseState!=ExpandCollapseState.Expanded)return [];
            candidates=visible.Where(m=>!session.Before.Contains(Id(m))).ToArray();
        }
        if(candidates.Length!=1){session.Observe($"candidate-count={candidates.Length}");return [];}
        var candidate=candidates[0];
        if(session.OpenedMenu!=null && Id(candidate)!=session.OpenedMenu)return [];
        var items=candidate.FindAll(Deep,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem))
            .Cast<AutomationElement>().Where(e=>!e.Current.IsOffscreen).ToArray();
        if(items.Length==0 || items.Any(e=>string.IsNullOrWhiteSpace(e.Current.Name) || !Has(e,InvokePattern.Pattern)))
        {session.Observe($"unsupported-items count={items.Length}");return [];}
        if(!ids.Contains(Id(candidate)) && !items.Any(e=>allowLabels.Contains(e.Current.Name.Trim(),StringComparer.OrdinalIgnoreCase)))
        {session.Observe("original-action-unmatched");return [];}
        session.OpenedMenu=Id(candidate);
        return items;
    }
    static Located? Find(Notice request,string title,MenuSession? menu=null,QuestionScope? questionScope=null)
    {
        if(request.Chat==Guid.Empty || title.Length==0)return null;
        bool approval=request.Kind==Kind.Permission;
        var prompts=request.Prompts??[];
        // Prefer the conversation over its activity window. Require a unique match at either priority.
        var documents=DocumentsNamed(title,approval,request.Chat,questionScope).GroupBy(doc=>doc.Current.Name==title).OrderByDescending(group=>group.Key);
        foreach(var group in documents)
        {
            Located? match=null;
            foreach(var doc in group)
            {
                if(menu!=null && Id(doc)!=menu.Document)continue;
                var forms=approval?PermissionForms(doc,title):QuestionForms(doc,prompts);
                foreach(var form in forms)
                {
                    string key=Id(form);
                    if(menu!=null && key!=menu.Form)continue;
                    var nodes=form.FindAll(Deep,All).Cast<AutomationElement>().ToArray();
                    var buttons=nodes.Where(e=>IsType(e,ControlType.Button) && !string.IsNullOrWhiteSpace(e.Current.Name)).ToArray();
                    var found=approval ? ReadApproval(request,doc,key,nodes,buttons,menu)
                        : ReadQuestion(prompts,doc,key,nodes,buttons);
                    if(found==null)continue;
                    if(match!=null)return null;
                    match=found;
                }
            }
            if(match!=null)return match;
        }
        return null;
    }

    static Located? ReadApproval(Notice request,AutomationElement doc,string key,AutomationElement[] nodes,
        AutomationElement[] buttons,MenuSession? menu)
    {
        // Editable and nested permission forms must be handled in the original application.
        if(nodes.Any(e=>IsType(e,ControlType.Edit) && Pattern<ValuePattern>(e,ValuePattern.Pattern) is {Current.IsReadOnly:false} ||
            IsType(e,ControlType.CheckBox) || IsType(e,ControlType.RadioButton) || IsType(e,ControlType.ComboBox)))return null;
        var labels=buttons.Select(b=>b.Current.Name).ToHashSet();
        var details=nodes.Where(e=>IsType(e,ControlType.Text)||IsType(e,ControlType.Edit))
            .Select(e=>e.Current.Name).Where(s=>s.Length>0 && !labels.Contains(s));
        string context=string.Join(Environment.NewLine,details);
        if(string.IsNullOrWhiteSpace(context))return null;
        if(request.NativeForm!="" && (key!=request.NativeForm || context!=request.Body))return null;

        var choices=buttons.Where(DecisionButton).ToArray();
        if(menu!=null)
        {
            var owner=choices.SingleOrDefault(b=>Id(b)==menu.Owner && LabelIn(b,MoreLabels));
            if(owner==null)return null;
            var allowLabels=choices.Where(b=>LabelIn(b,AllowLabels)).Select(b=>b.Current.Name.Trim()).ToArray();
            choices=MenuItems(doc,owner,menu,allowLabels);
        }
        var actions=new List<ActionItem>();
        var bound=new Dictionary<string,AutomationElement>();
        foreach(var button in choices)
        {
            if(!Has(button,InvokePattern.Pattern) && !Has(button,ExpandCollapsePattern.Pattern))continue;
            string buttonKey=Id(button);
            bound[buttonKey]=button;
            actions.Add(new(button.Current.Name,buttonKey,button.Current.IsEnabled,
                MenuButton(button) || menu==null && LabelIn(button,MoreLabels)));
        }
        if(actions.Count==0)return null;
        var view=new View(key,context,null,actions.ToArray(),false,doc.Current.ProcessId,menu);
        return new(doc,view,bound,null,null,[]);
    }

    static Located? ReadQuestion(Prompt[] prompts,AutomationElement doc,string key,AutomationElement[] nodes,AutomationElement[] buttons)
    {
        var hits=prompts.Where(p=>nodes.Any(e=>IsType(e,ControlType.Text)&&Eq(e.Current.Name,p.Title))).ToArray();
        var submits=buttons.Where(b=>SendNames.Contains(b.Current.Name.Trim())&&Has(b,InvokePattern.Pattern)).ToArray();
        if(hits.Length!=1 || submits.Length!=1 || submits[0].Current.IsOffscreen)return null;
        var edits=nodes.Where(e=>IsType(e,ControlType.Edit)&&Pattern<ValuePattern>(e,ValuePattern.Pattern) is {Current.IsReadOnly:false}).ToArray();
        if(edits.Length>1)return null;
        var editor=edits.SingleOrDefault();
        var question=hits[0];
        // An editable answer can carry an original option's label. Otherwise each option needs its own control.
        if(editor==null && (question.Options.Length==0 || question.Options.Any(label=>Choice(nodes,label)==null)))return null;
        var view=new View(key,question.Title,question,[],editor!=null,doc.Current.ProcessId);
        return new(doc,view,[],editor,submits[0],nodes);
    }
    static AutomationElement? Choice(IEnumerable<AutomationElement> nodes,string label)
    {
        var options=nodes.Where(e=>Eq(e.Current.Name,label) && (IsType(e,ControlType.RadioButton)||IsType(e,ControlType.ListItem)||
                IsType(e,ControlType.Button)||IsType(e,ControlType.CheckBox)) && (Has(e,SelectionItemPattern.Pattern)||Has(e,InvokePattern.Pattern)||Has(e,TogglePattern.Pattern))).ToArray();
        return options.Length==1?options[0]:null;
    }
    public static View? Read(Notice n,string title,MenuSession? menu=null,QuestionScope? questionScope=null)
    {
        try{return Find(n,title,menu,questionScope)?.View;}catch(Exception e) when(ExpectedError(e)){return null;}
    }
    static bool Same(View a,View b)=>a.MatchesForm(b) &&
        a.Actions.Select(x=>(x.Key,x.Label,x.Menu)).SequenceEqual(b.Actions.Select(x=>(x.Key,x.Label,x.Menu)));
    public static (MenuSession? Session,View? Choices) OpenMenu(Notice n,string title,View shown,string key)
    {
        try
        {
            var f=Find(n,title);
            if(f==null || !Same(shown,f.View) || !f.Buttons.TryGetValue(key,out var owner) ||
                !LabelIn(owner,MoreLabels) || !owner.Current.IsEnabled)return default;
            var before=VisibleMenus(f.Document).Select(Id).ToHashSet();
            var session=new MenuSession(key,Id(f.Document),f.View.FormKey,before);
            var expand=Pattern<ExpandCollapsePattern>(owner,ExpandCollapsePattern.Pattern);
            if(expand==null)Pattern<InvokePattern>(owner,InvokePattern.Pattern)?.Invoke();
            else if(expand.Current.ExpandCollapseState!=ExpandCollapseState.Expanded)
            {
                try{expand.Expand();}catch(InvalidOperationException){}
                // Some Chromium menu buttons expose Expand but only react to
                // their default action. Never repeat an already-open toggle.
                for(int i=0;i<5 && expand.Current.ExpandCollapseState==ExpandCollapseState.Collapsed;i++)Thread.Sleep(60);
                if(expand.Current.ExpandCollapseState==ExpandCollapseState.Collapsed)
                    Pattern<InvokePattern>(owner,InvokePattern.Pattern)?.Invoke();
            }
            for(int i=0;i<12;i++)
            {
                var child=Read(n,title,session);
                if(child!=null)return(session,child);
                Thread.Sleep(70);
            }
            return (session,null);
        }catch(Exception e) when(ExpectedError(e)){return default;}
    }
    public static void CollapseMenu(Notice n,string title,MenuSession menu)
    {
        try
        {
            var f=Find(n,title);
            if(f==null || Id(f.Document)!=menu.Document || f.View.FormKey!=menu.Form ||
                !f.Buttons.TryGetValue(menu.Owner,out var owner) || !LabelIn(owner,MoreLabels))return;
            var p=Pattern<ExpandCollapsePattern>(owner,ExpandCollapsePattern.Pattern);
            if(p?.Current.ExpandCollapseState==ExpandCollapseState.Expanded)p.Collapse();
        }catch(Exception e) when(ExpectedError(e)){}
    }
    public static string SubmitMenu(Notice n,string title,View parent,View shown,string key)
    {
        const string stale="原菜单已变化，请重新展开后选择。";
        if(shown.Menu is not {} menu || parent.FormKey!=menu.Form)return stale;
        var original=shown.Actions.SingleOrDefault(a=>a.Key==key);
        if(original==null || !original.Enabled || original.Menu)return stale;
        if(Read(n,title,menu) is {} live && Same(shown,live))
            return Submit(n,title,live,key,false);
        // A deliberate click on the card can close the native menu.
        var reopened=OpenMenu(n,title,parent,menu.Owner).Choices;
        var rebound=ReopenedAction(shown,reopened,original.Label);
        return rebound!=null?Submit(n,title,reopened!,rebound.Key,false):stale;
    }
    internal static ActionItem? ReopenedAction(View shown,View? reopened,string label)
    {
        if(shown.Menu is not {} menu || reopened?.Menu==null || reopened.Menu.Document!=menu.Document || reopened.Menu.Form!=menu.Form ||
            reopened.Menu.Owner!=menu.Owner || reopened.Context!=shown.Context ||
            !shown.Actions.Select(a=>(a.Label,a.Enabled,a.Menu)).SequenceEqual(reopened.Actions.Select(a=>(a.Label,a.Enabled,a.Menu))))return null;
        var matches=reopened.Actions.Where(a=>a.Label==label).ToArray();
        return matches is [{Enabled:true,Menu:false} only]?only:null;
    }
    public static string Submit(Notice n,string title,View shown,string value,bool custom,QuestionScope? questionScope=null)
    {
        const string stale="ChatGPT 中的请求已变化。请重新查看原选项。";
        try
        {
            var f=Find(n,title,shown.Menu,questionScope);
            if(f==null || !Same(shown,f.View))return stale;
            if(n.Kind==Kind.Permission)
            {
                var item=f.View.Actions.SingleOrDefault(a=>a.Key==value);
                if(item==null || !item.Enabled || !f.Buttons.TryGetValue(value,out var b))return stale;
                if(item.Menu)return stale;
                Pattern<InvokePattern>(b,InvokePattern.Pattern)!.Invoke();
                return "";
            }
            if(f.Submit==null || string.IsNullOrWhiteSpace(value))return "请先选择或填写答案。";
            var opt=custom?null:Choice(f.Nodes,value);
            if(!custom && f.View.Question?.Options.Contains(value)!=true)return stale;
            if(opt!=null && opt.Current.IsEnabled)
            {
                // Prefer the option's normal click action when Chromium exposes both patterns.
                if(Pattern<InvokePattern>(opt,InvokePattern.Pattern) is {} invoke)invoke.Invoke();
                else if(Pattern<SelectionItemPattern>(opt,SelectionItemPattern.Pattern) is {} select)select.Select();
                else if(Pattern<TogglePattern>(opt,TogglePattern.Pattern) is {} toggle)
                {if(toggle.Current.ToggleState!=ToggleState.On)toggle.Toggle();}
                else return stale;
                // An ordinary option click in the desktop async question already submits it.
                // A second click on Send races the app's pending submission.
                if(n.Async)
                {
                    Audit.Write($"question-submit-requested request={n.Id} input=option action=choose");
                    return "";
                }
            }
            else if(f.Editor!=null)
            {
                var write=Pattern<ValuePattern>(f.Editor,ValuePattern.Pattern)!;write.SetValue(value);
                if(write.Current.Value!=value)return "ChatGPT 尚未接收输入，请打开原对话查看。";
            }
            else return stale;
            // React may replace a control after a choice. Never send a different form.
            for(int i=0;i<8;i++)
            {
                var fresh=Find(n,title,questionScope:questionScope);
                if(fresh==null || !Same(shown,fresh.View))return stale;
                if(fresh.Submit?.Current.IsEnabled==true)
                {
                    var selected=Choice(fresh.Nodes,value);
                    var selection=selected==null?null:Pattern<SelectionItemPattern>(selected,SelectionItemPattern.Pattern);
                    var toggled=selected==null?null:Pattern<TogglePattern>(selected,TogglePattern.Pattern);
                    var written=fresh.Editor==null?null:Pattern<ValuePattern>(fresh.Editor,ValuePattern.Pattern);
                    // A previous selection can also leave Send enabled. Require a positive echo.
                    var marked=selected!=null && AutomationProperty.LookupById(30102) is {} aria &&
                        selected.GetCurrentPropertyValue(aria) is string flags &&
                        (flags.Split(';').Contains("checked=true")||flags.Split(';').Contains("pressed=true"));
                    bool checkedOption=selection?.Current.IsSelected==true || toggled?.Current.ToggleState==ToggleState.On || marked;
                    if(checkedOption || written?.Current.Value==value)
                    {
                        Pattern<InvokePattern>(fresh.Submit,InvokePattern.Pattern)!.Invoke();
                        Audit.Write($"question-submit-requested request={n.Id} input={(opt==null?"editor":"option")} action=send");
                        return "";
                    }
                }
                Thread.Sleep(70);
            }
            return "ChatGPT 尚未确认选中的答案。请打开原对话查看。";
        }catch(Exception e) when(ExpectedError(e)){return stale;}
    }
}

