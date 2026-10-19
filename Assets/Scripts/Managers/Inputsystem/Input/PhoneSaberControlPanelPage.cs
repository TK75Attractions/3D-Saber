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
.controls{display:flex;flex-wrap:wrap;gap:10px;margin:12px 0}button{font:inherit;font-weight:600;min-height:52px;padding:11px 18px;
border:1px solid #6383af;border-radius:10px;background:#293c58;color:#fff;cursor:pointer;touch-action:manipulation}
button:hover{background:#355377}button.selected{background:#205c58;border-color:#6de1bf}button:disabled{opacity:.4;cursor:default}
@media(max-width:850px){main{grid-template-columns:1fr}body{padding:12px}}
</style><header><h1>PhoneSaber 操作画面</h1><div id='connection' role='status'>接続中…</div>
<div>ゲーム: F8 でブラウザを開く ／ Shift+F8 でゲーム内の予備画面 ／ F7 で位置補正を採取</div>
<div id='message' role='status'></div></header><main id='panel'></main><script>
'use strict';
const token=new URLSearchParams(location.search).get('t')||'';
const panel=document.getElementById('panel'), connection=document.getElementById('connection'), message=document.getElementById('message');
let structure='', nodes=[], busy=false;
function url(path, extra={}){return path+'?'+new URLSearchParams({t:token,...extra});}
async function action(name,value){
 if(busy)return;busy=true;
 try{const r=await fetch(url('/action',{name,value}),{method:'POST',cache:'no-store',referrerPolicy:'no-referrer'});
 if(!r.ok)throw Error('HTTP '+r.status);message.textContent='操作を受け付けました。結果は各状態欄・スマホの返事を確認してください。';}
 catch(e){message.textContent='操作を送信できません: '+e.message;}finally{busy=false;}
}
function render(state){
 // ボタンの DOM を保ち、500ms の更新中もフォーカス・タッチ操作を維持する。
 const next=JSON.stringify(state.sections.map(s=>[s.title,s.items.map(i=>[i.name,i.value])]));
 if(next!==structure){structure=next;nodes=[];panel.replaceChildren();
  for(const s of state.sections){const section=document.createElement('section'), h=document.createElement('h2');h.textContent=s.title;section.append(h);
   let controls=null;
   for(const i of s.items){const el=document.createElement(i.name?'button':'p');
    if(i.name){if(!controls){controls=document.createElement('div');controls.className='controls';section.append(controls);}
     el.type='button';el.onclick=()=>action(i.name,i.value);controls.append(el);
    }else{controls=null;section.append(el);}nodes.push(el);
   }panel.append(section);
  }
 }
 let index=0;for(const s of state.sections)for(const i of s.items){const el=nodes[index++];el.textContent=(i.selected?'● ':'')+i.text;
  el.className=i.name?(i.selected?'selected':''):(i.warning?'warning':'');
  if(i.name){el.disabled=!i.enabled;el.setAttribute('aria-pressed',String(i.selected));}
 }
}
async function poll(){try{const r=await fetch(url('/state'),{cache:'no-store',referrerPolicy:'no-referrer'});
 if(!r.ok)throw Error('HTTP '+r.status);render(await r.json());connection.textContent='接続中 · 0.5秒ごとに更新';}
 catch(e){connection.textContent='接続できません。ゲームの起動を確認し、F8 で開き直してください。 '+e.message;}
 finally{setTimeout(poll,500);}}
poll();
</script></html>";
}
