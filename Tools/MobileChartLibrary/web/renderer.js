import {colors, clamp, project, stateAt} from './core.js';

export function resize(canvas) {
  const rect=canvas.getBoundingClientRect(), dpr=Math.min(window.devicePixelRatio||1,2);
  if(canvas.width!==Math.round(rect.width*dpr)||canvas.height!==Math.round(rect.height*dpr)){
    canvas.width=Math.round(rect.width*dpr);canvas.height=Math.round(rect.height*dpr);
  }
  const ctx=canvas.getContext('2d');ctx.setTransform(dpr,0,0,dpr,0,0);
  return {ctx,w:rect.width,h:rect.height};
}

export function draw(canvas, chart, time, approach=1) {
  const {ctx,w,h}=resize(canvas);if(!w||!h)return;
  const p=(x,y,z)=>project(x,y,z,w,h);
  ctx.fillStyle='#080f16';ctx.fillRect(0,0,w,h);
  const glow=ctx.createRadialGradient(w/2,h*.34,0,w/2,h*.38,w*.55);
  glow.addColorStop(0,'#112c36');glow.addColorStop(1,'#080f16');ctx.fillStyle=glow;ctx.fillRect(0,0,w,h);
  const line=(points,color,width=1)=>{ctx.strokeStyle=color;ctx.lineWidth=width;ctx.beginPath();points.forEach((v,i)=>{const q=p(...v);i?ctx.lineTo(q.x,q.y):ctx.moveTo(q.x,q.y);});ctx.stroke();};
  // ノーツと同じ透視投影で床と判定面を描く。
  for(let x=-5;x<=5;x+=1.25)line([[x,-1.9,-1],[x,-1.9,40]],x===0?'#23516a':'#19333e');
  for(let z=0;z<=40;z+=4)line([[-5,-1.9,z],[5,-1.9,z]],'#203843');
  for(let z=4;z<=40;z+=6)for(const side of [-1,1]){
    line([[side*5,-1.9,z],[side*5.2,1.5,z],[side*4.5,3,z]],'#243c48',3);
    line([[side*5,1,z],[side*4.8,1.9,z]],'#3d99ad',1);
  }
  const scale=chart?.coordScale||1;
  line([[-2.5*scale,-1.5*scale,0],[2.5*scale,-1.5*scale,0],[2.5*scale,1.5*scale,0],[-2.5*scale,1.5*scale,0],[-2.5*scale,-1.5*scale,0]],'#488896',1);
  if(!chart)return;
  const visible=[];
  for(const n of chart.notes){const state=stateAt(n,time,approach);if(state)visible.push({n,state});}
  visible.sort((a,b)=>b.state.z-a.state.z);
  const faces=[[0,1,5,4],[1,2,6,5],[3,2,6,7],[0,3,7,4],[4,5,6,7],[0,1,2,3]];
  for(const {n,state:s} of visible){
    const x=n.x*scale,y=n.y*scale,z=s.z,size=.4*s.shrink;
    const depth=.4*Math.min(1+n.hold/.7,6)*s.shrink;
    const corners=[[-1,-1,-1],[1,-1,-1],[1,1,-1],[-1,1,-1],[-1,-1,1],[1,-1,1],[1,1,1],[-1,1,1]].map(([a,b,c])=>p(x+a*size,y+b*size,z+c*depth));
    const color=colors[n.color]||colors.default;
    for(let i=0;i<faces.length;i++){
      ctx.beginPath();faces[i].forEach((k,j)=>j?ctx.lineTo(corners[k].x,corners[k].y):ctx.moveTo(corners[k].x,corners[k].y));ctx.closePath();
      ctx.fillStyle=color;ctx.globalAlpha=i===5?.88:.16;ctx.fill();ctx.globalAlpha=.62;ctx.strokeStyle=color;ctx.lineWidth=1;ctx.stroke();
    }
    ctx.globalAlpha=1;
    const front=p(x,y,z-depth), px=Math.max(2,size*front.scale*1.1);
    if(n.direction&&n.direction!=='none')drawArrow(ctx,front.x,front.y,px,n.direction);
    if(n.count>1&&s.remaining>0){
      const above=p(x,y+.7,z);ctx.font=`700 ${clamp(above.scale*.65,10,28)}px system-ui`;ctx.textAlign='center';ctx.textBaseline='bottom';ctx.lineWidth=3;ctx.strokeStyle='#08121c';ctx.strokeText(s.remaining,above.x,above.y);ctx.fillStyle='#fff';ctx.fillText(s.remaining,above.x,above.y);
      if(time>=n.hit){ctx.fillStyle='#07121b';ctx.fillRect(front.x-px,front.y+px*1.4,px*2,3);ctx.fillStyle=color;ctx.fillRect(front.x-px,front.y+px*1.4,px*2*clamp((n.hold-(time-n.hit))/n.hold,0,1),3);}
    }
    // 到達時の小さな輪は、同時押しのタイミングを見分ける目安。
    const elapsed=time-n.hit;
    if(elapsed>=0&&elapsed<.12){const q=p(x,y,0);ctx.strokeStyle=color;ctx.globalAlpha=1-elapsed/.12;ctx.lineWidth=1.5;ctx.beginPath();ctx.arc(q.x,q.y,(.5+elapsed*4)*q.scale,0,Math.PI*2);ctx.stroke();ctx.globalAlpha=1;}
  }
}

function drawArrow(ctx,x,y,size,direction){
  const angle={up:0,upright:Math.PI/4,right:Math.PI/2,downright:Math.PI*3/4,down:Math.PI,downleft:Math.PI*5/4,left:Math.PI*3/2,upleft:Math.PI*7/4}[direction];
  if(angle===undefined)return;
  ctx.save();ctx.translate(x,y);ctx.rotate(angle);ctx.strokeStyle='#fff';ctx.lineWidth=Math.max(1.7,size*.18);ctx.lineCap='round';ctx.lineJoin='round';ctx.beginPath();ctx.moveTo(0,size*.65);ctx.lineTo(0,-size*.65);ctx.moveTo(-size*.5,-size*.12);ctx.lineTo(0,-size*.65);ctx.lineTo(size*.5,-size*.12);ctx.stroke();ctx.restore();
}

export function drawDensity(canvas,chart,duration,time){
  const{ctx,w,h}=resize(canvas);ctx.clearRect(0,0,w,h);if(!chart)return;
  const bins=new Array(Math.max(1,Math.floor(w/3))).fill(0);
  for(const n of chart.notes){const at=Math.floor(n.hit/duration*bins.length);if(at>=0&&at<bins.length)bins[at]++;}
  const peak=Math.max(1,...bins);bins.forEach((count,i)=>{ctx.fillStyle=i/bins.length<=time/duration?'#80d6e7':'#35546a';const size=count/peak*(h-5);ctx.fillRect(i*3,h-size,2,Math.max(1,size));});
}
