using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FloatNotify;

internal sealed record ApprovalDetails(Guid Chat, Guid Turn, string Tool, string Reason, string Arguments, string Command, string Cwd)
{
    // Unknown policies, incomplete commands and unverified origins stay with the native form.
    internal static ApprovalDetails? Parse(JsonElement input, string sessions, Action<string>? deferred=null)
    {
        ApprovalDetails? Defer(string reason){deferred?.Invoke(reason);return null;}
        if (input.Str("hook_event_name") != "PermissionRequest" ||
            !Guid.TryParse(input.Str("session_id"), out var chat) || !Guid.TryParse(input.Str("turn_id"), out var turn) ||
            input.Str("tool_name") is not { Length: > 0 } tool ||
            !input.TryGetProperty("tool_input", out var args) || args.ValueKind != JsonValueKind.Object ||
            !args.EnumerateObject().Any() || args.GetRawText().Length > 32000) return Defer("request-fields");
        if (MonitorFiles.SessionFile(sessions,input.Str("transcript_path")) is not {} transcript) return Defer("session-path");
        if (!HumanTurn(transcript,chat,turn)) return Defer("human-turn-unconfirmed");

        string reason = args.Str("description");
        string command = args.Str("command");
        // Preserve additional arguments in full, including scope and sandbox/network parameters.
        bool simple = command.Length > 0 && args.EnumerateObject().All(p => p.Name is "command" or "description");
        string details = simple ? command : JsonSerializer.Serialize(args, PrettyJson);
        return new(chat, turn, tool, reason, details, command, input.Str("cwd"));
    }
    internal bool MatchesOriginal(Notice notice,string context)
    {
        if(notice.Chat!=Chat || (notice.Turn.Length>0 && notice.Turn!=Turn.ToString()) || string.IsNullOrWhiteSpace(Command))return false;
        string lines="\n"+context.ReplaceLineEndings("\n").Trim()+"\n";
        bool ContainsBlock(string value)=>lines.Contains("\n"+value.ReplaceLineEndings("\n").Trim()+"\n",StringComparison.Ordinal);
        return ContainsBlock(Command) && (Reason.Length==0 || ContainsBlock(Reason));
    }
    static readonly JsonSerializerOptions PrettyJson = new() {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static bool HumanTurn(string path, Guid chat, Guid turn)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (reader.ReadLine() is not string header) return false;
        using var meta = JsonDocument.Parse(header);
        var session = new Session();
        if (!session.Identify(meta.RootElement) || !session.DesktopUser || session.Chat != chat) return false;

        long offset = Math.Max(0, stream.Length - 2 * 1024 * 1024);
        reader.DiscardBufferedData();
        stream.Position = offset;
        if (offset != 0) reader.ReadLine();
        var result = ReadContext(reader,turn);
        if (result.HasValue || offset == 0) return result == true;
        // A long turn can outgrow the tail before the first permission request.
        reader.DiscardBufferedData();
        stream.Position = 0;
        return ReadContext(reader,turn) == true;
    }

    static bool? ReadContext(StreamReader reader,Guid turn)
    {
        bool? human = null;
        while (reader.ReadLine() is string line)
        {
            if (!line.Contains("\"turn_context\"", StringComparison.Ordinal) &&
                !line.Contains("\"turn_aborted\"", StringComparison.Ordinal) &&
                !line.Contains("\"task_complete\"", StringComparison.Ordinal)) continue;
            using var doc = JsonDocument.Parse(line);
            var row = doc.RootElement;
            if (!row.TryGetProperty("payload", out var data)) continue;
            if (row.Str("type") == "turn_context")
            {
                human = Guid.TryParse(data.Str("turn_id"), out var active) && active == turn &&
                    data.Str("approvals_reviewer") == "user" && data.TryGetProperty("approval_policy", out var policy) &&
                    (policy.ValueKind == JsonValueKind.String && policy.GetString() is "on-request" or "untrusted" or "on-failure" ||
                     policy.ValueKind == JsonValueKind.Object && policy.TryGetProperty("granular", out _));
            }
            if (row.Str("type") == "event_msg" && data.Str("type") is "turn_aborted" or "task_complete" &&
                Guid.TryParse(data.Str("turn_id"), out var ended) && ended == turn) human = false;
        }
        return human;
    }
}

