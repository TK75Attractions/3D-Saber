import { COLORS, clamp, clone, blankChart, parseChart, exportChart, audioTime, longSeconds, makeNote, formatTime, hasVariableGrid, quantizeMs, History } from './core.js';
import { RecordingTake, TapTempo } from './editing.js';
import { SongAudio } from './audio.js';
import { encodeWav } from './wav.js';
import { saveProject, listProjects, getProject, saveAudio, getAudio } from './storage.js';
const $ = id => document.getElementById(id), audio = new SongAudio();
let chart = blankChart(), history = new History(), project = null, selected = null;
let mode = 'record', color = 'auto', loading = false, saving = false, saveFailed = false, starting = false;
let saveTimer, toastTimer, persistChain = Promise.resolve(), flashes = [], lastFrameTime = 0;
let take = null, lastTake = null, playbackEnd = Infinity, saveRevision = 0, pendingSaves = 0;
const tapTempo = new TapTempo();
const touches = new Map();
const uid = () => crypto.randomUUID?.() || `p${Date.now()}-${Math.random().toString(36).slice(2)}`;
const settings = () => ({ snap: Number($('snap').value), rate: Number($('speed').value), longCount: $('longCount').value, direction: $('direction').value, color, latencyMs: clamp(Number($('latency').value)||0,-500,500), countIn:$('countIn').checked, clickSound:$('clickSound').checked, metronome:$('metronome').checked });
const preference = (key, value) => { try { if (value !== undefined) localStorage.setItem(key, value); else return localStorage.getItem(key); } catch {} };
function toast(message) { $('toast').textContent = message; $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').hidden = true, 5000); }
function fail(error) { console.error(error); toast(error?.message || '処理に失敗しました。内容を確認してください。'); }
function run(action) { return async event => { try { await action(event); } catch (e) { fail(e); } }; }
function status(message) { $('saveStatus').textContent = message; }
function refresh() {
  document.body.classList.toggle('previewing',mode==='preview');
  $('songTitle').textContent = project?.name || '曲を読み込んではじめよう';
  $('difficultyLabel').textContent = project ? `${$('difficulty').value.toUpperCase()} / ${chart.bpm} BPM` : 'NEW CHART';
  $('noteCount').textContent = `${chart.notes.length} NOTES`;
  $('undoButton').disabled = !history.past.length || audio.playing || loading;
  $('redoButton').disabled = !history.future.length || audio.playing || loading;
  $('playButton').disabled = !audio.buffer || loading || starting;
  $('exportButton').disabled = !project || loading;
  $('exportAudioButton').disabled = !audio.buffer || loading;
  $('libraryButton').disabled = loading;
  $('retryTake').disabled = !lastTake || audio.playing || loading || starting;
  $('takeStatus').textContent = take ? `今回 ${take.notes.length} ノーツ${take.replace ? ' · 区間を録り直し中' : ''}` : lastTake ? `直前の録音 ${lastTake.count} ノーツ` : '1回の録音をまとめて戻せます';
  for (const id of ['rangeStart','rangeEnd','rangeEnabled','replaceRange','setRangeStart','setRangeEnd']) $(id).disabled = !audio.buffer || loading || audio.playing || starting;
  $('replaceRange').disabled ||= !$('rangeEnabled').checked;
  $('rangeHint').textContent = $('rangeEnabled').checked && $('replaceRange').checked ? '入力したら、再生した区間の既存ノーツを置き換えます。途中停止では、その先を残します。「戻す」で復元できます。' : 'Aから始まりBで自動停止。ノーツは追加されます。';
  $('playButton').textContent = audio.playing ? '一時停止' : mode === 'record' ? '録音開始' : '再生';
  $('playButton').classList.toggle('recording', audio.playing && mode === 'record');
  for(const name of ['record','preview']){ $(name+'Mode').classList.toggle('active',mode===name);$(name+'Mode').setAttribute('aria-pressed',mode===name); }
  $('padHint').textContent = mode === 'record' ? '押した場所にノーツを配置。長押しでロング、2本指で同時押し。' : '確認モード。ノーツの配置とタイミングを見返せます。タップでは追加されません。';
  $('seek').max = audio.duration || 1; $('durationReadout').textContent = formatTime(audio.duration);
}
function fillChartFields() { $('bpm').value=chart.bpm;$('offset').value=chart.offsetMs;$('beatZero').value=chart.beatZeroMs;$('level').value=chart.displayLevel; }
function snapshotProject() { return {...project,chart:clone(chart),difficulty:$('difficulty').value,position:audio.time(),settings:settings(),updated:Date.now()}; }
function persist() {
  clearTimeout(saveTimer);saveTimer=null; if(!project)return Promise.resolve(); const value=snapshotProject(),revision=++saveRevision;
  pendingSaves++;
  saving=true;status('保存中…');
  persistChain=persistChain.catch(()=>{}).then(()=>saveProject(value)).then(()=>{
    saving=--pendingSaves>0;if(project?.id===value.id&&revision===saveRevision&&!saveTimer){saveFailed=false;status('この端末に保存済み');}if(project?.id===value.id)preference('saber-last-project',value.id);
  },error=>{saving=--pendingSaves>0;saveFailed=true;status('未保存 — 譜面を書き出してください');throw new Error('端末に保存できませんでした。空き容量を確認し、譜面を書き出して保管してください。',{cause:error});});
  return persistChain;
}
function changed() {chart.notes.sort((a,b)=>a.time-b.time);refresh();renderNotes();status('保存待ち…');clearTimeout(saveTimer);saveTimer=setTimeout(()=>persist().catch(fail),180);}
function commit(action) {lastTake=null;history.push(chart);action();changed();}
function cancelTouches() {for(const touch of touches.values())touch.marker.remove();touches.clear();}
function stop({finish=true}={}) {
  const end=Math.min(audio.time(),playbackEnd);
  if(finish&&audio.playing)for(const pointerId of [...touches.keys()])endTouch(pointerId,end);else cancelTouches();
  audio.pause();audio.position=end;playbackEnd=Infinity;
  if(take){if(take.notes.length){chart=take.compose(end);lastTake={before:take.before,start:take.start,count:take.notes.length};changed();}take=null;}
  refresh();if(project){clearTimeout(saveTimer);saveTimer=setTimeout(()=>persist().catch(fail),100);}
}
function resetSession() {
  take=null;lastTake=null;tapTempo.reset();$('tempoReadout').textContent='曲の拍に合わせて4回以上';$('applyTempo').disabled=true;
  $('rangeEnabled').checked=false;$('replaceRange').checked=false;$('rangeStart').value=0;$('rangeEnd').value=audio.duration.toFixed(3);$('noteDialog').close();
}
function restoreSettings(value={}) {
  for(const id of ['snap','longCount','direction'])if(value[id]!==undefined)$(id).value=value[id];
  $('speed').value=[1,.75,.5].includes(Number(value.rate))?value.rate:1;
  $('latency').value=value.latencyMs||0;
  for(const id of ['countIn','clickSound','metronome'])$(id).checked=value[id]??(id!=='metronome');
  setColor(['auto','blue','red','gold'].includes(value.color)?value.color:'auto');
}
async function loadAudioFile(file) {
  if(!file||loading)return;stop();loading=true;refresh();status('音源を読み込み中…');
  try {
    await audio.unlock();const buffer=await audio.decode(file);await persist();
    const id=uid();await saveAudio(id,file,file.name);
    const next={id:uid(),audioId:id,name:file.name.replace(/\.[^.]+$/,''),audioName:file.name,difficulty:'normal',position:0,updated:Date.now(),chart:blankChart()};
    await saveProject(next);project=next;chart=blankChart();history=new History();selected=null;
    audio.buffer=buffer;audio.position=0;$('difficulty').value='normal';$('snap').value='0';resetSession();
    fillChartFields();renderNotes();drawWaveform();$('library').close();await persist();
    navigator.storage?.persist?.().catch(()=>{});toast('音源を開きました。「録音開始」で曲に合わせてタップ。');
  } finally {loading=false;$('audioFile').value='';$('libraryAudioFile').value='';refresh();}
}
async function openProject(id) {
  if(loading)return;stop();loading=true;refresh();
  try {
    await audio.unlock();await persist();const next=await getProject(id);if(!next)throw new Error('保存した譜面が見つかりません。');
    const source=await getAudio(next.audioId);if(!source)throw new Error('保存した音源が見つかりません。');
    const parsed=parseChart(next.chart),buffer=await audio.decode(source.blob);
    project=next;chart=parsed;selected=null;history=new History();audio.buffer=buffer;audio.position=clamp(next.position||0,0,audio.duration);
    $('difficulty').value=next.difficulty||'normal';
    restoreSettings(next.settings);resetSession();
    fillChartFields();renderNotes();drawWaveform();preference('saber-last-project',next.id);$('library').close();status('この端末に保存済み');
  } finally {loading=false;refresh();}
}
async function showLibrary() {
  if(loading)return;
  stop();await persist();const projects=(await listProjects()).sort((a,b)=>b.updated-a.updated);$('projectList').replaceChildren();
  $('projectNameLabel').hidden=!project;$('projectName').value=project?.name||'';
  if(!projects.length){const p=document.createElement('p');p.className='hint';p.textContent='保存した曲はまだありません。';$('projectList').append(p);}
  for(const item of projects){
    const row=document.createElement('div');row.className='project';const button=document.createElement('button');button.className='open-project';
    const title=document.createElement('strong');title.textContent=item.name;const caption=document.createElement('small');
    caption.textContent=`${(item.difficulty||'normal').toUpperCase()} · ${item.chart?.notes?.length||0} notes · ${new Date(item.updated).toLocaleString('ja-JP')}`;
    button.append(title,caption);button.addEventListener('click',run(()=>openProject(item.id)));row.append(button);$('projectList').append(row);
  }
  $('library').showModal();
}
async function importChart(file) {
  if(!file||loading)return;if(!project)throw new Error('先に、この譜面に合う音源を開いてください。');
  if(file.size>10*1024*1024)throw new Error('譜面JSONは10MB以内にしてください。');
  stop();loading=true;refresh();
  try{
  const next=parseChart(await file.text());await persist();
  const difficulty=/chart_(easy|normal|hard)/i.exec(file.name)?.[1].toLowerCase()||$('difficulty').value;
  const replacement={...project,id:uid(),chart:next,difficulty,position:0,updated:Date.now()};await saveProject(replacement);
  project=replacement;chart=next;history=new History();selected=null;audio.seek(0);resetSession();$('difficulty').value=difficulty;$('snap').value='0';fillChartFields();changed();
  toast(hasVariableGrid(chart)?'テンポが変わる譜面です。元の時刻を維持し、拍への整列をOFFにしました。':'譜面を開きました。前の下書きは「曲・保存」に残っています。');
  }finally{loading=false;refresh();}
}
function downloadChart() {
  if(!project)return;stop();const blob=new Blob([JSON.stringify(exportChart(chart),null,2)+'\n'],{type:'application/json'});
  const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download=`chart_${$('difficulty').value}.json`;document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(a.href),60000);
  toast('譜面を書き出しました。スマホのダウンロード／ファイルを確認してください。');
}
function downloadAudio() {
  if(!audio.buffer)return;stop();
  const url=URL.createObjectURL(new Blob([encodeWav(audio.buffer)],{type:'audio/wav'})),link=document.createElement('a');
  link.href=url;link.download='audio.wav';document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),60000);
  toast('本編用のaudio.wavを書き出しました。譜面JSONと同じ曲フォルダーに置いてください。');
}
function renderNotes(centerTime) {
  const list=$('noteList');list.replaceChildren();$('emptyNotes').hidden=chart.notes.length>0;let start=0;
  if(centerTime!==undefined){const i=chart.notes.findIndex(n=>audioTime(n,chart)>=centerTime);start=Math.max(0,(i<0?chart.notes.length:i)-6);}
  else if(selected)start=Math.max(0,chart.notes.findIndex(n=>n.__editorId===selected)-6);else start=Math.max(0,chart.notes.length-100);
  if(start>0){const previous=document.createElement('button');previous.textContent='前のノーツ';previous.className='subtle';previous.addEventListener('click',()=>renderNotes(audioTime(chart.notes[Math.max(0,start-60)],chart)));list.append(previous);}
  for(const note of chart.notes.slice(start,start+100)){
    const row=document.createElement('button');row.className='note-row';row.classList.toggle('selected',note.__editorId===selected);row.dataset.noteId=note.__editorId;
    const dot=document.createElement('span');dot.className='note-dot';dot.style.setProperty('--note-color',COLORS[note.color]);
    const time=document.createElement('span');time.className='mono';time.textContent=formatTime(audioTime(note,chart),true);
    const kind=document.createElement('span');kind.textContent=note.count>1?`×${note.count}`:arrow(note.direction)||'TAP';
    const location=document.createElement('span');location.className='position';location.textContent=`${(note.x*chart.coordScale).toFixed(1)}, ${(note.y*chart.coordScale).toFixed(1)}`;
    row.append(dot,time,kind,location);row.setAttribute('aria-label',`${time.textContent} ${note.color} ${note.count}回`);row.addEventListener('click',()=>selectNote(note.__editorId));list.append(row);
  }
  if(start+100<chart.notes.length){const next=document.createElement('button');next.textContent='次のノーツ';next.className='subtle';next.addEventListener('click',()=>renderNotes(audioTime(chart.notes[start+100],chart)));list.append(next);}
  $('noteEditor').hidden=!selected;
}
function selectNote(id) {
  stop();const note=chart.notes.find(n=>n.__editorId===id);if(!note)return;selected=id;audio.seek(Math.max(0,audioTime(note,chart)-.4));
  $('noteTime').value=audioTime(note,chart).toFixed(3);$('noteColor').value=note.color;$('noteX').value=+(note.x*chart.coordScale).toFixed(3);$('noteY').value=+(note.y*chart.coordScale).toFixed(3);
  $('noteCuts').value=note.count;$('noteLength').value=longSeconds(note).toFixed(3);$('noteDirection').value=note.direction;renderNotes();refresh();refreshSelected();
  if(!$('noteDialog').open)$('noteDialog').showModal();
}
function refreshSelected() {
  const index=chart.notes.findIndex(n=>n.__editorId===selected),note=chart.notes[index];if(!note)return;
  $('selectedNoteLabel').textContent=`${index+1} / ${chart.notes.length} · ${formatTime(audioTime(note,chart),true)}`;
  $('previousNote').disabled=index===0;$('nextNote').disabled=index===chart.notes.length-1;
  $('positionMarker').style.left=`${clamp(note.x*chart.coordScale/5+.5,0,1)*100}%`;
  $('positionMarker').style.top=`${clamp(.5-note.y*chart.coordScale/3,0,1)*100}%`;
  $('positionMarker').style.background=COLORS[note.color];
  $('positionMarker').textContent=note.count>1?`×${note.count}`:arrow(note.direction);
}
function updateNote() {
  const note=chart.notes.find(n=>n.__editorId===selected);if(!note)return;
  const t=Number($('noteTime').value),x=Number($('noteX').value),y=Number($('noteY').value),count=Number($('noteCuts').value),length=Number($('noteLength').value);
  if(![t,x,y,count,length].every(Number.isFinite)||t<0||t>audio.duration||!Number.isInteger(count)||count<1||count>99||length<0)throw new Error('時刻・位置・回数・長さを確認してください。');
  if(count>1&&(length<=0||t+length>audio.duration+.001))throw new Error('ロングの長さを音源内に収めてください。');const direction=$('noteDirection').value;
  commit(()=>Object.assign(note,{time:t*1000-chart.offsetMs,beat:(t*1000-chart.offsetMs-chart.beatZeroMs)/(60000/chart.bpm),x:x/chart.coordScale,y:y/chart.coordScale,color:$('noteColor').value,direction,count,lengthMs:count>1?length*1000:0,type:count>1?'long':direction==='none'?'tap':'direction'}));refreshSelected();
}
function nudgeNote(milliseconds) {
  const length=Number($('noteCuts').value)>1?Number($('noteLength').value):0;
  $('noteTime').value=clamp(Number($('noteTime').value)+milliseconds/1000,0,Math.max(0,audio.duration-length)).toFixed(3);updateNote();
}
function moveSelected(event) {
  if(event.button!==undefined&&event.button!==0)return;event.preventDefault();
  const rect=$('positionPad').getBoundingClientRect();
  $('noteX').value=((clamp((event.clientX-rect.left)/rect.width,0,1)-.5)*5).toFixed(3);
  $('noteY').value=((.5-clamp((event.clientY-rect.top)/rect.height,0,1))*3).toFixed(3);updateNote();
}
function arrow(direction){return{up:'↑',down:'↓',left:'←',right:'→',upleft:'↖',upright:'↗',downleft:'↙',downright:'↘'}[direction]||'';}
function startTouch(event) {
  if(event.button!==undefined&&event.button!==0)return;event.preventDefault();
  if(loading||!audio.playing){toast('「録音開始」で曲を流してからタップしてください。');return;}
  if(mode!=='record'||!take||audio.rawTime(event.timeStamp)<audio.startOffset||audio.time(event.timeStamp)>=playbackEnd)return;
  if(chart.notes.length+touches.size>=50000){toast('ノーツ数の上限です。');return;}
  const rect=$('pad').getBoundingClientRect(),px=clamp((event.clientX-rect.left)/rect.width,0,1),py=clamp((event.clientY-rect.top)/rect.height,0,1);
  const marker=document.createElement('div');marker.className='touch-marker';marker.style.left=`${px*100}%`;marker.style.top=`${py*100}%`;
  const opts=settings(),chosen=opts.color==='auto'?(px<.5?'blue':'red'):opts.color;marker.style.color=COLORS[chosen];marker.textContent='•';$('touches').append(marker);
  touches.set(event.pointerId,{startAudio:audio.time(event.timeStamp),x:(px-.5)*5,y:(.5-py)*3,options:opts,marker});$('pad').setPointerCapture(event.pointerId);
  if($('clickSound').checked)audio.click(chosen==='blue'?570:chosen==='red'?760:950);
}
function endTouch(id,endAudio) {
  const touch=touches.get(id);if(!touch)return;touches.delete(id);touch.marker.remove();
  if(!take)return;
  if(!take.remembered){history.push(take.before);take.remembered=true;lastTake=null;}
  const note=take.add(makeNote(touch,Math.min(endAudio,take.end),chart,touch.options));chart=take.compose(endAudio);selected=null;changed();flashes.push({x:touch.x,y:touch.y,color:note.color,when:performance.now()});
}
function fitCanvas(canvas) {
  const dpr=Math.min(devicePixelRatio||1,2),w=canvas.clientWidth,h=canvas.clientHeight;
  if(canvas.width!==Math.round(w*dpr)||canvas.height!==Math.round(h*dpr)){canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);}
  const ctx=canvas.getContext('2d');ctx.setTransform(dpr,0,0,dpr,0,0);return{ctx,w,h};
}
function drawWaveform() {
  const{ctx,w,h}=fitCanvas($('waveform'));ctx.clearRect(0,0,w,h);if(!audio.buffer)return;
  const data=audio.buffer.getChannelData(0),step=Math.max(1,Math.floor(data.length/Math.max(1,w)));ctx.fillStyle='#576b8a';
  for(let x=0;x<w;x+=2){let peak=0;for(let i=x*step;i<Math.min(data.length,(x+2)*step);i+=Math.max(1,Math.floor(step/15)))peak=Math.max(peak,Math.abs(data[i]));ctx.fillRect(x,(h-peak*h)/2,1,Math.max(1,peak*h));}
}
function drawStage(t,now) {
  const{ctx,w,h}=fitCanvas($('stage'));ctx.clearRect(0,0,w,h);const vanish={x:w*.5,y:h*.18},gate=h*.8;ctx.strokeStyle='#263b56';ctx.lineWidth=1;
  for(let i=0;i<7;i++){ctx.beginPath();ctx.moveTo(vanish.x+(i-3)*5,vanish.y);ctx.lineTo(w*i/6,h);ctx.stroke();}
  for(let i=1;i<6;i++){const q=i/6,y=vanish.y+(h-vanish.y)*q*q;ctx.beginPath();ctx.moveTo(w*.5*(1-q*q),y);ctx.lineTo(w-w*.5*(1-q*q),y);ctx.stroke();}
  ctx.strokeStyle=mode==='record'&&audio.playing?'#ff6285':'#c3f36b';ctx.lineWidth=2;ctx.beginPath();ctx.moveTo(w*.10,gate);ctx.lineTo(w*.90,gate);ctx.stroke();
  const showing=chart.notes.filter(n=>audioTime(n,chart)>=t-Math.max(.12,longSeconds(n))&&audioTime(n,chart)<=t+1.8).reverse();
  for(const n of showing){
    const dt=audioTime(n,chart)-t,progress=clamp(1-dt/1.8,0,1),scale=.15+.85*progress*progress,x=vanish.x+n.x*chart.coordScale/5*w*.82*scale,y=vanish.y+(gate-vanish.y)*progress*progress-n.y*chart.coordScale/3*h*.55*scale;
    const size=Math.max(5,Math.min(w*.085,36)*scale);ctx.fillStyle=COLORS[n.color];ctx.globalAlpha=.3+.7*progress;ctx.fillRect(x-size/2,y-size/2,size,size);ctx.strokeStyle='#f4f8ff';ctx.lineWidth=1;ctx.strokeRect(x-size/2,y-size/2,size,size);
    ctx.globalAlpha=1;const label=n.count>1?String(n.count):arrow(n.direction);if(label){ctx.fillStyle='#071222';ctx.font=`bold ${Math.max(10,size*.65)}px system-ui`;ctx.textAlign='center';ctx.textBaseline='middle';ctx.fillText(label,x,y);}
    if(n.count>1){const remaining=clamp((longSeconds(n)+Math.min(0,dt))/Math.max(.01,longSeconds(n)),0,1);ctx.fillStyle=COLORS[n.color];ctx.fillRect(x-size/2,y+size/2+4,size*remaining,3);}
  }
  flashes=flashes.filter(f=>now-f.when<260);for(const f of flashes){const age=(now-f.when)/260;ctx.globalAlpha=1-age;ctx.strokeStyle=COLORS[f.color];ctx.lineWidth=2;const x=w/2+f.x/5*w*.82,y=gate-f.y/3*h*.55,size=22+age*36;ctx.strokeRect(x-size/2,y-size/2,size,size);}ctx.globalAlpha=1;
}
function tick(now) {
  if(audio.playing&&audio.rawTime()>=Math.min(audio.duration,playbackEnd)){stop();toast('区間の終わりまで再生しました。');}if(audio.playing&&audio.context.state!=='running'){stop();toast('音声が中断されたため、一時停止しました。');}
  const t=audio.time();
  if(audio.playing&&mode==='preview'&&$('clickSound').checked)for(const n of chart.notes){const hit=audioTime(n,chart);if(hit>lastFrameTime&&hit<=t&&t-lastFrameTime<.2)audio.click(n.color==='blue'?570:n.color==='red'?760:950);}
  lastFrameTime=t;$('timeReadout').textContent=formatTime(t,true);$('seek').value=t;
  const remaining=audio.playing?(audio.startedAt-audio.outputContextTime()):0,countdown=remaining>0?Math.ceil(remaining/(60/chart.bpm/audio.rate)):0;
  $('transportStatus').textContent=loading?'読み込み中':countdown>0?`カウント ${Math.min(4,countdown)}`:audio.playing?(mode==='record'?'録音中':'確認中'):'一時停止';
  $('stageMessage').textContent=loading?'音源を読み込み中…':!audio.buffer?'音源を開くと、曲に合わせて記録できます':countdown>0?String(Math.min(4,countdown)):'';
  for(const touch of touches.values()){const duration=Math.max(0,t-touch.startAudio);if(duration/touch.options.rate>=.24)touch.marker.textContent=`×${makeNote(touch,t,chart,touch.options).count}`;}
  drawStage(t,now);requestAnimationFrame(tick);
}
async function togglePlayback() {
  if(starting||loading)return;if(audio.playing){stop();return;}starting=true;refresh();
  try{
    const range=readRange();
    if(range)audio.seek(range.start);else if(audio.position>=audio.duration-.01)audio.seek(0);
    playbackEnd=range?.end??audio.duration;
    const started=await audio.play({rate:Number($('speed').value),countBeats:mode==='record'&&$('countIn').checked?4:0,bpm:chart.bpm,metronome:$('metronome').checked,beatOrigin:(chart.beatZeroMs+chart.offsetMs)/1000,end:playbackEnd});
    if(started===false)return;
    if(mode==='record')take=new RecordingTake(chart,{start:audio.startOffset,end:playbackEnd,replace:!!range&&$('replaceRange').checked});
    lastFrameTime=audio.time();
  }finally{starting=false;refresh();}
}
function readRange() {
  if(!$('rangeEnabled').checked)return null;
  const start=Number($('rangeStart').value),end=Number($('rangeEnd').value);
  if(!Number.isFinite(start)||!Number.isFinite(end)||start<0||end>audio.duration+.001||end-start<.1)throw new Error('区間は音源内で、AよりBを0.1秒以上後にしてください。');
  return{start,end:Math.min(end,audio.duration)};
}
async function retryTake() {
  if(!lastTake||audio.playing||loading||starting)return;
  readRange();
  const previous=lastTake;history.push(chart);chart=clone(previous.before);lastTake=null;selected=null;audio.seek(previous.start);changed();
  mode='record';await togglePlayback();
}
function setColor(value) {
  color=value;for(const button of document.querySelectorAll('[data-color]')){button.classList.toggle('active',button.dataset.color===value);button.setAttribute('aria-pressed',button.dataset.color===value);}
}
function seek(value){stop();audio.seek(value);lastFrameTime=audio.time();refresh();}
$('audioFile').addEventListener('change',run(e=>loadAudioFile(e.target.files[0])));$('libraryAudioFile').addEventListener('change',run(e=>loadAudioFile(e.target.files[0])));
$('chartFile').addEventListener('change',run(async e=>{try{await importChart(e.target.files[0]);}finally{e.target.value='';}}));
$('libraryButton').addEventListener('click',run(showLibrary));$('helpButton').onclick=()=>{$('help').showModal();};for(const b of document.querySelectorAll('[data-close]'))b.onclick=()=>$(b.dataset.close).close();
$('projectName').addEventListener('change',run(async()=>{if(project){project.name=$('projectName').value.trim()||project.audioName;refresh();await persist();}}));
$('playButton').addEventListener('click',run(togglePlayback));$('backButton').onclick=()=>seek(audio.time()-5);$('forwardButton').onclick=()=>seek(audio.time()+5);$('seek').addEventListener('input',e=>seek(Number(e.target.value)));
$('recordMode').onclick=()=>{stop();mode='record';refresh();};$('previewMode').onclick=()=>{stop();mode='preview';refresh();};
$('focusButton').onclick=()=>{document.body.classList.toggle('focus');$('focusButton').textContent=document.body.classList.contains('focus')?'戻る':'集中';$('focusButton').setAttribute('aria-pressed',document.body.classList.contains('focus'));drawWaveform();};
for(const b of document.querySelectorAll('[data-color]'))b.onclick=()=>{setColor(b.dataset.color);if(project){clearTimeout(saveTimer);saveTimer=setTimeout(()=>persist().catch(fail),180);}};
$('pad').addEventListener('pointerdown',startTouch);$('pad').addEventListener('pointerup',e=>{e.preventDefault();endTouch(e.pointerId,audio.time(e.timeStamp));});
for(const event of ['pointercancel','lostpointercapture'])$('pad').addEventListener(event,e=>{touches.get(e.pointerId)?.marker.remove();touches.delete(e.pointerId);});$('pad').addEventListener('contextmenu',e=>e.preventDefault());
$('undoButton').onclick=()=>{stop();lastTake=null;chart=history.undo(chart);selected=null;fillChartFields();changed();};$('redoButton').onclick=()=>{stop();lastTake=null;chart=history.redo(chart);selected=null;fillChartFields();changed();};
$('nearestButton').onclick=()=>renderNotes(audio.time());$('updateNote').addEventListener('click',run(()=>{updateNote();toast('ノーツを変更しました。');}));$('deleteNote').onclick=()=>{stop();commit(()=>{chart.notes=chart.notes.filter(n=>n.__editorId!==selected);selected=null;});$('noteDialog').close();toast('ノーツを削除しました。「戻す」で復元できます。');};$('noteDirection').innerHTML=$('direction').innerHTML;$('exportButton').onclick=downloadChart;
$('noteEditorHost').append($('noteEditor'));
$('positionPad').addEventListener('pointerdown',run(moveSelected));$('positionPad').addEventListener('contextmenu',event=>event.preventDefault());
$('earlierNote').addEventListener('click',run(()=>nudgeNote(-10)));$('laterNote').addEventListener('click',run(()=>nudgeNote(10)));
$('snapNote').addEventListener('click',run(()=>{
  const step=Number($('snap').value)||.25,time=Number($('noteTime').value)*1000-chart.offsetMs;
  const snapped=(quantizeMs(time,chart,step)+chart.offsetMs)/1000;
  nudgeNote((snapped-Number($('noteTime').value))*1000);toast(Number($('snap').value)?'設定した拍にそろえました。':'16分音符にそろえました。');
}));
for(const[id,delta]of[['previousNote',-1],['nextNote',1]])$(id).onclick=()=>{const index=chart.notes.findIndex(n=>n.__editorId===selected),next=chart.notes[index+delta];if(next)selectNote(next.__editorId);};
$('retryTake').addEventListener('click',run(retryTake));
for(const id of ['rangeStart','rangeEnd','rangeEnabled','replaceRange'])$(id).addEventListener('change',()=>{lastTake=null;refresh();});
for(const[id,field]of[['setRangeStart','rangeStart'],['setRangeEnd','rangeEnd']])$(id).onclick=()=>{$(field).value=audio.time().toFixed(3);lastTake=null;refresh();};
$('tapTempo').onclick=()=>{const result=tapTempo.tap(performance.now());$('tempoReadout').textContent=result.bpm?`${result.bpm} BPM · ${result.count}回`:`${result.count}回 · あと${Math.max(1,4-result.count)}回`; $('applyTempo').disabled=!result.bpm||loading;};
$('applyTempo').onclick=()=>{const result=tapTempo.result();if(!result.bpm||loading)return;stop();commit(()=>chart.bpm=result.bpm);fillChartFields();toast('BPMを設定しました。録音済みノーツの時刻はそのままです。');};
for(const[id,field,min,max]of[['bpm','bpm',20,400],['offset','offsetMs',-600000,600000],['beatZero','beatZeroMs',-600000,600000],['level','displayLevel',0,10]])$(id).addEventListener('change',run(()=>{stop();const v=Number($(id).value);if(!Number.isFinite(v)||v<min||v>max){fillChartFields();throw new Error('設定値の範囲を確認してください。');}commit(()=>{chart[field]=v;});}));
for(const id of ['speed','snap','longCount','direction','latency','difficulty','metronome','countIn','clickSound'])$(id).addEventListener('change',()=>{stop();refresh();if(id==='snap'&&Number($('snap').value)>0&&hasVariableGrid(chart))toast('テンポ変化のある曲では、固定BPMへの整列でずれることがあります。');});
document.addEventListener('visibilitychange',()=>{if(document.hidden){stop();persist().catch(fail);}});window.addEventListener('pagehide',()=>{stop();persist().catch(()=>{});});window.addEventListener('beforeunload',e=>{if(saving||saveFailed||saveTimer||touches.size){e.preventDefault();e.returnValue='';}});window.addEventListener('resize',drawWaveform);
window.addEventListener('keydown',run(async e=>{if(['INPUT','SELECT','TEXTAREA','BUTTON'].includes(e.target.tagName)||document.querySelector('dialog[open]'))return;if(e.code==='Space'){e.preventDefault();if(audio.buffer)await togglePlayback();}if((e.ctrlKey||e.metaKey)&&e.key==='z'){e.preventDefault();$(e.shiftKey?'redoButton':'undoButton').click();}}));
$('exportAudioButton').addEventListener('click',run(downloadAudio));
async function setupOffline(){
  if(!('serviceWorker'in navigator)||!window.isSecureContext){$('offlineStatus').textContent='オフライン機能はHTTPSで使用できます';return;}
  try{
    const hadController=!!navigator.serviceWorker.controller;
    navigator.serviceWorker.addEventListener('controllerchange',()=>{if(hadController){$('updateApp').hidden=false;$('updateBanner').hidden=false;}});
    const registration=await navigator.serviceWorker.register('./sw.js');
    $('updateApp').onclick=run(async()=>{stop();await persist();location.reload();});
    registration.update().catch(()=>{});
    await navigator.serviceWorker.ready;$('offlineStatus').textContent='オフラインで使用できます';
  }catch{$('offlineStatus').textContent='オフライン準備に失敗 — オンラインで再度開いてください';}
}
// エージェント向け操作も画面と同じ状態・操作を使う。
function registerTools(){
  const context=document.modelContext;if(!context?.registerTool)return;const lifecycle=new AbortController();window.addEventListener('pagehide',()=>lifecycle.abort(),{once:true});
  const tools=[{name:'read_chart_summary',description:'開いている譜面の音源名・難易度・ノーツ数と再生位置を読み取る。音源データは返さない。',inputSchema:{type:'object',properties:{},additionalProperties:false},annotations:{readOnlyHint:true,untrustedContentHint:true},execute:()=>({song:project?.name||null,difficulty:$('difficulty').value,bpm:chart.bpm,notes:chart.notes.length,position:audio.time(),playing:audio.playing})},
  {name:'seek_chart_preview',description:'譜面の指定秒へ移動して一時停止する。ノーツは変更しない。',inputSchema:{type:'object',properties:{seconds:{type:'number',minimum:0}},required:['seconds'],additionalProperties:false},annotations:{readOnlyHint:false,untrustedContentHint:false},execute:input=>{if(!input||typeof input.seconds!=='number'||!Number.isFinite(input.seconds)||input.seconds<0||!audio.buffer||input.seconds>audio.duration)throw new Error('読み込み済み音源内の秒数を指定してください。');seek(input.seconds);return{position:audio.time(),playing:false};}}];
  for(const tool of tools){try{Promise.resolve(context.registerTool(tool,{signal:lifecycle.signal})).catch(console.warn);}catch(e){console.warn(e);}}
}
async function init(){
  refresh();fillChartFields();renderNotes();requestAnimationFrame(tick);setupOffline();registerTools();
  try{const projects=await listProjects(),last=preference('saber-last-project');if(projects.length){const recent=projects.find(p=>p.id===last)||projects.sort((a,b)=>b.updated-a.updated)[0];status(`前回の下書き：${recent.name} — 「曲・保存」から再開`);}}catch{status('端末の保存が利用できません。ブラウザの通常モードで開いてください。');}
}
init();
