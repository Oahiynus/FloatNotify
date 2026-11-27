using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace FloatNotify;

internal enum CompletionView { OtherApp, SameChat, OtherChat, Unknown }

internal sealed class ForegroundChat
{
    static readonly Condition Document = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);
    Task<string?>? reading;
    IntPtr readingWindow;

    // Called on the resident's UI thread. An unresponsive accessibility provider
    // must not hold up an approval or start a new worker on every timer tick.
    public async Task<string?> Read(IntPtr window,CancellationToken stop)
    {
        if (reading == null || reading.IsCompleted)
        {
            readingWindow = window;
            reading = Task.Run(() => ReadTitle(window));
        }
        if (readingWindow != window) return null;
        try { return await reading.WaitAsync(TimeSpan.FromMilliseconds(800),stop).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }

    static string? ReadTitle(IntPtr window)
    {
        try
        {
            // The outer renderer document identifies the page. Sidebar entries
            // and documents embedded inside the page can name other conversations.
            var root = AutomationElement.FromHandle(window);
            var doc = root.FindFirst(TreeScope.Descendants, Document);
            if (doc == null || doc.Current.IsOffscreen || doc.Current.AutomationId != "RootWebArea") return null;
            var address = doc.GetCurrentPropertyValue(ValuePattern.ValueProperty, true) as string;
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                uri.Scheme != "app" || uri.Host != "-" || uri.AbsolutePath != "/index.html") return null;
            return doc.Current.Name;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or
            UnauthorizedAccessException or COMException or ArgumentException or TimeoutException) { return null; }
    }

    internal static CompletionView Match(Guid taskChat, string? foregroundTitle, IReadOnlyDictionary<Guid,string> names)
    {
        if (string.IsNullOrWhiteSpace(foregroundTitle) || foregroundTitle is "ChatGPT" or "Codex") return CompletionView.Unknown;
        var matches = names.Where(pair => pair.Value == foregroundTitle).Take(2).ToArray();
        if (matches.Length != 1 || taskChat == Guid.Empty) return CompletionView.Unknown;
        return matches[0].Key == taskChat ? CompletionView.SameChat : CompletionView.OtherChat;
    }
}
