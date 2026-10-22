import test from 'node:test';
import assert from 'node:assert/strict';
import { blankChart, parseChart, makeNote, exportChart, History, hasVariableGrid, rebeat } from '../dist/core.js';
import { freshSettings, tempoIsSet, barSeek, barSpan, editNote } from '../dist/mobile.js';
import { refineChart, chartReport } from '../dist/refinement.js';
import { verticalGeometry, verticalHits, verticalTime } from '../dist/vertical-timeline.js';
import { createBackup, readBackup } from '../dist/backup.js';

const chart=(notes,extra={})=>parseChart({...blankChart(),...extra,notes:notes.map(n=>({time:1000,x:0,y:0,color:'blue',direction:'none',...n}))});
test('unknown tempo never quantizes new input, old 120 BPM drafts remain confirmed',()=>{
 const c=blankChart(),options={...freshSettings(),snapMode:'always',snap:1,color:'blue',rate:1};
 const note=makeNote({startAudio:1.23,x:0,y:0},1.24,c,options);
 assert.equal(note.time,1230);assert.equal(tempoIsSet({}),true);assert.equal(tempoIsSet(freshSettings()),false);
 assert.equal(makeNote({startAudio:1.23,x:0,y:0},1.24,c,{...options,bpmConfirmed:true}).time,1000);
});
test('hold threshold follows real time and automatic counts follow difficulty; manual counts stay explicit',()=>{
 const c=blankChart(),g={startAudio:1,x:0,y:0},base={...freshSettings(),bpmConfirmed:true,color:'blue',rate:.5,longThresholdMs:400};
 assert.equal(makeNote(g,1.19,c,base).count,1);
 assert.equal(makeNote(g,3,c,{...base,difficulty:'easy'}).count,3);
 assert.equal(makeNote(g,3,c,{...base,difficulty:'normal'}).count,3);
 assert.equal(makeNote(g,3,c,{...base,difficulty:'hard'}).count,5);
 assert.equal(makeNote(g,3,c,{...base,longCount:'8',difficulty:'easy'}).count,8);
});
test('bar navigation follows meter changes, offset, negative pickup and exact boundaries',()=>{
 const c=chart([],{bpm:120,offsetMs:100,beatZeroMs:200,timeSignatures:[{beat:8,numerator:3,denominator:4},{beat:14,numerator:6,denominator:8}]});
 assert.equal(barSeek(c,.3,1,20),2.3);assert.equal(barSeek(c,4.3,1,20),5.8);assert.equal(barSeek(c,4.3,-1,20),2.3);
 assert.equal(barSeek(c,5.8,-1,20),4.3);assert.equal(barSeek(c,7.3,1,20),8.8);assert.equal(barSeek(c,0,-1,20),0);
});
test('confirming a BPM after recording keeps times and recounts beats, so a steady song is not taken for a tempo change',()=>{
 const c=chart([{time:2000,beat:4},{time:4000,beat:8},{time:6000,beat:12},{time:8000,beat:16,count:3,type:'long',lengthMs:900}]);c.bpm=150;
 assert.equal(hasVariableGrid(c),true,'仮の120で数えた beat のままだと取り違える');
 rebeat(c);assert.deepEqual(c.notes.map(n=>n.time),[2000,4000,6000,8000]);assert.deepEqual(c.notes.map(n=>n.beat),[5,10,15,20]);
 assert.equal(c.notes[3].lengthMs,900);assert.equal(hasVariableGrid(c),false);
});
test('two bars from here follow the meter, across a change, and stop at the end of the song',()=>{
 const c=chart([],{bpm:120,offsetMs:0,beatZeroMs:0,timeSignatures:[{beat:8,numerator:3,denominator:4}]});
 assert.equal(barSpan(c,0,2,60),4);assert.equal(barSpan(c,4,2,60),7);assert.equal(barSpan(c,3,2,60),6.25);assert.equal(barSpan(c,58,2,60),60);
 assert.equal(barSpan(chart([],{bpm:120,timeSignatures:[{beat:0,numerator:6,denominator:8}]}),0,2,60),3);
});
test('immediate single-field edits retain variable beat timing, unknown metadata, type and implicit long duration',()=>{
 const c=chart([{beat:9,editorMemo:'keep',count:3,type:'long',lengthMs:1400}],{coordScale:2,offsetMs:110}),id=c.notes[0].__editorId;
 const x=editNote(c,id,'x',1.2,10);assert.equal(x.notes[0].x,.6);assert.equal(x.notes[0].beat,9);assert.equal(x.notes[0].editorMemo,'keep');assert.equal(x.notes[0].lengthMs,1400);
 const moved=editNote(x,id,'time',1.21,10);assert.ok(Math.abs(moved.notes[0].beat-9.2)<1e-8);assert.equal(c.notes[0].time,1000);
 const h=new History();h.push(c);assert.deepEqual(exportChart(h.undo(x)),exportChart(c));
});
test('blank numbers, invalid counts and out-of-audio long ends reject the entire edit',()=>{
 const c=chart([{count:3,type:'long',lengthMs:1000}]),id=c.notes[0].__editorId,original=JSON.stringify(c);
 for(const[field,value]of [['time',''],['x',''],['count',1.5],['length',0],['time',9.5]])assert.throws(()=>editNote(c,id,field,value,10));
 assert.equal(JSON.stringify(c),original);
});
test('25ms alignment is bounded from first note and preserves long lengths, beats and excluded scope',()=>{
 const c=chart([{time:1000,beat:7},{time:1025,beat:9,count:3,type:'long',lengthMs:400},{time:1040,beat:11},{time:1065,beat:13},{time:1030,color:'gold'}]);
 const result=refineChart(c,{color:'blue'},{kind:'alignTime'},10);assert.equal(result.count,2);
 assert.deepEqual(result.chart.notes.map(n=>n.time),[1000,1000,1030,1040,1040]);
 const hold=result.chart.notes.find(n=>n.count===3);assert.equal(hold.lengthMs,400);assert.equal(hold.beat,8.95);assert.equal(c.notes[1].time,1025);
});
test('diagnostics exclude simultaneous 25ms pairs from rearm and do not demand both hands for gold',()=>{
 const c=chart([{time:1000,direction:'up'},{time:1025,direction:'up'},{time:1100,direction:'up'},{time:1120,color:'gold'}]);
 const before=JSON.stringify(c),report=chartReport(c,10,'hard');
 assert.equal(report.issues.filter(i=>i.text.includes('25〜120ms')).length,1);
 assert.equal(report.issues.filter(i=>i.text.includes('400ms')).length,1);
 assert.equal(report.issues.some(i=>i.id===c.notes[3].__editorId),false);assert.equal(JSON.stringify(c),before);
 const held=chart([{time:0,count:3,type:'long',lengthMs:2000},{time:0,color:'red',count:3,type:'long',lengthMs:2000},{time:1000,color:'gold'}]);
 assert.ok(chartReport(held,10).issues.some(i=>i.text.includes('空いている手')));
});
test('difficulty rules are advisory and not applied to hard or unspecified difficulty',()=>{
 const c=chart([{time:1000,direction:'up'},{time:1200},{time:2000,color:'gold'},{time:2000,color:'red'}]);
 assert.ok(chartReport(c,10,'easy').issues.some(i=>i.text.includes('生成基準')));
 assert.ok(chartReport(c,10,'normal').issues.some(i=>i.text.includes('生成基準')));
 assert.ok(!chartReport(c,10,'hard').issues.some(i=>i.text.includes('生成基準')));
});
test('vertical timeline uses scaled X, future goes upward, overlaps remain selectable with 44px targets',()=>{
 const c=chart([{x:-1,time:1000},{x:1,time:1000},{x:1,time:1001},{x:0,time:4000}],{coordScale:2}),window={start:0,end:5,span:5};
 const boxes=verticalGeometry(c,window,390,300);assert.ok(boxes[0].x<boxes[1].x);assert.ok(boxes[3].y<boxes[0].y);
 assert.equal(verticalHits(boxes,boxes[1].x+21,boxes[1].y).length,2);assert.equal(verticalTime(window,276,300),0);assert.equal(verticalTime(window,24,300),5);
});
test('a long note can be picked by its visible body even when its head is before the window; heads win',()=>{
 const c=chart([{x:0,time:500,count:4,type:'long',lengthMs:3000},{x:2,time:3000}]),window={start:2,end:7,span:5};
 const boxes=verticalGeometry(c,window,390,300),long=boxes.find(b=>b.note.count===4),tap=boxes.find(b=>b.note.count===1);
 assert.ok(long.y>276,'始点は表示区間より前(下へ外れる)');
 assert.deepEqual(verticalHits(boxes,long.x+4,(long.end+276)/2).map(n=>n.count),[4],'見えている胴を押せば選べる');
 assert.deepEqual(verticalHits(boxes,long.x+40,(long.end+276)/2),[],'胴の横は空白');
 assert.deepEqual(verticalHits(boxes,tap.x,tap.y).map(n=>n.count),[1],'始点は今までどおり');
});
test('audio backup retains explicit unknown-tempo state and hold threshold',async()=>{
 const c=chart([]),p={name:'新しい曲',difficulty:'normal',chart:c,settings:{...freshSettings(),longThresholdMs:450}};
 const restored=await readBackup(createBackup(p,{name:'音源.wav',blob:new Blob(['audio'])}));
 assert.equal(restored.settings.bpmConfirmed,false);assert.equal(restored.settings.snapMode,'off');assert.equal(restored.settings.longThresholdMs,450);
 assert.equal(restored.chart.bpm,120);assert.ok(!Object.hasOwn(exportChart(restored.chart),'bpmConfirmed'));
});
