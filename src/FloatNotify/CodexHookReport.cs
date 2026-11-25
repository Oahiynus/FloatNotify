using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace FloatNotify;

internal static class CodexHookReport
{
    // Use the desktop package's own reader so trust refers to the current hook definition.
    static string? FindCli()
    {
        const string repository=@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        using var packages=Registry.CurrentUser.OpenSubKey(repository);
        if(packages!=null)
        {
            var candidates=packages.GetSubKeyNames()
                .Where(n=>n.StartsWith("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase) && n.EndsWith("__2p2nqsd0c76g0",StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n=>Version.TryParse(n.Split('_')[1],out var v)?v:null);
            foreach(var name in candidates)
            {
                using var package=packages.OpenSubKey(name);
                if(package?.GetValue("PackageRootFolder") is not string root || !Path.IsPathFullyQualified(root) ||
                    !Path.GetFileName(root).Equals(name,StringComparison.OrdinalIgnoreCase))continue;
                string cli=Path.Combine(root,"app","resources","codex.exe");
                if(File.Exists(cli))return cli;
            }
        }
        string local=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Codex","resources","codex.exe");
        return File.Exists(local)?local:null;
    }

    public static async Task<string> ReadAsync(string home,string executable)
    {
        bool exists=Directory.Exists(home);
        string directory="Codex 目录："+(exists?"已找到":"未找到或不可访问");
        string localBindings()=>$"浮笺 PermissionRequest：{DiagnosticReport.HookRegistration(home,executable,"PermissionRequest")}\n浮笺 Stop：{DiagnosticReport.HookRegistration(home,executable)}";
        string unavailable(string reason)=>$"{directory}\nHooks 功能：未核实\nHook 事件：未取得清单（{reason}）\nHook 信任：未核实\n{localBindings()}";
        if(!exists)return unavailable("Codex 目录不可用");
        try
        {
            string? cli=FindCli();
            if(cli==null)return unavailable("未找到桌面版 Codex 诊断接口");
            var info=new ProcessStartInfo(cli){
                WorkingDirectory=home,UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8
            };
            info.ArgumentList.Add("app-server");info.ArgumentList.Add("--stdio");
            info.Environment["CODEX_HOME"]=home;
            using var p=Process.Start(info)??throw new InvalidOperationException();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var token=timeout.Token;
            try
            {
                p.BeginErrorReadLine();
                async Task<JsonElement> Request(int id,string method,object parameters)
                {
                    await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{id,method,@params=parameters}).AsMemory(),token);
                    while(await p.StandardOutput.ReadLineAsync(token) is {} line)
                    {
                        using var doc=JsonDocument.Parse(line);
                        var root=doc.RootElement;
                        if(!root.TryGetProperty("id",out var key) || key.ValueKind!=JsonValueKind.Number || !key.TryGetInt32(out var response) || response!=id)continue;
                        return root.TryGetProperty("result",out var result)?result.Clone():default;
                    }
                    throw new EndOfStreamException();
                }
                var init=await Request(1,"initialize",new{clientInfo=new{name="floatnotify_diagnostics",version=Application.ProductVersion},capabilities=new{experimentalApi=true}});
                if(init.ValueKind!=JsonValueKind.Object)return unavailable("接口初始化失败");
                await p.StandardInput.WriteLineAsync("""{"method":"initialized"}""".AsMemory(),token);
                var inventory=await Request(2,"hooks/list",new{cwds=new[]{home}});
                var config=await Request(3,"config/read",new{cwd=home,includeLayers=false});
                var features=await Request(4,"experimentalFeature/list",new{limit=500});
                string items=FormatInventory(inventory,home,executable) ??
                    "Hook 事件：未取得清单\nHook 信任：未核实\n"+localBindings();
                return $"{directory}\nHooks 功能：{FeatureSetting(config,features)}\n{items}";
            }
            finally
            {
                // Closing stdin ends only the diagnostic process created for this copy.
                p.StandardInput.Close();
                using var grace=new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                try {await p.WaitForExitAsync(grace.Token);}
                catch(OperationCanceledException){if(!p.HasExited)p.Kill();}
            }
        }
        catch(OperationCanceledException){return unavailable("读取超时");}
        catch(Exception ex) when(ex is IOException or Win32Exception or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or JsonException)
        {return unavailable("接口读取失败，"+ex.GetType().Name);}
    }

