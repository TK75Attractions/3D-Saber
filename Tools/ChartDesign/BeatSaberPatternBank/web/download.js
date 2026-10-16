// 動画本体を重複配信せず、確認済みのファイルだけを最大25 MiBのZIPへまとめる。
const $ = s => document.querySelector(s);
const encoder = new TextEncoder();
const table = new Uint32Array(256);
for (let n=0;n<256;n++){let c=n;for(let k=0;k<8;k++)c=(c&1)?0xedb88320^(c>>>1):c>>>1;table[n]=c>>>0;}
function crc32(bytes){let c=0xffffffff;for(const b of bytes)c=table[(c^b)&255]^(c>>>8);return (c^0xffffffff)>>>0;}
function header(length){const bytes=new Uint8Array(length);return {bytes,v:new DataView(bytes.buffer)};}
function safePath(path){if(typeof path!=='string'||!/^[A-Za-z0-9_./-]+$/.test(path)||path.startsWith('/')||path.split('/').some(x=>!x||x==='.'||x==='..'))throw Error('保存ファイルの指定を確認できません。');return path;}
let descriptor,controller,blobUrl;
async function buildZip(spec,signal){
 const local=[],central=[];let offset=0,done=0;const expected=spec.entries.reduce((n,e)=>n+e.sizeBytes,0);
 if(expected>25*1024*1024||spec.entries.length>65535)throw Error('保存サイズの上限を超えました。');
 for(const entry of spec.entries){
  signal.throwIfAborted();const path=safePath(entry.path),name=encoder.encode(safePath(entry.name));
  const response=await fetch(path,{signal,credentials:'same-origin',cache:'no-cache'});
  if(!response.ok)throw Error('素材の取得に失敗しました。通信を確認して、もう一度お試しください。');
  const bytes=new Uint8Array(await response.arrayBuffer());
  const hash=[...new Uint8Array(await crypto.subtle.digest('SHA-256',bytes))].map(v=>v.toString(16).padStart(2,'0')).join('');
  if(bytes.length!==entry.sizeBytes||hash!==entry.sha256)throw Error('素材が更新されました。このページを再読み込みしてからお試しください。');
  const crc=crc32(bytes),l=header(30),c=header(46);
  l.v.setUint32(0,0x04034b50,true);l.v.setUint16(4,20,true);l.v.setUint16(6,0x800,true);l.v.setUint16(12,33,true);l.v.setUint32(14,crc,true);l.v.setUint32(18,bytes.length,true);l.v.setUint32(22,bytes.length,true);l.v.setUint16(26,name.length,true);
  c.v.setUint32(0,0x02014b50,true);c.v.setUint16(4,20,true);c.v.setUint16(6,20,true);c.v.setUint16(8,0x800,true);c.v.setUint16(14,33,true);c.v.setUint32(16,crc,true);c.v.setUint32(20,bytes.length,true);c.v.setUint32(24,bytes.length,true);c.v.setUint16(28,name.length,true);c.v.setUint32(42,offset,true);
  local.push(l.bytes,name,bytes);central.push(c.bytes,name);offset+=30+name.length+bytes.length;done+=bytes.length;
  $('#progress').value=done/expected;$('#status').textContent=`準備中 ${Math.round(100*done/expected)}%`;
  await new Promise(resolve=>setTimeout(resolve,0));
 }
 signal.throwIfAborted();const end=header(22);const centralSize=central.reduce((n,b)=>n+b.length,0);
 end.v.setUint32(0,0x06054b50,true);end.v.setUint16(8,spec.entries.length,true);end.v.setUint16(10,spec.entries.length,true);end.v.setUint32(12,centralSize,true);end.v.setUint32(16,offset,true);
 const blob=new Blob([...local,...central,end.bytes],{type:'application/zip'});
 if(blob.size!==spec.sizeBytes)throw Error('保存ファイルのサイズを確認できません。');return blob;
}
$('#prepare').onclick=async()=>{
 controller=new AbortController();$('#prepare').disabled=true;$('#cancel').hidden=false;$('#progress').hidden=false;$('#progress').value=0;$('#save').hidden=true;$('#status').textContent='素材を確認しています。';
 if(blobUrl){URL.revokeObjectURL(blobUrl);blobUrl=undefined;}
 try{const blob=await buildZip(descriptor,controller.signal);blobUrl=URL.createObjectURL(blob);$('#save').href=blobUrl;$('#save').download=descriptor.filename;$('#save').hidden=false;$('#status').textContent='準備できました。「ZIPを保存」を押してください。';}
 catch(e){$('#status').textContent=e.name==='AbortError'?'準備を中止しました。もう一度準備できます。':e.message;}
 finally{$('#prepare').disabled=false;$('#cancel').hidden=true;}
};
$('#cancel').onclick=()=>controller?.abort();
addEventListener('pagehide',()=>{controller?.abort();if(blobUrl)URL.revokeObjectURL(blobUrl);});
try{
 const id=new URLSearchParams(location.search).get('package');if(!/^(good|conditional|avoid)(-\d+)?$/.test(id||''))throw Error('素材集の分類別保存リンクから開いてください。');
 const response=await fetch(`packages/${id}.json`,{cache:'no-cache'});if(!response.ok)throw Error('保存一覧を読み込めませんでした。');descriptor=await response.json();
 if(descriptor.schemaVersion!==1||!Array.isArray(descriptor.entries))throw Error('保存一覧の形式を確認できません。');
 $('#title').textContent=descriptor.title;$('#details').textContent=`${descriptor.count}素材 · 約${(descriptor.sizeBytes/1048576).toFixed(1)} MB`;$('#prepare').disabled=false;
}catch(e){$('#details').textContent='';$('#status').textContent=e.message;}
