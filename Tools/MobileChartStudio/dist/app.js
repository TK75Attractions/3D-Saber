import { installWorkspace } from './workspace.js';
import { tempoIsSet, freshSettings, barSeek, barSpan, editNote, timingSummary, differentSongSettings } from './mobile.js';
import { paintVerticalTimeline, verticalHits, verticalTime } from './vertical-timeline.js';
import { installRefinementUI } from './refinement-ui.js';
import { positionInput, safeBookmarks } from './refinement.js';
import { COLORS, clamp, clone, blankChart, parseChart, exportChart, audioTime, longSeconds, makeNote, formatTime, hasVariableGrid, rebeat, quantizeMs, History } from './core.js';
import { RecordingTake, TapTempo, rangeNotes, transformRange, copyChart } from './editing.js';
import { createBackup, readBackup } from './backup.js';
import { timelineWindow, visibleNotes, paintTimeline, timelineHit } from './timeline.js';
import { beginStroke, moveStroke, flickDirection, inputPreferences } from './gestures.js';
import { SongAudio } from './audio.js';
import { encodeWav } from './wav.js';
import { saveProject, listProjects, getProject, saveAudio, getAudio, saveProjectWithAudio, contentOf } from './storage.js';
const $ = id => document.getElementById(id), audio = new SongAudio();
// 画面に出す版。sw.js のキャッシュ名(saber-tap-shell-<版>)と同じにする(tests/offline.test.js が確かめる)。
const APP_VERSION='v17-20261022';
let refinementUI, workspaceUI;
let bpmConfirmed=true;
let chart = blankChart(), history = new History(), project = null, selected = null;
let mode = 'record', color = 'auto', loading = false, saving = false, saveFailed = false, starting = false;
let saveTimer, toastTimer, persistChain = Promise.resolve(), flashes = [], lastFrameTime = 0;
let take = null, lastTake = null, playbackEnd = Infinity, saveRevision = 0, pendingSaves = 0, loopingRange = false, tempoListening = false, copySource = null, copyCreating = false;
const tapTempo = new TapTempo();
const touches = new Map();
let mapStart=0, mapBoxes=[], mapLastPaint=0, batchRange=null, mapShown=null, savedContent=null, review=null, fieldFailures=0;
const uid = () => crypto.randomUUID?.() || `p${Date.now()}-${Math.random().toString(36).slice(2)}`;
const settings = () => ({ bpmConfirmed, difficulty:$('difficulty').value, longThresholdMs:Number($('longThresholdMs')?.value)||300, positionGrid:Number($('positionGrid').value), songVolume:Number($('songVolume').value), feedbackVolume:Number($('feedbackVolume').value), previewLoop:$('previewLoop').checked, snap: Number($('snap').value), snapMode:$('snapMode').value, snapWindowMs:Number($('snapWindowMs').value), flickEnabled:$('flickEnabled').checked, flickSensitivity:$('flickSensitivity').value, rate: Number($('speed').value), longCount: $('longCount').value, direction: $('direction').value, color, latencyMs: clamp(Number($('latency').value)||0,-500,500), countIn:$('countIn').checked, clickSound:$('clickSound').checked, metronome:$('metronome').checked });
const preference = (key, value) => { try { if (value !== undefined) localStorage.setItem(key, value); else return localStorage.getItem(key); } catch {} };
// 知らせは画面のいちばん上(曲名の帯)に出し、録音開始・パッド・グラフを隠さない。長い文ほど長く出す(2.5〜6秒)。
function toast(message) {
  const notice=$('toast'), dialog=[...document.querySelectorAll('dialog:modal')].at(-1);
  (dialog || document.body).append(notice);notice.textContent=message;notice.hidden=false;
  clearTimeout(toastTimer);toastTimer=setTimeout(()=>notice.hidden=true,clamp(2500+String(message).length*45,2500,6000));
}
function fail(error) { console.error(error); toast(error?.message || '処理に失敗しました。内容を確認してください。'); }
function run(action) { return async event => { try { await action(event); } catch (e) { fail(e); } }; }
function status(message) { $('saveStatus').textContent = message; $('saveStatus').classList.toggle('save-error',saveFailed); }
function refresh() {
  document.body.classList.toggle('previewing',mode==='preview');
  $('songTitle').textContent = project?.name || '曲を読み込んではじめよう';
  $('difficultyLabel').textContent = project ? `${$('difficulty').value.toUpperCase()} / ${bpmConfirmed?chart.bpm+' BPM':'BPM未設定'}` : 'NEW CHART';
  $('noteCount').textContent = `${chart.notes.length} NOTES`;
  $('undoButton').disabled = !history.past.length || audio.playing || loading;
  $('redoButton').disabled = !history.future.length || audio.playing || loading;
  $('playButton').disabled = !audio.buffer || loading || starting;
  $('exportButton').disabled = !project || loading;
  $('exportAudioButton').disabled = !audio.buffer || loading;
  $('libraryButton').disabled = loading;
  for(const id of ['timelineButton','batchButton','duplicateButton','backupButton'])$(id).disabled=!project||loading||starting;$('createCopy').disabled=!project&&!copySource||loading||starting||copyCreating;
  $('retryTake').disabled = !lastTake || audio.playing || loading || starting;
  $('takeStatus').textContent = take ? `今回 ${take.notes.length} ノーツ${take.replace ? ' · 区間を録り直し中' : ''}` : lastTake ? `直前 ${lastTake.count}ノーツ${lastTake.timing?' · 拍との差 平均'+Math.round(lastTake.timing.meanAbsolute)+'ms（目安）':''}` : '1回の録音をまとめて戻せます';
  for (const id of ['rangeStart','rangeEnd','rangeEnabled','replaceRange','setRangeStart','setRangeEnd']) $(id).disabled = !audio.buffer || loading || audio.playing || starting;
  $('replaceRange').disabled ||= !$('rangeEnabled').checked;
  $('rangeHint').textContent = $('rangeEnabled').checked && $('replaceRange').checked ? '入力したら、再生した区間の既存ノーツを置き換えます。途中停止では、その先を残します。「元に戻す」で復元できます。' : 'Aから始まりBで自動停止。ノーツは追加されます。';
  $('playButton').textContent = audio.playing ? '一時停止' : mode === 'record' ? '録音開始' : '再生';
  $('playButton').classList.toggle('recording', audio.playing && mode === 'record');
  for(const name of ['record','preview']){ $(name+'Mode').classList.toggle('active',mode===name);$(name+'Mode').setAttribute('aria-pressed',mode===name); }
  $('padHint').textContent = mode === 'record' ? $('flickEnabled').checked ? 'タップで配置、素早くはらうとフリック、長押しでロング。2本指も使えます。' : '押した場所にノーツを配置。長押しでロング、2本指で同時押し。' : '確認モード。ノーツの配置とタイミングを見返せます。タップでは追加されません。';
  refreshInputOptions();
  $('seek').max = audio.duration || 1; $('durationReadout').textContent = formatTime(audio.duration);
  if(workspaceUI?.active==='edit')renderMap();
  refinementUI?.refresh();
  workspaceUI?.refresh({project,loading,lastTake,playing:audio.playing});
  const byBar=bpmConfirmed&&!hasVariableGrid(chart);for(const[id,sign]of [['backButton','−'],['forwardButton','＋']]){$(id).textContent=sign+(byBar?'1小節':'5秒');$(id).setAttribute('aria-label',$(id).textContent); }
  const listen=$('tempoListen');if(listen){listen.textContent=audio.playing?'止める':'曲を流す';listen.disabled=!audio.buffer||loading||starting||(audio.playing&&mode==='record');}
}
function refreshInputOptions() {
  const mode=$('snapMode').value;
  $('padCenter').textContent=$('flickEnabled').checked?'TAP / FLICK / HOLD':'TAP / HOLD';
  const sides=color==='auto'?'左は青、右は赤。':`どこを押しても${{blue:'青',red:'赤',gold:'金'}[color]}。`;
  $('pad').setAttribute('aria-label',`ノーツ入力パッド。${sides}${$('flickEnabled').checked?'タップで配置、素早くはらうとフリック、長押しでロング。':'タップで配置、長押しでロング。フリック入力はOFF。'}`);
  $('snapMode').disabled=!bpmConfirmed; $('snapNote').disabled=!bpmConfirmed; $('countIn').disabled=!bpmConfirmed; $('metronome').disabled=!bpmConfirmed; $('snap').disabled=mode==='off'||!bpmConfirmed;$('snapWindowMs').disabled=mode!=='near';$('flickSensitivity').disabled=!$('flickEnabled').checked;
  $('snapHint').textContent=!bpmConfirmed?'BPM未設定：打った時刻をそのまま記録します。数値入力かタップでBPMを設定してください。':mode==='near'?'拍の近くだけ補正し、離れた入力はそのまま記録します。BPMと拍の始まりを合わせてください。':mode==='always'?'すべての新しい入力を、選んだ拍の刻みにそろえます。':'タップしたタイミングをそのまま記録します。';
}
function fillChartFields() { $('bpm').value=bpmConfirmed?chart.bpm:'';$('bpm').placeholder='未設定';$('offset').value=chart.offsetMs;$('beatZero').value=chart.beatZeroMs;$('level').value=chart.displayLevel; }
function snapshotProject() { return {...project,chart:clone(chart),difficulty:$('difficulty').value,position:audio.time(),settings:settings(),updated:Date.now()}; }
function editedContent(value) { return contentOf({...value,lastExportedAt:null}); }
function persist() {
  clearTimeout(saveTimer);saveTimer=null; if(!project)return Promise.resolve(); const value=snapshotProject(),revision=++saveRevision;
  // 最終更新は中身(譜面・設定・名前・難易度)が変わったときだけ進める。再生位置だけの保存・一覧を開くだけ・書き出しの記録では前の値のまま。
  const content=editedContent(value);if(content===savedContent&&project.updated)value.updated=project.updated;
  pendingSaves++;
  saving=true;status('保存中…');
  persistChain=persistChain.catch(()=>{}).then(()=>saveProject(value)).then(()=>{
    if(project?.id===value.id){savedContent=content;project.updated=value.updated;}
    saving=--pendingSaves>0;if(project?.id===value.id&&revision===saveRevision&&!saveTimer){saveFailed=false;status('この端末に保存済み');}if(project?.id===value.id)preference('saber-last-project',value.id);
  },error=>{saving=--pendingSaves>0;saveFailed=true;status(error.name==='ConflictError'?'別タブで更新 — ここを押して書き出してから再読み込み':'未保存 — ここを押して譜面かバックアップを書き出す');if(error.name==='ConflictError')throw error;throw new Error('端末に保存できませんでした。空き容量を確認し、譜面を書き出して保管してください。',{cause:error});});
  return persistChain;
}
function changed() {chart.notes.sort((a,b)=>a.time-b.time);refresh();renderNotes();refreshTabDetails();status('保存待ち…');clearTimeout(saveTimer);saveTimer=setTimeout(()=>persist().catch(fail),180);}
// 開いている「確かめる」の診断と「渡す」の注意を、譜面・設定・曲が変わったらその場で描き直す(タブを往復しなくてよい)。
function refreshTabDetails() {
  if(!project||!workspaceUI)return;
  if(workspaceUI.active==='preview')refinementUI.report({keepPlaying:true});else if(workspaceUI.active==='export')refreshExportWarnings().catch(fail);
}
// 履歴には、変える前の譜面と「BPMを確定したか」を一緒に残す(元に戻すで「未設定」にも戻れる)。
function commit(action) {lastTake=null;history.push(chart,{bpmConfirmed});action();changed();}
function cancelTouches() {for(const touch of touches.values())touch.marker.remove();touches.clear();}
function stop({finish=true}={}) {
  const end=Math.min(audio.time(),playbackEnd);
  if(finish&&audio.playing)for(const pointerId of [...touches.keys()])endTouch(pointerId,end);else cancelTouches();
  audio.pause();audio.position=end;playbackEnd=Infinity;loopingRange=false;
  if(take){if(take.notes.length){chart=take.compose(end);lastTake={before:take.before,start:take.start,end,count:take.notes.length,timing:bpmConfirmed?timingSummary(take.notes,chart):null};changed();}take=null;}
  refresh();if(project){clearTimeout(saveTimer);saveTimer=setTimeout(()=>persist().catch(fail),100);}
}
function resetSession() {
  take=null;lastTake=null;tapTempo.reset();$('tempoReadout').textContent='曲の拍に合わせて4回以上';$('applyTempo').disabled=true;
  $('rangeEnabled').checked=false;$('replaceRange').checked=false;$('rangeStart').value=0;$('rangeEnd').value=audio.duration.toFixed(3);$('noteDialog').close();
  $('inputFeedback').textContent='タップ・フリック・ロングの入力結果を表示します';$('inputFeedback').classList.remove('has-result');
  mapStart=0;batchRange=null;for(const id of ['batchDialog','duplicateDialog'])$(id).close();
}
function restoreSettings(value={}) {
  bpmConfirmed=tempoIsSet(value);if($('longThresholdMs'))$('longThresholdMs').value=clamp(Number(value.longThresholdMs)||300,150,1000);
  $('positionGrid').value=[.25,.5,1].includes(value.positionGrid)?value.positionGrid:0;
  $('songVolume').value=Number.isFinite(value.songVolume)?clamp(value.songVolume,0,1):1;$('feedbackVolume').value=Number.isFinite(value.feedbackVolume)?clamp(value.feedbackVolume,0,1):1;
  audio.setVolumes(Number($('songVolume').value),Number($('feedbackVolume').value));$('previewLoop').checked=value.previewLoop===true;
  const input=inputPreferences(value);for(const id of ['snap','snapMode','snapWindowMs','flickSensitivity'])$(id).value=input[id];$('flickEnabled').checked=input.flickEnabled;
  $('longCount').value=value.longCount||'auto';$('direction').value=value.direction||'none';
  $('speed').value=[1,.75,.5].includes(Number(value.rate))?value.rate:1;
  $('latency').value=value.latencyMs||0;
  for(const id of ['countIn','clickSound','metronome'])$(id).checked=value[id]??(id!=='metronome');
  setColor(['auto','blue','red','gold'].includes(value.color)?value.color:'auto');
}
async function loadAudioFile(file) {
  if(!file||loading)return;stop();loading=true;refresh();status('音源を読み込み中…');
  try {
    await audio.unlock();const buffer=await audio.decode(file);await persist();
    const id=uid();
    const next={id:uid(),audioId:id,name:file.name.replace(/\.[^.]+$/,''),audioName:file.name,difficulty:'normal',settings:freshSettings(),position:0,updated:Date.now(),chart:blankChart()};
    await saveProjectWithAudio(next,{id,blob:file,name:file.name});project=next;chart=blankChart();history=new History();selected=null;
    audio.buffer=buffer;audio.position=0;$('difficulty').value='normal';restoreSettings(next.settings);resetSession();workspaceUI.activate('record');
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
    savedContent=editedContent(snapshotProject());
  } finally {loading=false;refresh();refreshTabDetails();}
}
async function showLibrary() {
  if(loading)return;
  // 保存に失敗しても一覧は開く(知らせは出す)。別の下書きを開く操作は、保存できるまで止めて手元の編集を守る(openProject)。
  stop();await persist().catch(fail);const projects=(await listProjects()).sort((a,b)=>b.updated-a.updated);$('projectList').replaceChildren();
  $('projectNameLabel').hidden=!project;$('projectName').value=project?.name||'';
  if(!projects.length){const p=document.createElement('p');p.className='hint';p.textContent='保存した曲はまだありません。';$('projectList').append(p);}
  const groups=new Map();for(const item of projects){if(!groups.has(item.audioId)){const group=document.createElement('section');group.className='project-group';const title=document.createElement('h3');title.textContent=item.name;group.append(title);$('projectList').append(group);groups.set(item.audioId,group);}}
  for(const item of projects){
    const row=document.createElement('div');row.className='project';const button=document.createElement('button');button.className='open-project';
    const title=document.createElement('strong');title.textContent=item.name;const caption=document.createElement('small');
    caption.textContent=`${item.id===project?.id?'編集中 · ':''}${(item.difficulty||'normal').toUpperCase()} · ${item.lastExportedAt?'前回出力 '+new Date(item.lastExportedAt).toLocaleDateString('ja-JP'):'未出力'} · ${item.chart?.notes?.length||0} notes · ${new Date(item.updated).toLocaleString('ja-JP')}`;
    button.append(title,caption);button.addEventListener('click',run(()=>openProject(item.id)));row.append(button);refinementUI?.decorateProject(row,item);groups.get(item.audioId).append(row);
  }
  for(const [audioId,group] of groups){const siblings=projects.filter(p=>p.audioId===audioId),missing=['easy','normal','hard'].filter(d=>!siblings.some(p=>p.difficulty===d));for(const difficulty of missing){const b=document.createElement('button');b.className='subtle';b.textContent='＋ '+difficulty.toUpperCase();b.addEventListener('click',()=>showCopyDialog(project?.audioId===audioId?null:siblings[0],difficulty));group.append(b);}}
  if(!$('library').open)$('library').showModal();refinementUI?.refreshLibrary();
}
async function importChart(file) {
  if(!file||loading)return;if(!project)throw new Error('先に、この譜面に合う音源を開いてください。');
  if(file.size>10*1024*1024)throw new Error('譜面JSONは10MB以内にしてください。');
  stop();loading=true;refresh();
  try{
  const next=parseChart(await file.text());await persist();
  const difficulty=/chart_(easy|normal|hard)/i.exec(file.name)?.[1].toLowerCase()||$('difficulty').value;
  const replacement={...project,id:uid(),_revision:0,lastExportedAt:null,chart:next,difficulty,settings:{...settings(),bpmConfirmed:true,snapMode:'off'},position:0,updated:Date.now()};await saveProject(replacement);
  project=replacement;chart=next;bpmConfirmed=true;history=new History();selected=null;audio.seek(0);resetSession();$('difficulty').value=difficulty;$('snapMode').value='off';fillChartFields();changed();
  // 一覧は開く前の内容(編集中の印も前の下書き)のままなので閉じる。別の下書きを開いたときと同じ。
  $('library').close();
  toast(`${file.name} を「${project.name}」の ${difficulty.toUpperCase()} の新しい下書きとして開きました。前の下書きも一覧に残っています。${hasVariableGrid(chart)?'テンポが変わる譜面なので、元の時刻を維持し、拍への整列をOFFにしました。':''}`);
  }finally{loading=false;refresh();}
}
function downloadChart() {
  if(!project)return;stop();const blob=new Blob([JSON.stringify(exportChart(chart),null,2)+'\n'],{type:'application/json'});
  const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download=`chart_${$('difficulty').value}.json`;document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(a.href),60000);
  project.lastExportedAt=Date.now();persist().catch(fail);
  toast(bpmConfirmed?'譜面を書き出しました。スマホのダウンロード／ファイルを確認してください。':'BPM未設定のため互換値120で書き出しました。録音した時刻は維持しています。UnityでBPMを確認してください。');
}
async function refreshExportWarnings(){
  const current=project;if(!current)return;
  const siblings=(await listProjects()).filter(p=>p.audioId===current.audioId&&p.id!==current.id&&p.difficulty!==$('difficulty').value);
  if(project?.id!==current.id)return;
  const differing=[...new Set(siblings.filter(p=>differentSongSettings(chart,p.chart).length).map(p=>p.difficulty.toUpperCase()))];
  const messages=[];if(!chart.notes.length)messages.push('ノーツが0個です。「録る」でタップしてから書き出してください。');if(!bpmConfirmed)messages.push('BPM未設定です。JSONには互換値120が入ります。入力時刻は変えません。Unityへ渡す前に曲のBPMを確認してください。');
  if(differing.length)messages.push(differing.join('・')+'の下書きとBPM・OFFSET・拍の始まり・拍子・座標倍率のいずれかが異なります。意図した違いか設定で確認してください。');
  $('exportWarnings').textContent=messages.join('\n');$('exportWarnings').hidden=!messages.length;
  $('exportNames').textContent=exportNames(current,$('difficulty').value);
  // ② 同じ音源: ゲームが読める形式(mp3/ogg/wav)なら「元の音源を保存」、それ以外は「音源をWAV保存」を主ボタンにする。
  const direct=readableAudio(current.audioName);$('exportOriginalAudio').classList.toggle('primary',direct);$('exportAudioButton').classList.toggle('primary',!direct);
}
function readableAudio(name){return['mp3','ogg','wav'].includes((/\.([^.]+)$/.exec(name||'')?.[1]||'').toLowerCase());}
// 渡すときの実際のファイル名と、ゲームの曲フォルダーでの名前(元の音源は名前を変える。読めない形式はWAV保存)。
function exportNames(current,difficulty){
  const lines=[`譜面：chart_${difficulty}.json`],name=current.audioName||'',ext=(/\.([^.]+)$/.exec(name)?.[1]||'').toLowerCase();
  if(name)lines.push(readableAudio(name)?`元の音源：${name} → PCで audio.${ext} に名前を変える`:`元の音源：${name} はゲームで読めない形式です。「音源をWAV保存」で audio.wav を作ってください`);
  return lines.join('\n');
}
function downloadAudio() {
  if(!audio.buffer)return;stop();
  const url=URL.createObjectURL(new Blob([encodeWav(audio.buffer)],{type:'audio/wav'})),link=document.createElement('a');
  link.href=url;link.download='audio.wav';document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),60000);
  toast('本編用のaudio.wavを書き出しました。譜面JSONと同じ曲フォルダーに置いてください。');
}
function downloadBlob(blob,name) {
  const url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download=name;document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),60000);
}
// 端末への保存に失敗していても、手元の譜面と保存済みの音源でバックアップを作る(保存できないときこそ逃がし先が要る)。
async function downloadBackup() {
  if(!project||loading)return;stop();const value=snapshotProject();loading=true;refresh();
  try{
    let saveError=null;await persist().catch(error=>{saveError=error;});const source=await getAudio(value.audioId);
    const safeName=value.name.replace(/[<>:"/\\|?*\u0000-\u001f]/g,'_').slice(0,80)||'chart';
    downloadBlob(createBackup(value,source),`Saber-${safeName}-${value.difficulty}.saber`);
    toast(saveError?'端末への保存は失敗しましたが、今の譜面でバックアップを作りました。ファイルを保管してください。':'音源・譜面・設定をバックアップしました。別の端末では「バックアップを開く」で復元できます。');
  }finally{loading=false;refresh();}
}
async function restoreBackup(file) {
  if(!file||loading)return;stop();loading=true;refresh();status('バックアップを確認中…');
  try{
    const value=await readBackup(file),buffer=await audio.decode(value.audio.blob);await persist();
    const audioId=uid(),next={id:uid(),audioId,audioName:value.audio.name,name:value.name,difficulty:value.difficulty,chart:value.chart,settings:value.settings,bookmarks:safeBookmarks(value.bookmarks,buffer.duration),position:clamp(value.position,0,buffer.duration),updated:Date.now()};
    await saveProjectWithAudio(next,{id:audioId,...value.audio});
    project=next;chart=next.chart;history=new History();selected=null;audio.buffer=buffer;audio.seek(next.position);
    $('difficulty').value=next.difficulty;restoreSettings(next.settings);resetSession();fillChartFields();renderNotes();drawWaveform();$('library').close();await persist();
    toast('新しい下書きとして復元しました。元の下書きも残っています。');
  }catch(error){status(project?'読み込みを中止しました。現在の下書きはそのままです。':'バックアップを読み込めませんでした。');throw error;}
  finally{loading=false;refresh();refreshTabDetails();}
}
// 別の難易度・コピーを作る。source は「曲・保存」の「＋難易度」で、開いていない曲の下書きを元にするとき(作るまで今の下書きは切り替えない)。
function showCopyDialog(source=null,difficulty=null) {
  if(loading||!source&&!project)return;stop();$('library').close();copySource=source;
  const from=source?{name:source.name,difficulty:source.difficulty||'normal',notes:source.chart?.notes?.length||0,bpm:tempoIsSet(source.settings||{})?source.chart?.bpm:null,offset:source.chart?.offsetMs||0}:{name:project.name,difficulty:$('difficulty').value,notes:chart.notes.length,bpm:bpmConfirmed?chart.bpm:null,offset:chart.offsetMs};
  $('copyName').value=difficulty?from.name:from.name+' のコピー';$('copyDifficulty').value=difficulty||(from.difficulty==='normal'?'hard':'normal');$('copyContent').value=difficulty?'empty':'copy';
  $('copySource').textContent=`元にする下書き：${from.name} · ${from.difficulty.toUpperCase()} · ${from.notes} notes · ${from.bpm?`BPM ${from.bpm}`:'BPM未設定'} · OFFSET ${from.offset}ms${source?'':'（いま開いている下書き）'}。BPM・OFFSET・拍の始まり・拍子・座標倍率を引き継ぎます。`;
  refresh();$('duplicateDialog').showModal();
}
async function createProjectCopy() {
  if(copySource)return createFromStored(copySource);
  if(!project||loading)return;stop();loading=true;refresh();
  try{
    const next={...snapshotProject(),id:uid(),_revision:0,lastExportedAt:null,chart:copyChart(chart,$('copyContent').value==='empty'),name:$('copyName').value.trim().slice(0,120)||project.name+' のコピー',difficulty:$('copyDifficulty').value,position:0,updated:Date.now()};
    if(!['easy','normal','hard'].includes(next.difficulty))throw new Error('難易度を選んでください。');
    await persist();await saveProject(next);project=next;chart=next.chart;history=new History();selected=null;audio.seek(0);$('difficulty').value=next.difficulty;resetSession();fillChartFields();renderNotes();await persist();toast('新しい下書きを作りました。元の譜面は「曲・保存」に残っています。');
  }finally{loading=false;refresh();refreshTabDetails();}
}
async function createFromStored(source) {
  // 二度押しで下書きが2つできないよう、最初の await より前に「作成中」にする(openProject は loading で戻るので、別の印を使う)。
  // 作り終えるまで画面を閉じない(閉じて録音を始めると、作り終えたときの切り替えで録音が止まっていた)。開き終えると openProject が閉じる。
  if(loading||copyCreating)return;copyCreating=true;$('createCopy').disabled=true;const closer=document.querySelector('[data-close="duplicateDialog"]');if(closer)closer.disabled=true;stop();
  try{
    const difficulty=$('copyDifficulty').value;if(!['easy','normal','hard'].includes(difficulty))throw new Error('難易度を選んでください。');
    const next={...source,id:uid(),_revision:0,lastExportedAt:null,chart:copyChart(parseChart(source.chart),$('copyContent').value==='empty'),name:$('copyName').value.trim().slice(0,120)||source.name,difficulty,position:0,updated:Date.now()};
    await persist();await saveProject(next);copySource=null;await openProject(next.id);
    toast(`${next.name} の ${difficulty.toUpperCase()} を作りました。元の譜面は「曲・保存」に残っています。`);
  }finally{copyCreating=false;if(closer)closer.disabled=false;refresh();}
}
function mapWindow(){return timelineWindow(audio.duration,mapStart,Number($('mapZoom').value));}
// 一覧の1行は「番号・時刻・色・矢印・回数と長さ・位置」。同じ時刻・同じ色で重なったノーツも選び分けられるようにする。選んでいる行は印をつける。
function noteOrder(){return new Map(chart.notes.map((note,index)=>[note,index+1]));}
function noteButton(note,order){
  const button=document.createElement('button');button.className='map-note';button.style.setProperty('--note-color',COLORS[note.color]);
  button.textContent=`#${order.get(note)} ${formatTime(audioTime(note,chart),true)} ${{blue:'青',red:'赤',gold:'金'}[note.color]||'他'}${arrow(note.direction)?' '+arrow(note.direction):''}${note.count>1?` ×${note.count}・${longSeconds(note).toFixed(2)}秒`:''} (${(note.x*chart.coordScale).toFixed(1)}, ${(note.y*chart.coordScale).toFixed(1)})`;
  if(note.__editorId===selected){button.classList.add('selected');button.setAttribute('aria-current','true');}
  button.onclick=()=>selectNote(note.__editorId);return button;
}
function paintMap() {
  if(workspaceUI?.active!=='edit')return;
  let range=null;try{range=readRange(false);}catch{}
  mapBoxes=paintVerticalTimeline($('chartMap'),chart,audio.buffer,mapWindow(),audio.time(),selected,range);
  $('mapPosition').textContent=formatTime(audio.time(),true);
}
function renderMap() {
  if(workspaceUI?.active!=='edit')return;const window=mapWindow();mapStart=window.start;paintMap();
  const counts={blue:0,red:0,gold:0,default:0};for(const note of chart.notes)counts[note.color]++;
  $('mapStats').textContent=`青 ${counts.blue} · 赤 ${counts.red} · 金 ${counts.gold}${counts.default?' · 他 '+counts.default:''} / 合計 ${chart.notes.length}`;
  $('mapRange').textContent=`${formatTime(window.start,true)} 〜 ${formatTime(window.end,true)}`;
  $('mapPrevious').disabled=window.start<=0;$('mapNext').disabled=window.end>=audio.duration;
  $('mapPlay').textContent=audio.playing?'一時停止':'ここから聴く';
  $('mapSelection').textContent=`編集区間 A ${formatTime(Number($('rangeStart').value),true)} / B ${formatTime(Number($('rangeEnd').value),true)}`;
  mapShown=window;const list=$('mapNotes');list.replaceChildren();const visible=visibleNotes(chart,window),order=noteOrder();
  for(const note of visible.slice(0,100))list.append(noteButton(note,order));
  if(!visible.length){const p=document.createElement('p');p.className='hint';p.textContent='この範囲にノーツはありません。';list.append(p);}
  if(visible.length>100){const p=document.createElement('p');p.className='hint';p.textContent=`この表示には${visible.length}個あります。一覧は先頭100個です。表示幅を狭めて選んでください。`;list.append(p);}
}
function showTimeline() {
  if(!project||loading)return;stop();mapStart=Math.max(0,audio.time()-(Number($('mapZoom').value)||audio.duration)/3);workspaceUI.activate('edit');renderMap();
}
function showBatch() {
  if(!project||loading)return;stop();batchRange=readRange(false);$('batchColor').value='all';$('batchDestination').value=batchRange.end.toFixed(3);$('batchRangeLabel').textContent=`A ${formatTime(batchRange.start,true)} 〜 B ${formatTime(batchRange.end,true)}`;refreshBatch();$('batchDialog').showModal();
}
function refreshBatch() {
  if(!batchRange)return;const count=rangeNotes(chart,{...batchRange,color:$('batchColor').value}).length;
  $('batchCount').textContent=`対象 ${count} ノーツ`;for(const id of ['applyShift','applyCopy','applyMirror'])$(id).disabled=count===0;
}
function applyBatch(kind) {
  if(!batchRange||loading)return;stop();
  const value=Number($(kind==='shift'?'batchShift':'batchDestination').value);
  const result=transformRange(chart,{...batchRange,color:$('batchColor').value},{kind,value,swapColors:$('batchSwapColors').checked},audio.duration);
  const from=clamp(batchRange.start+result.deltaSeconds,0,audio.duration),to=clamp(batchRange.end+result.deltaSeconds,0,audio.duration);
  commit(()=>{chart=result.chart;selected=null;});$('rangeStart').value=from.toFixed(3);$('rangeEnd').value=to.toFixed(3);audio.seek(from);$('batchDialog').close();renderMap();renderNotes(from);toast(`${result.count}ノーツを${kind==='copy'?'コピー':kind==='mirror'?'左右反転':'移動'}しました。「元に戻す」で取り消せます。`);
}
function renderNotes(centerTime) {
  const list=$('noteList');list.replaceChildren();$('emptyNotes').hidden=chart.notes.length>0;let start=0;
  // 旧ノーツ一覧は今の画面では隠れている(直すの一覧は renderMap)。隠れている間は作り直さない(1000ノーツでは操作のたびに100行を作っていた)。
  if(list.closest('[hidden]')){$('noteEditor').hidden=!selected;return;}
  if(centerTime!==undefined){const i=chart.notes.findIndex(n=>audioTime(n,chart)>=centerTime);start=Math.max(0,(i<0?chart.notes.length:i)-6);}
  else if(selected)start=Math.max(0,chart.notes.findIndex(n=>n.__editorId===selected)-6);else start=Math.max(0,chart.notes.length-100);
  if(start>0){const previous=document.createElement('button');previous.textContent='前のノーツ';previous.className='subtle';previous.addEventListener('click',()=>renderNotes(audioTime(chart.notes[Math.max(0,start-60)],chart)));list.append(previous);}
  for(const note of chart.notes.slice(start,start+100)){
    const row=document.createElement('button');row.className='note-row';row.classList.toggle('selected',note.__editorId===selected);row.dataset.noteId=note.__editorId;
    const dot=document.createElement('span');dot.className='note-dot';dot.style.setProperty('--note-color',COLORS[note.color]);
    const time=document.createElement('span');time.className='mono';time.textContent=formatTime(audioTime(note,chart),true);
    const kind=document.createElement('span');kind.textContent=note.count>1?`×${note.count}`:arrow(note.direction)||'TAP';
    const location=document.createElement('span');location.className='position';location.textContent=`${(note.x*chart.coordScale).toFixed(1)}, ${(note.y*chart.coordScale).toFixed(1)}`;
    row.append(dot,time,kind,location);row.setAttribute('aria-label',`${time.textContent} ${note.color} ${note.count}回${arrow(note.direction)?' フリック '+arrow(note.direction):''}`);row.addEventListener('click',()=>selectNote(note.__editorId));list.append(row);
  }
  if(start+100<chart.notes.length){const next=document.createElement('button');next.textContent='次のノーツ';next.className='subtle';next.addEventListener('click',()=>renderNotes(audioTime(chart.notes[start+100],chart)));list.append(next);}
  $('noteEditor').hidden=!selected;
}
function selectNote(id,{review:from=null}={}) {
  stop();const note=chart.notes.find(n=>n.__editorId===id);if(!note)return;selected=id;review=from;audio.seek(Math.max(0,audioTime(note,chart)-.4));
  if(review)toast(`候補 ${review.index+1}/${review.items.length} · 診断したときの理由：${review.items[review.index].text}`);
  // 選んだノーツがグラフの表示区間の外なら、そこを含む区間へ動かす(診断・次へで選んだとき、グラフが前の区間のままにならない)。
  {const time=audioTime(note,chart),view=mapWindow();if(time<view.start||time>view.end)mapStart=Math.max(0,time-view.span/3);}
  $('noteTime').value=audioTime(note,chart).toFixed(3);$('noteColor').value=note.color;$('noteX').value=+(note.x*chart.coordScale).toFixed(3);$('noteY').value=+(note.y*chart.coordScale).toFixed(3);
  $('noteCuts').value=note.count;$('noteLength').value=longSeconds(note).toFixed(3);$('noteDirection').value=note.direction;renderNotes();refresh();refreshSelected();
  workspaceUI.activate('edit');if(!$('noteDialog').open)$('noteDialog').show();
  requestAnimationFrame(()=>{paintMap();const box=mapBoxes.find(b=>b.note.__editorId===selected),panel=$('timelineDialog'),canvas=$('chartMap');if(box)panel.scrollTop+=canvas.getBoundingClientRect().top-panel.getBoundingClientRect().top+box.y-panel.clientHeight/2;});
}
function refreshSelected() {
  const index=chart.notes.findIndex(n=>n.__editorId===selected),note=chart.notes[index];if(!note)return;
  // 色と回数も見出しに出す(下の欄までスクロールしなくても、何を選んだか分かる)。
  $('selectedNoteLabel').textContent=`${index+1} / ${chart.notes.length} · ${formatTime(audioTime(note,chart),true)} · ${{blue:'青',red:'赤',gold:'金'}[note.color]||'色なし'}${note.count>1?` ×${note.count}`:''}`;
  $('previousNote').disabled=index===0;$('nextNote').disabled=index===chart.notes.length-1;
  $('previousNote').textContent=review?'前の候補':'前へ';$('nextNote').textContent=review?'次の候補':'次へ';
  if(review){$('previousNote').disabled=review.index===0;$('nextNote').disabled=review.index>=review.items.length-1;$('selectedNoteLabel').textContent=`候補 ${review.index+1}/${review.items.length} · ${formatTime(audioTime(note,chart),true)}`;}
  $('positionMarker').style.left=`${clamp(note.x*chart.coordScale/5+.5,0,1)*100}%`;
  $('positionMarker').style.top=`${clamp(.5-note.y*chart.coordScale/3,0,1)*100}%`;
  $('positionMarker').style.background=COLORS[note.color];
  $('positionMarker').textContent=note.count>1?`×${note.count}`:arrow(note.direction);workspaceUI?.direction(note.direction);workspaceUI?.color(note.color);
}
const noteFields={noteTime:'time',noteX:'x',noteY:'y',noteCuts:'count',noteLength:'length',noteColor:'color',noteDirection:'direction'};
function fillSelectedFields(){const note=chart.notes.find(n=>n.__editorId===selected);if(!note)return;const values={noteTime:audioTime(note,chart).toFixed(3),noteX:+(note.x*chart.coordScale).toFixed(3),noteY:+(note.y*chart.coordScale).toFixed(3),noteCuts:note.count,noteLength:longSeconds(note).toFixed(3),noteColor:note.color,noteDirection:note.direction};for(const[id,value]of Object.entries(values))$(id).value=value;refreshSelected();}
function updateNoteField(id,value=$(id).value){
  if(!selected)return;stop();
  try{const next=editNote(chart,selected,noteFields[id],value,audio.duration);if(JSON.stringify(next)!==JSON.stringify(chart))commit(()=>chart=next);}
  catch(error){fieldFailures++;fillSelectedFields();throw error;}fillSelectedFields();
}
function updateNote(){for(const id of Object.keys(noteFields))updateNoteField(id);}
function nudgeNote(milliseconds) {
  const length=Number($('noteCuts').value)>1?Number($('noteLength').value):0;
  $('noteTime').value=clamp(Number($('noteTime').value)+milliseconds/1000,0,Math.max(0,audio.duration-length)).toFixed(3);updateNoteField('noteTime');
}
function moveSelected(event) {
  if(event.button!==undefined&&event.button!==0)return;event.preventDefault();if(!selected)return;stop();
  const rect=$('positionPad').getBoundingClientRect(),x=((clamp((event.clientX-rect.left)/rect.width,0,1)-.5)*5).toFixed(3),y=((.5-clamp((event.clientY-rect.top)/rect.height,0,1))*3).toFixed(3);
  const next=editNote(editNote(chart,selected,'x',x,audio.duration),selected,'y',y,audio.duration);commit(()=>chart=next);fillSelectedFields();
}
// 消したら、時刻の並びで次のノーツ(なければ前)を選んだままにして、打ち間違いを続けて消せるようにする。
// 二度押しで2個消さないよう、消した直後の少しの間は受け付けない。
let deleteGuardUntil=0;
function deleteSelectedNote(){
  if(!selected||performance.now()<deleteGuardUntil)return;stop();
  const index=chart.notes.findIndex(n=>n.__editorId===selected);
  commit(()=>{chart.notes=chart.notes.filter(n=>n.__editorId!==selected);selected=null;});
  deleteGuardUntil=performance.now()+400;
  // 点検モードなら次の候補へ(なければ閉じる)。ふだんは時刻の並びで次のノーツ。
  if(review){if(!continueReview())toast('ノーツを削除しました。残っている候補はありません。');return;}
  const next=chart.notes[index]||chart.notes[index-1];
  if(next)selectNote(next.__editorId);else $('noteDialog').close();
  toast('ノーツを削除しました。「元に戻す」で戻せます。');
}
function arrow(direction){return{up:'↑',down:'↓',left:'←',right:'→',upleft:'↖',upright:'↗',downleft:'↙',downright:'↘'}[direction]||'';}
function startTouch(event) {
  if(event.button!==undefined&&event.button!==0)return;event.preventDefault();
  if(loading||!audio.playing){toast('「録音開始」で曲を流してからタップしてください。');return;}
  if(mode!=='record'||!take||audio.rawTime(event.timeStamp)<audio.startOffset||audio.time(event.timeStamp)>=playbackEnd)return;
  if(chart.notes.length+touches.size>=50000){toast('ノーツ数の上限です。');return;}
  const rect=$('pad').getBoundingClientRect(),rawX=clamp((event.clientX-rect.left)/rect.width,0,1),rawY=clamp((event.clientY-rect.top)/rect.height,0,1);
  const inputPosition=positionInput((rawX-.5)*5,(.5-rawY)*3,Number($('positionGrid').value)),px=inputPosition.x/5+.5,py=.5-inputPosition.y/3;
  const marker=document.createElement('div');marker.className='touch-marker';marker.style.left=`${px*100}%`;marker.style.top=`${py*100}%`;
  const opts=settings(),chosen=opts.color==='auto'?(rawX<.5?'blue':'red'):opts.color;marker.style.color=COLORS[chosen];marker.textContent='•';$('touches').append(marker);
  touches.set(event.pointerId,{startAudio:audio.time(event.timeStamp),x:(px-.5)*5,y:(.5-py)*3,options:{...opts,color:chosen},marker,stroke:beginStroke(event.clientX,event.clientY,event.timeStamp),flickDirection:'none'});$('pad').setPointerCapture(event.pointerId);
  if($('clickSound').checked)audio.click(chosen==='blue'?570:chosen==='red'?760:950);
}
function moveTouch(event) {
  const touch=touches.get(event.pointerId);if(!touch)return;
  event.preventDefault();moveStroke(touch.stroke,event.clientX,event.clientY,event.timeStamp);
  touch.flickDirection=touch.options.flickEnabled?flickDirection(touch.stroke,touch.options.flickSensitivity):'none';
  touch.marker.classList.toggle('is-flick',touch.flickDirection!=='none');
  touch.marker.textContent=arrow(touch.flickDirection)||'•';
}
function releaseTouch(event) {
  moveTouch(event);endTouch(event.pointerId,audio.time(event.timeStamp));
}
function endTouch(id,endAudio) {
  const touch=touches.get(id);if(!touch)return;touches.delete(id);touch.marker.remove();
  if(!take)return;
  if(!take.remembered){history.push(take.before,{bpmConfirmed});take.remembered=true;lastTake=null;}
  const note=take.add(makeNote(touch,Math.min(endAudio,take.end),chart,touch.options));chart=take.compose(endAudio);selected=null;changed();flashes.push({x:touch.x,y:touch.y,color:note.color,direction:note.direction,when:performance.now()});
  const delta=Math.round(audioTime(note,chart)*1000-Math.max(0,touch.startAudio*1000-touch.options.latencyMs*touch.options.rate));
  const kind=note.count>1?`ロング ×${note.count}`:arrow(note.direction)?`${arrow(note.direction)} フリック`:'タップ';
  $('inputFeedback').textContent=`${kind} · ${delta?`時刻を${delta>0?'+':''}${delta}ms補正`:'入力した時刻のまま'}`;$('inputFeedback').classList.add('has-result');
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
  if(audio.playing&&audio.rawTime()>=Math.min(audio.duration,playbackEnd)){const loop=mode==='preview'&&loopingRange&&$('previewLoop').checked&&$('rangeEnabled').checked;stop();if(loop)togglePlayback().catch(fail);else toast('区間の終わりまで再生しました。');}if(audio.playing&&audio.context.state!=='running'){stop();toast('音声が中断されたため、一時停止しました。');}
  const t=audio.time();
  if(audio.playing&&mode==='preview'&&$('clickSound').checked)for(const n of chart.notes){const hit=audioTime(n,chart);if(hit>lastFrameTime&&hit<=t&&t-lastFrameTime<.2)audio.click(n.color==='blue'?570:n.color==='red'?760:950);}
  lastFrameTime=t;$('timeReadout').textContent=formatTime(t,true);$('seek').value=t;
  const remaining=audio.playing?(audio.startedAt-audio.outputContextTime()):0,countdown=remaining>0?Math.ceil(remaining/(60/chart.bpm/audio.rate)):0;
  $('transportStatus').textContent=loading?'読み込み中':countdown>0?`カウント ${Math.min(4,countdown)}`:audio.playing?(mode==='record'?'録音中':'確認中'):'一時停止';
  $('stageMessage').textContent=loading?'音源を読み込み中…':!audio.buffer?'音源を開くと、曲に合わせて記録できます':countdown>0?String(Math.min(4,countdown)):'';
  for(const touch of touches.values()){const duration=Math.max(0,t-touch.startAudio);if(duration/touch.options.rate>=(touch.options.longThresholdMs||300)/1000){touch.marker.textContent=`×${makeNote(touch,t,chart,touch.options).count}`;touch.marker.classList.remove('is-flick');}}
  if(workspaceUI?.active==='edit'&&now-mapLastPaint>100){paintMap();mapLastPaint=now;}
  drawStage(t,now);requestAnimationFrame(tick);
}
async function togglePlayback({useRange=true}={}) {
  if(starting||loading)return;if(audio.playing){stop();return;}starting=true;refresh();
  try{
    const range=useRange?readRange():null;
    loopingRange=!!range;
    if(range)audio.seek(range.start);else if(audio.position>=audio.duration-.01)audio.seek(0);
    playbackEnd=range?.end??audio.duration;
    // BPMを測るために流すときは、遅く聴く設定でも1.0倍(たたいた間隔がそのままBPMになるように)。
    const started=await audio.play({rate:tempoListening?1:Number($('speed').value),countBeats:mode==='record'&&bpmConfirmed&&$('countIn').checked?4:0,bpm:chart.bpm,metronome:bpmConfirmed&&$('metronome').checked,beatOrigin:(chart.beatZeroMs+chart.offsetMs)/1000,end:playbackEnd});
    if(started===false)return;
    if(mode==='record')take=new RecordingTake(chart,{start:audio.startOffset,end:playbackEnd,replace:!!range&&$('replaceRange').checked});
    lastFrameTime=audio.time();
  }finally{starting=false;refresh();}
}
function readRange(enabledOnly=true) {
  if(enabledOnly&&!$('rangeEnabled').checked)return null;
  const start=Number($('rangeStart').value),end=Number($('rangeEnd').value);
  if($('rangeStart').value===''||$('rangeEnd').value===''||!Number.isFinite(start)||!Number.isFinite(end)||start<0||end>audio.duration+.001||end-start<.1)throw new Error('区間は音源内で、AよりBを0.1秒以上後にしてください。');
  return{start,end:Math.min(end,audio.duration)};
}
async function retryTake() {
  if(!lastTake||audio.playing||loading||starting)return;
  readRange();
  const previous=lastTake;history.push(chart,{bpmConfirmed});chart=clone(previous.before);lastTake=null;selected=null;audio.seek(previous.start);changed();
  mode='record';await togglePlayback();
}
// 区間の画面の近道。今の位置から曲の終わりまでを置き換える録音にする(止めた所より先は残る)。
function redoFromHere() {
  if(!audio.buffer||audio.playing||loading)return;const from=audio.time();
  if(audio.duration-from<.5){toast('曲の終わりに近すぎます。少し前へ戻してから押してください。');return;}
  $('rangeStart').value=from.toFixed(3);$('rangeEnd').value=audio.duration.toFixed(3);$('rangeEnabled').checked=true;$('replaceRange').checked=true;lastTake=null;refresh();$('rangeDialog').close();
  toast(`${formatTime(from,true)} から録り直します。止めた所までを置き換えます。`);
}
// 確かめるの近道。今の位置から2小節(BPM未設定・テンポが変わる曲は5秒)をくり返し再生する区間にする(録音の置き換えはしない)。
function loopFromHere() {
  if(!audio.buffer||audio.playing||loading)return;const from=audio.time(),to=bpmConfirmed&&!hasVariableGrid(chart)?barSpan(chart,from,2,audio.duration):Math.min(audio.duration,from+5);
  if(to-from<.5){toast('曲の終わりに近すぎます。少し前へ戻してから押してください。');return;}
  $('rangeStart').value=from.toFixed(3);$('rangeEnd').value=to.toFixed(3);$('rangeEnabled').checked=true;$('replaceRange').checked=false;$('previewLoop').checked=true;lastTake=null;refresh();$('rangeDialog').close();
  toast(`${formatTime(from,true)}〜${formatTime(to,true)} をくり返します。「再生」で始まります。`);
}
function clearRange() {
  $('rangeEnabled').checked=false;$('replaceRange').checked=false;lastTake=null;refresh();$('rangeDialog').close();toast('区間を解除しました。録音は曲全体へ追加に戻ります。');
}
function setColor(value) {
  color=value;for(const button of document.querySelectorAll('[data-color]')){button.classList.toggle('active',button.dataset.color===value);button.setAttribute('aria-pressed',button.dataset.color===value);}
  // 色を固定したら、パッドの地と見出しもその色だけにする(左が青・右が赤になるのは「左右で色分け」のときだけ)。
  const fixed={blue:['BLUE','どこでも青'],red:['RED','どこでも赤'],gold:['GOLD','どこでも金']}[value],left=document.querySelector('.pad-hand.left'),right=document.querySelector('.pad-hand.right');
  const label=(element,name,note)=>{const small=document.createElement('small');small.textContent=note;element.replaceChildren(name,document.createElement('br'),small);};
  $('pad').dataset.ink=value;label(left,...(fixed||['BLUE','左手']));right.hidden=!!fixed;refreshInputOptions();
}
function seek(value){stop();audio.seek(value);lastFrameTime=audio.time();refresh();}
$('audioFile').addEventListener('change',run(e=>loadAudioFile(e.target.files[0])));$('libraryAudioFile').addEventListener('change',run(e=>loadAudioFile(e.target.files[0])));
$('chartFile').addEventListener('change',run(async e=>{try{await importChart(e.target.files[0]);}finally{e.target.value='';}}));
$('libraryButton').addEventListener('click',run(showLibrary));$('helpButton').onclick=()=>{$('help').showModal();};for(const b of document.querySelectorAll('[data-close]'))b.onclick=()=>$(b.dataset.close).close();
$('projectName').addEventListener('change',run(async()=>{if(project){project.name=$('projectName').value.trim()||project.audioName;refresh();await persist();if($('library').open)await showLibrary();}}));
$('playButton').addEventListener('click',run(togglePlayback));for(const[id,d]of [['backButton',-1],['forwardButton',1]])$(id).onclick=()=>seek(bpmConfirmed&&!hasVariableGrid(chart)?barSeek(chart,audio.time(),d,audio.duration):audio.time()+d*5);$('seek').addEventListener('input',e=>seek(Number(e.target.value)));
$('recordMode').onclick=()=>{stop();mode='record';refresh();};$('previewMode').onclick=()=>{stop();mode='preview';refresh();};
$('focusButton').onclick=()=>{document.body.classList.toggle('focus');$('focusButton').textContent=document.body.classList.contains('focus')?'戻る':'集中';$('focusButton').setAttribute('aria-pressed',document.body.classList.contains('focus'));drawWaveform();};
for(const b of document.querySelectorAll('[data-color]'))b.onclick=()=>{setColor(b.dataset.color);if(project){clearTimeout(saveTimer);saveTimer=setTimeout(()=>persist().catch(fail),180);}};
$('pad').addEventListener('pointerdown',startTouch);$('pad').addEventListener('pointermove',moveTouch);$('pad').addEventListener('pointerup',releaseTouch);
for(const event of ['pointercancel','lostpointercapture'])$('pad').addEventListener(event,e=>{touches.get(e.pointerId)?.marker.remove();touches.delete(e.pointerId);});$('pad').addEventListener('contextmenu',e=>e.preventDefault());
$('undoButton').onclick=()=>{stop();lastTake=null;restoreFromHistory(history.undo(chart,{bpmConfirmed}));};$('redoButton').onclick=()=>{stop();lastTake=null;restoreFromHistory(history.redo(chart,{bpmConfirmed}));};
// 元に戻す・やり直しのあと: BPMの確定も戻し、選んでいたノーツが残っていれば選んだまま(なければ編集パネルを閉じる)。
function restoreFromHistory(next){
  const keep=selected;chart=next;if(history.restored)bpmConfirmed=history.restored.bpmConfirmed!==false;
  selected=keep&&chart.notes.some(n=>n.__editorId===keep)?keep:null;if(!selected&&!review&&$('noteDialog').open)$('noteDialog').close();
  fillChartFields();changed();if(selected)fillSelectedFields();else if(review)continueReview();
}
// 点検中に選んでいたノーツが消えたら(削除・やり直し・まとめて直す)、残っている次の候補へ。なければ前の候補、それもなければ閉じる。
function continueReview(){
  const exists=item=>chart.notes.some(n=>n.__editorId===item.id),after=review.items.findIndex((item,i)=>i>review.index&&exists(item));
  const index=after>=0?after:review.items.findLastIndex((item,i)=>i<review.index&&exists(item));
  if(index>=0){selectNote(review.items[index].id,{review:{items:review.items,index}});return true;}
  review=null;if($('noteDialog').open)$('noteDialog').close();return false;
}
$('nearestButton').onclick=()=>renderNotes(audio.time());$('updateNote').addEventListener('click',run(()=>{updateNote();toast('ノーツを変更しました。');}));$('deleteNote').onclick=deleteSelectedNote;$('noteDirection').innerHTML=$('direction').innerHTML;$('exportButton').onclick=downloadChart;
$('noteEditorHost').append($('noteEditor'));
$('positionPad').addEventListener('pointerdown',run(moveSelected));$('positionPad').addEventListener('contextmenu',event=>event.preventDefault());
$('earlierNote').addEventListener('click',run(()=>nudgeNote(-10)));$('laterNote').addEventListener('click',run(()=>nudgeNote(10)));
$('snapNote').addEventListener('click',run(()=>{
  const step=Number($('snap').value)||.25,time=Number($('noteTime').value)*1000-chart.offsetMs;
  const snapped=(quantizeMs(time,chart,step)+chart.offsetMs)/1000;
  nudgeNote((snapped-Number($('noteTime').value))*1000);toast(Number($('snap').value)?'設定した拍にそろえました。':'16分音符にそろえました。');
}));
for(const[id,delta]of[['previousNote',-1],['nextNote',1]])$(id).onclick=()=>{if(review){stepReview(delta);return;}const index=chart.notes.findIndex(n=>n.__editorId===selected),next=chart.notes[index+delta];if(next)selectNote(next.__editorId);};
// 点検モード: 確かめるの候補から来たら、前へ/次へは前後の候補へ(消えたノーツの候補は飛ばす)。直接ノーツを選ぶ・パネルを閉じる・別の下書きを開くと抜ける。
function stepReview(delta){
  let index=review.index+delta;while(index>=0&&index<review.items.length&&!chart.notes.some(n=>n.__editorId===review.items[index].id))index+=delta;
  if(index<0||index>=review.items.length){toast(delta>0?'最後の候補です。「確かめる」で診断し直せます。':'最初の候補です。');return;}
  selectNote(review.items[index].id,{review:{items:review.items,index}});
}
$('noteDialog').addEventListener('close',()=>{if(review){review=null;refresh();}});
$('retryTake').addEventListener('click',run(retryTake));
for(const id of ['rangeStart','rangeEnd','rangeEnabled','replaceRange'])$(id).addEventListener('change',()=>{lastTake=null;refresh();});
for(const[id,field]of[['setRangeStart','rangeStart'],['setRangeEnd','rangeEnd']])$(id).onclick=()=>{$(field).value=audio.time().toFixed(3);lastTake=null;refresh();};
$('tapTempo').onclick=()=>{const result=tapTempo.tap(performance.now());$('tempoReadout').textContent=result.bpm?`${result.bpm} BPM · ${result.count}回`:`${result.count}回 · あと${Math.max(1,4-result.count)}回`; $('applyTempo').disabled=!result.bpm||loading;};
// BPMを初めて決めたとき、または一定テンポの譜面でBPMを直したときは、時刻を保ったまま拍を数え直す(テンポが変わる譜面は beat をそのまま残す)。
const recount=()=>!bpmConfirmed||!hasVariableGrid(chart);
$('applyTempo').onclick=()=>{const result=tapTempo.result();if(!result.bpm||loading)return;stop();commit(()=>{const again=recount();bpmConfirmed=true;chart.bpm=result.bpm;if(again)rebeat(chart);});fillChartFields();toast('BPMを設定しました。録音済みノーツの時刻はそのままです。');};
for(const[id,field,min,max]of[['bpm','bpm',20,400],['offset','offsetMs',-600000,600000],['beatZero','beatZeroMs',-600000,600000],['level','displayLevel',0,10]])$(id).addEventListener('change',run(()=>{stop();const v=Number($(id).value);if($(id).value===''||!Number.isFinite(v)||v<min||v>max){fillChartFields();throw new Error('設定値の範囲を確認してください。');}commit(()=>{const again=id==='bpm'&&recount();if(id==='bpm')bpmConfirmed=true;chart[field]=v;if(again)rebeat(chart);});}));
for(const id of ['positionGrid','previewLoop','speed','snap','snapMode','snapWindowMs','flickEnabled','flickSensitivity','longCount','direction','latency','metronome','countIn','clickSound'])$(id).addEventListener('change',()=>{stop();refresh();if(['snap','snapMode'].includes(id)&&$('snapMode').value!=='off'&&hasVariableGrid(chart))toast('テンポ変化のある曲では、固定BPMへの整列でずれることがあります。');});
$('difficulty').addEventListener('change',()=>{if(!project)return;const next=$('difficulty').value,previous=project.difficulty;if(next!==previous&&chart.notes.length&&!window.confirm(`この下書きの難易度を${previous.toUpperCase()}から${next.toUpperCase()}へ変えます。ノーツは変わりません。別の下書きを残す場合は「曲・保存」からコピーしてください。変更しますか？`)){$('difficulty').value=previous;return;}project.difficulty=next;stop();refresh();refreshTabDetails();});
for(const id of ['songVolume','feedbackVolume'])$(id).addEventListener('input',()=>{audio.setVolumes(Number($('songVolume').value),Number($('feedbackVolume').value));if(project){clearTimeout(saveTimer);saveTimer=setTimeout(()=>persist().catch(fail),180);}});
document.addEventListener('visibilitychange',()=>{if(document.hidden){stop();persist().catch(fail);}});window.addEventListener('pagehide',()=>{stop();persist().catch(()=>{});});window.addEventListener('beforeunload',e=>{if(saving||saveFailed||saveTimer||touches.size){e.preventDefault();e.returnValue='';}});window.addEventListener('resize',drawWaveform);
window.addEventListener('keydown',run(async e=>{if(['INPUT','SELECT','TEXTAREA','BUTTON'].includes(e.target.tagName)||document.querySelector('dialog:modal'))return;if(e.code==='Space'){e.preventDefault();if(audio.buffer)await togglePlayback();}if((e.ctrlKey||e.metaKey)&&e.key==='z'){e.preventDefault();$(e.shiftKey?'redoButton':'undoButton').click();}}));
$('exportAudioButton').addEventListener('click',run(downloadAudio));
$('backupButton').addEventListener('click',run(downloadBackup));
for(const id of ['backupFile','libraryBackupFile'])$(id).addEventListener('change',run(async event=>{try{await restoreBackup(event.target.files[0]);}finally{event.target.value='';}}));
$('duplicateButton').onclick=()=>showCopyDialog();$('duplicateDialog').addEventListener('close',()=>{copySource=null;refresh();});$('duplicateDialog').addEventListener('cancel',event=>{if(copyCreating)event.preventDefault();});$('createCopy').addEventListener('click',run(createProjectCopy));
$('timelineButton').onclick=showTimeline;$('timelineDialog').addEventListener('close',()=>stop());
// 表示幅を変えても、いま見ていた区間の真ん中を保つ(ドラッグで探した場所へ戻れなくならないように。再生位置へは「再生位置へ」で)。
$('mapZoom').onchange=()=>{const center=mapShown?(mapShown.start+mapShown.end)/2:audio.time();mapStart=Math.max(0,center-(Number($('mapZoom').value)||audio.duration)/2);preference('saber-map-zoom',$('mapZoom').value);renderMap();};
for(const[id,direction]of[['mapPrevious',-1],['mapNext',1]])$(id).onclick=()=>{mapStart+=mapWindow().span*.8*direction;renderMap();};
$('mapCurrent').onclick=()=>{mapStart=Math.max(0,audio.time()-mapWindow().span/3);renderMap();};
$('mapPlay').addEventListener('click',run(async()=>{if(!audio.playing)mode='preview';await togglePlayback({useRange:false});}));
for(const[id,field]of[['mapSetA','rangeStart'],['mapSetB','rangeEnd']])$(id).onclick=()=>{stop();$(field).value=audio.time().toFixed(3);lastTake=null;refresh();};
let mapPointer=null;
$('chartMap').addEventListener('pointerdown',event=>{stop();mapPointer={x:event.clientX,y:event.clientY,start:mapStart,moved:false};$('chartMap').setPointerCapture(event.pointerId);});
$('chartMap').addEventListener('pointermove',event=>{if(!mapPointer)return;const dy=event.clientY-mapPointer.y;if(Math.abs(dy)>6)mapPointer.moved=true;if(mapPointer.moved){mapStart=mapPointer.start+dy/Math.max(1,$('chartMap').clientHeight-48)*mapWindow().span;renderMap();}});
$('chartMap').addEventListener('pointercancel',()=>mapPointer=null);
$('chartMap').addEventListener('pointerup',event=>{
 const start=mapPointer;mapPointer=null;if(!start||start.moved)return;const rect=$('chartMap').getBoundingClientRect(),x=event.clientX-rect.left,y=event.clientY-rect.top,hits=verticalHits(mapBoxes,x,y);
 if(hits.length===1)selectNote(hits[0].__editorId);else if(hits.length>1){const list=$('mapNotes'),order=noteOrder();list.replaceChildren();for(const note of hits)list.append(noteButton(note,order));list.parentElement.open=true;list.scrollIntoView({block:'nearest'});toast('重なっているノーツを一覧から選んでください。');}else{seek(verticalTime(mapWindow(),y,rect.height));renderMap();}
});
for(const id of ['batchButton','mapBatch'])$(id).addEventListener('click',run(showBatch));
$('batchColor').onchange=refreshBatch;
for(const[id,kind]of[['applyShift','shift'],['applyCopy','copy'],['applyMirror','mirror']])$(id).addEventListener('click',run(()=>applyBatch(kind)));
window.addEventListener('resize',()=>renderMap());
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
  $('appBrand').textContent=`Saber Tap Studio ${APP_VERSION}`;
  refresh();fillChartFields();renderNotes();requestAnimationFrame(tick);setupOffline();registerTools();
  try{const projects=await listProjects(),last=preference('saber-last-project');if(projects.length){const recent=projects.find(p=>p.id===last)||projects.sort((a,b)=>b.updated-a.updated)[0];status(`前回の下書き：${recent.name}`);showResume(recent);}}catch{status('端末の保存が利用できません。ブラウザの通常モードで開いてください。');}
}
// 下書きを消したあと、「続きを開く」を残っている最新の下書きへ合わせる(なければ隠して「音源を開く」を主ボタンに戻す)。
async function refreshResume(){
  const button=$('resumeProject');if(!button||project)return;
  const projects=await listProjects().catch(()=>[]),last=preference('saber-last-project');
  if(!projects.length){button.hidden=true;button.onclick=null;$('audioFile').closest('label').classList.add('primary');return;}
  showResume(projects.find(p=>p.id===last)||projects.sort((a,b)=>b.updated-a.updated)[0]);
}
// 最初の画面の「続きを開く」。前回の下書きを1回で開く(曲・保存を開いて探さなくてよい)。
function showResume(recent){
  const button=$('resumeProject');if(!button||project)return;
  button.textContent=`続きを開く：${recent.name}（${(recent.difficulty||'normal').toUpperCase()}）`;button.hidden=false;
  $('audioFile').closest('label').classList.remove('primary');
  button.onclick=run(()=>openProject(recent.id));
}
refinementUI=installRefinementUI({getState:()=>({chart,project,audio,mode,loading,bpmConfirmed,difficulty:$('difficulty').value}),openPreview:()=>workspaceUI.activate('preview'),mutate:next=>{commit(()=>{const keep=selected;chart=next;selected=keep&&chart.notes.some(n=>n.__editorId===keep)?keep:null;});if(selected)fillSelectedFields();else if(review)continueReview();else if($('noteDialog').open)$('noteDialog').close();},stop,seek,persist,run,toast,selectNote,downloadBlob,fillChartFields,showLibrary,clearProject:()=>{project=null;chart=blankChart();history=new History();selected=null;audio.seek(0);audio.buffer=null;resetSession();fillChartFields();renderNotes();drawWaveform();refresh();status('下書きを削除しました。音源を開くか別の下書きを選んでください。');},projectsChanged:()=>refreshResume()});
workspaceUI=installWorkspace({fieldFailures:()=>fieldFailures,locked:()=>starting||copyCreating||(audio.playing&&mode==='record'),stop,redoFromHere,loopFromHere,clearRange,listenForTempo:run(async()=>{if(audio.playing){stop();return;}if(!audio.buffer||loading)return;tempoListening=true;mode='preview';tapTempo.reset();$('tempoReadout').textContent='曲の拍に合わせて4回以上';$('applyTempo').disabled=true;await togglePlayback({useRange:false});}),onTab:name=>{mode=name==='record'?'record':'preview';if(name==='preview')refinementUI.report();if(name==='export')refreshExportWarnings().catch(fail);refresh();drawWaveform();},listenTake:run(async()=>{if(!lastTake)return;const saved=lastTake;stop();workspaceUI.activate('preview');mode='preview';audio.seek(saved.start);await togglePlayback({useRange:false});playbackEnd=saved.end;refresh();}),listenNote:run(async()=>{const n=chart.notes.find(n=>n.__editorId===selected);if(!n)return;stop();mode='preview';audio.seek(Math.max(0,audioTime(n,chart)-60/chart.bpm));await togglePlayback({useRange:false});playbackEnd=Math.min(audio.duration,audioTime(n,chart)+longSeconds(n)+120/chart.bpm);refresh();}),edit:(id,value)=>{try{updateNoteField(id,value);}catch(e){fail(e);}},placeNote:()=>{if(!project)return;stop();/* 手で置くときは、録音用の入力補正と速度を混ぜない(今の再生位置にそのまま置く)。 */const at=clamp(audio.time(),0,audio.duration);let note=makeNote({startAudio:at,x:0,y:0},at,chart,{...settings(),snapMode:'off',latencyMs:0,rate:1,color:color==='auto'?'blue':color});commit(()=>chart.notes.push(note));selectNote(note.__editorId);},shareChart:run(async()=>{if(!project)return;stop();const file=new File([JSON.stringify(exportChart(chart),null,2)+'\n'],`chart_${$('difficulty').value}.json`,{type:'application/json'});if(navigator.canShare?.({files:[file]})){try{await navigator.share({files:[file],title:project.name});project.lastExportedAt=Date.now();persist().catch(fail);toast('譜面を共有しました。');}catch(e){if(e.name!=='AbortError')throw e;}}else downloadChart();})});
// 保存に失敗したら、上の「未保存」を押すと「渡す」(譜面JSONと音源入りバックアップ)へ行ける。
$('saveStatus').addEventListener('click',()=>{if(saveFailed&&project)workspaceUI.activate('export');});
// 「矢印の変更後に次へ」と直す画面の表示幅は、この端末で前回の選び方を覚える。
$('autoNext').checked=preference('saber-auto-next')==='1';$('autoNext').addEventListener('change',()=>preference('saber-auto-next',$('autoNext').checked?'1':'0'));
{const zoom=preference('saber-map-zoom');if([...$('mapZoom').options].some(option=>option.value===zoom))$('mapZoom').value=zoom;}
// 設定を閉じたら、BPMを測るために流した曲を止めて、今のタブの使い方(録る/聴く)へ戻す。
$('settingsDialog').addEventListener('close',()=>{if(!tempoListening)return;tempoListening=false;stop();mode=workspaceUI.active==='record'?'record':'preview';refresh();});
for(const id of Object.keys(noteFields)){$(id).addEventListener('change',run(()=>updateNoteField(id)));$(id).addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();$(id).blur();}});}
$('longThresholdMs').addEventListener('change',()=>{stop();$('longThresholdMs').value=clamp(Number($('longThresholdMs').value)||300,150,1000);persist().catch(fail);});
// 波形を押して位置を変える。録る画面では細い波形の上の時刻の行も含めた帯全体で押せる(スライダー本体は自分で動くので除く)。
{const band=document.querySelector('.timeline-wrap');let seekPointer=null,seekTab=null;
 const seekTo=e=>{const r=$('waveform').getBoundingClientRect();seek(clamp((e.clientX-r.left)/r.width,0,1)*audio.duration);};
 band.addEventListener('pointerdown',e=>{if(!audio.buffer||e.target===$('seek')||e.target!==$('waveform')&&!band.closest('.performance'))return;seekPointer=e.pointerId;seekTab=workspaceUI.active;band.setPointerCapture(e.pointerId);seekTo(e);});
 // 押したあとにタブが変わったら、その指では動かさない(帯は別のタブへ移るため)。
 band.addEventListener('pointermove',e=>{if(e.pointerId===seekPointer&&e.buttons&&workspaceUI.active===seekTab)seekTo(e);});
 for(const event of ['pointerup','pointercancel','lostpointercapture'])band.addEventListener(event,e=>{if(e.pointerId===seekPointer)seekPointer=null;});}
workspaceUI.activate('record');
init();
