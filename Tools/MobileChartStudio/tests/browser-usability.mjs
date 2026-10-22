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
const storedIn=async target=>target.evaluate(async()=>{const id=localStorage.getItem('saber-last-project');return new Promise((resolve,reject)=>{const r=indexedDB.open('saber-tap-studio-v1',1);r.onerror=()=>reject(r.error);r.onsuccess=()=>{const d=r.result,tx=d.transaction('projects','readonly'),q=tx.objectStore('projects').get(id);q.onsuccess=()=>resolve(q.result);tx.oncomplete=()=>d.close();};});});
const stored=()=>storedIn(page);
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
  assert.equal(await page.locator('#library').isVisible(),false,'読み込んだら一覧を閉じる');await page.locator('#libraryButton').click();await page.locator('#library').waitFor({state:'visible'});
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
  await round5();
  await round6();
  await codexRound6();
  await round7();
  await round8();
  await round9();
  await round10();
  await round11();
}finally{await browser.close();}

// 2026-10-22 11回目(第2回の Opus 議論 S1・S3): 拍の始まりを数値で直すと、時刻を保って拍を数え直す(テンポが変わる譜面・テンポ地図・拍子の変わり目は壊さない)。録音・録り直しで選択が消えたら、選択パネルも閉じる。
async function round11(){
  const ctx=await browser.newContext({viewport:{width:390,height:664},hasTouch:true}),p=await ctx.newPage(),errs=[];p.on('pageerror',e=>errs.push(e.message));
  const wait=()=>p.waitForTimeout(300),saved=()=>p.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
  const open=async(name,value)=>{await p.locator('#libraryButton').click();await p.locator('#library').waitFor({state:'visible'});await p.locator('#chartFile').setInputFiles({name,mimeType:'application/json',buffer:Buffer.from(JSON.stringify(value))});await wait();await saved();};
  const setZero=async ms=>{await p.locator('#settingsButton').click();await p.locator('#beatZero').fill(String(ms));await p.locator('#beatZero').press('Tab');await wait();await p.locator('#settingsDialogClose').click();await wait();await saved();};
  const playing=()=>p.waitForFunction(()=>document.querySelector('#playButton').textContent==='一時停止');
  const times=notes=>notes.map(n=>n.time),beats=notes=>notes.map(n=>n.beat),recounted=notes=>notes.map(n=>(n.time-250)/500);
  const steady=Array.from({length:8},(_,i)=>({time:1000+i*500,beat:2+i,x:0,y:0,color:i%2?'red':'blue',direction:'none',count:1,type:'tap',lengthMs:0}));
  const song={bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,displayLevel:2,timeSignatures:[]};
  try{
    await p.goto(site);await wait();
    await p.locator('#audioFile').setInputFiles({name:'拍の始まり.wav',mimeType:'audio/wav',buffer:wav()});await saved();
    // 1. 一定テンポ(120 BPM)で拍の始まりを0→250msにすると、時刻はそのまま、拍は (時刻−250)/500 に数え直す。小節で移動できるまま。元に戻すで拍も戻る。
    await open('chart_normal.json',{...song,notes:steady});assert.equal(await p.locator('#backButton').innerText(),'−1小節');
    await setZero(250);let value=(await storedIn(p)).chart;
    assert.equal(value.beatZeroMs,250);assert.deepEqual(times(value.notes),times(steady),'時刻はそのまま');assert.deepEqual(beats(value.notes),recounted(steady),'新しい拍の始まりから数え直す');
    assert.equal(await p.locator('#backButton').innerText(),'−1小節','テンポが変わる譜面と取り違えない');
    await p.locator('#undoButton').click();await wait();await saved();value=(await storedIn(p)).chart;
    assert.equal(value.beatZeroMs,0);assert.deepEqual(beats(value.notes),beats(steady),'元に戻すと拍も戻る');assert.equal(await p.locator('#beatZero').inputValue(),'0');
    await p.locator('#redoButton').click();await wait();await saved();assert.deepEqual(beats((await storedIn(p)).chart.notes),recounted(steady),'やり直すと数え直した拍');
    check('a typed beat origin recounts beats, keeps times and bar navigation, and undoes in one step');
    // 2. 途中に拍子の変わり目(4拍目から3/4)と未知の項目がある一定テンポの譜面。拍子の変わり目と未知の項目はそのまま、拍は同じく数え直す。
    const meters=[{beat:0,numerator:4,denominator:4},{beat:4,numerator:3,denominator:4}];
    await open('chart_hard.json',{...song,timeSignatures:meters,_comment:'残す',notes:steady.map(n=>({...n,customNote:'残す'}))});
    await setZero(250);value=(await storedIn(p)).chart;
    assert.deepEqual(value.timeSignatures,meters,'拍子の変わり目は拍のまま');assert.equal(value._comment,'残す');assert.ok(value.notes.every(n=>n.customNote==='残す'),'ノーツの未知の項目');
    assert.deepEqual(times(value.notes),times(steady));assert.deepEqual(beats(value.notes),recounted(steady));assert.equal(await p.locator('#backButton').innerText(),'−1小節');
    check('a meter change and unknown fields survive a typed beat origin');
    // 3. テンポが変わる譜面(拍が1つの格子に乗らない)は、拍の始まりを変えても拍をそのまま残す。
    const variable=steady.map((n,i)=>({...n,beat:2+i*1.5}));
    await open('chart_easy.json',{...song,notes:variable});assert.equal(await p.locator('#backButton').innerText(),'−5秒','テンポが変わる譜面として扱う');
    await setZero(250);value=(await storedIn(p)).chart;
    assert.equal(value.beatZeroMs,250);assert.deepEqual(times(value.notes),times(variable));assert.deepEqual(beats(value.notes),beats(variable),'拍はそのまま');
    // 4. テンポ地図(editorTempoMap)のある譜面は、本編とUnityの譜面エディターが地図で拍を数える。拍も地図もそのまま残す。
    const tempoMap=[{beat:0,timeMs:0},{beat:16,timeMs:8000}];
    await open('chart_normal.json',{...song,editorTempoMap:tempoMap,notes:steady});
    await setZero(250);value=(await storedIn(p)).chart;
    assert.deepEqual(value.editorTempoMap,tempoMap);assert.deepEqual(times(value.notes),times(steady));assert.deepEqual(beats(value.notes),beats(steady),'地図に合った拍のまま');
    check('variable-tempo beats and a tempo map are left alone');
    // 5. 「直す」でノーツを選んだまま録音して入力すると、選択が消える。直すへ戻ったとき、中身のない選択パネルを残さない。
    const pick=async()=>{await p.locator('#nav-edit').click();await wait();if(!await p.locator('.map-note-details').evaluate(e=>e.open))await p.locator('.map-note-details summary').click();await p.locator('#mapNotes button').first().click();await wait();assert.equal(await p.locator('#noteDialog').evaluate(e=>e.open),true);};
    const record=async tap=>{await p.locator('#nav-record').click();await wait();await p.locator('#playButton').click();await playing();await p.waitForTimeout(200);if(tap)await p.locator('#pad').tap({position:{x:60,y:100}});await p.waitForTimeout(200);await p.locator('#playButton').click();await wait();await saved();};
    const panel=async()=>{await p.locator('#nav-edit').click();await wait();return{open:await p.locator('#noteDialog').evaluate(e=>e.open),remove:await p.locator('#deleteNote').isVisible()};};
    await open('chart_normal.json',{...song,notes:steady});
    await pick();await record(true);assert.equal((await storedIn(p)).chart.notes.length,9);
    assert.deepEqual(await panel(),{open:false,remove:false},'録音で選択が消えたらパネルも閉じる');
    // 入力しなかった録音では、選択もパネルも残す(選択が消えたときだけ閉じる)。
    await pick();const label=await p.locator('#selectedNoteLabel').innerText();await record(false);
    assert.deepEqual(await panel(),{open:true,remove:true});assert.equal(await p.locator('#selectedNoteLabel').innerText(),label);
    // 6. 選んだまま「録り直す」(直前の録音の前へ戻して録る)も同じ。
    await p.locator('#nav-record').click();await wait();await p.locator('#retryTake').click();await playing();await p.waitForTimeout(200);await p.locator('#playButton').click();await wait();await saved();
    assert.equal((await storedIn(p)).chart.notes.length,8,'直前の録音の前へ戻る');
    assert.deepEqual(await panel(),{open:false,remove:false},'録り直しで選択が消えたらパネルも閉じる');
    check('recording or retrying a take closes the emptied note panel');
    assert.deepEqual(errs,[]);check('no browser runtime errors in round 11');
  }finally{await ctx.close();}
}

