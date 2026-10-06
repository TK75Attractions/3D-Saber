import { COLORS, audioTime, longSeconds, clamp, formatTime } from './core.js';
export function timelineWindow(duration, start, span) {
  const width = Math.min(duration || 1, span > 0 ? span : duration || 1);
  const from = clamp(start, 0, Math.max(0, duration - width));
  return {start:from, end:from+width, span:width};
}
export function visibleNotes(chart, window) {
  return chart.notes.filter(n => audioTime(n,chart) <= window.end && audioTime(n,chart) + longSeconds(n) >= window.start);
}
export function timelineGeometry(chart, window, width) {
  const left=34, right=Math.max(left+1,width-12), scale=(right-left)/window.span;
  const lanes=['blue','red','gold','default'];
  return visibleNotes(chart,window).map(note=>{
    const time=audioTime(note,chart), head=left+(time-window.start)*scale;
    const end=head+longSeconds(note)*scale;
    const x=clamp(head,left,right), x2=clamp(Math.max(head+10,end),left,right);
    return {note, x, y:56+Math.max(0,lanes.indexOf(note.color))*36, width:Math.max(3,x2-x), head};
  });
}
export function timelineHit(boxes,x,y) {
  return boxes.filter(b=>x>=b.x-8&&x<=b.x+b.width+8&&Math.abs(y-b.y)<=16)
    .sort((a,b)=>Math.abs(x-a.head)-Math.abs(x-b.head))[0]?.note;
}
export function paintTimeline(canvas, chart, buffer, window, position, selected, range) {
  const dpr=Math.min(devicePixelRatio||1,2),w=canvas.clientWidth,h=canvas.clientHeight;
  if(!w||!h)return [];
  if(canvas.width!==Math.round(w*dpr)||canvas.height!==Math.round(h*dpr)){canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);}
  const ctx=canvas.getContext('2d');ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,w,h);
  const left=34,right=w-12,scale=(right-left)/window.span,toX=time=>left+(time-window.start)*scale;
  if(range){ctx.fillStyle='#c3f36b13';const start=clamp(toX(range.start),left,right),end=clamp(toX(range.end),left,right);ctx.fillRect(start,38,Math.max(0,end-start),144);}
  if(buffer){
    const data=buffer.getChannelData(0);ctx.fillStyle='#68809c';
    for(let x=left;x<right;x+=2){const first=Math.floor((window.start+(x-left)/scale)*buffer.sampleRate),last=Math.min(data.length,Math.ceil((window.start+(x+2-left)/scale)*buffer.sampleRate));let peak=0;
      for(let i=first;i<last;i+=Math.max(1,Math.ceil((last-first)/12)))peak=Math.max(peak,Math.abs(data[i]||0));ctx.fillRect(x,22-peak*12,1,Math.max(1,peak*24));}
  }
  const beat=60/chart.bpm,origin=(chart.beatZeroMs+chart.offsetMs)/1000;
  const step=beat*scale>=14?beat:Math.max(1,Math.ceil(window.span/6));
  const base=beat*scale>=14?origin:0;
  ctx.strokeStyle='#304058';ctx.fillStyle='#a1b2ca';ctx.font='12px system-ui';ctx.textAlign='center';
  let labelX=-100;
  for(let time=base+Math.ceil((window.start-base)/step)*step;time<=window.end+.0001;time+=step){const x=toX(time);ctx.beginPath();ctx.moveTo(x,38);ctx.lineTo(x,182);ctx.stroke();if(x-labelX>56&&x<right-15){ctx.fillText(formatTime(time,window.span<=5),x,202);labelX=x;}}
  ['青','赤','金','他'].forEach((label,i)=>{ctx.fillStyle=Object.values(COLORS)[i];ctx.textAlign='left';ctx.fillText(label,8,60+i*36);ctx.strokeStyle='#233148';ctx.beginPath();ctx.moveTo(left,74+i*36);ctx.lineTo(right,74+i*36);ctx.stroke();});
  const boxes=timelineGeometry(chart,window,w);
  for(const b of boxes){ctx.fillStyle=COLORS[b.note.color];ctx.globalAlpha=b.note.__editorId===selected?1:.8;ctx.fillRect(b.x,b.y-10,b.width,20);ctx.globalAlpha=1;if(b.note.count>1){ctx.fillStyle='#102034';ctx.font='bold 12px system-ui';ctx.textAlign='left';if(b.width>18)ctx.fillText('×'+b.note.count,b.x+3,b.y+4);}if(b.note.__editorId===selected){ctx.strokeStyle='#fff';ctx.lineWidth=2;ctx.strokeRect(b.x-2,b.y-12,b.width+4,24);ctx.lineWidth=1;}}
  if(position>=window.start&&position<=window.end){const x=toX(position);ctx.strokeStyle='#c3f36b';ctx.lineWidth=2;ctx.beginPath();ctx.moveTo(x,6);ctx.lineTo(x,184);ctx.stroke();ctx.lineWidth=1;}
  return boxes;
}
