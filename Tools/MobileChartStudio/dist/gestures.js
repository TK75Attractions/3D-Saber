// 座標はCSSピクセル、時刻はPointerEventの実時間。再生速度や画面の解像度に依存させない。
export function beginStroke(x, y, time) {
  return {startX:x, startY:y, startTime:time, x, y, time, distance:0};
}
export function moveStroke(stroke, x, y, time) {
  if (![x,y,time].every(Number.isFinite) || time < stroke.time) return stroke;
  stroke.distance += Math.hypot(x-stroke.x,y-stroke.y);
  stroke.x=x;stroke.y=y;stroke.time=time;
  return stroke;
}
export function flickDirection(stroke, sensitivity='normal') {
  if (!stroke) return 'none';
  const dx=stroke.x-stroke.startX,dy=stroke.y-stroke.startY,distance=Math.hypot(dx,dy),elapsed=stroke.time-stroke.startTime;
  const minimum={sensitive:16,normal:24,firm:36}[sensitivity] || 24;
  // 小さな揺れ、長押し、遅いドラッグ、折り返した動きはフリックにしない。
  if (![distance,elapsed,stroke.distance].every(Number.isFinite) || elapsed<=0 || elapsed>=240 || distance<minimum || distance/elapsed<.16 || distance<stroke.distance*.7) return 'none';
  const sector=(Math.round(Math.atan2(dy,dx)/(Math.PI/4))+8)%8;
  return ['right','downright','down','downleft','left','upleft','up','upright'][sector];
}
export function inputPreferences(value={}) {
  const s=value&&typeof value==='object'?value:{},validStep=[1,.5,.25,1/3].includes(s.snap);
  const legacy=Object.hasOwn(s,'snap') && s.snapMode===undefined;
  return {snap:validStep?s.snap:.25,
    snapMode:['near','always','off'].includes(s.snapMode)?s.snapMode:legacy?(validStep?'always':'off'):'near',
    snapWindowMs:[20,35,50].includes(s.snapWindowMs)?s.snapWindowMs:35,
    flickEnabled:typeof s.flickEnabled==='boolean'?s.flickEnabled:true,
    flickSensitivity:['sensitive','normal','firm'].includes(s.flickSensitivity)?s.flickSensitivity:'normal'};
}
