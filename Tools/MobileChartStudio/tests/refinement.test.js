import test from 'node:test';
import assert from 'node:assert/strict';
import { blankChart, parseChart, exportChart, longSeconds, History } from '../dist/core.js';
import { refineChart, chordGroups, chartReport, noteSeek, beatSeek, chartWithBeatOrigin, positionInput, safeBookmarks, filterProjects } from '../dist/refinement.js';
import { createBackup, readBackup } from '../dist/backup.js';
import { SongAudio } from '../dist/audio.js';

const note = (time, overrides = {}) => ({time, x:-1, y:0, color:'blue', ...overrides});
const chart = (notes, overrides = {}) => parseChart({...blankChart(), ...overrides, notes});
const apply = (value, operation, scope = {}, duration = 30) => refineChart(value,scope,operation,duration);

test('whole-song -110ms correction preserves layout, holds, offsets and variable beat differences and undoes as one edit', () => {
  const original = chart([note(1000,{beat:8}),note(2000,{count:4,lengthMs:1200,type:'long'})],{offsetMs:20,beatZeroMs:50});
  const history = new History(); history.push(original);
  const result = apply(original,{kind:'shift',ms:-110});
  assert.equal(result.count,2); assert.deepEqual(result.chart.notes.map(n=>n.time),[890,1890]);
  assert.equal(result.chart.notes[0].beat,7.78); assert.equal(result.chart.notes[1].lengthMs,1200); assert.equal(result.chart.offsetMs,20);
  assert.deepEqual(history.undo(result.chart),original); assert.equal(original.notes[0].time,1000);
});
test('audio bounds reject an entire timing change, including implicit hold tails', () => {
  const original = chart([note(50),note(1000)]);
  assert.throws(()=>apply(original,{kind:'shift',ms:-110}),/音源の外/); assert.equal(original.notes[0].time,50);
  assert.throws(()=>apply(chart([note(28000,{count:5,type:'long'})]),{kind:'shift',ms:10}),/音源の外/);
});
test('simultaneous height averages groups while tolerance does not chain into later notes', () => {
  const original = chart([note(1000,{y:-.5}),note(1010,{color:'red',y:.7}),note(1020,{y:1})]);
  const result = apply(original,{kind:'height',toleranceMs:10});
  assert.deepEqual(result.chart.notes.map(n=>n.y),[.1,.1,1]); assert.equal(result.count,2);
  assert.equal(chordGroups(original.notes,0).length,0);
  assert.throws(()=>chordGroups(original.notes,51),/許容幅/);
});
test('simultaneous long counts use maximum without changing tap counts or implicit hold duration', () => {
  const original = chart([note(1000,{count:3,type:'long'}),note(1000,{count:5,type:'long',lengthMs:4000,color:'red'}),note(1000,{color:'gold'})]);
  const result = apply(original,{kind:'counts'});
  assert.deepEqual(result.chart.notes.map(n=>n.count),[5,5,1]); assert.equal(longSeconds(result.chart.notes[0]),1.4); assert.equal(result.chart.notes[1].lengthMs,4000);
});
test('center symmetry pairs blue and red, leaves gold and unpaired hits alone', () => {
  const original = chart([note(1000,{x:-.7}),note(1000,{x:1.3,color:'red'}),note(1000,{x:.1,color:'gold'}),note(2000,{x:-.2})],{coordScale:2});
  const result = apply(original,{kind:'symmetry'});
  assert.deepEqual(result.chart.notes.map(n=>n.x),[-1,1,.1,-.2]); assert.equal(result.count,2);
  assert.deepEqual(result.chart.notes.map(n=>n.time),original.notes.map(n=>n.time));
});
test('near-beat correction changes only close starts and keeps long duration', () => {
  const original = chart([note(520),note(560),note(1010,{count:3,type:'long',lengthMs:800})]);
  const result = apply(original,{kind:'snap',step:.25,windowMs:35});
  assert.deepEqual(result.chart.notes.map(n=>n.time),[500,560,1000]); assert.equal(result.chart.notes[2].lengthMs,800);
});
test('hold-end snapping keeps the start fixed and rejects an endpoint beyond the song', () => {
  const original = chart([note(1000,{count:3,type:'long',lengthMs:620}),note(2000)]);
  const result = apply(original,{kind:'ends',step:.25});
  assert.equal(result.chart.notes[0].lengthMs,625); assert.equal(result.chart.notes[0].time,1000); assert.equal(result.count,1);
  assert.throws(()=>apply(original,{kind:'ends',step:1},{},1.9),/音源の外/);
});
test('range deletion honors offset, color and half-open A/B boundaries', () => {
  const original = chart([note(900),note(1400,{color:'red'}),note(1900)],{offsetMs:100});
  const result = apply(original,{kind:'delete'},{start:1,end:2,color:'blue'});
  assert.deepEqual(result.chart.notes.map(n=>n.time),[1400,1900]); assert.equal(result.count,1);
});
test('exact deduplication retains simultaneous opposite hands, differing metadata and differing beats', () => {
  const original = chart([note(1000),note(1000),note(1000,{color:'red'}),note(1000,{custom:'keep'}),note(1000,{beat:99})]);
  const result = apply(original,{kind:'dedupe'});
  assert.equal(result.count,1); assert.equal(result.chart.notes.length,4); assert.equal(original.notes.length,5);
});
test('bulk color and direction edits only selected starts and preserve long semantics', () => {
  const original = chart([note(1000),note(2000,{type:'long',count:4,lengthMs:2000})]);
  const recolored = apply(original,{kind:'color',color:'gold'},{start:1,end:2});
  assert.deepEqual(recolored.chart.notes.map(n=>n.color),['gold','blue']);
  const directed = apply(original,{kind:'direction',direction:'upright'});
  assert.deepEqual(directed.chart.notes.map(n=>n.type),['direction','long']);
  const cleared = apply(directed.chart,{kind:'direction',direction:'none'});
  assert.deepEqual(cleared.chart.notes.map(n=>n.type),['tap','long']);
});
test('bulk long count keeps duration and ignores taps', () => {
  const original = chart([note(1000),note(2000,{type:'long',count:4})]);
  const result = apply(original,{kind:'longCount',count:9});
  assert.deepEqual(result.chart.notes.map(n=>n.count),[1,9]); assert.equal(longSeconds(result.chart.notes[1]),longSeconds(original.notes[1]));
  assert.throws(()=>apply(original,{kind:'longCount',count:1}),/2〜99/);
});
test('position translation uses screen coordinates and rejects off-screen motion without partial edits', () => {
  const original = chart([note(1000,{x:-.5,y:.3}),note(2000,{x:.5,y:.1})],{coordScale:2});
  const result = apply(original,{kind:'position',x:.4,y:.2});
  assert.deepEqual(result.chart.notes.map(n=>[n.x,n.y]),[[-.3,.4],[.7,.2]]);
  assert.throws(()=>apply(original,{kind:'position',x:2,y:0}),/入力パッドの外/); assert.equal(original.notes[0].x,-.5);
});
test('vertical reflection changes position and diagonal arrows but not color, time or horizontal direction', () => {
  const original = chart([note(1000,{y:.8,direction:'upleft',type:'direction'}),note(2000,{y:-.4,direction:'right',type:'direction'})]);
  const result = apply(original,{kind:'vertical'});
  assert.deepEqual(result.chart.notes.map(n=>[n.y,n.direction]),[[-.8,'downleft'],[.4,'right']]);
  assert.deepEqual(exportChart(apply(result.chart,{kind:'vertical'}).chart),exportChart(original));
});
test('diagnostic identifies out-of-audio, out-of-pad, duplicate and same-hand hold collisions without changing notes', () => {
  const original = chart([note(-20),note(1000,{count:4,type:'long',lengthMs:2000}),note(1500,{x:3}),note(1500,{x:3}),note(29000,{count:4,type:'long',lengthMs:2000})]);
  const before = JSON.stringify(original), report = chartReport(original,30);
  for (const text of ['音源の外','入力パッドの外','完全に重複','ロングが続いて']) assert.ok(report.issues.some(item=>item.text.includes(text)),text);
  assert.equal(JSON.stringify(original),before); assert.ok(report.issues.every(item=>item.id));
});
test('statistics count types and colors, localize 2-second density, and include silent buckets', () => {
  const original = chart([note(1000),note(1000,{color:'red',direction:'up'}),note(1200,{count:4,type:'long',lengthMs:2000}),note(21000,{color:'gold'})]);
  const report = chartReport(original,30);
  assert.deepEqual(report.buckets.map(x=>x.count),[3,0,1]); assert.equal(report.peakNps,1.5); assert.equal(report.peakStart,1); assert.equal(report.counts.flick,1); assert.equal(report.counts.long,1); assert.equal(report.counts.blue,2);
});
test('previous/next note navigation skips simultaneous duplicates and clamps song bounds', () => {
  const value = chart([note(1000),note(1000,{color:'red'}),note(2000)],{offsetMs:100});
  assert.equal(noteSeek(value,1.1,1,30),2.1); assert.equal(noteSeek(value,2.1,-1,30),1.1); assert.equal(noteSeek(value,0,-1,30),0); assert.equal(noteSeek(value,10,1,30),30);
});
test('beat seek follows chart origin and offset, including a current exact beat', () => {
  const value = chart([],{bpm:120,beatZeroMs:200,offsetMs:100});
  assert.equal(beatSeek(value,.8,1,10),1.3); assert.equal(beatSeek(value,.8,-1,10),.3); assert.equal(beatSeek(value,.81,-1,10),.8); assert.equal(beatSeek(value,0,-1,10),0);
});
test('position grid is optional, supports scaled spacing, clamps edge snaps and preserves freehand', () => {
  assert.deepEqual(positionInput(-.71,.28,.5),{x:-.5,y:.5}); assert.deepEqual(positionInput(-.71,.28,0),{x:-.71,y:.28}); assert.deepEqual(positionInput(2.49,1.49,1),{x:2,y:1});
});
test('setting beat origin uses audio position minus offset and preserves notes and relative beat timing', () => {
  const value=chart([note(1000,{beat:1.6}),note(2000,{beat:3.6})],{bpm:120,beatZeroMs:200,offsetMs:100});
  const result=chartWithBeatOrigin(value,.6);
  assert.equal(result.beatZeroMs,500); assert.deepEqual(result.notes.map(n=>n.beat),[1,3]); assert.deepEqual(result.notes.map(n=>n.time),[1000,2000]); assert.equal(value.beatZeroMs,200);
});
test('bookmarks validate, sort and preserve Japanese titles through original binary audio backup', async () => {
  const bookmarks = safeBookmarks([{name:'サビ',time:12.12345},{name:' A ',time:2},{name:'bad',time:-1},null],20);
  assert.deepEqual(bookmarks,[{name:'A',time:2},{name:'サビ',time:12.123}]);
  const settings = {songVolume:.35,feedbackVolume:.7,positionGrid:.5,previewLoop:true};
  const source = {name:'製作中.mp3',blob:new Blob([new Uint8Array([1,255,0,128])],{type:'audio/mpeg'})};
  const value = await readBackup(createBackup({name:'製作中',difficulty:'hard',chart:chart([]),bookmarks,settings},source));
  assert.deepEqual(value.bookmarks,bookmarks); for (const [key,expected] of Object.entries(settings)) assert.equal(value.settings[key],expected);
  assert.deepEqual(new Uint8Array(await value.audio.blob.arrayBuffer()),new Uint8Array([1,255,0,128]));
});
test('draft search is case and full-width tolerant and searches difficulty as well as title', () => {
  const projects = [{name:'製作中',difficulty:'hard'},{name:'TEST',difficulty:'normal'}];
  assert.deepEqual(filterProjects(projects,' ＨＡＲＤ '),[projects[0]]); assert.deepEqual(filterProjects(projects,'test'),[projects[1]]); assert.equal(filterProjects(projects,'').length,2);
});
test('song and feedback gain controls remain independent and can mute output without affecting the clock', () => {
  const value = new SongAudio(); value.songGain = {gain:{value:1}}; value.position=3;
  value.setVolumes(.35,.7); assert.equal(value.songGain.gain.value,.35); assert.equal(value.feedbackVolume,.7); assert.equal(value.position,3);
  value.setVolumes(-2,3); assert.equal(value.songVolume,0); assert.equal(value.feedbackVolume,1);
  value.setVolumes(1,0); value.context={state:'running',createOscillator:()=>assert.fail('muted feedback should not create oscillator')}; value.click();
});