// One duplex connection per invocation. A decision can travel back only on the connection
// whose card the user clicked. There is no IPC command for approving an existing card.
internal sealed class OfficialApproval(ApprovalDetails details)
{
    readonly TaskCompletionSource<string> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ApprovalDetails Details { get; } = details;
    public string Id { get; } = "hook/" + Guid.NewGuid().ToString("N");
    public bool Active => !answer.Task.IsCompleted;
    public bool Respond(string decision) => decision is "allow" or "deny" or "pass" && answer.TrySetResult(decision);
    const int WaitSeconds = 100;
    static string PipeName => Program.Pipe + "-approval";
    static readonly UTF8Encoding Utf8 = new(false);

    [DllImport("kernel32.dll")] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll")] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    static bool SameProgram(PipeStream pipe, bool server)
    {
        uint pid;
        if (!(server ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid) : GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid))) return false;
        using var process = Process.GetProcessById((int)pid);
        return string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }
    internal static string Output(string? decision) => decision is "allow" or "deny"
        ? JsonSerializer.Serialize(new {hookSpecificOutput = new {hookEventName = "PermissionRequest", decision = new {behavior = decision}}})
        : "{}";

    static ValueTask WriteLine(PipeStream pipe,string text,CancellationToken token) =>
        pipe.WriteAsync(Utf8.GetBytes(text+"\n").AsMemory(),token);

    internal static async Task RunHook(ApprovalDetails request, string? pipeName=null, TextWriter? response=null)
    {
        response ??= Console.Out;
        using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(WaitSeconds + 4));
        await pipe.ConnectAsync(500, timeout.Token);
        if (!SameProgram(pipe, false)) { response.WriteLine("{}"); return; }
        using var reader = new StreamReader(pipe, Utf8, leaveOpen:true);
        await WriteLine(pipe,JsonSerializer.Serialize(request),timeout.Token);
        string? decision = await reader.ReadLineAsync(timeout.Token);
        response.WriteLine(Output(decision));
        response.Flush();
        // After stdout is written, a failed receipt must never generate a second JSON response.
        try { await WriteLine(pipe,"received",timeout.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }

    internal static async Task Listen(Action<OfficialApproval> offer, Action<OfficialApproval,string> finish, CancellationToken stop, string? pipeName=null)
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new(pipeName ?? PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                _ = Serve(pipe, offer, finish, stop);
                pipe = null;
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { Audit.Write("approval-pipe-unavailable"); await Task.Delay(500).ConfigureAwait(false); }
            finally { pipe?.Dispose(); }
        }
    }

    static async Task Serve(NamedPipeServerStream pipe, Action<OfficialApproval> offer, Action<OfficialApproval,string> finish, CancellationToken stop)
    {
        using var connection = pipe;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        OfficialApproval? request = null;
        string outcome = "pass";
        try
        {
            if (!SameProgram(pipe, true)) return;
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var reader = new StreamReader(pipe, Utf8, leaveOpen:true);
            string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line == null || JsonSerializer.Deserialize<ApprovalDetails>(line) is not {} detail) return;
            request = new(detail);
            timeout.CancelAfter(TimeSpan.FromSeconds(WaitSeconds));
            using var cancel = timeout.Token.Register(() => request.Respond("pass"));
            var receipt = reader.ReadLineAsync(timeout.Token).AsTask();
            offer(request);
            if (await Task.WhenAny((Task)request.answer.Task, receipt).ConfigureAwait(false) == receipt) return;
            string selected = await request.answer.Task.ConfigureAwait(false);
            await WriteLine(pipe,selected,timeout.Token).ConfigureAwait(false);
            if (await receipt.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false) == "received") outcome = selected;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or OperationCanceledException or
            TimeoutException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or JsonException) { }
        finally
        {
            if (request != null) { request.Respond("pass"); finish(request, outcome); }
        }
    }
}