    static JsonElement Property(JsonElement item,string name)=>
        item.ValueKind==JsonValueKind.Object && item.TryGetProperty(name,out var value)?value:default;
    internal static string FeatureSetting(JsonElement config,JsonElement inventory)
    {
        var settings=Property(config,"config");
        var flags=Property(settings,"features");
        var flag=Property(flags,"hooks");
        if(flag.ValueKind==JsonValueKind.Undefined)flag=Property(flags,"codex_hooks");
        string setting=flag.ValueKind switch{
            JsonValueKind.True=>"显式开启（true）",JsonValueKind.False=>"显式关闭（false）",
            JsonValueKind.Undefined when settings.ValueKind==JsonValueKind.Object=>"未显式设置（unset）",_=>"配置未核实"
        };
        var features=Property(inventory,"data");
        if(features.ValueKind==JsonValueKind.Array)
            foreach(var item in features.EnumerateArray())
                if(item.Str("name")=="hooks")
                    return setting+(Property(item,"enabled").ValueKind switch {
                        JsonValueKind.True=>" · 当前已开启",JsonValueKind.False=>" · 当前已关闭",_=>""
                    });
        return setting+" · 当前状态未核实";
    }

    sealed record Item(string Event,string Status,string Trust,string Command);
    internal static string? FormatInventory(JsonElement result,string home,string executable)
    {
        var data=Property(result,"data");
        if(data.ValueKind!=JsonValueKind.Array)return null;
        var layer=data.EnumerateArray().FirstOrDefault(d=>string.Equals(d.Str("cwd"),home,StringComparison.OrdinalIgnoreCase));
        var hooks=Property(layer,"hooks");
        if(hooks.ValueKind!=JsonValueKind.Array)return null;
        var items=new List<Item>();
        foreach(var hook in hooks.EnumerateArray())
        {
            if(hook.Str("source")!="user" || hook.Str("eventName") is not {Length:>0} name)continue;
            name=char.ToUpperInvariant(name[0])+name[1..];
            string status=Property(hook,"enabled").ValueKind switch {JsonValueKind.True=>"已注册",JsonValueKind.False=>"条目已停用",_=>"启用状态未知"};
            string trust=hook.Str("trustStatus") switch { "trusted"=>"trusted","untrusted"=>"untrusted","modified"=>"modified","managed"=>"managed",_=>"未核实" };
            items.Add(new(name,status,trust,hook.Str("command")));
        }
        bool partial=new[]{"errors","warnings"}.Any(k=>Property(layer,k) is {ValueKind:JsonValueKind.Array} a && a.GetArrayLength()>0);
        string Summary(Func<Item,string> describe)
        {
            var groups=items.GroupBy(h=>h.Event).OrderBy(g=>g.Key,StringComparer.Ordinal);
            var values=groups.Select(g=>g.Key+"="+string.Join(" / ",g.GroupBy(describe).Select(v=>v.Key+(v.Count()>1?$" ×{v.Count()}":""))));
            return items.Count>0?string.Join("，",values):partial?"清单不完整":"无用户级条目";
        }
        string binding(string eventName)
        {
            var handlers=items.Where(h=>h.Event==eventName).ToArray();
            string command="& '"+executable.Replace("'","''")+"' --hook";
            var own=handlers.Where(h=>string.Equals(h.Command.Trim(),command,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(own.Length>0)return "本安装路径匹配 · "+string.Join("，",own.Select(h=>h.Status+" / "+h.Trust));
            if(handlers.Any(h=>h.Command.Contains("FloatNotify.exe",StringComparison.OrdinalIgnoreCase)&&h.Command.Contains("--hook")))
                return "发现其他 FloatNotify 命令，未匹配本安装路径";
            return partial?"清单不完整，未确认":"未在清单中找到";
        }
        return $"Hook 事件（用户级）：{Summary(h=>h.Status)}\nHook 信任（用户级）：{Summary(h=>h.Trust)}\n浮笺 PermissionRequest：{binding("PermissionRequest")}\n浮笺 Stop：{binding("Stop")}"+
            (partial?"\n清单说明：Codex 返回了加载提示或错误，以上仅列出已读取的条目。":"");
    }
}
