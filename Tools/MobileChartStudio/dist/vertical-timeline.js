import { COLORS, audioTime, longSeconds, clamp, formatTime } from './core.js';
import { visibleNotes } from './timeline.js';
export function verticalTime(window,y,height){return window.start+clamp((height-24-y)/Math.max(1,height-48),0,1)*window.span;}
export function verticalGeometry(chart,window,width,height){
  const left=48,right=Math.max(left+1,width-24),bottom=height-24,scale=Math.max(1,height-48)/window.span;
  return visibleNotes(chart,window).map(note=>({note,x:left+clamp(note.x*chart.coordScale/5+.5,0,1)*(right-left),y:bottom-(audioTime(note,chart)-window.start)*scale,end:bottom-(audioTime(note,chart)+longSeconds(note)-window.start)*scale}));
}
export function verticalHits(boxes,x,y){
  const heads=boxes.filter(b=>Math.abs(x-b.x)<=22&&Math.abs(y-b.y)<=22).sort((a,b)=>Math.hypot(x-a.x,y-a.y)-Math.hypot(x-b.x,y-b.y)).map(b=>b.note);
  if(heads.length)return heads;
  // ロングの胴(始点から終わりまでの棒)でも選べる。始点が表示区間の外にあっても、見えている棒を押せば選べる。始点が優先。
  return boxes.filter(b=>b.end<b.y-1&&Math.abs(x-b.x)<=16&&y>=b.end-4&&y<=b.y).sort((a,b)=>Math.abs(x-a.x)-Math.abs(x-b.x)).map(b=>b.note);
}
export function paintVerticalTimeline(canvas,chart,buffer,window,position,selected,range){
  const w=canvas.clientWidth,h=canvas.clientHeight;if(!w||!h)return [];
  const dpr=Math.min(devicePixelRatio||1,2);if(canvas.width!==Math.round(w*dpr)||canvas.height!==Math.round(h*dpr)){canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);}
  const ctx=canvas.getContext('2d');ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,w,h);
  const left=48,right=w-24,top=24,bottom=h-24,toY=time=>bottom-(time-window.start)/window.span*(bottom-top);
  ctx.strokeStyle='#253650';for(let i=0;i<=4;i++){const x=left+(right-left)*i/4;ctx.beginPath();ctx.moveTo(x,top);ctx.lineTo(x,bottom);ctx.stroke();}
  ctx.fillStyle='#9baac1';ctx.font='11px system-ui';ctx.textAlign='left';ctx.fillText('先の時刻 ↑',left,14);ctx.textAlign='right';ctx.fillText('X位置 →',right,h-5);
  if(range){const y1=clamp(toY(range.end),top,bottom),y2=clamp(toY(range.start),top,bottom);ctx.fillStyle='#c3f36b16';ctx.fillRect(left,y1,right-left,Math.max(0,y2-y1));}
  const beat=60/chart.bpm,step=beat*(bottom-top)/window.span>24?beat:Math.max(1,Math.ceil(window.span/6));
  const origin=(chart.beatZeroMs+chart.offsetMs)/1000;
  for(let t=origin+Math.ceil((window.start-origin)/step)*step;t<=window.end+.0001;t+=step){const y=toY(t);ctx.strokeStyle='#314158';ctx.beginPath();ctx.moveTo(left,y);ctx.lineTo(right,y);ctx.stroke();ctx.fillStyle='#a1b2ca';ctx.textAlign='right';ctx.fillText(step<1?t.toFixed(1)+'s':formatTime(t),left-7,y+4);}
  const boxes=verticalGeometry(chart,window,w,h),arrows={up:'↑',down:'↓',left:'←',right:'→',upleft:'↖',upright:'↗',downleft:'↙',downright:'↘'};
  ctx.save();ctx.beginPath();ctx.rect(left-22,top-12,right-left+44,bottom-top+24);ctx.clip();
  for(const b of boxes){ctx.fillStyle=COLORS[b.note.color];if(b.end<b.y){ctx.globalAlpha=.35;ctx.fillRect(b.x-6,b.end,12,b.y-b.end);ctx.globalAlpha=1;}ctx.fillRect(b.x-13,b.y-13,26,26);ctx.fillStyle='#0b1019';ctx.font='bold 18px system-ui';ctx.textAlign='center';ctx.textBaseline='middle';ctx.fillText(arrows[b.note.direction]||'•',b.x,b.y);if(b.note.count>1){ctx.font='11px system-ui';ctx.fillStyle=COLORS[b.note.color];ctx.fillText('×'+b.note.count,b.x+24,b.y);}if(b.note.__editorId===selected){ctx.strokeStyle='#fff';ctx.lineWidth=2;ctx.strokeRect(b.x-16,b.y-16,32,32);ctx.lineWidth=1;}}
  ctx.restore();if(position>=window.start&&position<=window.end){ctx.strokeStyle='#c3f36b';ctx.lineWidth=2;ctx.beginPath();ctx.moveTo(left,toY(position));ctx.lineTo(right,toY(position));ctx.stroke();ctx.lineWidth=1;}
  return boxes;
}
