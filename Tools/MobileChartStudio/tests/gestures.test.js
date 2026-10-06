import test from 'node:test';
import assert from 'node:assert/strict';
import {beginStroke,moveStroke,flickDirection,inputPreferences} from '../dist/gestures.js';
import {blankChart,makeNote,snapInputMs,audioTime,parseChart,exportChart} from '../dist/core.js';
import {RecordingTake} from '../dist/editing.js';
import {createBackup,readBackup} from '../dist/backup.js';
const options={rate:1,snap:.5,snapMode:'near',snapWindowMs:35,latencyMs:0,longCount:'auto',direction:'none',color:'auto',flickEnabled:true};
const gesture={startAudio:2.02,x:-1,y:.3};
const stroke=(x,y,time=120)=>moveStroke(beginStroke(100,100,1000),100+x,100+y,1000+time);

test('flicks map all eight screen directions into game directions',()=>{
  for(const [x,y,direction] of [[40,0,'right'],[40,40,'downright'],[0,40,'down'],[-40,40,'downleft'],[-40,0,'left'],[-40,-40,'upleft'],[0,-40,'up'],[40,-40,'upright']]){
    const detected=flickDirection(stroke(x,y));assert.equal(detected,direction);
    const note=makeNote({...gesture,flickDirection:detected},2.14,blankChart(),options);
    assert.equal(note.type,'direction');assert.equal(note.direction,direction);assert.equal(note.count,1);assert.equal(note.lengthMs,0);
    assert.equal(note.x,-1);assert.equal(note.y,.3);assert.equal(note.color,'blue');assert.equal(note.time,2000);
  }
});
test('jitter, slow drags, holds and return strokes remain non-flick inputs',()=>{
  assert.equal(flickDirection(stroke(8,9)),'none');
  assert.equal(flickDirection(stroke(25,0,220)),'none');
  assert.equal(flickDirection(stroke(80,0,240)),'none');
  const returning=stroke(70,0,80);moveStroke(returning,130,100,1120);assert.equal(flickDirection(returning),'none');
  moveStroke(returning,100,100,1150);assert.equal(flickDirection(returning),'none');
});
test('flick sensitivity uses CSS distance and ignores stale or invalid samples',()=>{
  assert.equal(flickDirection(stroke(20,0,80),'sensitive'),'right');
  assert.equal(flickDirection(stroke(20,0,80),'normal'),'none');
  assert.equal(flickDirection(stroke(30,0,80),'firm'),'none');
  assert.equal(flickDirection(stroke(40,0,80),'firm'),'right');
  const s=stroke(40,0);const before={...s};moveStroke(s,NaN,5,1140);moveStroke(s,1,1,999);assert.deepEqual(s,before);
  assert.equal(flickDirection(stroke(40,0,0)),'none');
});
test('flick OFF keeps manual direction; holding remains a long note at half speed',()=>{
  const g={...gesture,flickDirection:'upright'};
  const off=makeNote(g,2.12,blankChart(),{...options,flickEnabled:false,direction:'left'});
  assert.equal(off.direction,'left');assert.equal(off.type,'direction');
  const hold=makeNote(g,2.32,blankChart(),{...options,rate:.5});
  assert.equal(hold.type,'long');assert.equal(hold.direction,'none');assert.ok(hold.lengthMs>0);
  const flick=makeNote(g,2.10,blankChart(),{...options,rate:.5});assert.equal(flick.type,'direction');
});
test('two independent finger strokes retain direction, start position and color',()=>{
  const left=stroke(40,-40),right=stroke(-40,40);
  const a=makeNote({...gesture,flickDirection:flickDirection(left)},2.14,blankChart(),options);
  const b=makeNote({...gesture,x:1,flickDirection:flickDirection(right)},2.14,blankChart(),options);
  assert.equal(a.time,b.time);assert.equal(a.direction,'upright');assert.equal(b.direction,'downleft');assert.equal(a.color,'blue');assert.equal(b.color,'red');assert.notEqual(a.__editorId,b.__editorId);
});
test('near correction accepts both threshold edges but preserves off-grid timing',()=>{
  for(const delta of [-35,35])assert.equal(snapInputMs(2000+delta,blankChart(),options),2000);
  for(const delta of [-35.1,35.1,90])assert.equal(snapInputMs(2000+delta,blankChart(),options),2000+delta);
  assert.equal(snapInputMs(2020,blankChart(),{...options,snapMode:'off'}),2020);
  assert.equal(snapInputMs(2090,blankChart(),{...options,snapMode:'always'}),2000);
  assert.equal(snapInputMs(2090,blankChart(),{...options,snapMode:undefined}),2000);
});
test('near correction respects beat origin, chart offset and input latency exactly once',()=>{
  const chart={...blankChart(),offsetMs:250,beatZeroMs:100,coordScale:.5};
  const n=makeNote({...gesture,startAudio:2.392},2.45,chart,{...options,latencyMs:20});
  assert.equal(n.time,2100);assert.equal(audioTime(n,chart),2.35);assert.equal(n.x,-2);assert.equal(n.y,.6);
  const before=JSON.stringify(chart);makeNote(gesture,2.15,chart,options);assert.equal(JSON.stringify(chart),before);
});
test('slow playback preserves the real-time tolerance and dense grids keep unsnapped gaps',()=>{
  assert.equal(snapInputMs(2017.5,blankChart(),{...options,rate:.5}),2000);
  assert.equal(snapInputMs(2018,blankChart(),{...options,rate:.5}),2018);
  const dense={...blankChart(),bpm:300};
  assert.equal(snapInputMs(2012,dense,{...options,snap:.25,snapWindowMs:50}),2000);
  assert.equal(snapInputMs(2024,dense,{...options,snap:.25,snapWindowMs:50}),2024);
});
test('long-note start and release correct independently without changing long classification',()=>{
  const note=makeNote({...gesture,startAudio:2.021},2.785,blankChart(),options);
  assert.equal(note.type,'long');assert.equal(note.time,2000);assert.equal(note.lengthMs,750);
  const offGrid=makeNote({...gesture,startAudio:2.075},2.81,blankChart(),options);
  assert.equal(offGrid.time,2075);assert.equal(offGrid.lengthMs,735);
});
test('corrected notes stay inside punch boundaries and retain the game-compatible JSON schema',()=>{
  const chart={...blankChart(),offsetMs:250},take=new RecordingTake(chart,{start:2.02,end:2.9});
  const note=take.add(makeNote({...gesture,flickDirection:'upright'},2.12,chart,options));
  assert.equal(audioTime(note,chart),2.02);assert.equal(note.type,'direction');
  const exported=exportChart(take.compose(2.2));assert.deepEqual(exportChart(parseChart(exported)),exported);
  assert.equal(JSON.stringify(exported).includes('flickDirection'),false);assert.equal(JSON.stringify(exported).includes('snapMode'),false);
});
test('old OFF and forced-grid preferences retain behavior while new drafts use near correction',()=>{
  assert.equal(inputPreferences({snap:0}).snapMode,'off');
  assert.equal(inputPreferences({snap:.5}).snapMode,'always');assert.equal(inputPreferences({snap:.5}).snap,.5);
  assert.equal(inputPreferences().snapMode,'near');assert.equal(inputPreferences().snap,.25);
  const bad=inputPreferences({snap:999,snapWindowMs:999,flickEnabled:'yes',flickSensitivity:'bogus'});
  assert.equal(bad.snapMode,'off');assert.equal(bad.snapWindowMs,35);assert.equal(bad.flickSensitivity,'normal');
});
test('new flick and correction preferences survive audio backup and restore',async()=>{
  const settings={...options,flickEnabled:false,flickSensitivity:'firm',snapWindowMs:20};
  const restored=await readBackup(createBackup({name:'flick',difficulty:'normal',chart:blankChart(),settings},{name:'a.wav',blob:new Blob(['test'])}));
  for(const key of ['snap','snapMode','snapWindowMs','flickEnabled','flickSensitivity'])assert.equal(restored.settings[key],settings[key]);
});
