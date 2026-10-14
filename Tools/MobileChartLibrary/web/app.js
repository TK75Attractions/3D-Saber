import {clamp,prepare,timeLabel,chartStats} from './core.js';
import {draw,drawDensity} from './renderer.js';
const $=id=>document.getElementById(id), audio=$('audio');
let catalog,song,entry,chart,selection=0,request,onlyRecent=false,dirty=true,lastUI=-1,pendingSeek=null;
audio.volume=.8;
const status=text=>{$('status').textContent=text;};
const fail=error=>{status(error?.message||String(error));console.error(error);};
const duration=()=>song?.duration||0;
const cursor=()=>pendingSeek??audio.currentTime??0;

function updateURL(){if(!song||!entry)return;const url=new URL(location.href);url.searchParams.set('song',song.sourceId);url.searchParams.set('difficulty',entry.difficulty);url.searchParams.set('t',cursor().toFixed(2));history.replaceState(null,'',url);}
function pause(){audio.pause();dirty=true;updateURL();}
function seek(value){
  const t=clamp(value,0,duration());pendingSeek=t;
  if(audio.readyState>=1){audio.currentTime=t;pendingSeek=null;}
  $('seek').value=t;dirty=true;playbackUI();updateURL();
}
async function play(){
  if(!chart)return;
  if(!audio.paused){pause();return;}
  if(cursor()>=duration()-.05)seek(0);
  try{await audio.play();status('再生中 · スロー再生や区間移動で配置を確認できます。');}
  catch(e){status('音声を再生できませんでした。通信を確認して、もう一度「再生」を押してください。');}
}

function playbackUI(){
  $('play').textContent=audio.paused?'▶ 再生':'Ⅱ 一時停止';
  $('canvasPlay').hidden=!audio.paused||!chart||cursor()>.05;
  $('canvasPlay').disabled=!chart;dirty=true;
}

function renderLibrary(){
  const query=$('search').value.trim().toLocaleLowerCase();
  const visible=catalog.songs.filter(s=>(!onlyRecent||s.recent)&&`${s.title} ${s.composer} ${s.sourceId}`.toLocaleLowerCase().includes(query));
  $('songList').replaceChildren();$('libraryCount').textContent=`${visible.length}曲`;$('empty').hidden=visible.length>0;
  for(const s of visible){
    const b=document.createElement('button');b.type='button';b.className='song-card'+(s.id===song?.id?' active':'');b.dataset.song=s.sourceId;b.setAttribute('aria-pressed',String(s.id===song?.id));
    const art=document.createElement('span');art.className='art';art.setAttribute('aria-hidden','true');
    if(s.cover){const img=document.createElement('img');img.src=s.cover;img.alt='';img.loading='lazy';art.append(img);}else{const lines=document.createElement('span');lines.className='art-lines';art.append(lines);}
    const info=document.createElement('span');info.className='song-info';const title=document.createElement('strong');title.textContent=s.title;const meta=document.createElement('small');meta.textContent=`${timeLabel(s.duration)} · ${s.charts.length}譜面`;
    if(s.recent){const badge=document.createElement('span');badge.className='badge';badge.textContent='更新';meta.append(badge);}
    info.append(title,meta);b.append(art,info);b.onclick=()=>{selectSong(s,entry?.difficulty||'normal',0).catch(fail);if(matchMedia('(max-width:760px)').matches)$('songTitle').scrollIntoView({block:'start',behavior:matchMedia('(prefers-reduced-motion:reduce)').matches?'auto':'smooth'});};$('songList').append(b);
  }
}

function renderDifficulties(){
  $('difficulties').replaceChildren();for(const c of song.charts){
    const b=document.createElement('button');b.className='difficulty'+(entry===c?' active':'');b.dataset.difficulty=c.difficulty;b.setAttribute('aria-pressed',String(entry===c));
    const name=document.createElement('span');name.textContent=c.label;const level=document.createElement('small');level.textContent=c.level?`Lv.${c.level}`:'';b.append(name,level);b.onclick=()=>selectSong(song,c.difficulty,cursor()).catch(fail);$('difficulties').append(b);
  }
}

