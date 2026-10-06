const DB = 'saber-tap-studio-v1';
let connection;
async function database() {
  if (connection) return connection;
  connection = new Promise((resolve,reject) => {
    const request = indexedDB.open(DB, 1);
    request.onupgradeneeded = () => { request.result.createObjectStore('projects', { keyPath: 'id' }); request.result.createObjectStore('audio', { keyPath: 'id' }); };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => { connection = null; reject(request.error); };
  });
  return connection;
}
async function request(store, mode, operation) {
  const db = await database();
  return new Promise((resolve,reject) => {
    const tx = db.transaction(store, mode); const result = operation(tx.objectStore(store));
    tx.oncomplete = () => resolve(result.result); tx.onabort = tx.onerror = () => reject(tx.error || new Error('端末への保存に失敗しました。'));
  });
}
export const saveProject = project => request('projects', 'readwrite', s => s.put(project));
export const listProjects = () => request('projects', 'readonly', s => s.getAll());
export const getProject = id => request('projects', 'readonly', s => s.get(id));
export const saveAudio = (id, blob, name) => request('audio', 'readwrite', s => s.put({ id, blob, name }));
export const getAudio = id => request('audio', 'readonly', s => s.get(id));
