import { audioTime, clamp, clone } from './core.js';

// 区間は [開始, 終了)。途中停止では再生した部分だけを置き換える。
// 一度も入力しなかった録音と、カウント中の中断は既存ノーツを消さない。
export class RecordingTake {
  constructor(chart, { start, end, replace = false }) {
    this.before = clone(chart);
    this.start = start;
    this.end = end;
    this.replace = replace;
    this.notes = [];
    this.remembered = false;
  }
  add(note) {
    const result = clone(note), originalTime = audioTime(result, this.before);
    const time = clamp(originalTime, this.start, Math.max(this.start, this.end - .001));
    result.time = +(time * 1000 - this.before.offsetMs).toFixed(3);
    result.beat = +((result.time - this.before.beatZeroMs) / (60000 / this.before.bpm)).toFixed(6);
    result.lengthMs = Math.max(0, Math.min(result.lengthMs, (this.end - time) * 1000));
    if (result.count > 1 && result.lengthMs < 1) { result.count = 1; result.type = result.direction === 'none' ? 'tap' : 'direction'; result.lengthMs = 0; }
    this.notes.push(result);
    return result;
  }
  compose(through) {
    const result = clone(this.before), until = clamp(through, this.start, this.end);
    if (this.replace && this.notes.length) result.notes = result.notes.filter(n => audioTime(n, result) < this.start || audioTime(n, result) >= until);
    result.notes.push(...clone(this.notes));
    result.notes.sort((a, b) => a.time - b.time);
    return result;
  }
}

// 数回の手打ちのばらつきからテンポを推定。既存ノーツの時刻は変更しない。
export class TapTempo {
  constructor() { this.reset(); }
  reset() { this.times = []; }
  tap(now) {
    const previous = this.times.at(-1);
    if (previous !== undefined && (now - previous > 3200 || now <= previous)) this.reset();
    if (this.times.length && now - this.times.at(-1) < 120) return this.result();
    this.times.push(now);
    this.times = this.times.slice(-9);
    return this.result();
  }
  result() {
    const intervals = this.times.slice(1).map((t, i) => t - this.times[i]);
    const sorted = [...intervals].sort((a, b) => a - b), middle = sorted[Math.floor(sorted.length / 2)];
    const stable = intervals.filter(t => Math.abs(t - middle) <= middle * .25);
    const bpm = stable.length >= 3 ? Math.round(60000 / (stable.reduce((a, b) => a + b, 0) / stable.length) * 10) / 10 : null;
    return { count: this.times.length, bpm: bpm >= 20 && bpm <= 400 ? bpm : null };
  }
}
