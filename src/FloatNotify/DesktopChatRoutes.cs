using System.Globalization;

namespace FloatNotify;

// Electron window numbers are scoped to one app run. The file name supplies
// the run and process, while ownerRoutePath identifies the displayed page.
internal sealed class DesktopChatRoutes
{
    sealed class WindowRoute
    {
        public DateTimeOffset RouteAt, AppearanceAt, NavigationStart;
        public bool Primary;
        public Guid? Chat;
    }
    readonly Dictionary<(int Pid,string Instance,int Window),WindowRoute> windows=new();
    const string Prefix=" info [electron-message-handler] ";
    const string Owner="IAB_LIFECYCLE received browser sidebar owner sync ";
    const string Span="client.performance.span ";

    internal static Guid? ParsePath(string path)
    {
        var parts=path.Split('?', '#')[0].TrimEnd('/').Split('/');
        string? id=parts switch {
            ["","local" or "c",var chat] => chat,
            ["","g",{Length:>0},"c",var chat] => chat,
            _ => null
        };
        return Guid.TryParseExact(id,"D",out var value) && value!=Guid.Empty ? value : null;
    }

    static string Field(string line,string key)
    {
        string prefix=" "+key+"=";
        int i=line.IndexOf(prefix,StringComparison.Ordinal);
        if(i<0)return "";
        i+=prefix.Length;
        int end=line.IndexOf(' ',i);
        return end<0 ? line[i..] : line[i..end];
    }

    public void Observe(int pid,string instance,string line)
    {
        if(pid<=0 || instance.Length==0)return;
        int start=line.IndexOf(' ');
        if(start<0 || !line.AsSpan(start).StartsWith(Prefix,StringComparison.Ordinal))return;
        var message=line.AsSpan(start+Prefix.Length);
        bool route=message.StartsWith(Owner,StringComparison.Ordinal);
        bool span=message.StartsWith(Span,StringComparison.Ordinal);
        if(!route && !span && !message.StartsWith("[desktop-notifications] ",StringComparison.Ordinal))return;
        if(!int.TryParse(Field(line,route?"windowId":"rendererWindowId"),out var id) || id<=0 ||
           !DateTimeOffset.TryParse(line.AsSpan(0,start),CultureInfo.InvariantCulture,DateTimeStyles.None,out var stamp))return;
        var key=(pid,instance,id);
        lock(windows)
        {
            if(!windows.TryGetValue(key,out var w))windows[key]=w=new();
            if(route)
            {
                if(stamp < w.RouteAt)return;
                w.RouteAt=stamp;
                w.Chat=ParsePath(Field(line,"ownerRoutePath")); // Home/settings also clear the previous chat.
                return;
            }
            var appearance=Field(line,"rendererWindowAppearance");
            if(appearance.Length>0 && stamp>=w.AppearanceAt)
            {
                w.AppearanceAt=stamp;
                w.Primary=appearance=="primary";
            }
            if(span && Field(line,"name")=="thread_navigation" && Field(line,"outcome")=="success")
            {
                // A renderer span finishes after routing. Only a route belonging
                // to this navigation (or a later one) is usable.
                double.TryParse(Field(line,"wallDurationMs"),NumberStyles.Float,CultureInfo.InvariantCulture,out var duration);
                var began=stamp.AddMilliseconds(-Math.Clamp(double.IsFinite(duration)?duration:0,0,120000)-750);
                if(began>w.NavigationStart)w.NavigationStart=began;
            }
        }
    }

    public Guid? ForPrimary(int pid,DateTimeOffset processStarted)
    {
        lock(windows)
        {
            var primary=windows.Where(p=>p.Key.Pid==pid && p.Value.Primary && p.Value.AppearanceAt>=processStarted)
                .Select(p=>p.Value).Take(2).ToArray();
            if(primary.Length!=1)return null;
            var w=primary[0];
            return w.RouteAt>=processStarted && w.RouteAt>=w.NavigationStart ? w.Chat : null;
        }
    }
}
