using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloatNotify;

internal static class DiagnosticReport
{
    internal static string SourceName(string value) => value switch {
        "desktop" or "notification"=>"桌面日志", "session"=>"会话记录", "native"=>"原生审批表单", "hook"=>"Codex 官方审批 Hook", _=>"未记录"
    };
    internal static string MaterialName(string value) => value switch {
        "Liquid"=>"液态玻璃 · 局部像素折射", "LiquidSystem"=>"液态玻璃 · 系统合成", "Frosted"=>"毛玻璃", _=>"未记录"
    };

    public static string HookRegistration(string home,string executable,string eventName="Stop")=>InspectHook(home,executable,eventName).Text;
    internal static (bool? Registered,string Text) InspectHook(string home,string executable,string eventName)
    {
        string file=Path.Combine(home,"hooks.json");
        string expected="& '"+executable.Replace("'","''")+"' --hook";
        try
        {
            if (!File.Exists(file)) return (false,"未找到 hooks.json");
            using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var doc=JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind!=JsonValueKind.Object || !doc.RootElement.TryGetProperty("hooks",out var hooks) ||
                hooks.ValueKind!=JsonValueKind.Object || !hooks.TryGetProperty(eventName,out var groups) || groups.ValueKind!=JsonValueKind.Array)
                return (false,"未注册");
            bool otherPath=false;
            int i=-1;
            foreach (var group in groups.EnumerateArray())
            {
                i++;
                if (group.ValueKind!=JsonValueKind.Object || !group.TryGetProperty("hooks",out var entries) || entries.ValueKind!=JsonValueKind.Array) continue;
                int j=-1;
                foreach (var entry in entries.EnumerateArray())
                {
                    j++;
                    if (entry.Str("type")!="command") continue;
                    string command=entry.Str("commandWindows");
                    if (command.Length==0) command=entry.Str("command");
                    if (string.Equals(command.Trim(),expected,StringComparison.OrdinalIgnoreCase)){
                        var trust=TrustRecord(home,$"{file}:{(eventName=="PermissionRequest"?"permission_request":"stop")}:{i}:{j}",out bool disabled);
                        return (!disabled,$"已注册，路径匹配 · {trust}");
                    }
                    if (command.Contains("FloatNotify.exe",StringComparison.OrdinalIgnoreCase) && command.Contains("--hook"))
                        otherPath=true;
                }
            }
            return (false,otherPath?"发现其他 FloatNotify 命令，未匹配本安装路径":"未注册");
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException)
        { return (null,"无法读取注册配置（"+ex.GetType().Name+"）"); }
    }

    static string TrustRecord(string home,string id,out bool disabled)
    {
        // The presence of a saved fingerprint does not verify the content of today's command.
        disabled=false;
        try
        {
            bool active=false, saved=false;
            foreach (string raw in File.ReadLines(Path.Combine(home,"config.toml")))
            {
                string line=raw.Trim();
                if (line.StartsWith('['))
                {
                    if (active) break;
                    var header=Regex.Match(line, """^\[hooks\.state\.(?<key>'[^']*'|"(?:\\.|[^"\\])*")\]\s*(?:#.*)?$""");
                    if (!header.Success) continue;
                    string key=header.Groups["key"].Value;
                    string? value=key[0]=='\'' ? key[1..^1] : JsonSerializer.Deserialize<string>(key);
                    active=string.Equals(value,id,StringComparison.OrdinalIgnoreCase);
                }
                else if (active)
                {
                    saved |= Regex.IsMatch(line,"""^trusted_hash\s*=\s*["']sha256:[a-fA-F0-9]{64}["']\s*(?:#.*)?$""");
                    disabled |= Regex.IsMatch(line,@"^enabled\s*=\s*false\s*(?:#.*)?$");
                }
            }
            return disabled ? "条目已停用" : saved ? "有信任记录（未核验当前内容指纹）" : "未找到此条目的信任记录";
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException) { return "信任状态未核实"; }
    }

    internal static (string Popup,string Issue) ReadAudit(string folder)
    {
        string popup="现存日志中未记录真实弹窗", issue="现存日志中未记录监听或历史写入异常";
        var lastPopup=DateTimeOffset.MinValue;
        foreach (string name in new[]{"events.log.1","events.log"})
        {
            try
            {
                using var stream=new FileStream(Path.Combine(folder,name),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var reader=new StreamReader(stream);
                while (reader.ReadLine() is string line)
                {
                    var parts=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length<2 || !DateTimeOffset.TryParse(parts[0],out var at)) continue;
                    string Field(string k)=>parts.FirstOrDefault(p=>p.StartsWith(k+"=",StringComparison.Ordinal))?[(k.Length+1)..]??"";
                    string stamp=at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    string stage=parts[1] switch {
                        "history-write-failed"=>"写入提醒历史","monitor-retry"=>"监听目录扫描","monitor-error"=>Field("stage"),_=>""
                    };
                    if (stage.Length>0) { issue=(stamp+" · "+stage+" "+Field("error")).TrimEnd(); continue; }
                    string kind=parts[1] switch {"done-shown"=>"回复已完成","permission-shown"=>"请求审批","question-shown"=>"需要你的回答",_=>""};
                    if (kind.Length==0 || Field("source") is not ("desktop" or "native" or "session" or "notification" or "hook")) continue;
                    var shown=DateTimeOffset.TryParse(Field("shownAt"),out var t)?t:at;
                    if (shown<lastPopup) continue;
                    lastPopup=shown;
                    stamp=shown.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    string renderer=Field("renderer") switch {"pixels"=>"局部像素折射","system"=>"系统合成","tint"=>"静态底色",_=>"未记录"};
                    string position=Field("centered").ToLowerInvariant() switch {"true"=>"当前屏幕工作区中央","false"=>Field("face")!=""?"多卡排列":"未居中",_=>"未记录"};
                    string focus=Field("keyboardWindow") switch {"unchanged"=>"保持不变","changed"=>"已变化",_=>"未记录"};
                    var lines=new List<string>{
                        $"弹出时间：{stamp}",
                        $"提醒类型：{kind}（来源：{SourceName(Field("source"))}）",
                        $"弹出位置：{position}",
                        $"键盘焦点：{focus}"
                    };
                    if (kind=="回复已完成")
                        lines.Add("对话名核对："+(Field("named").Equals("true",StringComparison.OrdinalIgnoreCase)?"匹配":"未确认"));
                    lines.Add($"弹出时的设定材质：{MaterialName(Field("material"))}");
                    lines.Add($"实际渲染：{renderer}");
                    popup=string.Join(Environment.NewLine,lines);
                }
            }
            catch(FileNotFoundException) { }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            { issue=$"读取 {name} 失败（{ex.GetType().Name}），摘要可能不完整"; }
        }
        return (popup,issue);
    }
}
