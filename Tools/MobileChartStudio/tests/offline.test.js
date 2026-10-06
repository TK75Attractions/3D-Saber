import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
test('offline shell serves cached navigation and scripts without network; external requests are untouched',async()=>{
  const handlers={},stored=new Map(),deleted=[];let network=0;
  const cache={addAll:async paths=>{for(const p of paths){const file=p==='./'?'index.html':p.slice(2);assert.ok(fs.existsSync(new URL('../dist/'+file,import.meta.url)));stored.set(new URL(p,'https://editor.test/').pathname,'cached:'+file);}},match:async req=>stored.get(new URL(req.url).pathname)};
  const context={URL,Promise,self:{location:{origin:'https://editor.test'},registration:{scope:'https://editor.test/'},clients:{claim:async()=>{}},skipWaiting:async()=>{},addEventListener:(event,fn)=>handlers[event]=fn},
    caches:{open:async()=>cache,keys:async()=>['other-app','saber-tap-shell-v0'],delete:async name=>deleted.push(name)},fetch:async()=>{network++;throw new Error('offline');}};
  vm.runInNewContext(fs.readFileSync(new URL('../dist/sw.js',import.meta.url),'utf8'),context);
  let pending;handlers.install({waitUntil:p=>pending=p});await pending;
  handlers.activate({waitUntil:p=>pending=p});await pending;assert.deepEqual(deleted,['saber-tap-shell-v0']);
  for(const url of ['https://editor.test/','https://editor.test/app.js?version=2']){
    let response;handlers.fetch({request:{url,method:'GET'},respondWith:p=>response=p});assert.match(await response,/^cached:/);
  }
  assert.equal(network,0);let intercepted=false;handlers.fetch({request:{url:'https://outside.test/audio.mp3',method:'GET'},respondWith:()=>intercepted=true});assert.equal(intercepted,false);
});
