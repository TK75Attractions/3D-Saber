import { audioTime, clamp, clone, longSeconds } from './core.js';

// 未設定は新規作成時だけ明示する。旧下書き・外部譜面の120 BPMを推測で書き換えない。
export const tempoIsSet = settings => settings?.bpmConfirmed !== false;
export const freshSettings = () => ({bpmConfirmed:false, snap:.25, snapMode:'off', countIn:false, clickSound:false, metronome:false, longCount:'auto', direction:'none', longThresholdMs:300});
// 拍子変更は小節の起点。BPM未設定・可変テンポの場合はUI側で秒移動に戻す。
export function barSeek(chart, time, direction, duration) {
  const origin=(chart.beatZeroMs+chart.offsetMs)/1000, beat=(time-origin)*chart.bpm/60;
  const meters=[{beat:0,numerator:4,denominator:4},...chart.timeSignatures].sort((a,b)=>a.beat-b.beat);
  const at=beat+(direction<0?-1e-7:1e-7);
  const meter=meters.filter(m=>m.beat<=at).at(-1)||meters[0];
  const size=meter.numerator*4/meter.denominator, relative=(beat-meter.beat)/size;
  let target=meter.beat+(direction<0?Math.ceil(relative-1e-7)-1:Math.floor(relative+1e-7)+1)*size;
  if(direction>0){const next=meters.find(m=>m.beat>beat+1e-7);if(next)target=Math.min(target,next.beat);}
  return clamp(origin+target*60/chart.bpm,0,duration);
}
// 今の位置から bars 小節ぶん先の時刻。小節の長さはその位置の拍子で数え、拍子の変わり目をまたぐときはそれぞれの長さで数える。
export function barSpan(chart, time, bars, duration) {
  const origin=(chart.beatZeroMs+chart.offsetMs)/1000, meters=[{beat:0,numerator:4,denominator:4},...chart.timeSignatures].sort((a,b)=>a.beat-b.beat);
  let beat=(time-origin)*chart.bpm/60, left=bars;
  while(left>1e-9){
    const meter=meters.filter(m=>m.beat<=beat+1e-7).at(-1)||meters[0], size=meter.numerator*4/meter.denominator, next=meters.find(m=>m.beat>beat+1e-7);
    const take=Math.min(left,next?(next.beat-beat)/size:Infinity);beat+=take*size;left-=take;
  }
  return clamp(origin+beat*60/chart.bpm,0,duration);
}
// 個別編集では変えた項目だけを更新。時刻未変更時のbeatや未知の属性は保持する。
export function editNote(chart, id, field, value, duration) {
  const result=clone(chart), note=result.notes.find(n=>n.__editorId===id);
  if(!note)throw new Error('編集するノーツを選んでください。');
  const numeric=['time','x','y','count','length'];
  if(numeric.includes(field)&&(value===''||!Number.isFinite(Number(value))))throw new Error('数値を入力してください。');
  const v=Number(value), previous=note.time;
  switch(field){
    case 'time': note.time=v*1000-chart.offsetMs;note.beat+=(note.time-previous)*chart.bpm/60000;break;
    case 'x':case 'y':note[field]=v/chart.coordScale;break;
    case 'count':
      if(!Number.isInteger(v)||v<1||v>99)throw new Error('回数は1〜99で指定してください。');
      if(v>1&&!note.lengthMs)note.lengthMs=longSeconds(note)*1000||60000/chart.bpm;
      note.count=v;if(v===1)note.lengthMs=0;break;
    case 'length':if(v<=0)throw new Error('ロングの長さは0より大きくしてください。');note.lengthMs=v*1000;if(note.count===1)note.count=2;break;
    case 'color':if(!['blue','red','gold','default'].includes(value))throw new Error('色を選んでください。');note.color=value;break;
    case 'direction':if(!['none','up','down','left','right','upleft','upright','downleft','downright'].includes(value))throw new Error('方向を選んでください。');note.direction=value;break;
    default:throw new Error('編集項目を確認してください。');
  }
  if(['count','length','direction'].includes(field))note.type=note.count>1?'long':note.direction==='none'?'tap':'direction';
  if(['time','count','length'].includes(field)&&(audioTime(note,result)<0||audioTime(note,result)+longSeconds(note)>duration+.001))throw new Error('ノーツとロングの終端を音源内に収めてください。');
  result.notes.sort((a,b)=>a.time-b.time);
  return result;
}
export function timingSummary(notes, chart) {
  if(!notes.length)return null;
  const beat=60000/chart.bpm;
  const errors=notes.map(n=>{const t=n.time-chart.beatZeroMs;return t-Math.round(t/beat)*beat;});
  return {mean:errors.reduce((sum,v)=>sum+v,0)/errors.length, meanAbsolute:errors.reduce((sum,v)=>sum+Math.abs(v),0)/errors.length};
}
export function differentSongSettings(a,b){
  return ['bpm','offsetMs','beatZeroMs','coordScale','timeSignatures'].filter(key=>JSON.stringify(a[key])!==JSON.stringify(b[key]));
}
