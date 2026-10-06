import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { blankChart, parseChart, exportChart, makeNote, audioTime, longSeconds, History, hasVariableGrid } from '../dist/core.js';
const options = {rate:1,snap:0,latencyMs:0,longCount:'auto',direction:'none',color:'auto'};
const gesture = {startAudio:2,x:-1.5,y:.7};
test('tap stores audible time with chart offset exactly once and honors coordinate scale',()=>{
  const chart={...blankChart(),offsetMs:350,coordScale:.5};
  const n=makeNote(gesture,2.09,chart,options);
  assert.equal(n.time,1650);assert.equal(audioTime(n,chart),2);assert.equal(n.x,-3);assert.equal(n.y,1.4);assert.equal(n.type,'tap');assert.equal(n.color,'blue');
});
test('hold stores length separately from count, slow playback preserves song time',()=>{
  const chart=blankChart();const n=makeNote(gesture,3,chart,{...options,rate:.5,longCount:'4'});
  assert.equal(n.type,'long');assert.equal(n.lengthMs,1000);assert.equal(n.count,4);assert.equal(n.time,2000);
  const shortSlow=makeNote(gesture,2.15,chart,{...options,rate:.5});assert.equal(shortSlow.type,'long');assert.equal(shortSlow.lengthMs,150);
});
test('quantization applies to new notes only, relative to beat origin and offset',()=>{
  const chart={...blankChart(),beatZeroMs:100,offsetMs:200};chart.notes=[{time:1371,beat:2.542}];
  const n=makeNote({...gesture,startAudio:1.37},1.45,chart,{...options,snap:.5});
  assert.equal(n.time,1100);assert.equal(audioTime(n,chart),1.3);assert.equal(chart.notes[0].time,1371);
});
test('input latency is subtracted at playback rate, negative chart times are allowed with positive offset',()=>{
  const n=makeNote({...gesture,startAudio:.05},.1,{...blankChart(),offsetMs:300},{...options,latencyMs:100,rate:.5});
  assert.equal(n.time,-300);assert.equal(audioTime(n,{offsetMs:300}),0);
});
test('simultaneous fingers retain both notes and independent long durations',()=>{
  const a=makeNote(gesture,2.05,blankChart(),options),b=makeNote({...gesture,x:1.5},3,blankChart(),options);
  assert.equal(a.time,b.time);assert.notEqual(a.__editorId,b.__editorId);assert.equal(a.color,'blue');assert.equal(b.color,'red');assert.equal(a.count,1);assert.equal(b.count,3);
});
test('JSON round trip preserves time, offset, meters, unknown metadata and explicit long lengths',()=>{
  const input={...blankChart(),_comment:'Variable tempo',offsetMs:1296,timeSignatures:[{beat:8,numerator:6,denominator:8}],notes:[{time:61335,beat:96,x:.1,y:0,type:'long',count:3,lengthMs:2300,color:'gold',direction:'none',extra:'keep'}]};
  const output=exportChart(parseChart(JSON.stringify(input)));assert.deepEqual(output,input);assert.equal(longSeconds(output.notes[0]),2.3);assert.ok(!JSON.stringify(output).includes('__editorId'));
});
test('malformed charts fail before replacing current document',()=>{
  for(const input of [{},null,{...blankChart(),bpm:0},{...blankChart(),coordScale:0},{...blankChart(),notes:[{time:null,x:0,y:0}]},{...blankChart(),notes:[{time:1,x:0,y:0,count:0}]}])assert.throws(()=>parseChart(input));
});
test('undo and redo restore original chart, new edits discard only redo history',()=>{
  const h=new History(),original=blankChart();h.push(original);const edited={...original,notes:[makeNote(gesture,2.05,original,options)]};
  assert.deepEqual(h.undo(edited),original);assert.deepEqual(h.redo(original),edited);h.undo(edited);h.push(original);assert.equal(h.future.length,0);
});
test('variable-tempo warning detects nonuniform note times',()=>{
  const c=blankChart();c.notes=Array.from({length:20},(_,i)=>({beat:i,time:1000*i}));assert.equal(hasVariableGrid(c),true);
});
test('all installed game charts preserve gameplay fields when imported and exported',{skip:!process.env.SABER_SONGS_DIR},()=>{
  const root=process.env.SABER_SONGS_DIR;let count=0;
  for(const folder of fs.readdirSync(root)){const dir=path.join(root,folder);if(!fs.statSync(dir).isDirectory())continue;
    for(const file of fs.readdirSync(dir).filter(f=>/^chart_(easy|normal|hard)\.json$/.test(f))){
      const input=JSON.parse(fs.readFileSync(path.join(dir,file),'utf8').replace(/^\uFEFF/,'')),output=exportChart(parseChart(input));
      const sorted=[...input.notes].sort((a,b)=>a.time-b.time);assert.equal(output.notes.length,sorted.length);
      for(let i=0;i<sorted.length;i++)for(const key of Object.keys(sorted[i])){
        const expected=sorted[i][key],actual=output.notes[i][key],label=`${folder}/${file} note ${i}.${key}`;
        if(typeof expected==='number')assert.ok(actual===expected,label);else assert.deepEqual(actual,expected,label);
      }
      for(const key of Object.keys(input).filter(k=>k!=='notes'))assert.deepEqual(output[key],input[key]);count++;
    }
  }assert.ok(count>=24);console.log(`Round-trip verified ${count} real difficulty charts.`);
});
