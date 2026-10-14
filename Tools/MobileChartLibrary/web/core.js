export const colors = { blue: '#39c5ff', red: '#ff496f', gold: '#ffd465', default: '#e7f3ff' };
export const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
export const timeLabel = value => `${Math.floor(Math.max(0,value)/60)}:${String(Math.floor(Math.max(0,value)%60)).padStart(2,'0')}`;
export const hitTime = (n, chart) => (n.time + (chart.offsetMs || 0)) / 1000;
export const linger = n => n.count > 1 ? (n.lengthMs > 0 ? n.lengthMs / 1000 : (n.count - 1) * .7) : 0;

export function prepare(raw) {
  if (!Array.isArray(raw.notes)) throw new Error('譜面を読み込めませんでした。');
  return { ...raw, coordScale: raw.coordScale || 1,
    notes: raw.notes.map(n => ({ ...n, count: n.count || 1, hit: hitTime(n,raw), hold: linger(n) })).sort((a,b)=>a.hit-b.hit) };
}

export function stateAt(n, t, approach=1) {
  const dt = n.hit - t, elapsed = -dt;
  if (dt > approach || elapsed >= n.hold + .12) return null;
  const z = n.count > 1 && dt < 0 ? (elapsed <= n.hold ? -elapsed / n.hold : -1 - 20 / approach * (elapsed-n.hold)) : 20 / approach * dt;
  const remaining = elapsed < 0 ? n.count : n.count <= 1 ? 0 : Math.max(0, n.count - 1 - Math.floor((elapsed + .000001) / Math.max(.001,n.hold/(n.count-1))));
  return { z, remaining, shrink: elapsed > n.hold ? clamp(1-(elapsed-n.hold)/.12,0,1) : 1 };
}

// Unity の確認カメラと同じ位置・俯角・画角。縦画面でも判定面を画面内に収める。
export function project(x, y, z, w, h) {
  const theta = 12 * Math.PI / 180;
  const cy = (y-2.35)*Math.cos(theta) + (z+7)*Math.sin(theta);
  const cz = -(y-2.35)*Math.sin(theta) + (z+7)*Math.cos(theta);
  const focal = Math.min(h, w / 1.45) / (2*Math.tan(Math.PI/6));
  return {x:w/2+x*focal/cz, y:h/2-cy*focal/cz, scale:focal/cz};
}

export function chartStats(chart) {
  return { count:chart.notes.length, flicks:chart.notes.filter(n=>n.direction && n.direction!=='none').length,
    longs:chart.notes.filter(n=>n.count>1).length, gold:chart.notes.filter(n=>n.color==='gold').length };
}
