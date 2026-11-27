const $=s=>document.querySelector(s), $$=s=>[...document.querySelectorAll(s)];

const symbols={recent:0xe81c,appearance:0xe790,general:0xe713,done:0xe73e,question:0xe8f2,approval:0xe72e,chev:0xe70d,folder:0xe8b7,copy:0xe8c8};
const icon=k=>'<span aria-hidden="true" class="icon">'+String.fromCodePoint(symbols[k])+'</span>';
const clients={
  "codex": {
    "full": "Codex",
    "ready": true,
    "image": "codex.png"
  },
  "claude": {
    "full": "Claude Code",
    "ready": false,
    "image": "claude.png"
  },
  "deepseek": {
    "full": "DeepSeek Harness",
    "ready": false,
    "image": "deepseek.svg"
  },
  "gemini": {
    "full": "Antigravity",
    "ready": false,
    "image": "gemini.png"
  }
};
const labels={done:'回复已完成',question:'需要你的回答',approval:'请求审批'};
function harnessBadge(id, style='harnessIcon') {
 const c=clients[id];
 return '<span data-brand="'+id+'" class="'+style+'"><img src="'+c.image+'" alt=""></span>';
}
const pages={general:'通用设置',recent:'最近提醒',appearance:'弹窗外观'};

const sel=(val,active)=>val===active?'true':'false';
function connection(id) {
 if(!clients[id].ready) return {kind:'idle',label:'待适配'};
 return state.connection??{kind:'idle',label:'正在核实'};
}
function statusDot(status) {
 return '<span aria-hidden="true" class="dot" data-status="'+status.kind+'"></span>';
}
function sidebar(){
 $('#nav').innerHTML=Object.entries(pages).map(([key,title])=>'<button data-page="'+key+'" '+(state.page===key?'aria-current="page"':'')+'>'+icon(key)+title+'</button>').join('');
 $('#clients').innerHTML=Object.entries(clients).filter(([id])=>state.visible[id]).map(([id,c])=>{
   const status=connection(id);
   return '<button data-page="'+id+'" aria-label="'+c.full+'，'+status.label+'" title="'+status.label+'" '+(state.page===id?'aria-current="page"':'')+'>'+harnessBadge(id,'avatar')+c.full+statusDot(status)+'</button>';
 }).join('');
 $('#group').hidden=!Object.values(state.visible).some(Boolean);
}
const button=(key,label,which='')=>'<button class="button '+which+'" data-act="'+key+'">'+label+'</button>';
function head(title,caption,action=''){return '<header class="pagehead"><div><h1>'+title+'</h1><p>'+caption+'</p></div>'+action+'</header>'}
const segment=(group,items,value)=>'<div class="seg'+(group==='kind'?' previewModes':'')+'" role="group" aria-label="'+({filter:'提醒类型',kind:'预览卡片',mat:'卡片材质',renderer:'折射方式'}[group])+'">'+Object.entries(items).map(([id,label])=>'<button data-field="'+group+'" data-val="'+id+'" aria-pressed="'+sel(id,value)+'">'+(group==='kind'?icon(id):'')+label+'</button>').join('')+'</div>';
const section=title=>'<div class="sectionhead"><h2>'+title+'</h2></div>';
const row=(title,desc,control)=>'<div class="row"><div><strong>'+title+'</strong>'+(desc?'<p>'+desc+'</p>':'')+'</div>'+control+'</div>';
const toggle=(key,name,disabled=false)=>'<button aria-label="'+name+'" role="switch" class="switch" aria-checked="'+!!state.opts[key]+'" data-toggle="'+key+'" '+(disabled?'disabled':'')+'></button>';
function appearance(){
 return head('弹窗外观','在下方预览材质和背景，或在桌面上试弹。')+
 '<div class="surface appearanceSettings">'+row('卡片材质','',segment('mat',{rim:'液态玻璃',tonal:'明暗液态玻璃',adaptive:'自适应玻璃',soft:'毛玻璃'},state.mat))+'<div id="renderrow" '+(state.mat!=='rim'?'hidden':'')+'>'+row('折射方式','',segment('renderer',{pixels:'局部像素折射',system:'系统合成'},state.renderer))+'</div></div>'+
 '<section class="previewPanel" aria-label="卡片预览"><div class="sectionhead previewHead"><h2>卡片预览</h2>'+segment('kind',{approval:labels.approval,question:labels.question,done:labels.done},state.kind)+'</div>'+
 '<div class="trywrap"><div class="stage"><iframe title="定稿卡片预览"></iframe></div><div class="footbar"><div class="toolbar"><label for="bg">预览背景</label><select id="bg"><option value="silk">彩色桌面</option><option value="paper">浅色文档</option><option value="editor">深色编辑器</option><option value="split">深浅窗口交界</option></select></div>'+button('trial',trialActive?'结束试弹':'3 秒后试弹','primary')+'</div><div class="subnote">点击后设置窗口会最小化，3 秒后卡片会出现在你正在使用的窗口上。</div></div></section>';}
