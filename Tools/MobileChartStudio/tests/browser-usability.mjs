// 使いやすさの回帰確認(2026-10-22〜)。browser-smoke.mjs と同じく、独立したブラウザ・合成WAVで、実ユーザーの下書きは触らない。
// 使い方: 静的HTTPサーバーで dist を配り、SITE_URL・PLAYWRIGHT_MODULE・BROWSER_CHANNEL・QA_OUTPUT を指定して node で実行。
import {createRequire} from 'node:module';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
const require=createRequire(import.meta.url),{chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const output=process.env.QA_OUTPUT||'qa-output';fs.mkdirSync(output,{recursive:true});
const site=process.env.SITE_URL||'http://127.0.0.1:8769/';
const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL||'msedge',headless:true});
const context=await browser.newContext({viewport:{width:390,height:664},hasTouch:true});
const page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
const check=name=>console.log('PASS '+name),settle=()=>page.waitForTimeout(300);
const box=selector=>page.locator(selector).first().boundingBox();
const stored=async()=>page.evaluate(async()=>{const id=localStorage.getItem('saber-last-project');return new Promise((resolve,reject)=>{const r=indexedDB.open('saber-tap-studio-v1',1);r.onerror=()=>reject(r.error);r.onsuccess=()=>{const d=r.result,tx=d.transaction('projects','readonly'),q=tx.objectStore('projects').get(id);q.onsuccess=()=>resolve(q.result);tx.oncomplete=()=>d.close();};});});
function wav(){const length=16000*30,b=Buffer.alloc(44+length*2);b.write('RIFF');b.writeUInt32LE(b.length-8,4);b.write('WAVEfmt ',8);b.writeUInt32LE(16,16);b.writeUInt16LE(1,20);b.writeUInt16LE(1,22);b.writeUInt32LE(16000,24);b.writeUInt32LE(32000,28);b.writeUInt16LE(2,32);b.writeUInt16LE(16,34);b.write('data',36);b.writeUInt32LE(length*2,40);for(let i=0;i<length;i++)b.writeInt16LE(Math.sin(i/16000*440*Math.PI*2)*300,i*2+44);return b;}
const chart={bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,displayLevel:2,timeSignatures:[],notes:Array.from({length:30},(_,i)=>({time:1000+i*500,beat:i,x:(i%5)-2,y:0,color:i%2?'red':'blue',direction:'none',count:1,type:'tap',lengthMs:0}))};
try{
  await page.goto(site);await settle();
  assert.match(await page.locator('#appBrand').innerText(),/^Saber Tap Studio v\d+-\d{8}$/);check('the footer shows the app version');
  assert.equal(await page.locator('#settingsButton').isDisabled(),true,'曲を開く前は設定を押せない');check('settings wait until a song is open');
  await page.locator('#audioFile').setInputFiles({name:'使いやすさ.wav',mimeType:'audio/wav',buffer:wav()});
  await page.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
  // 1. 知らせは録音開始のボタンを隠さない(上の帯に出る)。
  assert.equal(await page.locator('#toast').isVisible(),true);
  const toast=await box('#toast'),play=await box('#playButton');
  assert.ok(toast.y+toast.height<=play.y,'知らせが録音開始の行より上');await page.screenshot({path:path.join(output,'usability-toast.png')});check('a toast stays above the record button');
  // 2. 曲・保存は、操作を2列に詰めて下書きの一覧が最初の画面に入る。
  await page.locator('#libraryButton').click();await page.locator('#library').waitFor({state:'visible'});
  await page.locator('#chartFile').setInputFiles({name:'chart_normal.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(chart))});await settle();
  await page.locator('[data-close="library"]').click();await page.locator('#libraryButton').click();await page.locator('#library').waitFor({state:'visible'});
  const group=await box('.project-group .open-project');assert.ok(group&&group.y+group.height<=664,'最初の下書きのボタンが画面内');
  await page.screenshot({path:path.join(output,'usability-library.png')});await page.locator('[data-close="library"]').click();check('the library shows drafts without scrolling');
  // 3. タブごとにスクロール位置を覚える。
  await page.locator('#nav-preview').click();await settle();await page.evaluate(()=>window.scrollTo(0,document.body.scrollHeight));await settle();
  const previewScroll=await page.evaluate(()=>window.scrollY);assert.ok(previewScroll>0,'確かめるは下へ送れる');
  await page.locator('#nav-export').click();await settle();assert.equal(await page.evaluate(()=>window.scrollY),0,'渡すは上から');
  await page.locator('#nav-preview').click();await settle();assert.equal(await page.evaluate(()=>window.scrollY),previewScroll,'確かめるは前の位置');check('each tab keeps its own scroll position');
  // 4. 直す: 削除は見出しの横。消すと次のノーツを選んだまま。二度押しで2個は消えない。
  await page.locator('#nav-edit').click();if(!await page.locator('.map-note-details').evaluate(e=>e.open))await page.locator('.map-note-details summary').click();
  await page.locator('#mapNotes button').first().click();await settle();
  const head=await box('.inline-note-head'),remove=await box('#deleteNote');assert.ok(remove.y>=head.y&&remove.y+remove.height<=head.y+head.height+1,'削除は見出しの行');
  await page.locator('#deleteNote').click();await page.locator('#deleteNote').click();await settle();
  assert.equal((await stored()).chart.notes.length,29,'続けて押しても1個だけ');assert.equal(await page.locator('#noteDialog').evaluate(e=>e.open),true,'次のノーツを選んだまま');
  assert.match(await page.locator('#selectedNoteLabel').innerText(),/^1 \/ 29/);
  await page.waitForTimeout(450);await page.locator('#deleteNote').click();await settle();assert.equal((await stored()).chart.notes.length,28,'少し待てば次も消せる');
  await page.screenshot({path:path.join(output,'usability-delete.png')});check('delete sits in the note header and keeps the next note selected');
  // 5. 設定で BPM をたたくとき、その場で曲を流せる(ノーツは増えない)。閉じれば止まる。
  await page.locator('#nav-record').click();await page.locator('#settingsButton').click();await page.locator('#settingsDialog').waitFor({state:'visible'});
  await page.locator('#tempoListen').click();await page.waitForTimeout(300);
  assert.equal(await page.locator('#tempoListen').innerText(),'止める');
  for(let i=0;i<5;i++){await page.locator('#tapTempo').click();await page.waitForTimeout(120);}
  await page.locator('#settingsDialogClose').click();await settle();
  assert.equal(await page.locator('#playButton').innerText(),'録音開始','閉じたら止まり、録る画面の使い方へ戻る');
  assert.equal((await stored()).chart.notes.length,28,'曲を流してもノーツは増えない');check('settings can play the song for tap tempo and stop on close');
  // 6. 区間の近道: 今の位置から最後まで録り直す(1回で A・B・置き換え)。区間を解除も1回で。区間ボタンは状態を見せる。
  await page.evaluate(()=>{const s=document.querySelector('#seek');s.value='12';s.dispatchEvent(new Event('input',{bubbles:true}));});await settle();
  await page.locator('#rangeButton').click();await page.locator('#rangeDialog').waitFor({state:'visible'});assert.equal(await page.locator('#clearRange').isVisible(),false,'区間がないときは解除を出さない');
  await page.locator('#redoFromHere').click();await settle();
  assert.equal(await page.locator('#rangeDialog').isVisible(),false);assert.equal(await page.locator('#rangeStart').inputValue(),'12.000');assert.equal(await page.locator('#rangeEnd').inputValue(),'30.000');
  assert.equal(await page.locator('#rangeEnabled').isChecked(),true);assert.equal(await page.locator('#replaceRange').isChecked(),true);
  assert.equal(await page.locator('#rangeButton').innerText(),'区間 ON');assert.match(await page.locator('#recordWarning').innerText(),/置き換え · 12\.00〜30\.00秒/);
  await page.waitForTimeout(3000);await page.screenshot({path:path.join(output,'usability-range.png')});
  await page.locator('#rangeButton').click();await page.locator('#clearRange').click();await settle();
  assert.equal(await page.locator('#rangeEnabled').isChecked(),false);assert.equal(await page.locator('#rangeButton').innerText(),'区間');check('one tap re-records from here, one tap clears the range');
  // 7. 「矢印の変更後に次へ」と表示幅は、この端末で覚える(読み直しても同じ)。
  await page.locator('#nav-edit').click();await page.locator('#mapZoom').selectOption('10');await settle();
  await page.locator('#mapNotes button').first().click();await page.locator('#autoNext').check();await settle();
  await page.reload();await settle();await page.locator('#resumeProject').waitFor({state:'visible'});
  assert.equal(await page.locator('#autoNext').isChecked(),true);assert.equal(await page.locator('#mapZoom').inputValue(),'10');check('arrow auto-next and the timeline zoom are remembered');
  // 8. 最初の画面から、前回の下書きを1回で開ける。
  await page.reload();await settle();assert.equal(await page.locator('#resumeProject').isVisible(),true);
  assert.match(await page.locator('#resumeProject').innerText(),/続きを開く：.*NORMAL/);await page.screenshot({path:path.join(output,'usability-resume.png')});
  await page.locator('#resumeProject').click();await page.waitForFunction(()=>!document.querySelector('#welcome')||document.querySelector('#welcome').hidden);
  assert.equal((await stored()).chart.notes.length,28);check('the welcome screen reopens the last draft in one tap');
  assert.deepEqual(errors,[]);check('no browser runtime errors');
  await fixesFound20261022();
}finally{await browser.close();}

// 2026-10-22 Codex の点検で見つかった食い違いの直し。新しい端末(別のブラウザの中身)で確かめる。
async function fixesFound20261022(){
  const ctx=await browser.newContext({viewport:{width:390,height:664},hasTouch:true}),p=await ctx.newPage(),errs=[];p.on('pageerror',e=>errs.push(e.message));p.on('dialog',d=>d.accept());
  const wait=()=>p.waitForTimeout(300);
  const fill=async(id,text)=>{await p.locator('#'+id).fill(String(text));await p.locator('#'+id).press('Tab');await wait();};
  try{
    await p.goto(site);await wait();
    await p.locator('#audioFile').setInputFiles({name:'直し.wav',mimeType:'audio/wav',buffer:wav()});
    await p.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
    // 1. BPMを決めて「元に戻す」と、未設定に戻る。やり直しで決めた値に戻る。
    await p.locator('#settingsButton').click();await fill('bpm',150);await p.locator('#settingsDialogClose').click();await wait();
    assert.match(await p.locator('#difficultyLabel').innerText(),/150 BPM/);
    await p.locator('#undoButton').click();await wait();assert.match(await p.locator('#difficultyLabel').innerText(),/BPM未設定/);
    await p.locator('#redoButton').click();await wait();assert.match(await p.locator('#difficultyLabel').innerText(),/150 BPM/);check('undoing a BPM brings back "not set"');
    // 2. 確かめるの診断は、同じタブのまま設定を変えると描き直す。
    await p.locator('#undoButton').click();await wait();await p.locator('#nav-preview').click();await wait();
    assert.match(await p.locator('#reportIssueCount').innerText(),/BPM未設定/);
    await p.locator('#settingsButton').click();await fill('bpm',140);await p.locator('#settingsDialogClose').click();await wait();
    assert.doesNotMatch(await p.locator('#reportIssueCount').innerText(),/BPM未設定/);check('diagnostics refresh without leaving the tab');
    // 3. 確かめるから区間・ループを決められる(録り直しの近道は出さない)。
    assert.equal(await p.locator('#previewRangeButton').isVisible(),true);await p.locator('#previewRangeButton').click();
    assert.equal(await p.locator('#loopHere').isVisible(),true);assert.equal(await p.locator('#redoFromHere').isVisible(),false);assert.equal(await p.locator('#replaceRange').isVisible(),false);
    await p.locator('#loopHere').click();await wait();
    assert.equal(await p.locator('#rangeEnabled').isChecked(),true);assert.equal(await p.locator('#previewLoop').isChecked(),true);assert.equal(await p.locator('#replaceRange').isChecked(),false);
    assert.equal(await p.locator('#previewRangeButton').innerText(),'区間・ループ ON');await p.screenshot({path:path.join(output,'fix-preview-loop.png')});check('preview can set a loop range');
    await p.locator('#previewRangeButton').click();await p.locator('#clearRange').click();await wait();
    // 4. 渡すに、実際のファイル名とゲームでの名前を出す。
    await p.locator('#nav-export').click();await wait();
    assert.match(await p.locator('#exportNames').innerText(),/譜面：chart_normal\.json/);assert.match(await p.locator('#exportNames').innerText(),/直し\.wav → PCで audio\.wav に名前を変える/);check('export shows the saved file names');
    // 5. 直す: 選んだノーツへグラフが追いかける。元に戻すで選んだまま値が戻り、やり直しもその場で。
    const notes=Array.from({length:12},(_,i)=>({time:1000+i*500,beat:2+i,x:0,y:0,color:i%2?'red':'blue',direction:'none',count:1,type:'tap',lengthMs:0}));
    await p.locator('#libraryButton').click();await p.locator('#chartFile').setInputFiles({name:'chart_normal.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify({bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,displayLevel:2,timeSignatures:[],notes}))});await wait();await p.locator('[data-close="library"]').click();
    await p.locator('#nav-edit').click();await p.locator('#mapZoom').selectOption('2');await wait();
    if(!await p.locator('.map-note-details').evaluate(e=>e.open))await p.locator('.map-note-details summary').click();
    await p.locator('#mapNotes button').first().click();await wait();for(let i=0;i<4;i++){await p.locator('#nextNote').click();await wait();}
    const range=(await p.locator('#mapRange').innerText()).match(/(\d+):(\d+\.\d+)/g).map(t=>{const[m,s]=t.split(':');return Number(m)*60+Number(s);});
    assert.ok(range[0]<=3&&3<=range[1],`選んだ3.0秒のノーツが表示区間 ${range} に入る`);
    await p.locator('#direction-left').click();await wait();const label=await p.locator('#selectedNoteLabel').innerText();
    await p.locator('#editUndo').click();await wait();assert.equal(await p.locator('#noteDialog').evaluate(e=>e.open),true,'元に戻しても選んだまま');
    assert.equal(await p.locator('#selectedNoteLabel').innerText(),label);assert.equal(await p.locator('#direction-none').getAttribute('aria-pressed'),'true');
    await p.locator('#editRedo').click();await wait();assert.equal(await p.locator('#direction-left').getAttribute('aria-pressed'),'true');check('the graph follows the selection and undo/redo keep it');
    // 6. まとめて直す: 曲全体のときは A・B 欄を隠す。確かめたあと、適用ボタンに操作と件数を書く。
    await p.locator('#mapRefine').click();await p.locator('#refineDialog').waitFor({state:'visible'});
    assert.equal(await p.locator('.refine-range').isVisible(),false);assert.match(await p.locator('#refineScopeNote').innerText(),/曲全体/);
    await p.locator('#refineScope').selectOption('range');assert.equal(await p.locator('.refine-range').isVisible(),true);await p.locator('#refineScope').selectOption('all');
    await p.locator('#refineKind').selectOption('shift');await p.locator('#refineCheck').click();await wait();
    assert.match(await p.locator('#refineApply').innerText(),/時刻移動を 12 個に適用/);await p.locator('[data-close="refineDialog"]').click();check('bulk edit makes the whole-song scope and the count explicit');
    // 7. 開いている下書きを消したら、「続きを開く」は残っている下書きを指す。
    await p.locator('#libraryButton').click();await p.locator('#library').waitFor({state:'visible'});
    const before=await p.locator('.project .danger').count();await p.locator('.project').filter({hasText:'編集中'}).locator('.danger').click();await wait();
    assert.equal(await p.locator('.project .danger').count(),before-1);await p.locator('[data-close="library"]').click();await wait();
    assert.equal(await p.locator('#resumeProject').isVisible(),true);assert.doesNotMatch(await p.locator('#resumeProject').innerText(),/編集中/);
    await p.locator('#resumeProject').click();await p.waitForFunction(()=>document.querySelector('#welcome').hidden);check('resume points at a draft that still exists after deleting');
    assert.deepEqual(errs,[]);check('no browser runtime errors after the fixes');
  }finally{await ctx.close();}
}
