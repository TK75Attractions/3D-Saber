import test from 'node:test';
import assert from 'node:assert/strict';

test('deleting one difficulty retains shared audio; deleting the last draft releases only its audio', async t => {
  const state = {
    projects:new Map([['normal',{id:'normal',audioId:'shared'}],['hard',{id:'hard',audioId:'shared'}],['other',{id:'other',audioId:'other-audio'}]]),
    audio:new Map([['shared',{id:'shared'}],['other-audio',{id:'other-audio'}]])
  };
  const database = {transaction(stores,mode) {
    assert.deepEqual(stores,['projects','audio']); assert.equal(mode,'readwrite');
    const tx = {objectStore(name) { return {
      getAll() { const request = {}; queueMicrotask(()=>{request.result=[...state[name].values()];request.onsuccess();queueMicrotask(()=>tx.oncomplete());});return request; },
      delete(id) { state[name].delete(id); }
    }; }}; return tx;
  }};
  globalThis.indexedDB = {open(){const request={};queueMicrotask(()=>{request.result=database;request.onsuccess();});return request;}};
  t.after(()=>delete globalThis.indexedDB);
  const {deleteProject} = await import('../dist/storage.js?delete-test');
  await deleteProject('normal'); assert.equal(state.projects.has('normal'),false); assert.equal(state.projects.has('hard'),true); assert.equal(state.audio.has('shared'),true);
  await deleteProject('missing'); assert.equal(state.audio.size,2);
  await deleteProject('hard'); assert.equal(state.audio.has('shared'),false); assert.equal(state.projects.has('other'),true); assert.equal(state.audio.has('other-audio'),true);
});