let updateInfo={phase:'idle',message:'',available:false};
function refreshUpdateControls(){
 const check=$('[data-act="checkUpdate"]');
 if(!check)return;
 const busy=updateInfo.phase!=='idle';
 check.disabled=busy;
 check.textContent=updateInfo.phase==='check'?'正在检查…':'检查更新';
 const install=$('[data-act="installUpdate"]'),notes=$('[data-act="openRelease"]');
 install.hidden=notes.hidden=!updateInfo.available;
 install.disabled=notes.disabled=busy;
 install.textContent=updateInfo.phase==='install'?'正在安装…':updateInfo.phase==='download'?'正在下载…':'更新并重启';
 $('.updateStatus').textContent=updateInfo.message;
}
function aboutSection(){
 const {version,windows,runtime,installDir,logDir}=state.about;
 const fields=[['运行环境',windows+' · '+runtime],['安装目录',installDir],['日志目录',logDir]];
 return section('关于')+'<div class="surface aboutCard"><div class="aboutIdentity"><img src="folio.png" width="40" height="40" alt=""><div><strong>浮笺 <span lang="en">FloatNotify</span></strong><p>版本 '+escapeHtml(version)+'</p></div>'+
   '<div class="updateActions">'+button('checkUpdate','检查更新')+'<button class="button primary" data-act="installUpdate" hidden>更新并重启</button><button class="button" data-act="openRelease" hidden>版本说明</button></div>'+
   '<p class="updateStatus" role="status"></p></div>'+
   '<dl class="aboutFields">'+fields.map(([label,value])=>'<div><dt>'+label+'</dt><dd>'+escapeHtml(value)+'</dd></div>').join('')+'</dl>'+
   '<div class="aboutActions">'+button('openLogs',icon('folder')+'打开日志目录')+button('copyDiagnostics',icon('copy')+'复制诊断信息')+'</div></div>';
}
function general(){
 const harnesses=Object.entries(clients).map(([id,c])=>{
   const status=connection(id).label;
   return '<div class="harnessRow">'+harnessBadge(id)+'<div class="harnessBody"><strong>'+c.full+'</strong><small>'+status+'</small></div><button type="button" role="switch" aria-label="在侧栏显示 '+c.full+'" class="switch" data-client="'+id+'" aria-checked="'+state.visible[id]+'"></button></div>';
 }).join('');
 return head('通用设置','管理启动方式、历史记录和客户端入口。')+
 section('系统')+'<div class="surface">'+row('登录 Windows 时启动','启动后驻留系统托盘。',toggle('start','登录 Windows 时启动'))+'</div>'+
 section('历史记录')+'<div class="surface">'+row('记录最近提醒','在本机保留最近 30 天内最多 200 条提醒。',toggle('history','记录最近提醒'))+'</div>'+
 '<div class="sectionhead harnessHeader"><h2>客户端</h2><small>显示在侧栏</small></div><div class="surface">'+harnesses+'</div>'+aboutSection();
}
function client(id){
 const c=clients[id],status=connection(id);
 const badge='<span class="connectionBadge" data-status="'+status.kind+'">'+statusDot(status)+escapeHtml(status.label)+'</span>';
 let content='<header class="clientHead">'+harnessBadge(id,'clientIcon')+'<div class="clientTitle"><h1>'+c.full+'</h1>'+badge+'</div></header>';
 if(!c.ready)return content+section('连接状态')+'<div class="surface connectionState">浮笺暂未适配 '+c.full+'。</div>';
 content+='<div class="surface">'+row('启用 Codex 提醒','开启时，浮笺会在 Codex 中添加审批和完成 Hook，并监听提问。关闭时会移除本机浮笺的两个 Hook。',toggle('codex','启用 Codex 提醒',state.changingCodex))+'</div>';
 if(state.opts.codex || state.retryCodex!=null){
   const retry=state.retryCodex!=null?button('retryCodex','重试'):'';
   content+=section('连接状态')+'<div class="surface connectionState">'+statusDot(status)+'<p>'+escapeHtml(status.summary)+'</p>'+retry+'</div>';
 }
 const disabled=!state.opts.codex || state.changingCodex;
 const hint=state.opts.codex?'':'<p class="reminderHint">开启 Codex 提醒后，即可选择要显示的浮笺卡片。</p>';
 return content+section('提醒类型')+'<div class="surface">'+hint+
   row('请求审批','在浮笺卡片上允许或拒绝工具请求。关闭后，可在 Codex 中处理审批。',toggle('approval','请求审批',disabled))+
   row('需要你的回答','Codex 等待你选择或输入时，显示问答卡片。',toggle('question','需要你的回答',disabled))+
   row('回复已完成','每轮回复完成时，显示带有对话名称的完成卡片。',toggle('done','回复已完成',disabled))+'</div>';
}

