// 実画面から再生し、Web Audioへ渡した予約時刻を記録する。独立コンテキスト・合成音源のみ。
// SITE_URL / PLAYWRIGHT_MODULE / BROWSER_CHANNEL / QA_OUTPUT は他のブラウザ試験と同じ。
import {createRequire} from 'node:module';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
const require=createRequire(import.meta.url),{chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const output=process.env.QA_OUTPUT||'qa-output';fs.mkdirSync(output,{recursive:true});
const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL||'msedge',headless:true});
const context=await browser.newContext({viewport:{width:390,height:844},hasTouch:true}),page=await context.newPage(),errors=[];
page.on('pageerror',e=>errors.push(e.message));
await context.addInitScript(()=>{
  window.__audioQa={sources:[],clicks:[]};
  const Audio=window.AudioContext||window.webkitAudioContext;
  Audio.prototype.getOutputTimestamp=function(){return{contextTime:Math.max(0,this.currentTime-.2),performanceTime:performance.now()};};
  const source=Audio.prototype.createBufferSource,oscillator=Audio.prototype.createOscillator;
  Audio.prototype.createBufferSource=function(){
    const n=source.call(this),ctx=this,r={when:null,offset:null,rate:null,stops:[]},start=n.start.bind(n),stop=n.stop.bind(n);
    window.__audioQa.sources.push(r);
    n.start=(when,offset,...args)=>{r.when=when;r.offset=offset;r.rate=n.playbackRate.value;return start(when,offset,...args);};
    n.stop=(when)=>{r.stops.push({when:when??null,now:ctx.currentTime});return when===undefined?stop():stop(when);};
    return n;
  };
  Audio.prototype.createOscillator=function(){
    const n=oscillator.call(this),ctx=this,r={when:null,frequency:null,stops:[]},start=n.start.bind(n),stop=n.stop.bind(n),set=n.frequency.setValueAtTime.bind(n.frequency);
    window.__audioQa.clicks.push(r);
    n.frequency.setValueAtTime=(value,when)=>{if(r.frequency===null)r.frequency=value;return set(value,when);};
    n.start=(when)=>{r.when=when??ctx.currentTime;return when===undefined?start():start(when);};
    n.stop=(when)=>{r.stops.push({when:when??null,now:ctx.currentTime});return when===undefined?stop():stop(when);};
    return n;
  };
});
function wav(){const n=8000*8,b=Buffer.alloc(44+n*2);b.write('RIFF');b.writeUInt32LE(b.length-8,4);b.write('WAVEfmt ',8);b.writeUInt32LE(16,16);b.writeUInt16LE(1,20);b.writeUInt16LE(1,22);b.writeUInt32LE(8000,24);b.writeUInt32LE(16000,28);b.writeUInt16LE(2,32);b.writeUInt16LE(16,34);b.write('data',36);b.writeUInt32LE(n*2,40);return b;}
const chart={bpm:120,offsetMs:0,beatZeroMs:0,coordScale:1,timeSignatures:[],notes:[1000,1250,1500,2000,2050].map((time,i)=>({time,beat:time/500,x:0,y:0,color:i%2?'red':'blue',type:'tap',direction:'none',count:1,lengthMs:0}))};
const data=()=>page.evaluate(()=>window.__audioQa),check=name=>console.log('PASS '+name);
const log=[];
async function finished(){await page.waitForFunction(()=>document.querySelector('#transportStatus').textContent!=='一時停止');await page.waitForFunction(()=>document.querySelector('#transportStatus').textContent==='一時停止',{},{timeout:10000});}
async function settings(rate=1,on=true){
  await page.locator('#settingsButton').click();
  const listening=page.locator('.settings-group').filter({has:page.locator('#speed')});
  if(!await listening.evaluate(e=>e.open))await listening.locator('summary').click();
  await page.locator('#speed').selectOption(String(rate));
  await page.locator('#clickSound').setChecked(on);await page.locator('#metronome').setChecked(false);await page.locator('#countIn').setChecked(false);
  await page.locator('#settingsDialogClose').click();
}
async function range(a,b){
  await page.locator('#previewRangeButton').click();await page.locator('#rangeStart').fill(String(a));await page.locator('#rangeEnd').fill(String(b));
  await page.locator('#rangeEnabled').check();await page.locator('#previewLoop').uncheck();await page.locator('#rangeDialogClose').click();
}
async function preview(a,b,rate=1){
  await settings(rate);await range(a,b);const previous=await data();await page.locator('#playButton').click();
  await finished();
  const observed=await data(),source=observed.sources[previous.sources.length],clicks=observed.clicks.slice(previous.clicks.length);
  const notes=chart.notes.filter(n=>n.time/1000>=a&&n.time/1000<b);
  log.push({kind:'preview',a,b,rate,source,clicks});
  assert.equal(clicks.length,notes.length,'開始を含み終端を除くノーツだけ、重複せず鳴らす');
  for(let i=0;i<notes.length;i++)assert.ok(Math.abs(clicks[i].when-(source.when+(notes[i].time/1000-a)/rate))<.00001,'描画と200msの出力遅延を加えず曲と同じ時計に予約');
  assert.ok(Math.abs(source.stops.find(x=>x.when!==null).when-(source.when+(b-a)/rate))<.00001,'曲の終端も同じ時計で予約');
}
try{
  await page.goto(process.env.SITE_URL||'http://127.0.0.1:8769/');
  await page.locator('#audioFile').setInputFiles({name:'audio-clock.wav',mimeType:'audio/wav',buffer:wav()});
  await page.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
  await page.locator('#libraryButton').click();await page.locator('#chartFile').setInputFiles({name:'chart_normal.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(chart))});
  await page.locator('#library').waitFor({state:'hidden'});await page.locator('#nav-preview').click();
  await preview(.8,2);check('preview clicks share the song clock despite 200ms output latency');
  await preview(.8,2,.5);check('half-speed preview schedules in source-song seconds');
  await preview(1,2);check('the range start note sounds once and the endpoint is excluded');
  // 個別ノーツの短い試聴も、呼び出した後で画面だけを止めず、曲とタップ音の終端を先にそろえる。
  await settings(1);await page.locator('#nav-edit').click();
  if(!await page.locator('.map-note-details').evaluate(e=>e.open))await page.locator('.map-note-details summary').click();
  await page.locator('#mapNotes button').first().click();let before=await data();await page.locator('#listenNote').click();
  await finished();
  let observed=await data(),source=observed.sources[before.sources.length],clicks=observed.clicks.slice(before.clicks.length);
  log.push({kind:'short-note',source,clicks});
  assert.equal(source.offset,.5);assert.equal(clicks.length,3);
  assert.ok(Math.abs(source.stops.find(x=>x.when!==null).when-(source.when+1.5))<.00001);
  check('short note preview schedules its endpoint before playback and excludes later clicks');
  await page.locator('#nav-preview').click();await settings(1,false);await range(.8,2);before=await data();
  await page.locator('#playButton').click();await finished();
  assert.equal((await data()).clicks.length,before.clicks.length);check('turning tap sound off schedules no preview clicks');
  await settings(1);await range(0,2);before=await data();await page.locator('#playButton').click();
  await page.waitForFunction(count=>window.__audioQa.clicks.length>count,before.clicks.length);
  await page.locator('#playButton').click();observed=await data();clicks=observed.clicks.slice(before.clicks.length);
  assert.ok(clicks.some(c=>c.stops.some(s=>s.when===null)),'停止は予約された音を取り消す');check('pausing cancels queued preview sounds');
  assert.deepEqual(errors,[]);check('no browser runtime errors');
  await page.screenshot({path:path.join(output,'audio-preview.png')});
}finally{
  fs.writeFileSync(path.join(output,'audio-schedule-observations.json'),JSON.stringify({observedAt:new Date().toISOString(),log,errors},null,2));
  await browser.close();
}
