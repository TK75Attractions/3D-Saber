// 別のタブとの保存の競合を、実際の IndexedDB と同じブラウザの2つのタブで確かめる(別のタブで曲を消す・同じ下書きを更新する)。
// 外部サイトや実ユーザーの下書きは使わない。合成WAVと、場面ごとに新しいブラウザの中身(コンテキスト)だけを使う。環境変数は browser-usability.mjs と同じ。
import {createRequire} from 'node:module';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
const require=createRequire(import.meta.url),{chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const output=process.env.QA_OUTPUT||'qa-output';fs.mkdirSync(output,{recursive:true});
const site=process.env.SITE_URL||'http://127.0.0.1:8769/';
const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL||'msedge',headless:true});
const check=name=>console.log('PASS '+name);
function wav(){const length=16000*30,b=Buffer.alloc(44+length*2);b.write('RIFF');b.writeUInt32LE(b.length-8,4);b.write('WAVEfmt ',8);b.writeUInt32LE(16,16);b.writeUInt16LE(1,20);b.writeUInt16LE(1,22);b.writeUInt32LE(16000,24);b.writeUInt32LE(32000,28);b.writeUInt16LE(2,32);b.writeUInt16LE(16,34);b.write('data',36);b.writeUInt32LE(length*2,40);for(let i=0;i<length;i++)b.writeInt16LE(Math.sin(i/16000*440*Math.PI*2)*300,i*2+44);return b;}
const chart={bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,displayLevel:3,timeSignatures:[],notes:Array.from({length:3},(_,i)=>({time:1000+i*500,beat:2+i,x:0,y:0,color:i%2?'red':'blue',direction:'none',count:1,type:'tap',lengthMs:0}))};
const saved=p=>p.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
const library=async p=>{await p.locator('#libraryButton').click();await p.locator('#library').waitFor({state:'visible'});};
const openDraft=async(p,name)=>{await library(p);await p.locator('.open-project').filter({hasText:name}).click();await p.locator('#library').waitFor({state:'hidden'});await p.waitForFunction(title=>document.querySelector('#songTitle').textContent===title&&!document.querySelector('#libraryButton').disabled,name);};
// この端末の下書きと音源を画面を通さずに読む。音源のない下書き(開けない下書き)を orphans に挙げる。
const inventory=p=>p.evaluate(()=>new Promise((resolve,reject)=>{const r=indexedDB.open('saber-tap-studio-v1',1);r.onerror=()=>reject(r.error);r.onsuccess=()=>{const d=r.result,tx=d.transaction(['projects','audio'],'readonly'),projects=tx.objectStore('projects').getAll(),audio=tx.objectStore('audio').getAllKeys();tx.oncomplete=()=>{d.close();const keys=new Set(audio.result);resolve({projects:projects.result.map(({id,name,difficulty,audioId,chart})=>({id,name,difficulty,audioId,notes:chart.notes})),audio:audio.result,orphans:projects.result.filter(x=>!keys.has(x.audioId)).map(x=>`${x.name}/${x.difficulty}`)});};};}));
// 前の知らせを消してから操作し、出た知らせを読む。読む前に少し待ち、遅れて届く自動保存の失敗で上書きされないことも確かめる。
const noticeAfter=async(p,action,settle=600)=>{await p.evaluate(()=>{const t=document.querySelector('#toast');t.hidden=true;t.textContent='';});await action();await p.locator('#toast').waitFor({state:'visible',timeout:5000});await p.waitForTimeout(settle);return p.locator('#toast').innerText();};
async function session(){
  const ctx=await browser.newContext({viewport:{width:390,height:664},hasTouch:true}),errs=[];
  const open=async()=>{const p=await ctx.newPage();p.on('pageerror',e=>errs.push(e.message));p.on('dialog',d=>d.accept());await p.goto(site);await p.waitForTimeout(300);return p;};
  return{ctx,errs,open};
}
try{
  await deletedSourceSong();
  await deletedSongStaleTab();
  await restoreWhileStale();
  await deletedBetweenSourceSaveAndNewDraft('copy');
  await deletedBetweenSourceSaveAndNewDraft('import');
  await newDraftSavedBeforeSourceDeletion();
}finally{await browser.close();}

