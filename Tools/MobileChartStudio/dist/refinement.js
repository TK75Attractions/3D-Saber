import { audioTime, clone, COLORS, DIRECTIONS, longSeconds, quantizeMs, snapInputMs, clamp } from './core.js';

const round = (value, places = 6) => +value.toFixed(places);
const isLong = note => note.count > 1 || note.type === 'long';
const signature = note => JSON.stringify(Object.fromEntries(Object.entries(note).filter(([key]) => key !== '__editorId').sort(([a], [b]) => a.localeCompare(b))));
export function notesInScope(chart, scope = {}) {
  return chart.notes.filter(note => (scope.color === undefined || scope.color === 'all' || note.color === scope.color)
    && (scope.start === undefined || audioTime(note, chart) >= scope.start)
    && (scope.end === undefined || audioTime(note, chart) < scope.end));
}

// 先頭ノーツからの差でまとめ、近いノーツが連鎖して別の拍まで巻き込まない。
export function chordGroups(notes, toleranceMs = 0) {
  if (!Number.isFinite(toleranceMs) || toleranceMs < 0 || toleranceMs > 50) throw new Error('同時押しの許容幅は0〜50msで指定してください。');
  const groups = [];
  for (const note of [...notes].sort((a, b) => a.time - b.time)) {
    const previous = groups.at(-1);
    if (previous && note.time - previous[0].time <= toleranceMs + 1e-7) previous.push(note);
    else groups.push([note]);
  }
  return groups.filter(group => group.length > 1);
}

export function refineChart(chart, scope, operation, duration) {
  const result = clone(chart), selected = notesInScope(result, scope);
  if (!selected.length) throw new Error('対象のノーツがありません。');
  const before = new Map(selected.map(note => [note.__editorId, signature(note)]));
  const number = (value, name) => { if (!Number.isFinite(value)) throw new Error(`${name}を数値で指定してください。`); return value; };
  const moveTime = (note, time) => { const delta = time - note.time; note.time = round(time, 3); note.beat = round(note.beat + delta * chart.bpm / 60000); };
  let removed = new Set();
  switch (operation.kind) {
    case 'shift':
      number(operation.ms, '移動量');
      for (const note of selected) moveTime(note, note.time + operation.ms);
      break;
    case 'height':
    case 'counts':
    case 'symmetry':
      for (const group of chordGroups(selected, operation.toleranceMs ?? 0)) {
        if (operation.kind === 'height') {
          const height = round(group.reduce((sum, note) => sum + note.y, 0) / group.length);
          for (const note of group) note.y = height;
        } else if (operation.kind === 'counts') {
          const longs = group.filter(isLong);
          if (longs.length < 2) continue;
          const count = Math.max(...longs.map(note => note.count));
          for (const note of longs) { if (note.count !== count && !note.lengthMs) note.lengthMs = longSeconds(note) * 1000; note.count = count; }
        } else {
          const hands = group.filter(note => note.color === 'blue' || note.color === 'red');
          if (!hands.some(note => note.color === 'blue') || !hands.some(note => note.color === 'red')) continue;
          const radius = round(hands.reduce((sum, note) => sum + Math.abs(note.x), 0) / hands.length);
          for (const note of hands) note.x = note.color === 'blue' ? -radius : radius;
        }
      }
      break;
    case 'snap':
      if (!(operation.step > 0) || !Number.isFinite(operation.step)) throw new Error('拍の刻みを選んでください。');
      for (const note of selected) moveTime(note, snapInputMs(note.time, chart, {snap: operation.step, snapMode: 'near', snapWindowMs: operation.windowMs ?? 35, rate: 1}));
      break;
    case 'ends':
      if (!(operation.step > 0) || !Number.isFinite(operation.step)) throw new Error('拍の刻みを選んでください。');
      for (const note of selected.filter(isLong)) {
        const end = quantizeMs(note.time + longSeconds(note) * 1000, chart, operation.step);
        if (end <= note.time) throw new Error('拍にそろえると長さが0になります。細かい刻みを選んでください。');
        note.lengthMs = round(end - note.time, 3);
      }
      break;
    case 'delete': removed = new Set(selected.map(note => note.__editorId)); break;
    case 'dedupe': {
      const seen = new Set();
      for (const note of selected) { const key = signature(note); if (seen.has(key)) removed.add(note.__editorId); else seen.add(key); }
      break;
    }
    case 'color':
      if (!Object.hasOwn(COLORS, operation.color)) throw new Error('色を選んでください。');
      for (const note of selected) note.color = operation.color;
      break;
    case 'direction':
      if (!DIRECTIONS.includes(operation.direction)) throw new Error('方向を選んでください。');
      for (const note of selected) { note.direction = operation.direction; note.type = isLong(note) ? 'long' : note.direction === 'none' ? 'tap' : 'direction'; }
      break;
    case 'longCount':
      if (!Number.isInteger(operation.count) || operation.count < 2 || operation.count > 99) throw new Error('ロングの回数は2〜99で指定してください。');
      for (const note of selected.filter(isLong)) { if (note.count !== operation.count && !note.lengthMs) note.lengthMs = longSeconds(note) * 1000; note.count = operation.count; }
      break;
    case 'position':
      number(operation.x, 'X移動量'); number(operation.y, 'Y移動量');
      for (const note of selected) { note.x = round(note.x + operation.x / chart.coordScale); note.y = round(note.y + operation.y / chart.coordScale); }
      break;
    case 'vertical': {
      const directions = {up: 'down', down: 'up', upleft: 'downleft', upright: 'downright', downleft: 'upleft', downright: 'upright'};
      for (const note of selected) { note.y = -note.y; note.direction = directions[note.direction] || note.direction; }
      break;
    }
    default: throw new Error('編集操作を選んでください。');
  }
  // 移動系の操作で音源外へ出る場合は、切り詰めず全体を中止する。
  if (['shift', 'snap', 'ends'].includes(operation.kind)) {
    if (!(duration > 0) || !Number.isFinite(duration)) throw new Error('音源を開いてください。');
    for (const note of selected) if (audioTime(note, result) < -1e-7 || audioTime(note, result) + longSeconds(note) > duration + 1e-7) throw new Error('ノーツやロング終端が音源の外へ出ます。変更量または対象区間を調整してください。');
  }
  if (operation.kind === 'position') for (const note of selected) if (Math.abs(note.x * chart.coordScale) > 2.5 || Math.abs(note.y * chart.coordScale) > 1.5) throw new Error('移動すると入力パッドの外へ出ます。移動量を小さくしてください。');
  const count = selected.filter(note => removed.has(note.__editorId) || before.get(note.__editorId) !== signature(note)).length;
  result.notes = result.notes.filter(note => !removed.has(note.__editorId)).sort((a, b) => a.time - b.time);
  return {chart: result, count, targetCount: selected.length};
}

