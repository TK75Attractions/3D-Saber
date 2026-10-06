// 本編が直接読めないM4Aなども、同じ音源時刻のPCM WAVとして渡せるようにする。
export function encodeWav(buffer) {
  const channels=Math.min(2,buffer.numberOfChannels),frames=buffer.length;
  const bytes=new ArrayBuffer(44+frames*channels*2),view=new DataView(bytes);
  const text=(at,value)=>{for(let i=0;i<value.length;i++)view.setUint8(at+i,value.charCodeAt(i));};
  text(0,'RIFF');view.setUint32(4,bytes.byteLength-8,true);text(8,'WAVE');text(12,'fmt ');
  view.setUint32(16,16,true);view.setUint16(20,1,true);view.setUint16(22,channels,true);
  view.setUint32(24,buffer.sampleRate,true);view.setUint32(28,buffer.sampleRate*channels*2,true);
  view.setUint16(32,channels*2,true);view.setUint16(34,16,true);text(36,'data');view.setUint32(40,frames*channels*2,true);
  const data=Array.from({length:channels},(_,i)=>buffer.getChannelData(i));let offset=44;
  for(let i=0;i<frames;i++)for(let c=0;c<channels;c++){const value=Math.max(-1,Math.min(1,data[c][i]));view.setInt16(offset,Math.round(value*(value<0?32768:32767)),true);offset+=2;}
  return bytes;
}