// 1. 曲Aを開いたまま曲Bの「＋ EASY」の作成画面を開き、その間に別のタブが曲Bの最後の下書き(と音源)を消す。
//    作るを押しても、音源のない(開けない)下書きを残さない。曲Aはそのまま。音源が残っている曲からは今までどおり作れる。
async function deletedSourceSong(){
  const{ctx,errs,open}=await session();
  try{
    const p=await open();
    await p.locator('#audioFile').setInputFiles({name:'いまの曲.wav',mimeType:'audio/wav',buffer:wav()});await saved(p);
    await library(p);await p.locator('#libraryAudioFile').setInputFiles({name:'コピー元の曲.wav',mimeType:'audio/wav',buffer:wav()});await saved(p);
    await openDraft(p,'いまの曲');const before=(await inventory(p)).projects.find(x=>x.name==='いまの曲');
    await library(p);await p.locator('.project-group').filter({hasText:'コピー元の曲'}).getByRole('button',{name:'＋ EASY',exact:true}).click();await p.locator('#duplicateDialog').waitFor({state:'visible'});
    const other=await open();await library(other);await other.locator('.project-group').filter({hasText:'コピー元の曲'}).locator('.danger').click();
    await other.waitForFunction(()=>![...document.querySelectorAll('#projectList h3')].some(h=>h.textContent==='コピー元の曲'));
    let stored=await inventory(p);assert.deepEqual(stored.projects.map(x=>x.name),['いまの曲'],'別のタブで曲Bの下書きが消えた');assert.equal(stored.audio.length,1,'曲Bの音源も消えた');await other.close();
    assert.equal(await noticeAfter(p,()=>p.locator('#createCopy').click()),'元の曲の音源が別のタブで削除されています。「曲・保存」を開き直してください。');
    stored=await inventory(p);assert.deepEqual(stored.orphans,[],'音源のない下書きを作らない');assert.equal(stored.projects.length,1);
    assert.deepEqual(stored.projects.find(x=>x.id===before.id),before,'曲Aの下書きはそのまま');
    assert.equal(await p.locator('#duplicateDialog').evaluate(e=>e.open),false,'作れない作成画面は閉じる');
    assert.equal(await p.locator('#songTitle').innerText(),'いまの曲');assert.equal(await p.locator('#saveStatus').innerText(),'この端末に保存済み');
    await p.screenshot({path:path.join(output,'storage-race-deleted-source.png')});check('no draft is created from a song another tab deleted, and the open song is untouched');
    // 開き直した一覧に曲Bは出ない。音源が残っている別の曲(C)からは、今までどおり作れて開く。
    await library(p);assert.equal(await p.locator('.project-group').filter({hasText:'コピー元の曲'}).count(),0);
    await p.locator('#libraryAudioFile').setInputFiles({name:'もう一つの曲.wav',mimeType:'audio/wav',buffer:wav()});await saved(p);
    await openDraft(p,'いまの曲');await library(p);await p.locator('.project-group').filter({hasText:'もう一つの曲'}).getByRole('button',{name:'＋ EASY',exact:true}).click();await p.locator('#duplicateDialog').waitFor({state:'visible'});
    assert.match(await noticeAfter(p,()=>p.locator('#createCopy').click(),300),/^もう一つの曲 の EASY を作りました。/);
    await p.waitForFunction(()=>!document.querySelector('#libraryButton').disabled);await saved(p);
    assert.equal(await p.locator('#songTitle').innerText(),'もう一つの曲');assert.match(await p.locator('#difficultyLabel').innerText(),/^EASY/);
    stored=await inventory(p);assert.deepEqual(stored.orphans,[]);assert.deepEqual(stored.projects.map(x=>`${x.name}/${x.difficulty}`).sort(),['いまの曲/normal','もう一つの曲/easy','もう一つの曲/normal']);
    check('adding a difficulty from a song whose audio is still stored works as before');
    assert.deepEqual(errs,[]);check('no browser runtime errors when the source song is deleted');
  }finally{await ctx.close();}
}

