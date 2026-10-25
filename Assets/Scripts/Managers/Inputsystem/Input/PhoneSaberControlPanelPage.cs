// 外部リソースを読み込まない。DOM は textContent だけで更新し、状態文字列を HTML として扱わない。
public static class PhoneSaberControlPanelPage
{
    public const string Html = @"<!doctype html>
<html lang='ja'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>PhoneSaber 操作画面</title><style>
:root{color-scheme:dark;font:17px/1.6 system-ui,sans-serif;background:#10131c;color:#edf2fa}
body{margin:0;padding:22px;max-width:1450px;margin:auto}header{position:sticky;top:0;background:#10131cf5;padding:8px 0;z-index:1}
h1{font-size:26px;margin:0}#connection{color:#a9c9ff}#message{min-height:28px;color:#ffd080}
main{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:18px;align-items:start}
section{background:#1b2332;border:1px solid #354259;border-radius:14px;padding:20px}h2{font-size:21px;margin:0 0 14px}
p{white-space:pre-wrap;overflow-wrap:anywhere;margin:10px 0}.warning{color:#ffd080}
.result{flex-basis:100%;margin:4px 8px 12px}
section.current{grid-column:1/-1;border:2px solid #75d9c2}section.current>p{margin:5px 0}
.phone-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px;margin-bottom:12px}
.phone{background:#101b2a;border-radius:10px;padding:14px}.phone h3{margin:0 0 10px;font-size:19px}
.settings{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px 8px}
.setting{display:flex;flex-direction:column}.setting strong{font-size:20px;color:#9bffe0}.setting small{font-size:12px;color:#b3c2d7}
.setting .unknown{color:#ffd080}.setting .stale{color:#ffd080}
section.health{grid-column:1/-1}section.health p{margin:4px 0}
summary{font-size:21px;font-weight:700;cursor:pointer;min-height:52px;color:#ffd080}
.danger{border-color:#c48955}.danger button{border-color:#c48955}
.health-ok{color:#80e3a0}.health-warn{color:#ffd080}.health-bad{color:#ff8c8c}
.controls{display:flex;flex-wrap:wrap;gap:4px;margin:12px 0;padding:4px;background:#111b2a;border-radius:12px}button{font:inherit;font-weight:500;min-height:52px;padding:11px 18px;
border:1px solid #465a77;border-radius:8px;background:#202e43;color:#c4d1e3;cursor:pointer;touch-action:manipulation}
button:hover{background:#355377}button.selected{background:#80f0ce;color:#06291f;border:2px solid #c6ffef;font-weight:850;box-shadow:0 0 0 1px #80f0ce}button:disabled{cursor:default;opacity:.55}button.selected:disabled{opacity:1}button.pending{color:#ffe0a1;opacity:1;border-color:#ffd080}button:focus-visible,summary:focus-visible{outline:3px solid #9fcaff;outline-offset:3px}
@media(max-width:850px){main,.phone-grid{grid-template-columns:1fr}body{padding:12px}.settings{grid-template-columns:repeat(2,minmax(0,1fr))}}
</style><header><h1>PhoneSaber 操作画面</h1><div id='connection' role='status'>接続中…</div>
<div>ゲーム: F8 でブラウザを開く ／ Shift+F8 でゲーム内の予備画面 ／ Ctrl+Shift+P で10秒学習 ／ F7 は詳細の4隅測定</div>
<div id='message' role='status'></div></header><main id='panel'></main><script>
'use strict';
const token=new URLSearchParams(location.search).get('t')||'';
const panel=document.getElementById('panel'), connection=document.getElementById('connection'), message=document.getElementById('message');
let structure='', nodes=[], phoneNodes=[], busy=false, lastPhoneName=null;
const sending=new Map();
const settingLabels={trackLock:'追跡ロック',exposureEv:'露出 EV',shutter:'シャッター',iso:'ISO',colouredEmitter:'色付き発光体',ignoreRed:'無視範囲 赤',ignoreBlue:'無視範囲 青',playArea:'プレイ範囲'};
function url(path, extra={}){return path+'?'+new URLSearchParams({t:token,...extra});}
async function action(item){
 if(busy||!item.enabled||item.pending||sending.has(item.name))return;
 if(item.requiresConfirmation&&!window.confirm('「'+item.text+'」を実行しますか？\n'+(item.name==='judge-mode'?'判定の遅れが大きく変わります。':'保存済みの設定を消します。')))return;
 busy=true;lastPhoneName=item.name.startsWith('phone-')?item.name:null;sending.set(item.name,{value:item.value,base:item.commandId});refreshButtons();
 try{const r=await fetch(url('/action',{name:item.name,value:item.value,confirm:item.requiresConfirmation?'1':'0'}),{method:'POST',cache:'no-store',referrerPolicy:'no-referrer'});
 if(!r.ok)throw Error('HTTP '+r.status);
 if(!item.name.startsWith('phone-'))sending.delete(item.name);
 message.textContent=item.name.startsWith('phone-')?'送信中… iPhoneの返事を待っています。':'操作を受け付けました。現在の設定を確認してください。';}
 catch(e){sending.delete(item.name);lastPhoneName=null;message.textContent='操作を送信できません: '+e.message;}finally{busy=false;refreshButtons();}
}
function refreshButtons(){
 for(const el of nodes){const i=el.item;if(!i||!i.name)continue;
  const local=sending.get(i.name), pending=i.pending||!!local&&local.value===i.value;
  el.textContent=(pending?'送信中… ':i.selected?'✓ ':'')+i.text;
  el.className=(i.selected?'selected ':'')+(pending?'pending':'');
  el.disabled=!i.enabled||i.pending||!!local;el.setAttribute('aria-pressed',String(i.selected));
  el.resultNode.textContent=i.result||'';el.resultNode.hidden=!i.result;el.resultNode.className='result'+(i.resultWarning?' warning':'');
 }
}
function render(state){
 // 選択値や経過時間ではDOMを作り直さず、開いた詳細・フォーカスを維持する。
 const phones=state.phones||[];
 const next=JSON.stringify([phones.map(p=>[p.station,p.sender,p.model,p.app,p.reportedStation,p.metricsAgeSeconds>15]),state.sections.map(s=>[s.title,s.dangerous,s.items.map(i=>[i.name,i.requiresConfirmation])])]);
 if(next!==structure){structure=next;nodes=[];phoneNodes=[];panel.replaceChildren();
  for(const [index,s] of state.sections.entries()){
   const section=document.createElement('section');
   let content=section;
   if(s.dangerous){const details=document.createElement('details'),summary=document.createElement('summary');summary.textContent=s.title+'（確認して実行）';details.append(summary);section.append(details);content=details;section.className='danger';}
   else{const h=document.createElement('h2');h.textContent=s.title;section.append(h);section.className=index===0?'current':s.items.some(i=>i.grade)?'health':'';}
   if(index===0){const grid=document.createElement('div');grid.className='phone-grid';
    for(const p of phones){const card=document.createElement('div'),h=document.createElement('h3'),settings=document.createElement('div');card.className='phone';h.textContent=(p.model||'機種未報告')+' · 台'+(p.reportedStation==='none'||p.reportedStation==='?'?'未指定（このPCでは台'+p.station+'扱い）':p.station)+(p.sender?' · '+p.sender:' · 報告なし')+' · app '+(p.app||'未報告')+(p.metricsAgeSeconds>15?'（古い報告）':'');settings.className='settings';card.append(h,settings);
     for(const [key,label] of Object.entries(settingLabels)){const cell=document.createElement('div'),title=document.createElement('span'),value=document.createElement('strong'),age=document.createElement('small');cell.className='setting';title.textContent=label;cell.append(title,value,age);settings.append(cell);phoneNodes.push({phone:p.station,sender:p.sender,key,value,age});}grid.append(card);
    }content.append(grid);
   }
   let controls=null,group=null;
   for(const i of s.items){const el=document.createElement(i.name?'button':'p');
    if(i.name){if(!controls||group!==i.name){controls=document.createElement('div');controls.className='controls';controls.setAttribute('role','group');controls.setAttribute('aria-label',i.text);content.append(controls);group=i.name;}
     el.type='button';el.onclick=()=>action(el.item);el.resultNode=document.createElement('p');el.resultNode.setAttribute('role','status');controls.append(el,el.resultNode);
    }else{controls=null;group=null;content.append(el);}nodes.push(el);
   }panel.append(section);
  }
 }
 for(const n of phoneNodes){const phone=phones.find(p=>p.station===n.phone&&p.sender===n.sender),r=phone.settings[n.key];
  n.value.textContent=r.value===null?'未確認':r.value;n.value.className=r.value===null?'unknown':'';
  n.age.textContent=r.ageSeconds===null?'報告なし':r.source+' · '+Math.floor(r.ageSeconds)+'秒前'+(r.ageSeconds>15?'（古い報告）':'');n.age.className=r.ageSeconds>15?'stale':'';
 }
 let index=0;for(const s of state.sections)for(const i of s.items){const el=nodes[index++];el.item=i;
  if(i.name){const local=sending.get(i.name);if(local&&local.base!==i.commandId)sending.delete(i.name);}
  else{el.textContent=i.text;el.className=i.grade==='OK'?'health-ok':i.grade==='WARN'?'health-warn':i.grade==='BAD'?'health-bad':i.warning?'warning':'';}
 }
 refreshButtons();
 if(lastPhoneName&&!sending.has(lastPhoneName)){
  const items=nodes.map(el=>el.item).filter(i=>i.name===lastPhoneName),item=items.find(i=>i.result);
  if(item&&!items.some(i=>i.pending)){message.textContent=item.result;lastPhoneName=null;}
 }
}
async function poll(){try{const r=await fetch(url('/state'),{cache:'no-store',referrerPolicy:'no-referrer'});
 if(!r.ok)throw Error('HTTP '+r.status);render(await r.json());connection.textContent='接続中 · 0.5秒ごとに更新';}
 catch(e){connection.textContent='接続できません。ゲームの起動を確認し、F8 で開き直してください。 '+e.message;}
 finally{setTimeout(poll,500);}}
poll();
</script></html>";
}
