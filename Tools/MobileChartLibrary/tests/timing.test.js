import test from 'node:test';
import assert from 'node:assert/strict';
import {prepare,stateAt,hitTime,linger,project} from '../web/core.js';

test('音源時刻にoffsetを一度だけ適用し、可変テンポでも明示timeを使う',()=>{
  const chart=prepare({bpm:120,offsetMs:250,coordScale:2,editorTempoMap:[{beat:0,timeMs:0},{beat:2,timeMs:1700}],notes:[{time:1700,beat:2,x:1,y:1,count:1}]});
  assert.equal(chart.notes[0].hit,1.95);assert.equal(hitTime(chart.notes[0],chart),1.95);assert.equal(chart.coordScale,2);
  assert.equal(stateAt(chart.notes[0],.949),null);assert.ok(stateAt(chart.notes[0],.951));assert.equal(stateAt(chart.notes[0],1.95).z,0);
});
test('ロングの長さ・回数は巻き戻しや途中再生でも復元される',()=>{
  const chart=prepare({notes:[{time:1000,count:4,lengthMs:1500}]});const n=chart.notes[0];
  assert.equal(linger(n),1.5);assert.equal(stateAt(n,.9).remaining,4);assert.equal(stateAt(n,1).remaining,3);assert.equal(stateAt(n,1.5).remaining,2);assert.equal(stateAt(n,2).remaining,1);assert.equal(stateAt(n,2.5).remaining,0);assert.equal(stateAt(n,3),null);assert.equal(stateAt(n,.9).remaining,4);
  assert.ok(Math.abs(linger({count:4})-2.1)<1e-9);
});
test('全8方向・金・同時押しのデータを保持する',()=>{
  const directions=['up','down','left','right','upleft','upright','downleft','downright'];
  const notes=directions.map(direction=>({time:1000,x:-1,y:1,color:'gold',type:'direction',direction}));
  const chart=prepare({notes});assert.equal(chart.notes.length,8);assert.deepEqual(chart.notes.map(n=>n.direction),directions);assert.ok(chart.notes.every(n=>n.color==='gold'&&n.hit===1));
});
test('スマホ縦画面・横画面で判定枠の全域が見える',()=>{
  for(const [w,h] of [[343,257],[358,268],[812,250],[740,462]])for(const x of [-2.5,2.5])for(const y of [-1.5,1.5]){
    const p=project(x,y,0,w,h);assert.ok(p.x>=0&&p.x<=w,`${w}x${h} x=${p.x}`);assert.ok(p.y>=0&&p.y<=h,`${w}x${h} y=${p.y}`);
  }
});
