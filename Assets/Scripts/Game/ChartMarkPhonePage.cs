// スマホで開く「譜面の印」の画面(ChartMarkPhoneServer の GET /m、2026-10-28)。縦長・大きな字・暗い色(Neon Focus に寄せた
// 青緑 #3cc9f5・桃 #ff5d8f・金 #f2c14e)。外のファイルは読まない。DOM は textContent だけで書き、受けた文字を HTML として扱わない。
// 1 秒ごとに /api/state を読む。「印」は演奏中だけ押せる。一言は打ってから 1 秒あけて自動で保存する。
// 開発者と運営だけが見る画面なので、数字(時刻・小節・件数)を出してよい(ノーツの近くに出さない決まりはゲームの画面の話)。
public static class ChartMarkPhonePage
{
    // 一言の長さ(ChartMarks の一言の上限と同じ 120 文字)。
    public const int CommentMaxLength = 120;
    // 前の回の印は最近の 20 件まで出す。
    public const int PastMarks = 20;

    public const string Html = @"<!doctype html>
<html lang='ja'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,viewport-fit=cover'>
<meta name='theme-color' content='#070b14'><link rel='icon' href='data:,'>
<title>譜面の印</title><style>
:root{color-scheme:dark;--bg:#070b14;--card:#0f1726;--line:#24324a;--ink:#eef3fb;--muted:#9aa9bf;--cyan:#3cc9f5;--pink:#ff5d8f;--gold:#f2c14e}
*{box-sizing:border-box}html{-webkit-text-size-adjust:100%;background:var(--bg)}
body{margin:0 auto;max-width:640px;padding:12px 16px calc(32px + env(safe-area-inset-bottom));background:var(--bg);color:var(--ink);
font:18px/1.55 system-ui,-apple-system,'Hiragino Sans','Noto Sans JP',sans-serif}
header{display:flex;align-items:baseline;justify-content:space-between;gap:8px}
h1{font-size:24px;margin:4px 0;letter-spacing:.05em}#station{color:var(--muted);font-size:16px}
#conn{font-size:14px;color:var(--muted);min-height:22px}#conn.bad{color:var(--pink)}
.now{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:14px 16px;margin:10px 0}
.badge{display:inline-block;font-size:15px;font-weight:700;padding:3px 12px;border-radius:999px;border:1px solid var(--line);color:var(--muted)}
.badge.playing{color:#04131a;background:var(--cyan);border-color:var(--cyan)}.badge.result{color:#1d1503;background:var(--gold);border-color:var(--gold)}
#song{font-size:22px;font-weight:700;margin-top:8px;overflow-wrap:anywhere}#diff{color:var(--muted)}
#mark{display:block;width:100%;min-height:160px;margin:14px 0 6px;border-radius:24px;border:3px solid var(--cyan);background:#0b2531;
color:var(--ink);font:inherit;font-size:60px;font-weight:800;line-height:1.2;box-shadow:0 0 28px rgba(60,201,245,.35);
touch-action:manipulation;-webkit-tap-highlight-color:transparent;user-select:none;-webkit-user-select:none;cursor:pointer}
#mark:active:not(:disabled){background:var(--cyan);color:#04131a}
#mark:disabled{border-color:#3a4558;background:#141a25;color:#5d6878;box-shadow:none;cursor:default}
#mark small{display:block;font-size:16px;font-weight:500;letter-spacing:0}
#msg{min-height:32px;font-size:20px;font-weight:700;text-align:center;overflow-wrap:anywhere}#msg.ok{color:var(--gold)}#msg.ng{color:var(--pink)}#msg.wait{color:var(--cyan)}
#open{color:var(--muted);text-align:center;font-size:16px}
h2{font-size:19px;margin:24px 0 8px;border-left:4px solid var(--cyan);padding-left:10px}h2.past{border-color:var(--gold)}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:12px 14px;margin:10px 0}
.card .head{font-weight:700;font-size:19px}.card .sub{color:var(--muted);font-size:15px;overflow-wrap:anywhere}
.card textarea{display:block;width:100%;margin:8px 0 6px;min-height:68px;padding:10px 12px;border-radius:10px;border:1px solid #3a4b68;
background:#0a111d;color:var(--ink);font:inherit;font-size:18px;resize:vertical}
.card textarea:focus{outline:2px solid var(--cyan);border-color:var(--cyan)}
.card .foot{display:flex;align-items:center;justify-content:space-between;gap:10px}
.card .save{font-size:15px;color:var(--muted);overflow-wrap:anywhere}.card .save.ok{color:#7fe0b0}.card .save.ng{color:var(--pink)}
.card button{flex:none;min-height:48px;min-width:96px;padding:8px 16px;border-radius:10px;border:1px solid #4a5a75;background:#1a2538;
color:var(--ink);font:inherit;font-size:16px;touch-action:manipulation;cursor:pointer}
.card.canceled{opacity:.6}.card.canceled .head{text-decoration:line-through;color:var(--muted)}
.empty{color:var(--muted)}footer{margin-top:28px;color:var(--muted);font-size:14px}
</style></head><body>
<header><h1>譜面の印</h1><span id='station'></span></header>
<div id='conn' role='status'>つないでいます…</div>
<section class='now'><span id='state' class='badge'>—</span><div id='song'>—</div><div id='diff'></div></section>
<button id='mark' type='button' disabled>印<small id='markHint'>演奏中だけ押せます</small></button>
<div id='msg' role='status' aria-live='polite'></div>
<div id='open'></div>
<h2>この回の印</h2><div id='current'></div>
<p id='currentEmpty' class='empty'>まだありません。演奏中に「印」を押すと、ここに出ます。</p>
<h2 class='past'>前の回の印(最近 20 件)</h2><div id='past'></div><p id='pastEmpty' class='empty'>ありません。</p>
<footer>印は PC の F5 と同じで、そのときの画面の写真も残ります。一言は打ってから 1 秒で PC に保存します(Enter で確定)。<br>
このページをブックマークしておくと、次からそのまま開けます(合言葉は変わりません)。</footer>
<script>
'use strict';
const token=new URLSearchParams(location.search).get('t')||'';
const $=id=>document.getElementById(id);
const cards=new Map();
const PAST=20,MAXLEN=120;
let state=null,failures=0,marking=false,polling=false,again=false,pollTimer=0,msgTimer=0;
function api(path){return path+'?t='+encodeURIComponent(token);}
function clock(s){if(typeof s!=='number'||!isFinite(s))return '-';const neg=s<0;s=Math.abs(s);const m=Math.floor(s/60);let r=(s-m*60).toFixed(1);if(r.length<4)r='0'+r;return (neg?'-':'')+m+':'+r;}
function when(t){const m=/^\d{4}-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(t||'');return m?(+m[1])+'/'+(+m[2])+' '+m[3]+':'+m[4]:(t||'');}
function fail(text,status){const e=Error(text);e.status=status;return e;}
// 返事が 6 秒来なければ打ち切る(status 0)。HTTP の失敗は status にその番号を入れる。
async function request(path,body,post){
 const init={method:post?'POST':'GET',cache:'no-store'};
 if(body!==undefined){init.headers={'Content-Type':'application/json'};init.body=JSON.stringify(body);init.keepalive=true;}
 const ctl=window.AbortController?new AbortController():null,limit=ctl?setTimeout(()=>ctl.abort(),6000):0;
 if(ctl)init.signal=ctl.signal;
 let r;try{r=await fetch(api(path),init);}catch(e){throw fail('通信できません',0);}finally{clearTimeout(limit);}
 let data=null;try{data=await r.json();}catch(e){}
 if(!r.ok)throw fail(data&&data.message?data.message:'HTTP '+r.status,r.status);
 if(!data)throw fail('返事を読めません',r.status);
 return data;
}
function say(text,ok){const m=$('msg');m.textContent=text;m.className=ok===null?'wait':ok?'ok':'ng';clearTimeout(msgTimer);msgTimer=setTimeout(()=>{m.textContent='';},8000);}
function canMark(){return !!state&&!!state.ready&&state.state==='playing'&&failures<2;}
function newId(){const b=new Uint8Array(12);crypto.getRandomValues(b);return Array.from(b,x=>x.toString(16).padStart(2,'0')).join('');}
let markId=null;
async function mark(){
 if(marking||!canMark())return;
 marking=true;render();
 // 要求 id は、返事で結果が分かるまで使い続ける。返事が来ない・混んでいる(503)ときは、同じ id で 1 回だけ送り直す。
 // それでも分からなければ、次に押したときも同じ id で送る(PC は同じ id の印を二度付けず、前の結果を返す)。
 if(!markId)markId=newId();
 const body={requestId:markId};
 try{
  let d;
  try{d=await request('/api/mark',body,true);}
  catch(e){if(e.status!==0&&e.status!==503)throw e;d=await request('/api/mark',body,true);}
  if(d.pending)say(d.message||'PC の返事が遅れています。印は付いたかもしれないので一覧を見てください。',null);
  else{markId=null;say(d.message||(d.ok?'印を付けました':'印を付けられません'),!!d.ok);if(d.ok&&navigator.vibrate)navigator.vibrate(40);}
 }
 catch(e){
  if(e.status!==0&&e.status!==503)markId=null;
  say('送れません: '+e.message+(markId?'(もう一度押すと、同じ印の結果を確かめます)':''),false);
 }
 finally{setTimeout(()=>{marking=false;render();},300);poll();}
}
function note(c,text,kind){c.save.textContent=text;c.save.className='save'+(kind?' '+kind:'');}
function later(c,ms){clearTimeout(c.timer);c.timer=setTimeout(()=>save(c),ms);}
function edited(c){c.dirty=true;c.fails=0;if(!c.saving)note(c,'…','');later(c,1000);}
async function save(c){
 clearTimeout(c.timer);c.timer=0;
 if(c.saving){c.again=true;return;}
 const text=c.area.value;c.saving=true;note(c,'保存しています…','');
 let error='';
 try{const d=await request('/api/comment',{id:c.id,text:text},true);if(d.ok)c.known=text;else error=d.message||'保存できません';}
 catch(e){error=e.message;}
 c.saving=false;
 const more=c.area.value!==c.known;
 if(error)c.fails++;else c.fails=0;
 if(!more){c.dirty=false;c.again=false;}
 if(error)note(c,'保存できません: '+error+(more&&c.fails<5?'(もう一度送ります)':''),'ng');
 else if(!more)note(c,'保存しました','ok');
 if(more&&(c.again||c.fails<5))later(c,error&&!c.again?3000:1000);
 c.again=false;
}
async function toggle(c){
 if(c.busy)return;
 c.busy=true;c.cancel.disabled=true;
 const next=c.status!=='canceled';
 try{const d=await request('/api/cancel',{id:c.id,canceled:next},true);
  if(!d.ok)throw Error(d.message||'できません');
  c.status=next?'canceled':'open';c.root.classList.toggle('canceled',next);c.cancel.textContent=next?'戻す':'取り消す';
  say(next?'印を取り消しました(戻せます)':'印を戻しました',true);}
 catch(e){say((next?'取り消せません: ':'戻せません: ')+e.message,false);}
 finally{c.busy=false;c.cancel.disabled=false;poll();}
}
function card(m){
 const c={id:m.id,status:m.status,known:m.comment||'',dirty:false,saving:false,again:false,busy:false,fails:0,timer:0};
 c.root=document.createElement('div');c.root.className='card';
 c.head=document.createElement('div');c.head.className='head';
 c.sub=document.createElement('div');c.sub.className='sub';
 c.area=document.createElement('textarea');c.area.rows=2;c.area.maxLength=MAXLEN;c.area.enterKeyHint='done';
 c.area.placeholder='一言(例: ここのノーツいらない / 半拍おそく)';c.area.setAttribute('aria-label','印'+m.number+'の一言');c.area.value=c.known;
 const foot=document.createElement('div');foot.className='foot';
 c.save=document.createElement('span');c.save.className='save';c.save.setAttribute('role','status');
 c.cancel=document.createElement('button');c.cancel.type='button';
 foot.append(c.save,c.cancel);c.root.append(c.head,c.sub,c.area,foot);
 c.area.addEventListener('input',e=>{if(e.isComposing){c.dirty=true;return;}edited(c);});
 c.area.addEventListener('compositionend',()=>edited(c));
 c.area.addEventListener('blur',()=>{if(c.dirty)save(c);});
 c.area.addEventListener('keydown',e=>{if(e.key==='Enter'&&!e.isComposing&&e.keyCode!==229){e.preventDefault();c.area.blur();}});
 c.cancel.addEventListener('click',()=>toggle(c));
 return c;
}
function fill(c,m){
 if(!c.busy){c.status=m.status;c.root.classList.toggle('canceled',m.status==='canceled');c.cancel.textContent=m.status==='canceled'?'戻す':'取り消す';}
 c.head.textContent='印'+m.number+' ・ '+clock(m.songSeconds)+' ・ '+m.bar+'小節目'+(m.status==='canceled'?'(取り消し)':'');
 const parts=[];
 if(!m.current){parts.push(m.title||m.song||'');if(m.difficulty)parts.push(m.difficulty);if(m.savedAt)parts.push(when(m.savedAt));}
 parts.push(m.source==='phone'?'スマホで付けた印':'F5 で付けた印');
 if(m.station&&state&&m.station!==state.station)parts.push('台'+m.station);
 c.sub.textContent=parts.filter(x=>x).join(' ・ ');
 const text=m.comment||'';
 // 打っていない欄は、選んでいるだけ(フォーカス中)でも最新の一言にそろえる(古い表示に足して PC の一言を消さない)。
 if(!c.dirty&&!c.saving){c.known=text;if(c.area.value!==text)c.area.value=text;}
}
function place(box,list,typing,newestFirst,seen){
 for(const m of list){
  seen.add(m.id);let c=cards.get(m.id);
  if(!c){c=card(m);cards.set(m.id,c);if(typing){if(newestFirst)box.prepend(c.root);else box.append(c.root);}}
  fill(c,m);
 }
 // 打っている途中は並べ替えない(欄が動くと、スマホのキーボードが閉じる)。
 if(typing)return;
 const nodes=list.map(m=>cards.get(m.id).root),now=box.children;
 let same=nodes.length===now.length;
 for(let i=0;same&&i<nodes.length;i++)same=nodes[i]===now[i];
 if(!same)for(const n of nodes)box.append(n);
}
function render(){
 const s=state,ok=canMark();
 $('station').textContent=s?(s.station&&s.station!=='PC'?'台'+s.station:'この PC'):'';
 const badge=$('state');
 badge.textContent=!s?'—':!s.ready?'印の部品が止まっています':s.state==='playing'?'演奏中':s.state==='result'?'結果画面':'曲の外(タイトル・選曲など)';
 badge.className='badge'+(s&&s.ready&&s.state==='playing'?' playing':s&&s.ready&&s.state==='result'?' result':'');
 const inSong=!!s&&s.state!=='other';
 $('song').textContent=inSong?(s.title||s.song||'—'):'—';
 $('diff').textContent=inSong&&s.difficulty?'難易度 '+s.difficulty:'';
 $('mark').disabled=!ok||marking;
 $('markHint').textContent=marking?'送っています…':ok?'いま気になった所に印を付けます':'演奏中だけ押せます';
 const marks=s&&Array.isArray(s.marks)?s.marks:[];
 const current=marks.filter(m=>m.current),past=marks.filter(m=>!m.current).slice(0,PAST);
 const live=current.filter(m=>m.status!=='canceled').length;
 $('open').textContent=s?'この PC の直していない印 '+(s.open==null?'—':s.open)+' 件(この回 '+live+' 件)':'';
 const typing=!!document.activeElement&&document.activeElement.tagName==='TEXTAREA';
 const seen=new Set();
 place($('current'),current,typing,true,seen);
 place($('past'),past,typing,false,seen);
 for(const [id,c] of cards)if(!seen.has(id)&&!c.dirty&&!c.saving&&document.activeElement!==c.area){c.root.remove();cards.delete(id);}
 $('currentEmpty').hidden=current.length>0;$('pastEmpty').hidden=past.length>0;
}
async function poll(){
 clearTimeout(pollTimer);
 if(polling){again=true;return;}
 polling=true;
 try{state=await request('/api/state');failures=0;$('conn').textContent='つながっています(1 秒ごとに更新)';$('conn').className='';}
 catch(e){failures++;if(failures>=3)state=null;
  $('conn').textContent='PC とつながりません: '+e.message+'。同じ Wi-Fi(ホットスポット)か、ゲームが動いているか確かめてください。';$('conn').className='bad';}
 finally{polling=false;
  if(again){again=false;setTimeout(poll,0);}else pollTimer=setTimeout(poll,failures?2000:1000);
  try{render();}catch(e){console.error(e);}}
}
$('mark').addEventListener('click',mark);
document.addEventListener('visibilitychange',()=>{
 if(document.visibilityState==='visible')poll();
 else for(const c of cards.values())if(c.dirty&&!c.saving)save(c);
});
if(!token)say('アドレスに合言葉(?t=…)がありません。PC に出ているアドレスを開き直してください。',false);
poll();
</script></body></html>";
}