function renderChapters(){
  $('chapters').replaceChildren();
  const raw=[{time:0,label:'最初から'},...((chart.editorMarkers||[]).length?(chart.editorMarkers||[]).map(m=>({time:m.timeMs/1000,label:m.name})):(song.chapters||[]).map(c=>({time:c.startSeconds,label:c.label})))];
  const sections=[];for(const c of raw){if(c.time<0||c.time>duration()||sections.some(s=>Math.abs(s.time-c.time)<.3))continue;sections.push(c);}
  if(sections.length===1&&song.previewTime>0)sections.push({time:song.previewTime,label:'メイン区間'});
  // 長い曲でも区間を隠さず表示する。
  for(const c of sections){const b=document.createElement('button');b.textContent=c.label||'区間';const small=document.createElement('small');small.textContent=timeLabel(c.time);b.append(small);b.onclick=()=>{seek(Math.max(0,c.time));status(`${c.label} · ${timeLabel(c.time)}へ移動しました。`);};$('chapters').append(b);}
}

async function selectSong(next,difficulty='normal',at=0){
  const token=++selection;request?.abort();request=new AbortController();audio.pause();chart=null;$('play').disabled=true;$('canvasPlay').hidden=true;
  const changed=next.id!==song?.id;song=next;entry=song.charts.find(c=>c.difficulty===difficulty)||song.charts.find(c=>c.difficulty==='normal')||song.charts[0];
  $('songTitle').textContent=song.title;$('songArtist').textContent=song.composer&&song.composer!=='???'?song.composer:'3D SABER';
  document.title=`${song.title} · ${entry.label} | 3D SABER`;
  $('stageLabel').textContent=`${entry.label.toUpperCase()} · 配置プレビュー`;$('stats').replaceChildren();$('chapters').replaceChildren();
  renderDifficulties();renderLibrary();status('譜面を読み込んでいます…');
  if(changed||audio.error){audio.src=song.audio;audio.load();audio.playbackRate=Number($('speed').value);}
  $('seek').max=duration();$('duration').textContent=timeLabel(duration());seek(at);dirty=true;
  try{
    const response=await fetch(entry.path,{signal:request.signal});if(!response.ok)throw new Error('譜面を取得できませんでした。曲を選び直してください。');
    const raw=await response.json();if(token!==selection)return;chart=prepare(raw);
    const stats=chartStats(chart);for(const [label,value] of [['ノーツ',stats.count],['フリック',stats.flicks],['ロング',stats.longs],['BPM',chart.bpm]]){
      const div=document.createElement('div');div.className='stat';const b=document.createElement('b');b.textContent=Number(value).toLocaleString('ja-JP',{maximumFractionDigits:1});const span=document.createElement('span');span.textContent=label;div.append(b,span);$('stats').append(div);
    }
    $('downloadChart').href=entry.path;$('downloadChart').download=`${song.sourceId}_${entry.difficulty}.json`;
    $('credits').hidden=!song.credits;if(song.credits)$('credits').href=song.credits;
    renderChapters();$('play').disabled=false;playbackUI();status('再生を押すと音付きで流れます。拡大して横向きでも確認できます。');dirty=true;updateURL();
  }catch(e){if(e.name!=='AbortError'&&token===selection){chart=null;status(e.message);}}
}

function tick(){
  const t=cursor(),refresh=dirty;if(!audio.paused||refresh){draw($('stage'),chart,t,Number($('approach').value));dirty=false;}
  if(Math.abs(t-lastUI)>.04||refresh){
    $('position').textContent=timeLabel(t);$('seek').value=t;$('seek').setAttribute('aria-valuetext',`${timeLabel(t)} / ${timeLabel(duration())}`);
    $('noteProgress').textContent=chart?`${chart.notes.filter(n=>n.hit<=t).length.toLocaleString()} / ${chart.notes.length.toLocaleString()}`:'0 / 0';
    drawDensity($('density'),chart,duration(),t);lastUI=t;
  }
  requestAnimationFrame(tick);
}