// 2. 別のタブがこの曲の下書きをすべて消した(音源も消えた)あとの、古いタブの逃がし方。
//    バックアップは作れないので「先に譜面JSON、次に音源をWAV保存」と案内する。書き出しの成功の知らせは残り、上の常設の表示は「別タブで更新」のまま。
async function deletedSongStaleTab(){
  const{ctx,errs,open}=await session();
  try{
    const p=await open();
    await p.locator('#audioFile').setInputFiles({name:'取り残し.wav',mimeType:'audio/wav',buffer:wav()});await saved(p);
    await library(p);await p.locator('#chartFile').setInputFiles({name:'chart_hard.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(chart))});
    await p.waitForFunction(()=>/^HARD/.test(document.querySelector('#difficultyLabel').textContent));await p.waitForTimeout(300);await saved(p);
    // 別のタブで、この曲の下書きを2つとも消す(最後の1つと一緒に音源も消える)。
    const other=await open();await library(other);
    for(const left of [1,0]){await other.locator('#projectList .project .danger').first().click();await other.waitForFunction(n=>document.querySelectorAll('#projectList .project').length===n,left);}
    assert.deepEqual(await inventory(p),{projects:[],audio:[],orphans:[]},'下書きも音源も消えた');await other.close();
    await p.locator('#nav-export').click();await p.waitForTimeout(400);
    // バックアップは作れない。ダウンロードは起きず、譜面JSON → WAV の順に案内する。
    let downloads=0;const counted=()=>downloads++;p.on('download',counted);
    if(!await p.locator('#backupButton').isVisible())await p.locator('#tab-export .advanced>summary').click();
    const refused=await noticeAfter(p,()=>p.locator('#backupButton').click());p.off('download',counted);
    assert.equal(downloads,0);assert.equal(refused,'この曲の音源が別のタブで削除されたため、バックアップを作れません。先に「譜面を書き出す」で譜面JSONを、次に「音源をWAV保存」で音源を保存してください。');
    assert.match(await p.locator('#saveStatus').innerText(),/^別タブで更新/);
    await p.screenshot({path:path.join(output,'storage-race-stale-backup.png')});check('a stale tab explains how to rescue a song whose audio another tab deleted');
    // 案内どおり先に譜面JSON。手元の3ノーツが入り、書き出しの知らせは自動保存の失敗で消えない。
    const json=p.waitForEvent('download');const exported=await noticeAfter(p,()=>p.locator('#exportButton').click());const file=await json;
    assert.equal(file.suggestedFilename(),'chart_hard.json');assert.equal(JSON.parse(fs.readFileSync(await file.path(),'utf8')).notes.length,3,'手元の譜面');
    assert.match(exported,/^譜面を書き出しました。/);
    // 次にWAV。開いている音源から作れて、知らせも残る。
    const sound=p.waitForEvent('download');const saving=await noticeAfter(p,()=>p.locator('#exportAudioButton').click());
    assert.equal((await sound).suggestedFilename(),'audio.wav');assert.match(saving,/^本編用のaudio\.wavを書き出しました。/);
    // 「元の音源を保存」も行き止まりにせず、WAV保存へ案内する。
    assert.equal(await noticeAfter(p,()=>p.locator('#exportOriginalAudio').click(),200),'元の音源が別のタブで削除されています。「音源をWAV保存」で、開いている音源を保存してください。');
    // 共有できたときも、共有の成功の知らせを残す。
    await p.evaluate(()=>{Object.defineProperty(navigator,'canShare',{value:()=>true,configurable:true});Object.defineProperty(navigator,'share',{value:async()=>{},configurable:true});});
    assert.equal(await noticeAfter(p,()=>p.locator('#shareChart').click()),'譜面を共有しました。');
    // 上の常設の表示は「別タブで更新」のまま(押せる・閉じるときの確認も残る)。
    assert.match(await p.locator('#saveStatus').innerText(),/^別タブで更新/);assert.equal(await p.locator('#saveStatus').evaluate(e=>e.classList.contains('save-error')),true);
    check('exports keep their success notice while the persistent status still says the draft is not saved');
    assert.deepEqual(errs,[]);check('no browser runtime errors in the stale tab');
  }finally{await ctx.close();}
}

// 3. 別のタブが同じ下書きを更新したあと、古いタブで「バックアップを開く」。今の下書きを保存できないので復元は止まり、
//    上の常設の表示は persist が出した「別タブで更新 — ここを押して…」のまま(「読み込みを中止しました。…そのままです」で上書きしない)。
async function restoreWhileStale(){
  const{ctx,errs,open}=await session();
  try{
    const p=await open();
    await p.locator('#audioFile').setInputFiles({name:'復元.wav',mimeType:'audio/wav',buffer:wav()});await saved(p);
    // 復元に使うバックアップを先に作る。
    await p.locator('#nav-export').click();await p.waitForTimeout(300);if(!await p.locator('#backupButton').isVisible())await p.locator('#tab-export .advanced>summary').click();
    const made=p.waitForEvent('download');await p.locator('#backupButton').click();const saber=fs.readFileSync(await (await made).path());
    // 別のタブで同じ下書きを開き、曲名を変えて保存する(古いタブの版は古くなる)。古いタブはこの間なにも保存しない。
    const other=await open();await openDraft(other,'復元');await library(other);
    await other.locator('#projectName').fill('別のタブで改名');await other.locator('#projectName').press('Tab');
    for(let i=0;i<30&&!(await inventory(p)).projects.some(x=>x.name==='別のタブで改名');i++)await p.waitForTimeout(100);
    const count=(await inventory(p)).projects.length;assert.ok((await inventory(p)).projects.some(x=>x.name==='別のタブで改名'),'別のタブの保存が済んだ');await other.close();
    const notice=await noticeAfter(p,()=>p.locator('#backupFile').setInputFiles({name:'復元.saber',mimeType:'application/octet-stream',buffer:saber}),300);
    await p.waitForFunction(()=>!document.querySelector('#libraryButton').disabled);
    assert.match(notice,/^別のタブでこの下書きが更新・削除されました/);
    assert.match(await p.locator('#saveStatus').innerText(),/^別タブで更新/,'「読み込みを中止しました」で上書きしない');assert.equal(await p.locator('#saveStatus').evaluate(e=>e.classList.contains('save-error')),true);
    assert.equal((await inventory(p)).projects.length,count,'復元は止まり、下書きは増えない');
    await p.locator('#nav-record').click();await p.waitForTimeout(300);await p.locator('#saveStatus').click();assert.equal(await p.locator('#nav-export').getAttribute('aria-selected'),'true','押すと渡すへ');
    check('a restore stopped by a stale draft keeps the actionable status');
    assert.deepEqual(errs,[]);check('no browser runtime errors in the stale restore');
  }finally{await ctx.close();}
}

// この後の競合試験では、手元に3ノーツのHARDを開き、同じ音源の下書きをこの1件だけにする。
// 取り込みで自動作成された空のNORMALだけを消す。合成データの専用コンテキスト内に限る。
async function currentSource(p){
  await p.locator('#audioFile').setInputFiles({name:'作成中の曲.wav',mimeType:'audio/wav',buffer:wav()});await saved(p);
  await library(p);await p.locator('#chartFile').setInputFiles({name:'chart_hard.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(chart))});
  await p.locator('#library').waitFor({state:'hidden'});
  await p.waitForFunction(()=>/^HARD/.test(document.querySelector('#difficultyLabel').textContent)&&!document.querySelector('#libraryButton').disabled);await saved(p);
  const source=(await inventory(p)).projects.find(item=>item.difficulty==='hard');assert.ok(source);assert.equal(source.notes.length,3);
  await p.evaluate(async id=>{const s=await import('./storage.js');for(const item of await s.listProjects())if(item.id!==id)await s.deleteProject(item.id);},source.id);
  assert.equal((await inventory(p)).projects.length,1);return source;
}

// 実際のIDBトランザクションを開いた直後に、本物のdeleteProjectをキューする。
// データや保存結果は差し替えない。同じ接続でも、別タブと同じ重複スコープの直列化で順序が決まる。
// afterWrite=1: 元のpersist→削除→新規保存。2: 元のpersist→新規保存→削除。
async function queueSourceDeletion(p,sourceId,afterWrite){
  await p.evaluate(async({sourceId,afterWrite})=>{
    const s=await import('./storage.js'),native=IDBDatabase.prototype.transaction;let writes=0;
    window.storageRace={queued:false,done:false,error:null,notices:[]};
    const notice=document.querySelector('#toast');
    new MutationObserver(()=>{if(!notice.hidden&&notice.textContent)window.storageRace.notices.push(notice.textContent);}).observe(notice,{childList:true,subtree:true,attributes:true});
    IDBDatabase.prototype.transaction=function(stores,mode,...rest){
      const tx=native.call(this,stores,mode,...rest),names=typeof stores==='string'?[stores]:Array.from(stores);
      if(mode==='readwrite'&&names.includes('projects')&&++writes===afterWrite){
        IDBDatabase.prototype.transaction=native;window.storageRace.queued=true;
        s.deleteProject(sourceId).then(()=>window.storageRace.done=true,error=>{window.storageRace.error=error.message;window.storageRace.done=true;});
      }
      return tx;
    };
  },{sourceId,afterWrite});
}
async function deletionFinished(p){
  await p.waitForFunction(()=>window.storageRace?.done);
  const race=await p.evaluate(()=>window.storageRace);assert.equal(race.queued,true);assert.equal(race.error,null);return race;
}

// 4/5. 開いている曲のコピーとJSON取り込み。最初のpersistが成功しても、その次の新規保存までに
//      元の最後の下書きが消える順序を再現する。新IDへ切り替えず、現在の譜面はJSONで救出できる。
async function deletedBetweenSourceSaveAndNewDraft(action){
  const{ctx,errs,open}=await session();
  try{
    const p=await open(),source=await currentSource(p);
    await library(p);
    if(action==='copy'){
      await p.locator('#duplicateButton').click();await p.locator('#copyName').fill('作れないコピー');await p.locator('#copyDifficulty').selectOption('easy');
    }
    // コピー画面を開いたstopの保存タイマーを先に終え、確定ボタンのpersistだけを狙う。
    await p.waitForTimeout(200);await saved(p);await queueSourceDeletion(p,source.id,1);
    const replacement={...chart,notes:[{...chart.notes[0],time:9000,beat:18,color:'gold'}]};
    const refused=await noticeAfter(p,()=>action==='copy'?p.locator('#createCopy').click():p.locator('#chartFile').setInputFiles({name:'chart_easy.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(replacement))}));
    const race=await deletionFinished(p);
    assert.equal(refused,'元の曲の音源が別のタブで削除されています。「曲・保存」を開き直してください。');
    assert.equal(race.notices.some(text=>/新しい下書き.*(?:作りました|開きました)/.test(text)),false,'失敗した新規保存を成功と案内しない');
    assert.deepEqual(await inventory(p),{projects:[],audio:[],orphans:[]},'元の削除は完了し、新しい孤児下書きは残らない');
    assert.equal(await p.locator('#songTitle').innerText(),source.name);assert.match(await p.locator('#difficultyLabel').innerText(),/^HARD/);
    assert.equal(await p.locator('#noteCount').innerText(),'3 NOTES','取り込もうとした別譜面へ切り替わらない');
    // 失敗時に残るダイアログを閉じて、手元の元HARDを救う。DBを再作成する操作ではない。
    for(const id of ['duplicateDialog','library'])if(await p.locator('#'+id).evaluate(e=>e.open))await p.locator(`[data-close="${id}"]`).click();
    await p.locator('#nav-export').click();await p.waitForTimeout(300);
    const downloaded=p.waitForEvent('download');const notice=await noticeAfter(p,()=>p.locator('#exportButton').click());const file=await downloaded;
    assert.equal(file.suggestedFilename(),'chart_hard.json');assert.deepEqual(JSON.parse(fs.readFileSync(await file.path(),'utf8')),chart,'未適用の取り込みではなく手元の全譜面を保持');
    assert.match(notice,/^譜面を書き出しました。/);assert.equal(await p.locator('#exportAudioButton').isDisabled(),false,'音声バッファも保持');
    assert.deepEqual(await inventory(p),{projects:[],audio:[],orphans:[]},'救出用の書き出しでも削除済み下書きを復活させない');
    assert.match(await p.locator('#saveStatus').innerText(),/^別タブで更新/);assert.deepEqual(errs,[]);
    await p.screenshot({path:path.join(output,`storage-race-${action}-after-source-save.png`)});
    check(`${action} refuses audio deleted after source persistence, creates no orphan, and exports the unchanged local chart`);
  }finally{await ctx.close();}
}

// 6. 反対の順序: コピーの保存トランザクションが削除より先なら、削除側は新しい共有参照を読み、
//    元だけを消して音源を残す。コピーは再読み込み後にも開け、全ノーツを保持する。
async function newDraftSavedBeforeSourceDeletion(){
  const{ctx,errs,open}=await session();
  try{
    const p=await open(),source=await currentSource(p);
    await library(p);await p.locator('#duplicateButton').click();await p.locator('#copyName').fill('先に保存されたコピー');await p.locator('#copyDifficulty').selectOption('easy');
    await p.waitForTimeout(200);await saved(p);await queueSourceDeletion(p,source.id,2);
    assert.match(await noticeAfter(p,()=>p.locator('#createCopy').click()),/^新しい下書きを作りました。/);await deletionFinished(p);await saved(p);
    const stored=await inventory(p);assert.equal(stored.projects.length,1);assert.deepEqual(stored.orphans,[]);assert.deepEqual(stored.audio,[source.audioId]);
    const copy=stored.projects[0];assert.notEqual(copy.id,source.id);assert.equal(copy.name,'先に保存されたコピー');assert.equal(copy.difficulty,'easy');
    assert.deepEqual(copy.notes.map(({__editorId,...note})=>note),chart.notes);
    await p.reload();await p.locator('#resumeProject').click();await p.waitForFunction(()=>document.querySelector('#songTitle').textContent==='先に保存されたコピー'&&!document.querySelector('#libraryButton').disabled);
    assert.equal(await p.locator('#noteCount').innerText(),'3 NOTES');assert.match(await p.locator('#difficultyLabel').innerText(),/^EASY/);assert.deepEqual(errs,[]);
    check('a copy saved before source deletion keeps shared audio and reopens with all notes');
  }finally{await ctx.close();}
}
