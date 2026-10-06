import { exportChart, parseChart, DIRECTIONS, clamp } from './core.js';
import { inputPreferences } from './gestures.js';
const MAGIC = new TextEncoder().encode('SABERSTUDIO1\n');
const MAX_META = 32 * 1024 * 1024, MAX_AUDIO = 100 * 1024 * 1024;
const invalid = () => new Error('Saber Tap Studioのバックアップを選んでください。ファイルが途中で壊れていないか確認してください。');
function safeSettings(value) {
  const s=value&&typeof value==='object'?value:{};
  return {...inputPreferences(s),rate:[1,.75,.5].includes(s.rate)?s.rate:1,
    longCount:['auto','2','3','4','6','8'].includes(String(s.longCount))?String(s.longCount):'auto',direction:DIRECTIONS.includes(s.direction)?s.direction:'none',
    color:['auto','blue','red','gold'].includes(s.color)?s.color:'auto',latencyMs:Number.isFinite(s.latencyMs)?clamp(s.latencyMs,-500,500):0,
    countIn:typeof s.countIn==='boolean'?s.countIn:true,clickSound:typeof s.clickSound==='boolean'?s.clickSound:true,metronome:typeof s.metronome==='boolean'?s.metronome:false};
}

export function createBackup(project, source) {
  if (!source?.blob || source.blob.size > MAX_AUDIO || !source.blob.size) throw new Error('バックアップする音源が見つかりません。');
  const metadata = {version:1, name:project.name, difficulty:project.difficulty, position:project.position, settings:project.settings || {}, chart:exportChart(project.chart), audio:{name:source.name, type:source.blob.type, size:source.blob.size}};
  const bytes = new TextEncoder().encode(JSON.stringify(metadata));
  if (bytes.length > MAX_META) throw new Error('譜面情報が大きすぎてバックアップできません。');
  const length = new Uint8Array(4);new DataView(length.buffer).setUint32(0, bytes.length, true);
  // 音源をBase64に変換せず、元のバイナリのまま格納する。
  return new Blob([MAGIC, length, bytes, source.blob], {type:'application/octet-stream'});
}

export async function readBackup(file) {
  const headerSize = MAGIC.length + 4;
  if (!file || file.size < headerSize || file.size > headerSize + MAX_META + MAX_AUDIO) throw invalid();
  const header = new Uint8Array(await file.slice(0, headerSize).arrayBuffer());
  if (!MAGIC.every((value, i) => header[i] === value)) throw invalid();
  const length = new DataView(header.buffer).getUint32(MAGIC.length, true);
  if (!length || length > MAX_META || headerSize + length >= file.size) throw invalid();
  let metadata;
  try { metadata = JSON.parse(await file.slice(headerSize, headerSize + length).text()); } catch { throw invalid(); }
  if (metadata?.version !== 1 || typeof metadata.name !== 'string' || !['easy','normal','hard'].includes(metadata.difficulty) || !metadata.audio || !Number.isSafeInteger(metadata.audio.size) || metadata.audio.size <= 0 || metadata.audio.size > MAX_AUDIO || metadata.audio.size !== file.size - headerSize - length) throw invalid();
  const chart = parseChart(metadata.chart);
  const blob = file.slice(headerSize + length, file.size, typeof metadata.audio.type === 'string' ? metadata.audio.type.slice(0,100) : '');
  return {name:metadata.name.slice(0,120) || '復元した曲', difficulty:metadata.difficulty, position:Number.isFinite(metadata.position) ? Math.max(0,metadata.position) : 0, settings:safeSettings(metadata.settings), chart, audio:{name:typeof metadata.audio.name === 'string' ? metadata.audio.name.slice(0,240) : 'audio', blob}};
}
