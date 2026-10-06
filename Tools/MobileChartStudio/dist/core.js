// 音源時刻を正とし、offsetMs は保存時に一度だけ取り除く。
export const COLORS = { blue: '#64d2ff', red: '#ff6285', gold: '#ffd468', default: '#dee7fa' };
export const DIRECTIONS = ['none', 'up', 'down', 'left', 'right', 'upleft', 'upright', 'downleft', 'downright'];
export const clamp = (v, min, max) => Math.max(min, Math.min(max, v));
export const clone = value => JSON.parse(JSON.stringify(value));
let sequence = 0;
export const noteId = () => `n${Date.now().toString(36)}-${++sequence}`;
export function finite(value, name, fallback) {
  if (value === undefined && fallback !== undefined) return fallback;
  if (typeof value !== 'number' || !Number.isFinite(value)) throw new Error(`${name}は数値で指定してください。`);
  return value;
}
export function blankChart() {
  return { bpm: 120, coordScale: 1, offsetMs: 0, beatZeroMs: 0, displayLevel: 0, timeSignatures: [], notes: [] };
}
export function parseChart(input) {
  const source = typeof input === 'string' ? JSON.parse(input.replace(/^\uFEFF/, '')) : clone(input);
  if (!source || typeof source !== 'object' || Array.isArray(source) || !Array.isArray(source.notes)) throw new Error('notes配列のある3D-Saberの譜面JSONを選んでください。');
  if (source.notes.length > 50000) throw new Error('ノーツ数は50,000個までです。');
  const chart = { ...blankChart(), ...source };
  chart.bpm = finite(chart.bpm, 'BPM');
  chart.coordScale = finite(chart.coordScale, '座標スケール');
  if (chart.bpm <= 0 || chart.bpm > 1000 || chart.coordScale <= 0) throw new Error('BPMと座標スケールは正の値が必要です。');
  for (const key of ['offsetMs', 'beatZeroMs', 'displayLevel']) finite(chart[key], key);
  if (!Array.isArray(chart.timeSignatures)) throw new Error('拍子情報の形式を確認してください。');
  for (const meter of chart.timeSignatures) {
    finite(meter.beat, '拍子の開始拍');
    if (!Number.isInteger(meter.numerator) || meter.numerator < 1 || ![1,2,4,8,16,32,64].includes(meter.denominator)) throw new Error('拍子情報の分子・分母を確認してください。');
  }
  chart.notes = chart.notes.map((n, i) => {
    if (!n || typeof n !== 'object' || Array.isArray(n)) throw new Error(`${i + 1}個目のノーツが不正です。`);
    const note = { type: 'tap', color: 'default', direction: 'none', count: 1, lengthMs: 0, ...n, __editorId: noteId() };
    for (const key of ['time', 'x', 'y']) finite(note[key], `${i + 1}個目の${key}`);
    note.beat = finite(note.beat, '拍', (note.time - chart.beatZeroMs) / (60000 / chart.bpm));
    if (!Object.hasOwn(COLORS, note.color) || !DIRECTIONS.includes(note.direction) || !['tap', 'direction', 'long'].includes(note.type)) throw new Error(`${i + 1}個目の色・方向・種類が未対応です。`);
    if (!Number.isInteger(note.count) || note.count < 1 || note.count > 99 || finite(note.lengthMs, '長さ') < 0) throw new Error(`${i + 1}個目の回数・長さを確認してください。`);
    return note;
  }).sort((a,b) => a.time - b.time);
  return chart;
}
export function exportChart(chart) {
  const result = clone(chart);
  result.notes.sort((a,b) => a.time - b.time);
  for (const note of result.notes) delete note.__editorId;
  return result;
}
export function audioTime(note, chart) { return (note.time + chart.offsetMs) / 1000; }
export function longSeconds(note) { return note.count > 1 || note.type === 'long' ? (note.lengthMs / 1000 || (note.count - 1) * .7) : 0; }
export function quantizeMs(rawMs, chart, step = 0) {
  if (!(step > 0)) return rawMs;
  const tick = (60000 / chart.bpm) * step;
  return chart.beatZeroMs + Math.round((rawMs - chart.beatZeroMs) / tick) * tick;
}
export function makeNote(gesture, endAudio, chart, options) {
  const rate = options.rate || 1;
  const correction = (options.latencyMs || 0) * rate;
  const startAudioMs = Math.max(0, gesture.startAudio * 1000 - correction);
  const raw = quantizeMs(startAudioMs - chart.offsetMs, chart, options.snap);
  const time = Math.max(-chart.offsetMs, raw);
  const end = quantizeMs(Math.max(startAudioMs, endAudio * 1000 - correction) - chart.offsetMs, chart, options.snap);
  const held = (endAudio - gesture.startAudio) / rate >= .24;
  const lengthMs = held ? Math.max(50, end - time) : 0;
  const automatic = clamp(1 + Math.round(lengthMs / (60000 / chart.bpm)), 2, 99);
  const count = held ? (options.longCount === 'auto' ? automatic : clamp(Number(options.longCount), 2, 99)) : 1;
  const direction = options.direction || 'none';
  const color = options.color === 'auto' ? (gesture.x < 0 ? 'blue' : 'red') : options.color;
  return { __editorId: noteId(), time: +time.toFixed(3), beat: +((time - chart.beatZeroMs) / (60000 / chart.bpm)).toFixed(6),
    x: +(gesture.x / chart.coordScale).toFixed(6), y: +(gesture.y / chart.coordScale).toFixed(6),
    color, direction, type: held ? 'long' : direction !== 'none' ? 'direction' : 'tap', count, lengthMs: +lengthMs.toFixed(3) };
}
export function hasVariableGrid(chart) {
  return chart.notes.filter(n => Math.abs(n.time - (chart.beatZeroMs + n.beat * 60000 / chart.bpm)) > 80).length > Math.max(2, chart.notes.length * .12);
}
export function formatTime(seconds, precise = false) {
  const ms = Math.round(Math.max(0, seconds) * 1000);
  return `${Math.floor(ms / 60000)}:${String(Math.floor(ms / 1000) % 60).padStart(2, '0')}${precise ? '.' + String(ms % 1000).padStart(3, '0') : ''}`;
}
// Undoは譜面単位。音源Blobは履歴へ複製しない。
export class History {
  constructor() { this.past = []; this.future = []; }
  push(chart) { this.past.push(clone(chart)); if (this.past.length > 80) this.past.shift(); this.future = []; }
  undo(chart) { if (!this.past.length) return chart; this.future.push(clone(chart)); return this.past.pop(); }
  redo(chart) { if (!this.future.length) return chart; this.past.push(clone(chart)); return this.future.pop(); }
}
