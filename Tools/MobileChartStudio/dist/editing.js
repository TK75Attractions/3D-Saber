import { audioTime, clamp, clone, noteId, longSeconds, parseChart, exportChart } from './core.js';

export function rangeNotes(chart, {start, end, color = 'all'}) {
  if (![start, end].every(Number.isFinite) || start < 0 || end <= start) throw new Error('開始Aと終了Bを確認してください。');
  return chart.notes.filter(n => audioTime(n, chart) >= start && audioTime(n, chart) < end && (color === 'all' || n.color === color));
}

// 変更後の全ノーツを検証してから返す。途中まで適用した譜面は作らない。
export function transformRange(chart, range, operation, duration) {
  const selected = rangeNotes(chart, range);
  if (!selected.length) throw new Error('この区間に対象のノーツがありません。');
  if (!['shift', 'copy', 'mirror'].includes(operation.kind)) throw new Error('未対応の区間操作です。');
  if (!Number.isFinite(duration) || duration <= 0) throw new Error('音源を開いてください。');
  if (operation.kind !== 'mirror' && !Number.isFinite(operation.value)) throw new Error('移動量・コピー先を数値で入力してください。');
  if (operation.kind === 'copy' && chart.notes.length + selected.length > 50000) throw new Error('コピーするとノーツ数の上限を超えます。');
  const deltaMs = operation.kind === 'shift' ? operation.value : operation.kind === 'copy' ? (operation.value - range.start) * 1000 : 0;
  const mirrored = {left:'right', right:'left', upleft:'upright', upright:'upleft', downleft:'downright', downright:'downleft'};
  const modified = selected.map(original => {
    const note = clone(original);
    if (operation.kind === 'mirror') {
      note.x = -note.x;
      note.direction = mirrored[note.direction] || note.direction;
      if (operation.swapColors) note.color = note.color === 'blue' ? 'red' : note.color === 'red' ? 'blue' : note.color;
    } else {
      note.time = +(note.time + deltaMs).toFixed(3);
      note.beat = +(note.beat + deltaMs * chart.bpm / 60000).toFixed(6);
      const time = audioTime(note, chart);
      if (time < 0 || time + longSeconds(note) > duration + .000001) throw new Error('ノーツやロングの終わりが音源の外に出ます。移動量・コピー先を調整してください。');
    }
    if (operation.kind === 'copy') note.__editorId = noteId();
    return note;
  });
  const result = clone(chart), replacements = new Map(modified.map(n => [n.__editorId, n]));
  result.notes = operation.kind === 'copy' ? [...result.notes, ...modified] : result.notes.map(n => replacements.get(n.__editorId) || n);
  result.notes.sort((a,b) => a.time - b.time);
  return {chart:result, count:selected.length, deltaSeconds:deltaMs/1000};
}

export function copyChart(chart, empty = false) {
  const result = parseChart(exportChart(chart));
  if (empty) result.notes = [];
  return result;
}

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
