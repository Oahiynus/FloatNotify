namespace FloatNotify;

internal sealed partial class Resident
{
    readonly Dictionary<string,OverflowWindow> overflow=new(StringComparer.OrdinalIgnoreCase);
    Entry? requested;
    bool layoutPosted, arranging;

    void DisplaysChanged(object? sender,EventArgs e)=>Post(ScheduleLayout);
    void ScheduleLayout()
    {
        if(shuttingDown || layoutPosted)return;
        layoutPosted=true;
        Post(()=>{layoutPosted=false;ArrangeCards();});
    }
    void AddCard(Entry entry)
    {
        if(!cards.Contains(entry)){
            entry.MonitorName=Screen.FromHandle(Native.GetForegroundWindow()).DeviceName;
            cards.Add(entry);
        }
        ScheduleLayout();
    }
    void RemoveCard(Entry entry)
    {
        if(!cards.Remove(entry))return;
        if(requested==entry)requested=null;
        entry.KeepFaceClear=false;entry.InOverflow=false;
        var old=entry.Window;entry.Window=null;old?.Dispose();
        if(settingsTrial==entry.Notice.Id){settingsTrial=null;settings?.RefreshState();}
        ScheduleLayout();
    }
    void RemoveCards(Predicate<Entry> match)
    {
        foreach(var e in cards.FindAll(match))RemoveCard(e);
    }
    void Reveal(Entry entry)
    {
        if(!cards.Contains(entry))return;
        foreach(var e in cards.Where(e=>e.MonitorName==entry.MonitorName))e.KeepFaceClear=false;
        requested=entry;
        ArrangeCards();
    }
    Card BuildCard(Entry entry)
    {
        var notice=entry.Notice;
        var title=CachedTitle(notice.Chat);
        bool CanBind()
        {
            if(!entry.Visible || !activeRequests.Contains(entry) || notice.Chat==Guid.Empty)return false;
            if(notice.Kind==Kind.Permission)return HasUniqueTitle(notice.Chat,title) && activeRequests.Count(e=>e.Notice.Chat==notice.Chat && e.Notice.Kind==Kind.Permission)==1;
            var request=entry.Notice;
            return request.Prompts!=null && !activeRequests.Any(other=>other!=entry && other.Notice.Kind==Kind.Question &&
                other.Notice.Chat==request.Chat && other.Notice.Prompts?.Any(p=>request.Prompts.Any(q=>q.Title==p.Title))==true);
        }
        void Dismiss(){
            if(!cards.Contains(entry))return;
            if(entry.Notice.Approval is {} approval)HandBack(approval);
            RemoveCard(entry);
        }
        void OpenOriginal(){
            try{
                if(notice.Chat!=Guid.Empty)Program.OpenThread(notice.Chat);
                Dismiss();
            }catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException){
                entry.Window?.SetBody("请从任务栏打开 Codex，进入这条对话。");
            }
        }
        var card=new Card(notice,title,OpenOriginal,Dismiss,CanBind,material,notice.Kind==Kind.Question
            ? ()=>new FormBridge.QuestionScope(monitor.Routes,HasUniqueTitle(notice.Chat,title)) : null);
        card.LayoutRequested+=ScheduleLayout;
        card.HeaderClicked+=()=>Reveal(entry);
        return card;
    }

    void ArrangeCards()
    {
        if(shuttingDown)return;
        if(arranging){ScheduleLayout();return;}
        arranging=true;
        try{
            if(cards.Count>0)RefreshTitles();
            var screens=Screen.AllScreens;
            if(screens.Length==0)return;
            var fallback=Screen.FromHandle(Native.GetForegroundWindow());
            var target=requested;requested=null;
            foreach(var e in cards){
                if(e.Visible && e.Window!.Detached)e.MonitorName=Screen.FromHandle(e.Window.Handle).DeviceName;
                if(!screens.Any(s=>s.DeviceName==e.MonitorName))e.MonitorName=fallback.DeviceName;
            }
            foreach(var s in screens)ArrangeScreen(s,target);
            foreach(var name in overflow.Keys.Where(k=>!screens.Any(s=>s.DeviceName==k)).ToArray())CloseOverflow(name);
        }finally{arranging=false;}
    }

    void ArrangeScreen(Screen screen,Entry? target)
    {
        var group=cards.Where(e=>e.MonitorName==screen.DeviceName).ToArray();
        if(group.Length==0){CloseOverflow(screen.DeviceName);return;}
        var area=screen.WorkingArea;
        double scale=Card.ScaleFor(area);
        var blocks=new List<CardPlacement.Block>();
        var fixedEntries=new HashSet<Entry>();
        foreach(var e in cards){
            if(!e.Visible || !(e.KeepFaceClear || e.Window!.Protected))continue;
            var rect=e.Window!.FaceBounds;
            if(!rect.IntersectsWith(area))continue;
            blocks.Add(new(rect,true,e.Window.Detached));
            fixedEntries.Add(e);
        }
        var selected=group.Contains(target)?target:null;
        if(selected!=null && !fixedEntries.Contains(selected) && !selected.Measured.IsEmpty){
            // Leave existing completion bars in place when opening an overflow item.
            // Only an explicit page change may exchange a bar when the display is full.
            var bars=group.Where(e=>e!=selected && e.Visible && e.Notice.Kind==Kind.Done && !fixedEntries.Contains(e)).ToArray();
            var keep=bars.Select(e=>new CardPlacement.Block(e.Window!.FaceBounds,true)).ToArray();
            if(CardPlacement.Find(area,selected.Measured,scale,selected.Notice.Kind==Kind.Done,
                blocks.Concat(keep).ToArray(),useSideSpace:true)!=null){
                blocks.AddRange(keep);
                fixedEntries.UnionWith(bars);
            }
        }
        var movable=group.Where(e=>!fixedEntries.Contains(e)).ToArray();
        // A deliberate selection gets a full face. Previously shown cards keep their position
        // whenever it fits. Passive arrivals never displace a displayed completion.
        var ordered=movable.OrderBy(e=>e==selected?0:e.Visible && e.Notice.Kind==Kind.Done?1:e.Visible?2:3).ToArray();
        var layers=new List<Card>();
        bool changeZ=false;
        foreach(var e in ordered){
            bool wasVisible=e.Visible;
            var card=e.Window;
            if(wasVisible || e.Measured.IsEmpty || e.MeasuredArea!=area || e.MeasuredScale!=scale){
                card??=BuildCard(e);
                e.Measured=card.MeasureFace(area,scale);
                if(wasVisible && !card.FaceBounds.IsEmpty)e.Measured=card.FaceBounds.Size;
                e.MeasuredArea=area;e.MeasuredScale=scale;
            }
            Point? old=wasVisible?card!.FaceBounds.Location:null;
            var face=CardPlacement.Find(area,e.Measured,scale,e.Notice.Kind==Kind.Done,blocks,old,e==selected);
            if(face==null){
                if(!e.InOverflow){
                    e.InOverflow=true;
                    Audit.Write($"card-overflow kind={e.Notice.Kind} source={e.Notice.Source} chat={e.Notice.Chat} request={e.Notice.Id} reason=space");
                    if(e.Notice.Kind==Kind.Done)Record(()=>history.Update(e.HistoryId,r=>r with{Status="已折叠"}));
                }
                if(wasVisible)card!.Hide();
                else if(e.Window==null)card?.Dispose();
                continue;
            }
            card??=BuildCard(e);
            foreach(var behind in layers)
                if(behind.FaceBounds.IntersectsWith(face.Value))behind.SetExposure(1,true);
            e.InOverflow=false;e.Window=card;
            if(!wasVisible){
                card.MeasureFace(area,scale);
                var fg=Native.GetForegroundWindow();
                var focus=Native.KeyboardWindow(fg);
                card.Place(face.Value,scale);
                TrackRead(RecordShown(e,card,area,fg,focus));
                changeZ=true;
            }else if(old!=face.Value.Location){
                card.Place(face.Value,scale);
                changeZ=true;
            }
            var bounds=card.FaceBounds;
            blocks.Add(new(bounds,e.Notice.Kind==Kind.Done || e==selected));
            layers.Add(card);
        }
        if(changeZ || selected!=null){
            // The first visible header remains clickable under every later overlapping face.
            foreach(var card in layers)card.RaisePassive();
            if(selected?.Window is {IsVisible:true} chosen){selected.KeepFaceClear=true;chosen.RaisePassive();}
        }
        var obscured=new HashSet<Card>();
        for(int i=0;i<layers.Count;i++){
            var current=layers[i];
            bool covered=false;
            if(!current.Protected && selected?.Window!=current){
                var bounds=current.FaceBounds;
                for(int j=i+1;j<layers.Count && !covered;j++)covered=bounds.IntersectsWith(layers[j].FaceBounds);
            }
            if(covered)obscured.Add(current);
        }
        int exposed=group.Count(e=>e.Visible)-obscured.Count;
        foreach(var e in group)if(e.Visible)e.Window!.SetExposure(exposed,obscured.Contains(e.Window));
        var hidden=group.Where(e=>!e.Visible).ToArray();
        if(hidden.Length==0){CloseOverflow(screen.DeviceName);return;}
        if(!overflow.TryGetValue(screen.DeviceName,out var chip))
            overflow[screen.DeviceName]=chip=new OverflowWindow(Reveal);
        chip.Update(hidden.Select(e=>new OverflowWindow.Item(e,KindName(e.Notice.Kind),CachedTitle(e.Notice.Chat))).ToArray(),area,scale,blocks);
        if(selected!=null && !selected.Visible)chip.ExplainBlocked();
    }
    void CloseOverflow(string name){if(overflow.Remove(name,out var chip))chip.Close();}

    async Task RecordShown(Entry entry,Card card,Rectangle area,IntPtr foreground,IntPtr previousFocus)
    {
        var notice=entry.Notice;
        bool centered=card.Centered(area);
        var after=Native.GetForegroundWindow();
        var focus=Native.KeyboardWindow(after);
        string keyboard=previousFocus==IntPtr.Zero||focus==IntPtr.Zero?"unavailable":
            after==foreground && previousFocus==focus?"unchanged":"changed";
        var rect=card.FaceBounds;
        string shown=$"{notice.Kind.ToString().ToLowerInvariant()}-shown shownAt={DateTimeOffset.Now:O} source={notice.Source} focused={after==card.Handle} centered={centered} face={rect.X},{rect.Y},{rect.Width},{rect.Height} material={material} renderer={card.Renderer} liveGlass={card.LiveBackdrop} rim={card.Refracting}";
        if(notice.Kind==Kind.Done){
            bool named=names.TryGetValue(notice.Chat,out var indexed) && card.DisplayedTitle==indexed;
            var (view,match)=await ViewOf(entry,foreground);
            if(shuttingDown)return;
            if(entry.Visible && entry.Window==card)entry.ReturnedToTask(view);
            shown+=$" chat={notice.Chat} named={named} match={match} keyboardWindow={keyboard} turn={notice.Id} view={view}";
        }else shown+=$" chat={notice.Chat} turn={notice.Turn} request={notice.Id} keyboardWindow={keyboard}";
        Record(()=>history.Update(entry.HistoryId,r=>r with{Geometry=centered.ToString(),Focus=keyboard,Status=r.Kind==Kind.Done?"已显示":r.Status}));
        Audit.Write(shown);
    }
}

