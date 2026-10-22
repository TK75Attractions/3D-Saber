const DB = 'saber-tap-studio-v1';
let connection;
const revisions = new Map();
export const contentOf = project => {
  const {_revision,updated,position,...content}=project;
  return JSON.stringify({...content,chart:{...content.chart,notes:content.chart.notes.map(({__editorId,...note})=>note)}});
};
async function database() {
  if (connection) return connection;
  connection = new Promise((resolve,reject) => {
    const request = indexedDB.open(DB, 1);
    request.onupgradeneeded = () => { request.result.createObjectStore('projects', { keyPath: 'id' }); request.result.createObjectStore('audio', { keyPath: 'id' }); };
    request.onsuccess = () => { const db=request.result;db.onversionchange=()=>{db.close();connection=null;};resolve(db); };
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
// 同じ下書きを別タブで更新していたら上書きしない。照合と保存は同一トランザクション。
// requireAudio: 開いていない曲の下書きから作るときだけ、その曲の音源が残っていることも同じトランザクションで確かめ、なければ書かない
// (一覧を開いてから作るまでの間に別タブで曲ごと消されると、開けない下書きが残っていた)。ふだんの保存は音源を見ない。
export async function saveProject(project,{requireAudio=false}={}) {
  const db=await database(), expected=revisions.get(project.id)??project._revision??0;
  return new Promise((resolve,reject)=>{
    const tx=db.transaction(requireAudio?['projects','audio']:'projects','readwrite'),store=tx.objectStore('projects');
    // 要求は出した順に処理されるので、下書きを読み終えた時点で音源の数も分かっている。
    const audio=requireAudio?tx.objectStore('audio').count(project.audioId):null,read=store.get(project.id);
    let conflict,nextRevision=expected+1;
    read.onsuccess=()=>{
      if(audio&&!audio.result){conflict=new Error('元の曲の音源が別のタブで削除されています。「曲・保存」を開き直してください。');conflict.name='MissingAudioError';tx.abort();return;}
      const current=read.result;
      // 再生位置だけの保存や、開き直した際の内部IDの違いは編集競合にしない。
      // 中身が同じなら最終更新は新しいほうを残す(別タブで変えて戻したあと、古いタブの再生位置の保存で時刻が巻き戻らないように)。
      if(current&&contentOf(current)===contentOf(project)){nextRevision=current._revision||0;project.updated=Math.max(project.updated||0,current.updated||0);store.put({...project,_revision:nextRevision});return;}
      if((current?current._revision||0:0)!==expected){conflict=new Error('別のタブでこの下書きが更新・削除されました。手元の譜面を書き出してから、画面を再読み込みしてください。');conflict.name='ConflictError';tx.abort();return;}
      store.put({...project,_revision:expected+1});
    };
    tx.oncomplete=()=>{revisions.set(project.id,nextRevision);project._revision=nextRevision;resolve();};
    tx.onabort=tx.onerror=()=>reject(conflict||tx.error||new Error('端末への保存に失敗しました。'));
  });
}
export const listProjects = () => request('projects', 'readonly', s => s.getAll());
export const getProject = async id => {const project=await request('projects','readonly',s=>s.get(id));if(project)revisions.set(id,project._revision||0);return project;};
export const saveAudio = (id, blob, name) => request('audio', 'readwrite', s => s.put({ id, blob, name }));
export const getAudio = id => request('audio', 'readonly', s => s.get(id));

// 同じ音源を使う難易度が残っていれば、その音源は削除しない。
export async function deleteProject(id) {
  const db = await database();
  return new Promise((resolve, reject) => {
    const tx = db.transaction(['projects','audio'], 'readwrite'), projects = tx.objectStore('projects');
    const list = projects.getAll();
    list.onsuccess = () => {
      const project = list.result.find(item => item.id === id);
      if (!project) return;
      projects.delete(id);
      if (!list.result.some(item => item.id !== id && item.audioId === project.audioId)) tx.objectStore('audio').delete(project.audioId);
    };
    tx.oncomplete = () => resolve();
    tx.onabort = tx.onerror = () => reject(tx.error || new Error('下書きを削除できませんでした。'));
  });
}

// 復元時は音源と譜面を同じトランザクションで保存し、片方だけを残さない。
export async function saveProjectWithAudio(project, audio) {
  const db = await database();
  return new Promise((resolve, reject) => {
    const tx = db.transaction(['projects','audio'], 'readwrite');
    tx.objectStore('audio').put(audio);tx.objectStore('projects').put({...project,_revision:1});
    tx.oncomplete = () => {revisions.set(project.id,1);project._revision=1;resolve();};
    tx.onabort = tx.onerror = () => reject(tx.error || new Error('バックアップを端末に保存できませんでした。'));
  });
}