const bridge=window.chrome.webview;
const send=(action,key='',value=null)=>bridge.postMessage({action,key,value});
const state={page:'recent',filter:'all',source:'all',kind:'done',mat:'rim',renderer:'pixels',background:'editor',
  visible:{},opts:{},events:[],revision:-1};
const escapeHtml=value=>String(value??'').replace(/[&<>"']/g,ch=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
let initialized=false, trialActive=false,toastTimer=0;
function feedback(message){
 const toast=$('#feedback');toast.textContent=message;toast.hidden=false;clearTimeout(toastTimer);
 toastTimer=setTimeout(()=>{toast.hidden=true;},4500);
}
function eventItem(record,index){
 const {id,type}=record;
 const fields=record.detail.map(([label,value,path])=>'<div><dt>'+escapeHtml(label)+'</dt><dd'+(path?' title="'+escapeHtml(path)+'"':'')+'>'+escapeHtml(value)+'</dd></div>').join('');
 const link=record.canOpen?'<button type="button" class="button'+(record.pending?' primary':'')+'" data-original="'+id+'">'+(record.pending?'回到原题':'打开对话')+'</button>':'';
 return '<details class="event'+(record.pending?' pending':'')+'" data-record="'+id+'" data-type="'+type+'" '+(index===0?'open':'')+'><summary>'+harnessBadge(record.client,'entrySource')+'<div><div class="meta"><span class="clientName">'+clients[record.client].full+'</span><span class="typeName">'+icon(type)+labels[type]+'</span></div><strong>'+escapeHtml(record.title)+'</strong></div><div class="eventRight"><span class="result">'+escapeHtml(record.result)+'</span><time class="mono">'+escapeHtml(record.time)+'</time><span class="chev">'+icon('chev')+'</span></div></summary><div class="detail"><dl>'+fields+'</dl><div class="detailActions">'+link+'<button class="button" data-copy="'+id+'">复制诊断信息</button><span role="status" class="copyStatus"></span></div></div></details>';
}
function recent(){
 const events=state.events.filter(e=>(state.filter==='all'||state.filter===e.type)&&(state.source==='all'||state.source===e.client));
 const groups=new Map();for(const r of events){if(!groups.has(r.date))groups.set(r.date,[]);groups.get(r.date).push(r);}
 let i=0;const list=[...groups].map(([date,rows])=>'<div class="sectionhead"><span class="date">'+escapeHtml(date)+'</span><span class="quiet">'+rows.length+' 条提醒</span></div><div class="events">'+rows.map(r=>eventItem(r,i++)).join('')+'</div>').join('');
 return head('最近提醒','本机最近 30 天的提醒，最多保留 200 条。',state.events.length?button('clear','清空记录'):'')+
 '<div class="filterbar">'+segment('filter',{all:'全部',approval:'审批',question:'问答',done:'完成'},state.filter)+'<select id="source" aria-label="筛选客户端"><option value="all">全部客户端</option>'+Object.entries(clients).map(([key,c])=>'<option value="'+key+'">'+c.full+'</option>').join('')+'</select></div>'+
 (list||'<div class="surface empty quiet">'+(state.events.length?'当前条件下没有提醒。':state.opts.history?'收到提醒后会显示在这里。':'记录已暂停。可在通用设置中开启。')+'</div>');
}
function syncFrame(frame=$('#content .stage iframe')){
 const preview=frame?.contentWindow;
 if(typeof preview?.renderPreview!=='function')return false;
 const {kind,mat,background,renderer}=state;
 preview.renderPreview({kind,mat,background,renderer});
 return true;
}
function appearanceControls(page=$('#content')){
 for(const el of page.querySelectorAll('button[data-field]'))el.setAttribute('aria-pressed',sel(el.dataset.val,state[el.dataset.field]));
 page.querySelector('#renderrow').hidden=state.mat!=='rim';
 return syncFrame(page.querySelector('iframe'));
}
function paint(){
 sidebar();const content=$('#content');
 content.innerHTML=state.page==='recent'?recent():state.page==='general'?general():client(state.page);
 if(state.page==='general')refreshUpdateControls();
 if($('#source'))$('#source').value=state.source;
}
let pendingAppearance;
function openAppearance(){
 const current=$('#content');
 const next=document.createElement('main');
 next.className='main';next.inert=true;
 next.innerHTML=appearance();
 next.querySelector('#bg').value=state.background;
 const frame=next.querySelector('iframe');
 frame.onload=()=>{
   if(pendingAppearance!==next || !appearanceControls(next))return;
   const face=frame.contentDocument.querySelector('.lens-card');
   const fit=()=>{frame.parentElement.style.minHeight=state.kind==='done'?'':(face.offsetHeight+34)+'px';};
   new frame.contentWindow.ResizeObserver(fit).observe(face);fit();
   frame.dataset.ready='';
   next.querySelector('[data-act="trial"]').textContent=trialActive?'结束试弹':'3 秒后试弹';
   // The hidden pane is already laid out in the same grid cell as the outgoing page.
   current.remove();next.id='content';next.inert=false;
   pendingAppearance=null;state.page='appearance';sidebar();
 };
 pendingAppearance=next;frame.src='preview.html';current.after(next);
}
function go(page){
 if(!initialized)return;
 if(!Object.hasOwn(pages,page)&&(!Object.hasOwn(clients,page)||!state.visible[page]))page='recent';
 pendingAppearance?.remove();pendingAppearance=null;
 if(page==='appearance'){
   if(state.page!=='appearance')openAppearance();
   return;
 }
 state.page=page;paint();$('#content').scrollTop=0;
}
bridge.addEventListener('message',({data})=>{
 if(typeof data.maximized==='boolean'){
   const button=$('[data-window="maximize"]'),label=data.maximized?'还原':'最大化';
   $('.topResize').hidden=data.maximized;
   button.title=label;button.setAttribute('aria-label',label);
   button.firstElementChild.textContent=String.fromCodePoint(data.maximized?0xe923:0xe922);
   return;
 }
 if(data.update){updateInfo=data.update;refreshUpdateControls();return;}
 if(data.error){const b=$('[data-act=trial]');if(b)b.disabled=false;feedback(data.error);return;}
 if(data.copied==='application'){feedback('已复制浮笺的应用诊断信息');return;}
 if(data.copied){const note=$('[data-record="'+data.copied+'"] .copyStatus');if(note)note.textContent='已复制这条提醒';return;}
 const configChanged=JSON.stringify(state.opts)!==JSON.stringify(data.opts)||JSON.stringify(state.visible)!==JSON.stringify(data.visible)||JSON.stringify(state.connection)!==JSON.stringify(data.connection)||state.changingCodex!==data.changingCodex||state.retryCodex!==data.retryCodex;
 const historyChanged=state.revision!==data.revision;
 trialActive=!!data.trial;
 state.about=data.about;state.connection=data.connection;state.changingCodex=data.changingCodex;state.retryCodex=data.retryCodex;
 state.opts=data.opts;state.visible=data.visible;state.events=data.events;state.revision=data.revision;
 state.mat=data.material==='Adaptive'?'adaptive':data.material==='TonalLiquid'?'tonal':data.material==='Frosted'?'soft':'rim';
 if(state.mat==='rim')state.renderer=data.material==='LiquidSystem'?'system':'pixels';
 if(!initialized){
   initialized=true;
   if(location.hash==='#about'){
     history.replaceState(null,'','#general');go('general');
     $('.aboutCard')?.scrollIntoView({block:'start'});
   }else go(location.hash.slice(1));
   return;
 }
 if(state.page==='appearance'){
   if(configChanged)sidebar();
   appearanceControls();const b=$('[data-act=trial]');b.disabled=false;b.textContent=trialActive?'结束试弹':'3 秒后试弹';
 }
 else if(configChanged || (historyChanged&&state.page==='recent')){
   const expanded=$$('.event[open]').map(el=>el.dataset.record);
   const preserve=state.page==='recent'&&!!$('.event');
   paint();if(preserve)for(const el of $$('.event'))el.open=expanded.includes(el.dataset.record);
 }
});
$('.topResize').addEventListener('pointerdown',e=>{
 if(e.button!==0 || !e.target.dataset.edge)return;
 e.preventDefault();send('window','resize-'+e.target.dataset.edge);
});
document.addEventListener('click',e=>{
 const b=e.target.closest('button');if(!b||b.disabled)return;
 const d=b.dataset;
 if(d.window){send('window',d.window);return;}
 if(d.page){location.hash=d.page;return;}
 if(d.copy||d.original){send(d.copy?'copy':'open',d.copy||d.original);return;}
 if(d.client){send('visible',d.client,!state.visible[d.client]);return;}
 if(d.toggle){send('toggle',d.toggle,!state.opts[d.toggle]);return;}
 if(d.field){
   if(d.field==='filter'){state.filter=d.val;paint();return;}
   if(d.field==='kind'){state.kind=d.val;appearanceControls();return;}
   let mat=d.field==='mat'?d.val:state.mat,renderer=d.field==='renderer'?d.val:state.renderer;
   send('material',mat==='adaptive'?'Adaptive':mat==='tonal'?'TonalLiquid':mat==='soft'?'Frosted':renderer==='system'?'LiquidSystem':'Liquid');return;
 }
 switch(d.act){
   case 'retryCodex':send('toggle','codex',state.retryCodex);break;
   case 'copyDiagnostics':feedback('正在读取 Codex 的 Hook 状态…');send(d.act);break;
   case 'checkUpdate':case 'installUpdate':case 'openRelease':case 'openLogs':send(d.act);break;
   case 'clear':$('#confirm').showModal();break;
   case 'cancel':$('#confirm').close();break;
   case 'confirm':$('#confirm').close();send('clear');break;
   case 'trial':b.disabled=true;send(trialActive?'endtrial':'trial',state.kind);break;
 }
});
document.addEventListener('change',e=>{
 if(e.target.id==='source'){state.source=e.target.value;paint();}
 if(e.target.id==='bg'){state.background=e.target.value;syncFrame();}
});
window.addEventListener('hashchange',()=>go(location.hash.slice(1)));
send('init');
