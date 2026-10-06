import test from 'node:test';
import assert from 'node:assert/strict';
import { blankChart, parseChart, exportChart, History } from '../dist/core.js';
import { RecordingTake, TapTempo } from '../dist/editing.js';

const source = () => parseChart({...blankChart(),offsetMs:250,notes:[0,1,2,3,4,5].map((time,i)=>({time:time*1000-250,x:0,y:0,color:'blue',tag:i}))});
const input = (seconds, fields={}) => ({time:seconds*1000-250,x:1,y:0,color:'red',type:'tap',count:1,lengthMs:0,direction:'none',...fields});
test('punch recording replaces only the reached interval, preserving later notes and boundary notes',()=>{
  const original=source(),take=new RecordingTake(original,{start:1,end:4,replace:true});
  take.add(input(1.5));
  assert.deepEqual(take.compose(2.2).notes.map(n=>n.time),[-250,1250,2750,3750,4750]);
  assert.deepEqual(take.compose(4).notes.map(n=>n.time),[-250,1250,3750,4750]);
  assert.equal(original.notes.length,6);assert.equal(original.notes[1].tag,1);
});
test('no input leaves existing notes intact, including cancellation during count-in',()=>{
  const original=source(),take=new RecordingTake(original,{start:1,end:4,replace:true});
  assert.deepEqual(take.compose(4),original);assert.deepEqual(take.compose(.9),original);
});
test('simultaneous taps and holds survive as one undoable take, append never removes existing notes',()=>{
  const original=source(),take=new RecordingTake(original,{start:1,end:4}),history=new History();history.push(original);
  take.add(input(2,{__editorId:'left'}));take.add(input(2,{__editorId:'right',count:3,type:'long',lengthMs:900}));
  const result=take.compose(4);assert.equal(result.notes.length,8);
  assert.equal(result.notes.filter(n=>n.time===1750).length,3);
  assert.deepEqual(history.undo(result),original);assert.deepEqual(history.redo(original),result);
});
test('snapped notes and holds stay inside A/B while chart offsets are applied only once',()=>{
  const take=new RecordingTake(source(),{start:1.1,end:2.3});
  const first=take.add(input(1,{count:3,type:'long',lengthMs:5000}));
  assert.equal(first.time,850);assert.ok(Math.abs(first.lengthMs-1200)<1e-8);
  const last=take.add(input(2.5));assert.equal(last.time,2049);
  assert.doesNotThrow(()=>parseChart(exportChart(take.compose(2.3))));
});
test('tap tempo tolerates small fluctuations and one missed beat, and resets after an idle gap',()=>{
  const tempo=new TapTempo();
  for(const time of [0,501,999,1500,2500,3001])tempo.tap(time);
  assert.ok(Math.abs(tempo.result().bpm-120)<.2);
  assert.equal(tempo.tap(7000).bpm,null);assert.equal(tempo.result().count,1);
  tempo.tap(7001);assert.equal(tempo.result().count,1);
});