export function chartReport(chart, duration) {
  const counts = {blue: 0, red: 0, gold: 0, default: 0, tap: 0, flick: 0, long: 0}, issues = [], bucketWidth = 10;
  const bucketCount = Math.max(1, Math.ceil(Math.max(0, duration) / bucketWidth));
  const buckets = Array.from({length: bucketCount}, (_, i) => ({start: i * bucketWidth, count: 0}));
  const notes = [...chart.notes].sort((a, b) => a.time - b.time), seen = new Set(), lastHand = {}, heldHands = {};
  let peak = 0, peakStart = 0, first = 0, issueCount = 0;
  const issue = (note, text) => { issueCount++; if (issues.length < 200) issues.push({id: note.__editorId, time: audioTime(note, chart), text}); };
  for (let index = 0; index < notes.length; index++) {
    const note = notes[index], time = audioTime(note, chart), end = time + longSeconds(note);
    counts[note.color]++; counts[isLong(note) ? 'long' : note.direction !== 'none' ? 'flick' : 'tap']++;
    if (time >= 0 && time < duration) buckets[Math.min(buckets.length - 1, Math.floor(time / bucketWidth))].count++;
    while (notes[index].time - notes[first].time >= 2000) first++;
    if (index - first + 1 > peak) { peak = index - first + 1; peakStart = audioTime(notes[first], chart); }
    if (time < 0 || end > duration + .001) issue(note, '音源の外に開始・終了があります');
    if (Math.abs(note.x * chart.coordScale) > 2.5 || Math.abs(note.y * chart.coordScale) > 1.5) issue(note, '入力パッドの外に配置されています');
    const key = signature(note); if (seen.has(key)) issue(note, '同じノーツが完全に重複しています'); seen.add(key);
    if (note.color === 'blue' || note.color === 'red') {
      const previous = lastHand[note.color], hold = heldHands[note.color];
      if (hold && hold.end > time + .001) issue(note, '同じ色のロングが続いている間に次のノーツがあります');
      if (previous && time - previous.time < .1 && Math.hypot(note.x - previous.note.x, note.y - previous.note.y) * chart.coordScale > 2) issue(note, '同じ手が100ms未満で大きく移動します');
      lastHand[note.color] = {note, time};
      if (isLong(note) && (!hold || end > hold.end)) heldHands[note.color] = {end};
    }
  }
  return {counts, buckets, issues, issueCount, average: duration > 0 ? notes.length / duration : 0, peakNps: peak / 2, peakStart};
}

export function noteSeek(chart, time, direction, duration) {
  const times = chart.notes.map(note => audioTime(note, chart)).sort((a, b) => a - b);
  return direction < 0 ? Math.max(0, times.filter(value => value < time - .001).at(-1) ?? 0) : Math.min(duration, times.find(value => value > time + .001) ?? duration);
}
export function beatSeek(chart, time, direction, duration) {
  const origin = (chart.beatZeroMs + chart.offsetMs) / 1000, beat = (time - origin) * chart.bpm / 60;
  return clamp(origin + (direction < 0 ? Math.ceil(beat - 1e-7) - 1 : Math.floor(beat + 1e-7) + 1) * 60 / chart.bpm, 0, duration);
}
export function chartWithBeatOrigin(chart, audioSeconds) {
  if (!Number.isFinite(audioSeconds) || audioSeconds < 0) throw new Error('音源内の再生位置を指定してください。');
  const result = clone(chart), origin = round(audioSeconds * 1000 - chart.offsetMs, 3);
  for (const note of result.notes) note.beat = round(note.beat + (result.beatZeroMs - origin) * result.bpm / 60000);
  result.beatZeroMs = origin;
  return result;
}
export function positionInput(x, y, step = 0) {
  if (![.25, .5, 1].includes(step)) return {x, y};
  return {x: clamp(round(Math.round(x / step) * step), -2.5, 2.5), y: clamp(round(Math.round(y / step) * step), -1.5, 1.5)};
}
export function safeBookmarks(value, duration = Infinity) {
  if (!Array.isArray(value)) return [];
  return value.filter(item => item && typeof item.name === 'string' && Number.isFinite(item.time) && item.time >= 0 && item.time <= duration).slice(0, 100).map(item => ({name: item.name.trim().slice(0, 60) || '目印', time: round(item.time, 3)})).sort((a, b) => a.time - b.time);
}
export function filterProjects(projects, query) {
  const text = query.trim().normalize('NFKC').toLocaleLowerCase();
  return projects.filter(project => `${project.name} ${project.difficulty}`.normalize('NFKC').toLocaleLowerCase().includes(text));
}
