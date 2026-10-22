// 実HTTP・実Service Workerで更新する。製品JS/CSSは変えず、SWのキャッシュ名だけ世代を変える。
// distは開始時に全てメモリへ固定。PWA_DIST_ROOTで保存済みの修正前distも検証できる。
// 実ユーザーのブラウザ/DB/音源は使わず、各場面は新規コンテキストと合成WAVだけを使う。
import {createRequire} from 'node:module';
import {createServer} from 'node:http';
import {createHash} from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
const require=createRequire(import.meta.url),{chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const dist=path.resolve(process.env.PWA_DIST_ROOT||path.join(path.dirname(fileURLToPath(import.meta.url)),'../dist'));
const output=process.env.QA_OUTPUT||'qa-output';fs.mkdirSync(output,{recursive:true});
const assets=new Map(fs.readdirSync(dist,{withFileTypes:true}).filter(x=>x.isFile()).map(x=>['/'+x.name,fs.readFileSync(path.join(dist,x.name))]));
const worker=assets.get('/sw.js').toString(),cache=/const CACHE = '([^']+)';/.exec(worker);assert.ok(cache,'SWのキャッシュ定義を確認');
const record={startedAt:new Date().toISOString(),dist,snapshot:Object.fromEntries([...assets].map(([name,bytes])=>[name,createHash('sha256').update(bytes).digest('hex')])),checks:[]};
let generation=1;
const cacheName=()=>`${cache[1]}-pwa-test-generation-${generation}`;
const mime={'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png','.webmanifest':'application/manifest+json'};
const server=createServer((req,res)=>{
  const pathname=new URL(req.url,'http://localhost').pathname,key=pathname==='/'?'/index.html':pathname;
  if(!assets.has(key)){res.writeHead(404);res.end();return;}
  res.writeHead(200,{'Content-Type':mime[path.extname(key)]||'application/octet-stream','Cache-Control':'no-store'});
  res.end(key==='/sw.js'?worker.replace(cache[0],`const CACHE = '${cacheName()}';`):assets.get(key));
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const site=`http://127.0.0.1:${server.address().port}/`,browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL||'msedge',headless:true});
record.site=site;
const fixture={bpm:120,coordScale:1,offsetMs:100,beatZeroMs:0,displayLevel:3,timeSignatures:[],customRoot:'pwa-keeps-this',notes:[{time:900,beat:1.8,x:-.2,y:0,color:'blue',direction:'none',type:'tap',count:1,lengthMs:0,customNote:'keep'}]};
function wav(){const n=8000*5,b=Buffer.alloc(44+n*2);b.write('RIFF');b.writeUInt32LE(b.length-8,4);b.write('WAVEfmt ',8);b.writeUInt32LE(16,16);b.writeUInt16LE(1,20);b.writeUInt16LE(1,22);b.writeUInt32LE(8000,24);b.writeUInt32LE(16000,28);b.writeUInt16LE(2,32);b.writeUInt16LE(16,34);b.write('data',36);b.writeUInt32LE(n*2,40);for(let i=0;i<n;i++)b.writeInt16LE(Math.round(300*Math.sin(i*Math.PI*2*440/8000)),44+2*i);return b;}
const saved=p=>p.waitForFunction(()=>document.querySelector('#saveStatus').textContent==='この端末に保存済み');
async function controlled(p,revisit=false){
  await p.goto(site);await p.waitForFunction(()=>!!navigator.serviceWorker.controller);
  if(revisit){await p.reload();await p.waitForFunction(()=>!!navigator.serviceWorker.controller);}
  await p.locator('#libraryButton').waitFor({state:'visible'});
}
async function deploy(p,next){
  generation=next;
  await p.evaluate(async()=>{
    const registration=await navigator.serviceWorker.ready;
    await new Promise((resolve,reject)=>{
      const changed=()=>{clearTimeout(timer);resolve();};
      const timer=setTimeout(()=>{navigator.serviceWorker.removeEventListener('controllerchange',changed);reject(new Error('新しいSWが制御を取得しませんでした。'));},15000);
      navigator.serviceWorker.addEventListener('controllerchange',changed,{once:true});registration.update().catch(reject);
    });
  });
  assert.equal(await p.evaluate(async name=>(await caches.keys()).includes(name),cacheName()),true,'新しい世代のシェルを保存済み');
}
async function inventory(p){
  return p.evaluate(async()=>{
    const storage=await import('./storage.js'),projects=await storage.listProjects(),audio=[];
    for(const id of new Set(projects.map(p=>p.audioId))){
      const source=await storage.getAudio(id);if(!source)throw new Error('保存済み音源がありません。');
      const sha256=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',await source.blob.arrayBuffer()))).map(x=>x.toString(16).padStart(2,'0')).join('');
      audio.push({id,name:source.name,type:source.blob.type,size:source.blob.size,sha256});
    }
    // 再生位置・更新日時・内部IDは保存操作で変わり得る。利用者の内容と下書きIDは全て比較する。
    return {last:localStorage.getItem('saber-last-project'),projects:projects.map(({_revision,updated,position,...p})=>({...p,chart:{...p.chart,notes:p.chart.notes.map(({__editorId,...note})=>note)}})).sort((a,b)=>a.id.localeCompare(b.id)),audio:audio.sort((a,b)=>a.id.localeCompare(b.id))};
  });
}
async function seed(p){
  await p.locator('#audioFile').setInputFiles({name:'PWA検証.wav',mimeType:'audio/wav',buffer:wav()});await saved(p);
  await p.locator('#libraryButton').click();await p.locator('#chartFile').setInputFiles({name:'chart_hard.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(fixture))});
  await p.locator('#library').waitFor({state:'hidden'});await saved(p);
  await p.locator('#settingsButton').click();
  await p.locator('#settingsDialog').getByText('この端末',{exact:true}).click();await p.locator('#latency').fill('75');await p.locator('#latency').press('Tab');
  await p.locator('#settingsDialog').getByText('聴き方',{exact:true}).click();await p.locator('#speed').selectOption('0.5');await p.locator('#settingsDialogClose').click();
  // 「曲・保存」はpersistを待ってから開く。非同期DB式をwaitForFunctionの真偽判定に渡さない。
  await p.locator('#libraryButton').click();await p.locator('#library').waitFor({state:'visible'});await saved(p);
  const snapshot=await inventory(p),current=snapshot.projects.find(item=>item.id===snapshot.last);
  assert.equal(current.difficulty,'hard');assert.equal(current.settings.latencyMs,75);assert.equal(current.settings.rate,.5);
  await p.locator('[data-close="library"]').click();return snapshot;
}
async function scenario(name,run){
  generation=1;const context=await browser.newContext({viewport:{width:390,height:844},hasTouch:true}),p=await context.newPage(),errors=[];
  p.on('pageerror',e=>errors.push(e.message));
  try{const evidence=await run(p,context);assert.deepEqual(errors,[]);record.checks.push({name,passed:true,evidence});console.log('PASS '+name);}
  catch(error){record.checks.push({name,passed:false,error:error.stack,errors});console.error('FAIL '+name+'\n'+error.message);await p.screenshot({path:path.join(output,`pwa-failure-${record.checks.length}.png`)}).catch(()=>{});process.exitCode=1;}
  finally{await context.close();}
}
try{
  await scenario('first claim stays quiet; a later deployment in the same first visit shows the update',async p=>{
    await controlled(p);assert.equal(await p.locator('#updateBanner').isHidden(),true);assert.equal(await p.locator('#updateApp').evaluate(e=>e.hidden),true);
    await deploy(p,2);assert.equal(await p.locator('#updateBanner').isHidden(),false,'同じ初回訪問の2回目のcontrollerchangeで通知する');assert.equal(await p.locator('#updateApp').evaluate(e=>e.hidden),false);
  });
  await scenario('a returning controlled visit shows an update and the banner does not block the library at 390px',async p=>{
    await controlled(p,true);await deploy(p,2);assert.equal(await p.locator('#updateBanner').isHidden(),false);assert.equal(await p.locator('#updateApp').evaluate(e=>e.hidden),false);
    await p.locator('#libraryButton').click({timeout:3000});await p.locator('#library').waitFor({state:'visible'});assert.equal(await p.locator('#updateApp').isVisible(),true);
  });
  await scenario('update saves the latest name and retains drafts, settings and original audio, including offline reopening',async(p,context)=>{
    await controlled(p,true);const before=await seed(p);
    // 更新前から保存画面を開くのも通常の操作。バナーの成否に関係なく保存・再読み込みを独立に確かめる。
    await p.locator('#libraryButton').click();await deploy(p,2);await p.locator('#updateApp').waitFor({state:'visible'});
    await p.locator('#projectName').fill('更新直前の曲名');
    const navigated=p.waitForEvent('framenavigated',frame=>frame===p.mainFrame());await p.locator('#updateApp').click();await navigated;await p.waitForLoadState('domcontentloaded');await p.locator('#resumeProject').waitFor({state:'visible'});
    const expected=structuredClone(before);expected.projects.find(item=>item.id===expected.last).name='更新直前の曲名';
    const after=await inventory(p);record.updateSnapshots={before,after};assert.deepEqual(after,expected,'更新を押す直前の編集と保存内容を保持');
    await context.setOffline(true);await p.reload();await p.locator('#resumeProject').waitFor({state:'visible'});await p.locator('#resumeProject').click();
    await p.waitForFunction(()=>document.querySelector('#songTitle').textContent==='更新直前の曲名'&&!document.querySelector('#libraryButton').disabled);
    assert.equal(await p.locator('#noteCount').innerText(),'1 NOTES');assert.match(await p.locator('#difficultyLabel').innerText(),/^HARD/);assert.equal(await p.locator('#playButton').isDisabled(),false);
    assert.deepEqual(await inventory(p),expected,'オフラインで譜面と音源を再開できる');
    return {drafts:after.projects.length,audio:after.audio,settings:after.projects.find(item=>item.id===after.last).settings};
  });
  await scenario('a failed save prevents update reload and keeps the local chart available',async p=>{
    await controlled(p,true);const before=await seed(p);await p.locator('#libraryButton').click();await deploy(p,2);
    await p.evaluate(()=>{
      window.pwaDocumentToken='same-document';const native=IDBDatabase.prototype.transaction;
      // 保存の実トランザクションを1回だけabortし、保存失敗を発生させる。偽の成功/失敗値は返さない。
      IDBDatabase.prototype.transaction=function(stores,mode,...rest){const tx=native.call(this,stores,mode,...rest),names=typeof stores==='string'?[stores]:Array.from(stores);if(mode==='readwrite'&&names.includes('projects')){IDBDatabase.prototype.transaction=native;queueMicrotask(()=>tx.abort());}return tx;};
    });
    let navigations=0;p.on('framenavigated',frame=>{if(frame===p.mainFrame())navigations++;});
    await p.locator('#updateApp').click();await p.waitForFunction(()=>document.querySelector('#saveStatus').textContent.startsWith('未保存'));await p.waitForTimeout(400);
    assert.equal(navigations,0);assert.equal(await p.evaluate(()=>window.pwaDocumentToken),'same-document');assert.equal(await p.locator('#library').evaluate(e=>e.open),true);assert.equal(await p.locator('#noteCount').innerText(),'1 NOTES');
    assert.deepEqual(await inventory(p),before,'失敗した保存でDBや手元の譜面を切り替えない');
  });
}finally{
  record.finishedAt=new Date().toISOString();record.passed=record.checks.every(check=>check.passed);fs.writeFileSync(path.join(output,'pwa-updates-result.json'),JSON.stringify(record,null,2)+'\n');
  await browser.close();await new Promise(resolve=>server.close(resolve));
}