$('play').onclick=play;$('canvasPlay').onclick=play;
$('back').onclick=()=>seek(cursor()-5);$('forward').onclick=()=>seek(cursor()+5);
$('seek').oninput=e=>seek(Number(e.target.value));
$('speed').onchange=e=>{audio.playbackRate=Number(e.target.value);status(`再生速度を${e.target.value}倍にしました。`);};
$('volume').oninput=e=>{audio.volume=Number(e.target.value);};
$('approach').onchange=()=>{dirty=true;};
$('search').oninput=()=>renderLibrary();
for(const [id,value] of [['allFilter',false],['recentFilter',true]])$(id).onclick=()=>{onlyRecent=value;for(const [i,v] of [['allFilter',false],['recentFilter',true]]){$(i).classList.toggle('active',v===onlyRecent);$(i).setAttribute('aria-pressed',String(v===onlyRecent));}renderLibrary();};
$('expand').onclick=()=>{const expanded=document.body.classList.toggle('expanded');$('expand').textContent=expanded?'×':'⛶';$('expand').setAttribute('aria-label',expanded?'拡大を閉じる':'プレイヤーを拡大');dirty=true;};
document.addEventListener('keydown',e=>{if(e.key==='Escape'&&document.body.classList.contains('expanded'))$('expand').click();});
$('share').onclick=async()=>{
  updateURL();const url=location.href;
  try{if(navigator.share)await navigator.share({title:`${song.title} ${entry.label} ${timeLabel(cursor())}`,url});else{await navigator.clipboard.writeText(url);status('この曲・難易度・再生位置のリンクをコピーしました。');}}
  catch(e){if(e.name!=='AbortError'){status('リンクはブラウザのアドレス欄からコピーできます。');}}
};
audio.addEventListener('loadedmetadata',()=>{if(pendingSeek!==null){audio.currentTime=clamp(pendingSeek,0,Math.min(audio.duration,duration()));pendingSeek=null;}audio.playbackRate=Number($('speed').value);dirty=true;});
audio.addEventListener('play',playbackUI);audio.addEventListener('pause',()=>{playbackUI();updateURL();});
audio.addEventListener('ended',()=>{playbackUI();status('最後まで再生しました。もう一度再生するか、別の難易度を選べます。');});
audio.addEventListener('waiting',()=>{if(!audio.paused)status('音源を読み込んでいます…');});
audio.addEventListener('playing',()=>status('再生中 · スロー再生や区間移動で配置を確認できます。'));
audio.addEventListener('seeked',()=>{dirty=true;});
audio.addEventListener('error',()=>status('音源を読み込めませんでした。通信を確認して、曲を選び直してください。'));
document.addEventListener('visibilitychange',()=>{if(document.hidden)pause();});
window.addEventListener('pagehide',pause);window.addEventListener('resize',()=>{dirty=true;});

async function init(){
  const response=await fetch('catalog.json');if(!response.ok)throw new Error('曲一覧を読み込めませんでした。ページを再読み込みしてください。');catalog=await response.json();
  $('catalogCount').textContent=`${catalog.songs.length}曲 / ${catalog.chartCount}譜面`;$('updated').textContent=`UPDATED ${catalog.updated.replaceAll('-','.')}`;
  const params=new URLSearchParams(location.search),initial=catalog.songs.find(s=>s.sourceId===params.get('song')||s.id===params.get('song'))||catalog.songs[0];
  const at=Number(params.get('t'));
  await selectSong(initial,params.get('difficulty')||'normal',Number.isFinite(at)?at:0);
}
requestAnimationFrame(tick);init().catch(fail);