// 2026-10-22 10回目: Codex が見つけた、今日の機能どうしの組み合わせの不具合。
async function round10(){
  const ctx=await browser.newContext({viewport:{width:390,height:664},hasTouch:true}),p=await ctx.newPage(),errs=[];p.on('pageerror',e=>errs.push(e.message));
  const wait=()=>p.waitForTimeout(300),saved=()=>p.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
  const setBpm=async v=>{await p.locator('#settingsButton').click();await p.locator('#bpm').fill(String(v));await p.locator('#bpm').press('Tab');await wait();await p.locator('#settingsDialogClose').click();await wait();};
  try{
    await p.goto(site);await wait();
    await p.locator('#audioFile').setInputFiles({name:'組み合わせ.wav',mimeType:'audio/wav',buffer:wav()});await saved();
    // 1. BPMを決めたあと(150)、一定テンポのまま直しても(180)、小節で移動でき、拍は新しいBPMで数え直す。
    await p.evaluate(()=>{const s=document.querySelector('#seek');s.value='10';s.dispatchEvent(new Event('input',{bubbles:true}));});await wait();
    await p.locator('#playButton').click();await p.waitForTimeout(200);for(let i=0;i<3;i++){await p.locator('#pad').tap({position:{x:60+i*40,y:100}});await p.waitForTimeout(300);}await p.locator('#playButton').click();await wait();
    await setBpm(150);await setBpm(180);assert.equal(await p.locator('#backButton').innerText(),'−1小節');await saved();
    assert.deepEqual((await storedIn(p)).chart.notes.map(n=>Math.abs(n.beat-n.time*180/60000)<1e-3),[true,true,true]);check('correcting a confirmed BPM on a steady chart recounts beats');
    // 2. 時刻に音源の外(99秒)を打ちかけて矢印を押すと、時刻は採用されず、矢印も変えない(次へも進まない)。
    await p.locator('#nav-edit').click();await wait();if(!await p.locator('.map-note-details').evaluate(e=>e.open))await p.locator('.map-note-details summary').click();
    await p.locator('#mapCurrent').click();await wait();await p.locator('#mapNotes button').first().click();await wait();await p.locator('#autoNext').check();const label=await p.locator('#selectedNoteLabel').innerText();
    await p.locator('#noteTime').fill('99');await p.evaluate(()=>document.getElementById('direction-up').click());await wait();
    const first=(await storedIn(p)).chart.notes[0];assert.equal(first.direction,'none','矢印は変えない');assert.ok(first.time<11000,'時刻も採用しない');
    assert.equal(await p.locator('#selectedNoteLabel').innerText(),label,'次へ進まない');await p.locator('#autoNext').uncheck();check('a failed typed value stops the arrow edit and auto-next');
    // 3. 点検モードで候補を消して、元に戻す→前の候補→やり直しても、点検は残りの候補で続く。
    const notes=Array.from({length:6},(_,i)=>({time:1000+i*500,beat:2+i,x:i%2===0?3:0,y:0,color:'blue',direction:'none',count:1,type:'tap',lengthMs:0}));
    await p.locator('#libraryButton').click();await p.locator('#chartFile').setInputFiles({name:'chart_hard.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify({bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,displayLevel:5,timeSignatures:[],notes}))});await wait();
    await p.locator('#nav-preview').click();await wait();await p.locator('#reportIssues button').first().click();await wait();
    await p.locator('#deleteNote').click();await wait();assert.match(await p.locator('#selectedNoteLabel').innerText(),/^候補 2\/3/);
    await p.locator('#editUndo').click();await wait();await p.locator('#previousNote').click();await wait();assert.match(await p.locator('#selectedNoteLabel').innerText(),/^候補 1\/3/);
    await p.locator('#editRedo').click();await wait();assert.match(await p.locator('#selectedNoteLabel').innerText(),/^候補 2\/3/,'やり直しで消えたら次の候補へ');assert.equal(await p.locator('#nextNote').innerText(),'次の候補');
    check('review mode continues when redo removes the selected candidate');
    assert.deepEqual(errs,[]);check('no browser runtime errors in round 10');
  }finally{await ctx.close();}
}

// 2026-10-22 9回目: 確かめるの候補から直すへ「点検モード」(前後の候補を順に回る)・候補への近道・最終更新は中身が変わったときだけ。
async function round9(){
  const ctx=await browser.newContext({viewport:{width:390,height:664},hasTouch:true}),p=await ctx.newPage(),errs=[];p.on('pageerror',e=>errs.push(e.message));
  const wait=()=>p.waitForTimeout(300),saved=()=>p.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
  try{
    await p.goto(site);await wait();
    await p.locator('#audioFile').setInputFiles({name:'点検.wav',mimeType:'audio/wav',buffer:wav()});await saved();
    // 1. 候補が3件(パッドの外)ある譜面。確かめるの統計の下に「見直す候補 3 件 ↓」、押すと一覧へ。
    const notes=Array.from({length:8},(_,i)=>({time:1000+i*500,beat:2+i,x:i%3===0?3:0,y:0,color:'blue',direction:'none',count:1,type:'tap',lengthMs:0}));
    await p.locator('#libraryButton').click();await p.locator('#chartFile').setInputFiles({name:'chart_hard.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify({bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,displayLevel:5,timeSignatures:[],notes}))});await wait();
    await p.locator('#nav-preview').click();await wait();assert.equal(await p.locator('#reportJump').innerText(),'見直す候補 3 件 ↓');
    await p.locator('#reportJump').click();await wait();const head=await p.locator('#reportIssueCount').boundingBox();assert.ok(head.y>=0&&head.y+head.height<=600,'一覧の見出しが画面の中へ');check('the preview shows a shortcut to the review candidates');
    // 2. 候補を押すと点検モード: 見出しは「候補 1/3」、ボタンは「前の候補/次の候補」、理由は知らせに出る。
    await p.locator('#reportIssues button').first().click();await wait();
    assert.match(await p.locator('#selectedNoteLabel').innerText(),/^候補 1\/3 · 0:01\.000$/);assert.ok((await p.locator('#selectedNoteLabel').boundingBox()).height<30,'1行に収まる');assert.equal(await p.locator('#nextNote').innerText(),'次の候補');assert.equal(await p.locator('#previousNote').isDisabled(),true);
    assert.match(await p.locator('#toast').innerText(),/候補 1\/3 · 診断したときの理由：入力パッドの外/);await p.screenshot({path:path.join(output,'round9-review.png')});
    await p.locator('#nextNote').click();await wait();assert.match(await p.locator('#selectedNoteLabel').innerText(),/^候補 2\/3 · 0:02\.500/);
    // 3. 点検モードで消すと、次の候補へ。最後の候補では「次の候補」を押せない。
    await p.locator('#deleteNote').click();await wait();assert.match(await p.locator('#selectedNoteLabel').innerText(),/^候補 3\/3 · 0:04\.000/);assert.equal(await p.locator('#nextNote').isDisabled(),true);
    // 4. パネルを閉じると点検モードを抜け、ふだんの「前へ/次へ」に戻る。
    await p.locator('#closeNote').click();await wait();if(!await p.locator('.map-note-details').evaluate(e=>e.open))await p.locator('.map-note-details summary').click();
    await p.locator('#mapNotes button').first().click();await wait();assert.equal(await p.locator('#nextNote').innerText(),'次へ');assert.match(await p.locator('#selectedNoteLabel').innerText(),/^\d+ \/ 7 · /);
    check('review mode walks the candidates, survives a delete and ends when the panel closes');
    // 5. 一覧を開くだけ・再生位置を動かすだけでは「最終更新」は変わらない。中身を変えたら進む。
    await saved();const u1=(await storedIn(p)).updated;
    await p.locator('#closeNote').click();await p.locator('#libraryButton').click();await p.locator('#library').waitFor({state:'visible'});await p.locator('[data-close="library"]').click();await p.waitForTimeout(500);await saved();
    assert.equal((await storedIn(p)).updated,u1,'見ただけでは変わらない');
    await p.locator('#mapNotes button').first().click();await wait();await p.locator('#laterNote').click();await p.waitForTimeout(500);await saved();
    assert.ok((await storedIn(p)).updated>u1,'中身を変えたら進む');check('the last-updated time moves only when the content changes');
    // 6. 候補が200件を超えても「続きの N 件を表示」で全部たどれる(EASY に矢印210個 → 210件)。
    const many=Array.from({length:210},(_,i)=>({time:500+i*130,beat:1+i*.26,x:0,y:0,color:i%2?'red':'blue',direction:'up',count:1,type:'tap',lengthMs:0}));
    await p.locator('#closeNote').click();await p.locator('#libraryButton').click();await p.locator('#chartFile').setInputFiles({name:'chart_easy.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify({bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,displayLevel:2,timeSignatures:[],notes:many}))});await wait();
    await p.locator('#nav-preview').click();await wait();const total=Number((await p.locator('#reportJump').innerText()).match(/(\d+) 件/)[1]);assert.ok(total>200,`${total}件`);
    assert.equal(await p.locator('#reportIssues > button').count(),201,'200件+続きのボタン');
    for(let i=0;i<10&&/^続きの/.test(await p.locator('#reportIssues > button').last().innerText());i++){await p.locator('#reportIssues > button').last().click();await wait();}
    assert.equal(await p.locator('#reportIssues > button').count(),total,'続きを出し切ると全部');check('more than 200 candidates can all be reached');
    assert.deepEqual(errs,[]);check('no browser runtime errors in round 9');
  }finally{await ctx.close();}
}

// 2026-10-22 8回目: ノーツが多い譜面で重くしない(隠れている旧ノーツ一覧を、操作のたびに作り直さない)。
async function round8(){
  const ctx=await browser.newContext({viewport:{width:390,height:664},hasTouch:true}),p=await ctx.newPage(),errs=[];p.on('pageerror',e=>errs.push(e.message));
  const wait=()=>p.waitForTimeout(300);
  try{
    await p.goto(site);await wait();
    await p.locator('#audioFile').setInputFiles({name:'多い.wav',mimeType:'audio/wav',buffer:wav()});
    await p.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
    const notes=Array.from({length:300},(_,i)=>({time:500+i*90,beat:1+i*.18,x:(i%5)-2,y:0,color:i%2?'red':'blue',direction:'none',count:1,type:'tap',lengthMs:0}));
    await p.locator('#libraryButton').click();await p.locator('#chartFile').setInputFiles({name:'chart_hard.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify({bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,displayLevel:8,timeSignatures:[],notes}))});await wait();
    await p.locator('#nav-edit').click();await wait();if(!await p.locator('.map-note-details').evaluate(e=>e.open))await p.locator('.map-note-details summary').click();
    await p.locator('#mapNotes button').first().click();await wait();for(let i=0;i<3;i++){await p.locator('#nextNote').click();await p.waitForTimeout(80);}await p.locator('#laterNote').click();await wait();
    assert.equal(await p.locator('#noteList').evaluate(e=>e.children.length),0,'隠れた一覧は空のまま');
    assert.match(await p.locator('#selectedNoteLabel').innerText(),/^4 \/ 300 · /);assert.equal(await p.locator('#noteEditor').isHidden(),false,'選んだノーツの欄は出る');
    check('a hidden legacy list is not rebuilt on every edit');
    // 2. 一覧の行は「番号・時刻・色・矢印・回数と長さ・位置」。選んでいる行に印。
    const row=await p.locator('#mapNotes button.selected').innerText();assert.match(row,/^#4 0:00\.780 赤 \(1\.0, 0\.0\)$/);check('note list rows carry number, colour, arrow, count and position');
    // 3. 表示幅を変えても、見ていた区間の真ん中を保つ。
    await p.locator('#mapZoom').selectOption('5');await wait();for(let i=0;i<2;i++){await p.locator('#mapNext').click();await wait();}
    const shown=async()=>(await p.locator('#mapRange').innerText()).match(/(\d+):(\d+\.\d+)/g).map(t=>{const[m,s]=t.split(':');return Number(m)*60+Number(s);});
    const before=await shown();await p.locator('#mapZoom').selectOption('2');await wait();const after=await shown();
    assert.ok(Math.abs((before[0]+before[1])/2-(after[0]+after[1])/2)<.01,`真ん中 ${before} → ${after}`);check('changing the zoom keeps the centre of the shown range');
    // 4. 「＋ 置く」は録音用の入力補正・速度を混ぜず、今の位置にそのまま置く。
    await p.evaluate(()=>{for(const[id,v]of[['latency','100'],['speed','0.5']]){const e=document.getElementById(id);e.value=v;e.dispatchEvent(new Event('change',{bubbles:true}));}const s=document.querySelector('#seek');s.value='10';s.dispatchEvent(new Event('input',{bubbles:true}));});await wait();
    await p.locator('#placeNote').click();await wait();
    assert.ok((await storedIn(p)).chart.notes.some(n=>Math.abs(n.time-10000)<1),'10.000秒に置く');check('manual placement ignores the recording latency and speed');
    // 5. 一覧を開いたまま曲名を変えると、一覧と検索もその場で新しい名前になる。
    await p.locator('#libraryButton').click();await p.locator('#library').waitFor({state:'visible'});
    await p.locator('#projectName').fill('改名した曲');await p.locator('#projectName').press('Tab');await wait();
    assert.match(await p.locator('#projectList').innerText(),/改名した曲/);await p.locator('#projectSearch').fill('改名');await wait();
    assert.equal(await p.locator('#projectList .project:visible').count(),1);await p.locator('#projectSearch').fill('');check('renaming refreshes the open list and search');
    // 6. 削除の確認に、ノーツ数・更新日時・編集中と、音源も消えるかを書く(ここではキャンセル)。
    let message='';p.once('dialog',async d=>{message=d.message();await d.dismiss();});
    await p.locator('.project .danger').first().click();await wait();
    assert.match(message,/301 notes・最終更新 .*・編集中/);assert.match(message,/ほかの下書き 1 件が同じ音源を使うので、音源は残ります。/);
    assert.equal(await p.locator('.project').count(),2,'キャンセルしたら消さない');
    p.once('dialog',d=>d.accept());await p.locator('.project').filter({hasText:'NORMAL'}).locator('.danger').click();await wait();assert.equal(await p.locator('.project').count(),1);
    p.once('dialog',async d=>{message=d.message();await d.dismiss();});await p.locator('.project .danger').first().click();await wait();
    assert.match(message,/この曲の最後の下書きなので、音源もこの端末から削除されます。/);await p.locator('[data-close="library"]').click();check('delete confirmation names the draft and says whether the audio goes too');
    assert.deepEqual(errs,[]);check('no browser runtime errors in round 8');
  }finally{await ctx.close();}
}

// 2026-10-22 7回目: 選んだノーツのパネルは、スクロールなしで「削除・前後・10ms・聴く・矢印3×3・色・拍にそろえる・次へ」まで見える。色は1回で変わる。設定のBPMを測る道具はBPMのすぐ下。
async function round7(){
  const ctx=await browser.newContext({viewport:{width:390,height:664},hasTouch:true}),p=await ctx.newPage(),errs=[];p.on('pageerror',e=>errs.push(e.message));
  const wait=()=>p.waitForTimeout(300);
  try{
    await p.goto(site);await wait();
    await p.locator('#audioFile').setInputFiles({name:'パネル.wav',mimeType:'audio/wav',buffer:wav()});
    await p.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
    // 1. 設定: 曲を流す・タップで測る・BPMに適用は、BPMの欄のすぐ下(拍の始まりより上)で、開いたときの画面に入る。
    await p.locator('#settingsButton').click();await p.locator('#settingsDialog').waitFor({state:'visible'});
    const bpm=await p.locator('#bpm').boundingBox(),tap=await p.locator('#tapTempo').boundingBox(),apply=await p.locator('#applyTempo').boundingBox(),zero=await p.locator('#beatZero').boundingBox();
    assert.ok(bpm.y<tap.y&&tap.y<zero.y&&apply.y<zero.y,'BPM → 測る → 拍の始まり の順');assert.ok(apply.y+apply.height<=664,'開いたときの画面に入る');
    await p.locator('#settingsDialogClose').click();check('tempo tools sit right under the BPM field');
    // 2. 直す: パネルを開いた直後(スクロールなし)に、よく使う操作がすべてパネルの中に見える。
    const notes=Array.from({length:6},(_,i)=>({time:1000+i*500,beat:2+i,x:0,y:0,color:'blue',direction:'none',count:1,type:'tap',lengthMs:0}));
    await p.locator('#libraryButton').click();await p.locator('#chartFile').setInputFiles({name:'chart_normal.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify({bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,displayLevel:2,timeSignatures:[],notes}))});await wait();
    await p.locator('#nav-edit').click();await wait();if(!await p.locator('.map-note-details').evaluate(e=>e.open))await p.locator('.map-note-details summary').click();
    await p.locator('#mapNotes button').first().click();await wait();
    const inside=await p.evaluate(()=>{const panel=document.getElementById('noteDialog'),r=panel.getBoundingClientRect(),ids=['deleteNote','closeNote','previousNote','nextNote','earlierNote','laterNote','listenNote','snapNote','autoNext','noteColor-blue','noteColor-red','noteColor-gold',...[...document.querySelectorAll('#directionGrid button')].map(b=>b.id)];
      return{scrollTop:panel.scrollTop,outside:ids.filter(id=>{const e=document.getElementById(id),b=(id==='autoNext'?e.closest('label'):e).getBoundingClientRect();return b.top<r.top||b.bottom>r.bottom||b.left<r.left||b.right>r.right;})};});
    assert.equal(inside.scrollTop,0);assert.deepEqual(inside.outside,[],'パネルの外に出ている操作');await p.screenshot({path:path.join(output,'round7-note-panel.png')});check('the note panel shows every frequent control without scrolling');
    // 3. 色ボタンは1回で変わり、押された状態になる。元に戻すは1回で前の色へ。
    await p.locator('#noteColor-gold').click();await wait();
    assert.equal((await storedIn(p)).chart.notes[0].color,'gold');assert.equal(await p.locator('#noteColor-gold').getAttribute('aria-pressed'),'true');assert.equal(await p.locator('#noteColor-blue').getAttribute('aria-pressed'),'false');
    assert.match(await p.locator('#selectedNoteLabel').innerText(),/金/);
    await p.locator('#editUndo').click();await wait();assert.equal((await storedIn(p)).chart.notes[0].color,'blue');assert.equal(await p.locator('#noteColor-blue').getAttribute('aria-pressed'),'true');
    check('colour buttons change the note in one tap and undo in one step');
    // 4. 時刻を打ちかけのまま(確定せずに)矢印を押しても、打った時刻は消えない(ボタンがフォーカスを取らない端末と同じく click だけを送る)。
    await p.locator('#noteTime').fill('1.234');await p.evaluate(()=>document.getElementById('direction-up').click());await wait();
    const first=(await storedIn(p)).chart.notes[0];assert.equal(first.time,1234);assert.equal(first.direction,'up');check('an unconfirmed time survives an arrow tap');
    // 5. 渡す: ① 譜面 → ② 同じ音源 の順。WAV は「元の音源を保存」、ゲームが読めない形式(M4A)は「音源をWAV保存」が主ボタン。
    await p.locator('#nav-export').click();await wait();
    const heads=await p.locator('.export-panel > h2').allInnerTexts();assert.deepEqual(heads,['① 譜面','② 同じ音源']);
    const order=await p.evaluate(()=>['exportButton','exportOriginalAudio','exportAudioButton','exportNames'].map(id=>document.getElementById(id).getBoundingClientRect().top));assert.ok(order[0]<order[1]&&order[1]<=order[2]&&order[2]<order[3],'譜面 → 音源 → ファイル名');
    assert.equal(await p.locator('#exportOriginalAudio').evaluate(e=>e.classList.contains('primary')),true);assert.equal(await p.locator('#exportAudioButton').evaluate(e=>e.classList.contains('primary')),false);
    await p.locator('#libraryButton').click();await p.locator('#libraryAudioFile').setInputFiles({name:'変換.m4a',mimeType:'audio/mp4',buffer:wav()});await p.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');await wait();
    await p.locator('#nav-export').click();await wait();
    assert.equal(await p.locator('#exportAudioButton').evaluate(e=>e.classList.contains('primary')),true);assert.equal(await p.locator('#exportOriginalAudio').evaluate(e=>e.classList.contains('primary')),false);
    assert.match(await p.locator('#exportNames').innerText(),/変換\.m4a はゲームで読めない形式です/);await p.screenshot({path:path.join(output,'round7-export.png'),fullPage:true});check('export reads chart first, then the matching audio button');
    assert.deepEqual(errs,[]);check('no browser runtime errors in round 7');
  }finally{await ctx.close();}
}

// 2026-10-22 Codex の第2回の点検で見つかった食い違い(BPMを後で決める・BPMを測る速さ・3拍子のループ・まとめて直したあとの選択)。
async function codexRound6(){
  const ctx=await browser.newContext({viewport:{width:390,height:664},hasTouch:true}),p=await ctx.newPage(),errs=[];p.on('pageerror',e=>errs.push(e.message));
  const wait=()=>p.waitForTimeout(300),seekTo=seconds=>p.evaluate(s=>{const e=document.querySelector('#seek');e.value=String(s);e.dispatchEvent(new Event('input',{bubbles:true}));},seconds);
  try{
    await p.goto(site);await wait();
    await p.locator('#audioFile').setInputFiles({name:'後で決める.wav',mimeType:'audio/wav',buffer:wav()});
    await p.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
    // 1. BPM未設定で録ってから BPM を決めても、テンポが変わる譜面と取り違えない(小節で移動できる)。
    await seekTo(10);await wait();await p.locator('#playButton').click();await p.waitForTimeout(200);
    for(let i=0;i<3;i++){await p.locator('#pad').tap({position:{x:60+i*40,y:100}});await p.waitForTimeout(300);}
    await p.locator('#playButton').click();await wait();assert.equal((await storedIn(p)).chart.notes.length,3);
    await p.locator('#settingsButton').click();await p.locator('#bpm').fill('150');await p.locator('#bpm').press('Tab');await wait();
    // 2. 遅く聴く設定でも、BPMを測るための再生は1.0倍。
    await p.evaluate(()=>{const s=document.querySelector('#speed');s.value='0.5';s.dispatchEvent(new Event('change',{bubbles:true}));});
    await p.locator('#tempoListen').click();await p.waitForTimeout(300);const t0=Number(await p.locator('#seek').inputValue());await p.waitForTimeout(1000);const t1=Number(await p.locator('#seek').inputValue());
    assert.ok(t1-t0>.8,`1秒で${(t1-t0).toFixed(2)}秒進む(0.5倍なら0.5秒)`);await p.locator('#tempoListen').click();await p.locator('#settingsDialogClose').click();await wait();
    assert.equal(await p.locator('#backButton').innerText(),'−1小節','BPMを決めたら小節で移動');
    assert.deepEqual((await storedIn(p)).chart.notes.map(n=>Math.abs(n.beat-(n.time-0)*150/60000)<1e-3),[true,true,true],'beat は新しいBPMで数え直す');
    check('setting a BPM after recording keeps bar navigation; tap-tempo playback runs at 1.0x');
    // 3. 3拍子の曲で「今の位置からくり返し聴く」は2小節=6拍(120 BPMで3秒)。
    const notes=Array.from({length:6},(_,i)=>({time:1000+i*500,beat:2+i,x:0,y:0,color:i%2?'red':'blue',direction:'none',count:1,type:'tap',lengthMs:0}));
    await p.locator('#libraryButton').click();await p.locator('#chartFile').setInputFiles({name:'chart_normal.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify({bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,displayLevel:2,timeSignatures:[{beat:0,numerator:3,denominator:4}],notes}))});await wait();
    await p.locator('#nav-preview').click();await seekTo(0);await wait();await p.locator('#previewRangeButton').click();await p.locator('#loopHere').click();await wait();
    assert.equal(await p.locator('#rangeEnd').inputValue(),'3.000','3拍子の2小節');await p.locator('#previewRangeButton').click();await p.locator('#clearRange').click();await wait();check('the preview loop is two bars of the current meter');
    // 4. まとめて直したあとも、選んだノーツが残っていれば選んだまま(値は新しく)。消えたらパネルを閉じる。
    await p.locator('#nav-edit').click();await wait();if(!await p.locator('.map-note-details').evaluate(e=>e.open))await p.locator('.map-note-details summary').click();
    await p.locator('#mapNotes button').first().click();await wait();assert.match(await p.locator('#selectedNoteLabel').innerText(),/0:01\.000/);
    const refine=async(kind,fields)=>{await p.locator('#mapRefine').click();await p.locator('#refineDialog').waitFor({state:'visible'});await p.locator('#refineScope').selectOption('all');await p.locator('#refineKind').selectOption(kind);for(const[id,value]of Object.entries(fields))await p.locator('#'+id).fill(String(value));await p.locator('#refineCheck').click();await wait();await p.locator('#refineApply').click();await wait();};
    await refine('shift',{refineShift:100});
    assert.equal(await p.locator('#noteDialog').evaluate(e=>e.open),true,'選んだまま');assert.match(await p.locator('#selectedNoteLabel').innerText(),/0:01\.100/);
    await refine('delete',{});assert.equal(await p.locator('#noteDialog').evaluate(e=>e.open),false,'消えたら閉じる');check('bulk edits keep a surviving selection and close the panel otherwise');
    assert.deepEqual(errs,[]);check('no browser runtime errors in the Codex round-6 fixes');
  }finally{await ctx.close();}
}

// 2026-10-22 6回目: 「＋難易度」は作るまで今の下書きを切り替えず、何を元にするかを見せる。
async function round6(){
  const ctx=await browser.newContext({viewport:{width:390,height:664},hasTouch:true}),p=await ctx.newPage(),errs=[];p.on('pageerror',e=>errs.push(e.message));
  const wait=()=>p.waitForTimeout(300),saved=()=>p.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
  try{
    await p.goto(site);await wait();
    await p.locator('#audioFile').setInputFiles({name:'六回目A.wav',mimeType:'audio/wav',buffer:wav()});await saved();
    await p.locator('#settingsButton').click();await p.locator('#bpm').fill('150');await p.locator('#bpm').press('Tab');await wait();await p.locator('#settingsDialogClose').click();await wait();
    // 0. まだノーツがないとき、渡すは「0個」を先に知らせる(注意は1件ずつ改行)。
    await p.locator('#nav-export').click();await wait();assert.match(await p.locator('#exportWarnings').innerText(),/^ノーツが0個です。/);
    await p.locator('#nav-record').click();await wait();check('export warns about an empty chart first');
    await p.locator('#libraryButton').click();await p.locator('#libraryAudioFile').setInputFiles({name:'六回目B.wav',mimeType:'audio/wav',buffer:wav()});await saved();await wait();
    await p.locator('#playButton').click();await p.waitForTimeout(200);await p.locator('#pad').tap({position:{x:60,y:100}});await p.waitForTimeout(200);await p.locator('#playButton').click();await wait();
    assert.equal(await p.locator('#undoButton').isDisabled(),false);
    // 1. 開いていない曲Aの「＋EASY」: 元にする下書き(A・NORMAL・BPM 150)を見せる。閉じれば曲Bのまま、元に戻すも残る。
    await p.locator('#libraryButton').click();await p.locator('#library').waitFor({state:'visible'});
    const groupA=p.locator('.project-group').filter({hasText:'六回目A'});await groupA.locator(':scope>button').filter({hasText:'EASY'}).click();
    await p.locator('#duplicateDialog').waitFor({state:'visible'});
    assert.match(await p.locator('#copySource').innerText(),/元にする下書き：六回目A · NORMAL · 0 notes · BPM 150 · OFFSET 0ms。/);
    assert.equal(await p.locator('#copyDifficulty').inputValue(),'easy');assert.equal(await p.locator('#copyContent').inputValue(),'empty');
    await p.locator('[data-close="duplicateDialog"]').click();await wait();
    assert.equal((await storedIn(p)).name,'六回目B','閉じたら今の下書きのまま');assert.equal(await p.locator('#undoButton').isDisabled(),false,'元に戻すも残る');
    // 2. もう一度押して作ると、曲Aの EASY(空・BPM 150)が開く。
    await p.locator('#libraryButton').click();await p.locator('#library').waitFor({state:'visible'});
    await p.locator('.project-group').filter({hasText:'六回目A'}).locator(':scope>button').filter({hasText:'EASY'}).click();await p.locator('#createCopy').dblclick();await wait();await saved();
    const all=await p.evaluate(()=>new Promise((resolve,reject)=>{const r=indexedDB.open('saber-tap-studio-v1',1);r.onerror=()=>reject(r.error);r.onsuccess=()=>{const d=r.result,q=d.transaction('projects','readonly').objectStore('projects').getAll();q.onsuccess=()=>{resolve(q.result.map(x=>`${x.name}/${x.difficulty}`));d.close();};};}));
    assert.equal(all.filter(x=>x==='六回目A/easy').length,1,'二度押しでも1つだけ');
    const made=await storedIn(p);assert.equal(made.name,'六回目A');assert.equal(made.difficulty,'easy');assert.equal(made.chart.notes.length,0);assert.equal(made.chart.bpm,150);assert.equal(made.settings.bpmConfirmed,true);
    assert.match(await p.locator('#difficultyLabel').innerText(),/EASY \/ 150 BPM/);check('adding a difficulty shows its source and keeps the open draft until confirmed');
    // 3. 開いている曲の「＋難易度」と「別の難易度・コピー」は、いま開いている下書きを元にすると書く。
    await p.locator('#libraryButton').click();await p.locator('#library').waitFor({state:'visible'});await p.locator('#duplicateButton').click();
    assert.match(await p.locator('#copySource').innerText(),/六回目A · EASY · 0 notes · BPM 150 · OFFSET 0ms（いま開いている下書き）/);await p.locator('[data-close="duplicateDialog"]').click();check('copying names the open draft as its source');
    assert.deepEqual(errs,[]);check('no browser runtime errors in round 6');
  }finally{await ctx.close();}
}

// 2026-10-22 5回目: 色を固定したパッドの表示・時刻の行でも位置を変える・譜面を開くの行き先・横向きの入力結果・押せる高さ。
async function round5(){
  const ctx=await browser.newContext({viewport:{width:390,height:664},hasTouch:true}),p=await ctx.newPage(),errs=[];p.on('pageerror',e=>errs.push(e.message));
  const wait=()=>p.waitForTimeout(300),seekValue=async()=>Number(await p.locator('#seek').inputValue());
  const record=async position=>{await p.locator('#playButton').click();await p.waitForTimeout(200);await p.locator('#pad').tap({position});await p.waitForTimeout(200);await p.locator('#playButton').click();await wait();};
  try{
    await p.goto(site);await wait();
    await p.locator('#audioFile').setInputFiles({name:'五回目.wav',mimeType:'audio/wav',buffer:wav()});
    await p.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
    // 1. 金に固定すると、パッドの地と見出しも金だけ(右の RED は消える)。右側を押しても金になる。左右で色分けに戻すと元どおり。
    await p.locator('[data-color="gold"]').click();
    assert.equal(await p.locator('#pad').getAttribute('data-ink'),'gold');assert.match(await p.locator('.pad-hand.left').innerText(),/GOLD/);
    assert.equal(await p.locator('.pad-hand.right').isVisible(),false);assert.match(await p.locator('#pad').getAttribute('aria-label'),/どこを押しても金/);
    const pad=await p.locator('#pad').boundingBox();await record({x:pad.width-40,y:pad.height/2});
    assert.deepEqual((await storedIn(p)).chart.notes.map(n=>n.color),['gold'],'右側を押しても金');
    await p.locator('[data-color="auto"]').click();assert.equal(await p.locator('.pad-hand.right').isVisible(),true);assert.match(await p.locator('.pad-hand.left').innerText(),/BLUE/);
    assert.match(await p.locator('#pad').getAttribute('aria-label'),/左は青、右は赤/);check('a fixed colour repaints the pad labels and saves that colour');
    // 2. 録る画面は、細い波形の上の時刻の行を押しても位置が変わる。確かめる画面の時刻の行は、スクロールのために押しても動かさない。
    const row=await p.locator('.timeline-wrap .time-line').boundingBox();await p.mouse.click(row.x+row.width/2,row.y+row.height/2);await wait();
    assert.ok(Math.abs(await seekValue()-15)<.6,`時刻の行の真ん中で約15秒 (${await seekValue()})`);
    await p.locator('#nav-preview').click();await wait();const before=await seekValue(),previewRow=await p.locator('.timeline-wrap .time-line').boundingBox();
    await p.mouse.click(previewRow.x+previewRow.width*.2,previewRow.y+previewRow.height/2);await wait();assert.equal(await seekValue(),before,'確かめる画面の時刻の行では動かさない');
    await p.locator('#nav-record').click();await wait();check('the record tab seeks from the whole time row');
    // 3. 「譜面を開く」に入る先の曲名を書き、開いたら「どの曲のどの難易度の新しい下書きか」を知らせる。
    await p.locator('#libraryButton').click();await p.locator('#library').waitFor({state:'visible'});
    assert.equal(await p.locator('#importTarget').innerText(),'→「五回目」へ');
    const notes=Array.from({length:4},(_,i)=>({time:1000+i*500,beat:2+i,x:0,y:0,color:'blue',direction:'none',count:1,type:'tap',lengthMs:0}));
    await p.locator('#chartFile').setInputFiles({name:'chart_hard.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify({bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,displayLevel:2,timeSignatures:[],notes}))});await wait();
    assert.match(await p.locator('#toast').innerText(),/chart_hard\.json を「五回目」の HARD の新しい下書きとして開きました/);
    assert.equal(await p.locator('#library').isVisible(),false,'読み込んだら一覧を閉じる');check('JSON import names the song it joins and closes the stale list');
    // 4. 横向きでも、入力したあとに結果がパッドの下端に出る(入力前は出さない)。
    await p.setViewportSize({width:844,height:390});await wait();
    assert.equal(await p.locator('#inputFeedback').isVisible(),false,'入力前は出さない');
    const wide=await p.locator('#pad').boundingBox();await record({x:60,y:wide.height/2});
    assert.equal(await p.locator('#inputFeedback').isVisible(),true);assert.match(await p.locator('#inputFeedback').innerText(),/タップ/);
    const shown=await p.locator('#inputFeedback').boundingBox(),padNow=await p.locator('#pad').boundingBox();
    assert.ok(shown.y>=padNow.y&&shown.y+shown.height<=padNow.y+padNow.height&&shown.x>=padNow.x&&shown.x+shown.width<=padNow.x+padNow.width,'パッドの中の下端');
    assert.equal(await p.evaluate(()=>document.documentElement.scrollWidth),844);await p.screenshot({path:path.join(output,'round5-landscape.png')});check('landscape shows the input result over the pad');
    await p.setViewportSize({width:390,height:664});await wait();
    // 5. チェック項目と開閉見出しは44px以上。区間の画面では閉じても意味のない見出しを出さない。
    await p.locator('#settingsButton').click();await p.locator('#settingsDialog').waitFor({state:'visible'});
    for(const s of await p.locator('#settingsDialog summary').all())if(!await s.evaluate(e=>e.parentElement.open))await s.click();
    for(const label of await p.locator('#settingsDialog label.check').all())if(await label.isVisible())assert.ok((await label.boundingBox()).height>=44,'設定のチェック項目');
    await p.locator('#settingsDialogClose').click();await p.locator('#rangeButton').click();
    assert.equal(await p.locator('#rangeDialog .range-tools>summary').isVisible(),false);assert.equal(await p.locator('#rangeStart').isVisible(),true);
    for(const label of await p.locator('#rangeDialog label.check').all())if(await label.isVisible())assert.ok((await label.boundingBox()).height>=44,'区間のチェック項目');
    await p.locator('#rangeDialogClose').click();await p.locator('#nav-export').click();await wait();
    assert.ok((await p.locator('#tab-export .advanced>summary').boundingBox()).height>=44,'バックアップの見出し');check('checkboxes and disclosure headers are 44px targets');
    assert.deepEqual(errs,[]);check('no browser runtime errors in round 5');
  }finally{await ctx.close();}
}

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
    await p.locator('#libraryButton').click();await p.locator('#chartFile').setInputFiles({name:'chart_normal.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify({bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,displayLevel:2,timeSignatures:[],notes}))});await wait();
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
