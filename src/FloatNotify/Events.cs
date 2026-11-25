using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloatNotify;

internal enum Kind { Done, Question, Permission, CheckApproval, Answered, TurnEnded, RequestAnswered }
internal sealed record Prompt(string Title, string[] Options);
internal sealed record RequestRef(string Source,string Id,string Turn,bool Async,string? CallId=null)
{
    internal string? QuestionCall => Source is "session" or "notification" ? Id : CallId;
}
internal sealed record Notice(Kind Kind, Guid Chat, string Id, string Body = "", string Turn = "", string Source = "", bool Async = false, Prompt[]? Prompts = null, string NativeForm = "", string Client = "ChatGPT", OfficialApproval? Approval = null,string? CallId=null)
{
    public bool Urgent => Kind is Kind.Question or Kind.Permission;
    public RequestRef Reference => new(Source,Id,Turn,Async,CallId);

    public bool Resolves(Guid chat, IReadOnlyCollection<RequestRef> requests)
    {
        if(Kind==Kind.RequestAnswered)
            return requests.Any(r=>r.Source=="desktop" && r.Id==Id &&
                requests.All(other=>other.Source!="session" || other.Id==r.CallId));
        if(Chat!=chat)return false;
        if(Kind==Kind.Answered)return requests.Any(r=>r.Id==Id || r.CallId==Id);
        if(Kind!=Kind.TurnEnded || Turn.Length==0 || requests.Count==0)return false;
        if(Body!="turn_aborted" && requests.Any(r=>r.Async))return false;
        return requests.Any(r=>r.Turn==Turn);
    }
}
internal static class Events
{
    public static string Str(this JsonElement x, string key) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
    // Electron main-process log, restricted to the local desktop producer.
    static readonly Regex Field = new(@"(?:^| )(?<key>conversationId|turnId|requestId|callId|kind|method|id|hostId)=(?<value>[^\s,]+)", RegexOptions.Compiled);
    // The notification service includes the exact tool-call ID even when the
    // Electron show event has no server request ID. Each question index shares it.
    static Notice? ForwardedQuestion(string payload)
    {
        if(payload.Length>8192)return null;
        try{
            var reader=new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(payload));
            using var outer=JsonDocument.ParseValue(ref reader);
            var a=outer.RootElement;
            if(a.ValueKind!=JsonValueKind.Array || a.GetArrayLength()!=6 || !a[2].ValueEquals("local") ||
                !Guid.TryParse(a[3].GetString(),out var chat) || !Guid.TryParse(a[4].GetString(),out var turn))return null;
            using var inner=JsonDocument.Parse(a[5].GetString()??"");
            var item=inner.RootElement;
            if(item.ValueKind!=JsonValueKind.Array || item.GetArrayLength()!=3 || !item[0].ValueEquals("request_user_input_async") ||
                item[1].GetString() is not {Length:>0} call || !item[2].TryGetInt32(out var index) || index<0)return null;
            return new(Kind.Question,chat,call,Turn:turn.ToString(),Source:"notification",Async:true);
        }catch(Exception ex) when(ex is JsonException or InvalidOperationException){return null;}
    }
    public static Notice? Desktop(string line)
    {
        if (line.Length < 35 || !char.IsDigit(line[0])) return null;
        int marker = line.IndexOf(" info [", StringComparison.Ordinal);
        if (marker is < 19 or > 35) return null;
        string text = line[(marker + 6)..];
        const string head = "[electron-message-handler] [desktop-notifications] ";
        const string reveal = "[electron-message-handler] [windows-review-request] reveal requested ";
        const string response = "[host-app-server-manager] Sending server response ";
        const string forwarded="[notifications-service] [desktop-notifications] forward show kind=question notificationId=async-question-";
        if(text.StartsWith(forwarded,StringComparison.Ordinal))return ForwardedQuestion(text[forwarded.Length..]);
        if (!(text.StartsWith(head) || text.StartsWith(reveal) || text.StartsWith(response))) return null;
        int payload = text.IndexOf(" response=", StringComparison.Ordinal);
        var fields = new Dictionary<string,string>();
        foreach (Match m in Field.Matches(payload < 0 ? text : text[..payload]))
            fields.TryAdd(m.Groups["key"].Value, m.Groups["value"].Value);
        string F(string k) => fields.GetValueOrDefault(k) switch { null or "null" or "undefined"=>"",var value=>value };
        if (text.StartsWith(response) && IsInteractive(F("method")) && F("id") != "")
            return new(Kind.RequestAnswered, Guid.Empty, F("id"), Source:"desktop");
        if (!Guid.TryParse(F("conversationId"), out var chat)) return null;
        string req=F("requestId"), turn=F("turnId");
        if (text.StartsWith(head+"received turn-complete ") && Guid.TryParse(turn, out _))
            return new(Kind.Done, chat, turn, Turn:turn, Source:"desktop");
        if (req == "") return null;
        if (text.StartsWith(head+"received approval ") || (text.StartsWith(head+"show notification ") && F("kind")=="permission") ||
            (text.StartsWith(reveal) && F("hostId")=="local" && IsInteractive(F("method")) && F("method")!="item/tool/requestUserInput"))
            return new(Kind.Permission,chat,req,Turn:turn,Source:"desktop");
        if (text.StartsWith(head+"received question ") || (text.StartsWith(head+"show notification ") && F("kind")=="question") ||
            (text.StartsWith(reveal) && F("method")=="item/tool/requestUserInput"))
            return new(Kind.Question,chat,req,Turn:turn,Source:"desktop",CallId:
                F("callId") is {Length:>0} call ? call : req.StartsWith("call_",StringComparison.Ordinal) ? req : null);
        return null;
    }
    static bool IsInteractive(string method) => method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval"
        or "item/permissions/requestApproval" or "mcpServer/elicitation/request" or "item/tool/requestUserInput";
}

