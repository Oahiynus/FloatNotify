using System.Text.Json;

namespace FloatNotify;

internal sealed record WorkspaceInfo(string Label, string Path = "");

internal sealed class ThreadWorkspaces(string codexHome)
{
    readonly string index = Path.Combine(codexHome, ".codex-global-state.json");
    Dictionary<Guid,WorkspaceInfo> projects = [];
    readonly Dictionary<Guid,WorkspaceInfo> sessions = [];
    readonly Dictionary<Guid,string> sessionFiles = [];
    DateTime nextFileScan;
    static readonly WorkspaceInfo NoProject = new("未归入项目");
    DateTime modified;
    internal static readonly WorkspaceInfo Unknown = new("未记录");

    public void Refresh()
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(index);
            if (stamp == modified) return;
            if (!File.Exists(index)) { projects.Clear(); modified=stamp; return; }
            using var input = Open(index);
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var next = new Dictionary<Guid,WorkspaceInfo>();
            if (root.TryGetProperty("projectless-thread-ids", out var loose) && loose.ValueKind == JsonValueKind.Array)
                foreach (var item in loose.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var chat))
                        next[chat] = NoProject;
            foreach (var group in root.EnumerateObject())
                if (group.Name.StartsWith("electron-owner-value:thread-workspace-root-hints:", StringComparison.Ordinal) && group.Value.ValueKind == JsonValueKind.Object)
                    foreach (var item in group.Value.EnumerateObject())
                        if (Guid.TryParse(item.Name, out var chat) && item.Value.ValueKind == JsonValueKind.String && item.Value.GetString() is { Length: >0 } path)
                            next[chat] = FromPath(path);
            if (root.TryGetProperty("thread-project-assignments", out var assignments) && assignments.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("local-projects", out var local) && local.ValueKind == JsonValueKind.Object)
                foreach (var item in assignments.EnumerateObject())
                {
                    if (!Guid.TryParse(item.Name, out var chat) || item.Value.Str("projectKind")!="local" ||
                        !local.TryGetProperty(item.Value.Str("projectId"),out var project) || project.ValueKind != JsonValueKind.Object) continue;
                    string name = project.Str("name");
                    string paths = project.TryGetProperty("rootPaths",out var roots) && roots.ValueKind==JsonValueKind.Array
                        ? string.Join(Environment.NewLine,roots.EnumerateArray().Where(r=>r.ValueKind==JsonValueKind.String).Select(r=>r.GetString())) : "";
                    if (name.Length>0) next[chat]=new(name,paths);
                }
            projects=next;
            modified=stamp;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    static FileStream Open(string path) => new(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
    static WorkspaceInfo FromPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase)) path=@"\\"+path[8..];
        else if (path.StartsWith(@"\\?\",StringComparison.Ordinal)) path=path[4..];
        string name=path.TrimEnd('\\','/').Split('\\','/').LastOrDefault() ?? "";
        return new(name.Length>0 && !name.EndsWith(':') ? name : path,path);
    }

    public WorkspaceInfo For(Guid chat)
    {
        if (chat==Guid.Empty) return Unknown;
        var project=projects.GetValueOrDefault(chat);
        if (project!=null && project!=NoProject) return project;
        if (sessions.TryGetValue(chat,out var known)) return known;
        if (DateTime.UtcNow>=nextFileScan)
        {
            IndexSessionFiles();
            nextFileScan=DateTime.UtcNow.AddMinutes(1);
        }
        if (sessionFiles.TryGetValue(chat,out var file) && ReadHeader(chat,file) is {} workspace)
        {
            sessions[chat]=workspace;
            return workspace;
        }
        return project ?? Unknown;
    }

    void IndexSessionFiles()
    {
        // Index filenames once across all visible reminders; read only matching first lines.
        sessionFiles.Clear();
        foreach (string folder in new[]{"sessions","archived_sessions"})
        {
            try
            {
                var dir=Path.Combine(codexHome,folder);
                if (!Directory.Exists(dir)) continue;
                var options=new EnumerationOptions{RecurseSubdirectories=folder=="sessions",IgnoreInaccessible=true};
                foreach (var file in Directory.EnumerateFiles(dir,"rollout-*.jsonl",options))
                {
                    string name=Path.GetFileNameWithoutExtension(file);
                    if(name.Length>=36 && Guid.TryParseExact(name.AsSpan(name.Length-36),"D",out var id))
                        sessionFiles[id]=file;
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static WorkspaceInfo? ReadHeader(Guid chat,string file)
    {
        try
        {
            using var input=Open(file);
            using var reader=new StreamReader(input);
            using var doc=JsonDocument.Parse(reader.ReadLine()??"");
            var header=doc.RootElement;
            if (header.Str("type")=="session_meta" && header.TryGetProperty("payload",out var payload) &&
                Guid.TryParse(payload.Str("id"),out var id) && id==chat && payload.Str("cwd") is { Length: >0 } path)
                return FromPath(path);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }
}
