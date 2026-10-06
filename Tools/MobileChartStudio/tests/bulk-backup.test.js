import test from 'node:test';
import assert from 'node:assert/strict';
import {blankChart,parseChart,exportChart,audioTime,History} from '../dist/core.js';
import {rangeNotes,transformRange,copyChart} from '../dist/editing.js';
import {createBackup,readBackup} from '../dist/backup.js';
import {timelineWindow,visibleNotes,timelineGeometry,timelineHit} from '../dist/timeline.js';
const source=()=>parseChart({...blankChart(),offsetMs:250,coordScale:.5,_comment:'keep',notes:[
  {time:750,beat:1.5,x:-2,y:1,type:'direction',color:'blue',direction:'upleft',count:1,lengthMs:0,tag:'A'},
  {time:1750,beat:3.5,x:2,y:0,type:'long',color:'red',direction:'none',count:3,lengthMs:2000},
  {time:2750,beat:5.5,x:0,y:-1,type:'tap',color:'gold',direction:'none',count:1,lengthMs:0}]});
test('batch selection uses audio seconds, starts and half-open boundaries',()=>{
  const chart=source();assert.deepEqual(rangeNotes(chart,{start:1,end:3}).map(n=>n.color),['blue','red']);
  assert.equal(rangeNotes(chart,{start:1,end:3,color:'blue'}).length,1);
  assert.throws(()=>rangeNotes(chart,{start:2,end:1}));
});
test('copy keeps relative timing, long length, metadata and independent IDs; one undo restores all',()=>{
  const chart=source(),history=new History();history.push(chart);
  const result=transformRange(chart,{start:1,end:3},{kind:'copy',value:5},10).chart;
  assert.equal(result.notes.length,5);assert.equal(chart.notes.length,3);
  assert.equal(audioTime(result.notes[3],result),5);assert.equal(audioTime(result.notes[4],result),6);
  assert.equal(result.notes[4].lengthMs,2000);assert.equal(result.notes[3].tag,'A');assert.equal(result._comment,'keep');
  assert.notEqual(result.notes[3].__editorId,chart.notes[0].__editorId);assert.deepEqual(history.undo(result),chart);
});
test('mirror also reflects directional arrows, with optional color swapping, without changing time or scale',()=>{
  const chart=source();const result=transformRange(chart,{start:1,end:3},{kind:'mirror',swapColors:true},10).chart;
  assert.equal(result.notes[0].x,2);assert.equal(result.notes[0].direction,'upright');assert.equal(result.notes[0].color,'red');assert.equal(result.notes[0].time,750);
  assert.deepEqual(result.notes[2],chart.notes[2]);assert.equal(result.coordScale,.5);
  const restored=transformRange(result,{start:1,end:3},{kind:'mirror',swapColors:true},10).chart;assert.deepEqual(restored,chart);
});
test('invalid moves and copies fail atomically, including a long tail outside the audio',()=>{
  const chart=source(),before=JSON.stringify(chart);
  assert.throws(()=>transformRange(chart,{start:1,end:3},{kind:'shift',value:-1500},10),/音源の外/);
  assert.throws(()=>transformRange(chart,{start:1,end:3},{kind:'copy',value:8},10),/音源の外/);
  assert.throws(()=>transformRange(chart,{start:1,end:3},{kind:'shift',value:NaN},10));
  assert.equal(JSON.stringify(chart),before);
  const result=transformRange(chart,{start:1,end:3,color:'red'},{kind:'shift',value:10},10).chart;
  assert.equal(result.notes[1].time,1760);assert.equal(result.notes[0].time,750);assert.equal(result.notes[1].lengthMs,2000);
});
test('difficulty copy regenerates IDs and can keep tempo/offset while starting empty',()=>{
  const chart=source(),copy=copyChart(chart);assert.deepEqual(exportChart(copy),exportChart(chart));assert.notEqual(copy.notes[0].__editorId,chart.notes[0].__editorId);
  copy.notes[0].time=999;assert.equal(chart.notes[0].time,750);const empty=copyChart(chart,true);assert.equal(empty.notes.length,0);assert.equal(empty.offsetMs,250);assert.equal(empty._comment,'keep');
});
test('backup round trip retains binary audio, Unicode names, chart fields and settings',async()=>{
  const chart=source(),bytes=new Uint8Array([0,255,123,42,0,128]);
  const blob=createBackup({name:'朝の曲 🎵',chart,difficulty:'hard',position:2.2,settings:{rate:.5,color:'gold',countIn:false,metronome:true}}, {name:'音源.m4a',blob:new Blob([bytes],{type:'audio/mp4'})});
  const restored=await readBackup(blob);assert.equal(restored.name,'朝の曲 🎵');assert.equal(restored.audio.name,'音源.m4a');assert.equal(restored.audio.blob.type,'audio/mp4');
  assert.deepEqual(new Uint8Array(await restored.audio.blob.arrayBuffer()),bytes);assert.deepEqual(exportChart(restored.chart),exportChart(chart));assert.equal(restored.settings.rate,.5);assert.equal(restored.settings.countIn,false);assert.equal(restored.settings.metronome,true);assert.equal(restored.settings.color,'gold');assert.equal(restored.position,2.2);
});
test('truncated, wrong-format, and oversized-header backups fail before restoration',async()=>{
  const valid=createBackup({name:'test',chart:source(),difficulty:'normal'}, {name:'a.wav',blob:new Blob(['data'])});
  await assert.rejects(()=>readBackup(valid.slice(0,valid.size-1)));
  await assert.rejects(()=>readBackup(new Blob(['not a backup'])));
  const bytes=new Uint8Array(await valid.arrayBuffer());new DataView(bytes.buffer).setUint32(13,0xffffffff,true);await assert.rejects(()=>readBackup(new Blob([bytes])));
});
test('invalid backup preferences are normalized without injecting unavailable controls',async()=>{
  const blob=createBackup({name:'t',chart:source(),difficulty:'easy',settings:{rate:100,color:'bogus',metronome:'yes',latencyMs:9999}}, {name:'a',blob:new Blob(['x'])});
  const value=await readBackup(blob);assert.equal(value.settings.rate,1);assert.equal(value.settings.color,'auto');assert.equal(value.settings.metronome,false);assert.equal(value.settings.latencyMs,500);
});
test('timeline clamps zoom/panning and includes long notes that started before the window',()=>{
  assert.deepEqual(timelineWindow(15,99,5),{start:10,end:15,span:5});assert.deepEqual(timelineWindow(15,9,0),{start:0,end:15,span:15});
  const chart=source(),window=timelineWindow(10,2.5,2);assert.deepEqual(visibleNotes(chart,window).map(n=>n.color),['red','gold']);
  const boxes=timelineGeometry(chart,window,320);assert.equal(boxes[0].x,34);assert.equal(timelineHit(boxes,40,boxes[0].y).__editorId,chart.notes[1].__editorId);assert.equal(timelineHit(boxes,40,0),undefined);
});
