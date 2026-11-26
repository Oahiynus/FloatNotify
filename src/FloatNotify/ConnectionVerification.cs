using System.Text.Json;
using static FloatNotify.DiagnosticReport;

namespace FloatNotify;

internal sealed record ConnectionState(string Kind,string Label,string Detail);

internal sealed class ConnectionVerification(string home,string folder)
{
    // Bump only the affected revision when the event/handler contract changes.
    const string ApprovalRevision="permission-request/1", QuestionRevision="desktop-question/1", DoneRevision="stop-and-completion/1";
    readonly string scope=Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)).ToUpperInvariant();
    readonly string file=Path.Combine(folder,"connection.json");
    internal enum Signal { Approval, Question, Completion, Stop }
    internal sealed record Receipt(string Signature="",DateTimeOffset? Event=null,DateTimeOffset? Stop=null,string Reason="");
    sealed record Saved(Receipt Approval,Receipt Question,Receipt Done);
    Saved? saved;
    bool dirty;

    internal static Signal? FromNotice(Notice notice)=>(notice.Kind,notice.Source) switch{
        (Kind.Permission,"hook") when notice.Approval!=null=>Signal.Approval,
        (Kind.Question,"session" or "desktop" or "notification")=>Signal.Question,
        (Kind.Done,"session" or "desktop")=>Signal.Completion,
        _=>null
    };

    public ConnectionState Update(Hook approval,Hook stop,bool listening,Signal? signal=null)
    {
        try{
            if(saved==null){
                try{saved=JsonSerializer.Deserialize<Saved>(File.ReadAllText(file),LocalData.Json);}
                catch(FileNotFoundException){}
                catch(DirectoryNotFoundException){}
                catch(JsonException){Audit.Write("connection-record-invalid");}
                saved??=new(new(),new(),new());
            }
            string signature(string revision,Hook? hook=null)=>scope+"|"+revision+"|"+hook?.Definition;
            var question=new Hook(listening?true:null,listening?"会话或桌面日志监听已挂接":"等待会话或桌面日志监听挂接");
            var next=new Saved(
                Observe(saved.Approval??new(),approval,signature(ApprovalRevision,approval),signal==Signal.Approval),
                Observe(saved.Question??new(),question,signature(QuestionRevision),signal==Signal.Question),
                Observe(saved.Done??new(),stop,signature(DoneRevision,stop),signal==Signal.Completion,signal==Signal.Stop));
            dirty|=saved!=next;
            saved=next;
            if(dirty){
                Directory.CreateDirectory(folder);
                File.WriteAllText(file+".tmp",JsonSerializer.Serialize(next,LocalData.Json));
                File.Move(file+".tmp",file,true);
                dirty=false;
            }
            bool verified(Receipt r,Hook h,bool needsStop=false)=>h.Ready==true && r.Event!=null && (!needsStop || r.Stop!=null);
            int count=(verified(next.Approval,approval)?1:0)+(verified(next.Question,question)?1:0)+(verified(next.Done,stop,true)?1:0);
            string line(string name,Receipt r,Hook h,bool needsStop=false){
                if(h.Ready!=true)return name+" · "+h.Text;
                if(verified(r,h,needsStop))return name+" · 已验证 "+(needsStop && r.Stop>r.Event?r.Stop:r.Event)!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                var wait=needsStop && r.Event!=null?"等待真实 Stop Hook":needsStop && r.Stop!=null?"已收到 Stop，等待真实完成提醒":"等待真实提醒";
                return name+" · "+(r.Reason==""?"":r.Reason+"，")+wait;
            }
            string detail=string.Join("\n",line("审批",next.Approval,approval),line("问答",next.Question,question),line("完成",next.Done,stop,true));
            string label=approval.Ready==false && stop.Ready==false?"待配置":
                approval.Ready==false || stop.Ready==false?"部分配置":
                approval.Ready==null || stop.Ready==null?"配置未核实":
                count==3?"已接入":count>0?"部分已验证":"待验证";
            return new(count==3?"on":"idle",label,detail);
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
            return new("idle","验证记录不可用","无法读写本地接入记录 connection.json（"+ex.GetType().Name+"）");
        }
    }

    static Receipt Observe(Receipt prior,Hook health,string signature,bool received,bool stop=false)
    {
        if(string.IsNullOrEmpty(prior.Signature) && (prior.Event!=null || prior.Stop!=null))
            prior=new(Reason:"旧记录缺少对应的接入定义");
        if(health.Ready==null)return prior;
        if(health.Ready==false)return string.IsNullOrEmpty(prior.Signature)?prior:new(Reason:"上次检查："+health.Text);
        if(!string.IsNullOrEmpty(prior.Signature) && prior.Signature!=signature)
            prior=new(Reason:prior.Signature[..(prior.Signature.LastIndexOf('|')+1)]==signature[..(signature.LastIndexOf('|')+1)]
                ?"Hook 定义或信任记录已变化":"接入目录或事件处理版本已变化");
        if(!received && !stop)return prior;
        var now=DateTimeOffset.UtcNow;
        return prior with{Signature=signature,Event=prior.Event??(received?now:null),Stop=prior.Stop??(stop?now:null)};
    }
}