internal sealed class Session
{
    public Guid Chat;
    public string Turn = "";
    public bool DesktopUser;
    readonly Dictionary<string,(string Turn,bool Async,int Count,HashSet<int> Answers)> questions = new();
    public bool Identify(JsonElement row)
    {
        if (row.Str("type")!="session_meta" || !row.TryGetProperty("payload",out var data)) return false;
        string origin = data.Str("originator"), source = data.Str("thread_source");
        DesktopUser = Guid.TryParse(data.Str("id"),out Chat) && origin.Equals("Codex Desktop",StringComparison.OrdinalIgnoreCase)
            && (source is "" or "user" || source=="agent_created_thread" && data.Str("source")=="vscode")
            && !(data.TryGetProperty("source",out var s) && s.ValueKind==JsonValueKind.Object);
        return true;
    }
    public IEnumerable<Notice> Read(JsonElement row)
    {
        if (Identify(row) || !DesktopUser || !row.TryGetProperty("payload",out var data)) yield break;
        string record=row.Str("type"), type=data.Str("type"), id=data.Str("call_id");
        if (record=="event_msg")
        {
            if (type=="task_started") { Turn=data.Str("turn_id"); }
            if (type is "task_complete" or "turn_aborted")
            {
                string turn=data.Str("turn_id");
                if (turn=="") turn=Turn;
                if (turn!="")
                {
                    yield return new(Kind.TurnEnded, Chat, turn, type, turn,"session");
                    if(type=="task_complete") yield return new(Kind.Done,Chat,turn,Turn:turn,Source:"session");
                }
                if(turn!="")
                    foreach(var key in questions.Where(p=>p.Value.Turn==turn && (!p.Value.Async || type=="turn_aborted")).Select(p=>p.Key).ToArray())questions.Remove(key);
                if(Turn==turn)Turn="";
            }
            yield break;
        }
        if (record!="response_item") yield break;
        // A tool call only wakes the native form reader. It cannot establish that human approval is needed.
        string tool=data.Str("name").Split('.').Last();
        if (id!="" && (type=="custom_tool_call" && tool=="exec" && data.Str("input").Contains("require_escalated",StringComparison.Ordinal)
            || type=="function_call" && tool=="exec_command" && data.Str("arguments").Contains("require_escalated",StringComparison.Ordinal)))
            yield return new(Kind.CheckApproval,Chat,id,Turn:Turn,Source:"session");
        if (type=="function_call" && id!="" && tool is "request_user_input" or "request_user_input_async")
        {
            bool async=tool=="request_user_input_async";
            using var args=JsonDocument.Parse(data.Str("arguments"));
            if (!args.RootElement.TryGetProperty("questions",out var list) || list.ValueKind!=JsonValueKind.Array || list.GetArrayLength()==0) yield break;
            var prompts=list.EnumerateArray().Select(item=>new Prompt(item.Str(async?"title":"question"),
                item.TryGetProperty("options",out var opts) && opts.ValueKind==JsonValueKind.Array
                    ? opts.EnumerateArray().Select(o=>o.ValueKind==JsonValueKind.String?o.GetString()!:o.Str("label")).Where(s=>s.Length>0).ToArray()
                    : Array.Empty<string>())).ToArray();
            questions[id]=(Turn,async,list.GetArrayLength(),new());
            yield return new(Kind.Question,Chat,id,prompts[0].Title,Turn,"session",async,prompts);
        }
        else if(type=="function_call_output" && questions.TryGetValue(id,out var q))
        {
            // Async acknowledgment means the question is on screen, not answered.
            bool ended=!q.Async;
            if(q.Async && data.Str("output").StartsWith("{"))
            {
                using var result=JsonDocument.Parse(data.Str("output"));
                ended=result.RootElement.TryGetProperty("accepted",out var accepted) && accepted.ValueKind==JsonValueKind.False;
            }
            if (ended) {questions.Remove(id); yield return new(Kind.Answered,Chat,id,Turn:Turn,Source:"session");}
        }
        else if(type=="message" && data.Str("role")=="user" && data.TryGetProperty("content",out var parts) && parts.ValueKind==JsonValueKind.Array)
        {
            foreach(var part in parts.EnumerateArray())
            {
                string msg=part.Str("text").Trim();
                const string start="<send_user_message_question_reply>", end="</send_user_message_question_reply>";
                if(!msg.StartsWith(start) || !msg.EndsWith(end)) continue;
                using var answers=JsonDocument.Parse(msg[start.Length..^end.Length]);
                if(answers.RootElement.ValueKind!=JsonValueKind.Array) continue;
                foreach(var answer in answers.RootElement.EnumerateArray())
                {
                    if(!answer.Str("questionItemId").StartsWith("[")) continue;
                    using var key=JsonDocument.Parse(answer.Str("questionItemId"));
                    var v=key.RootElement;
                    if(v.ValueKind!=JsonValueKind.Array||v.GetArrayLength()!=3||v[0].ToString()!="request_user_input_async") continue;
                    string call=v[1].ToString();
                    if(!questions.TryGetValue(call,out var pending)||!v[2].TryGetInt32(out int index)||index<0||index>=pending.Count) continue;
                    pending.Answers.Add(index);
                    if(pending.Answers.Count==pending.Count){questions.Remove(call);yield return new(Kind.Answered,Chat,call,"reply",Turn,"session");}
                }
            }
        }
    }
}
