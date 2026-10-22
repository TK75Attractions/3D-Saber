import test from 'node:test';
import assert from 'node:assert/strict';
import { SongAudio } from '../dist/audio.js';
test('uses audible output timestamp and original pointer timestamp, rather than animation frames',()=>{
  const a=new SongAudio(),now=performance.now();a.context={currentTime:10.5,getOutputTimestamp:()=>({contextTime:10,performanceTime:now})};
  assert.ok(Math.abs(a.outputContextTime(now-50)-9.95)<1e-8);
  a.playing=true;a.buffer={duration:60};a.startOffset=7;a.startedAt=9;a.rate=.5;
  assert.ok(Math.abs(a.time(now)-7.5)<1e-8);
});
test('fallback subtracts output latency once and pause cancels scheduled sources',()=>{
  const a=new SongAudio();let stopped=0,disconnected=0;
  a.context={currentTime:10,outputLatency:.08};a.playing=true;a.buffer={duration:60};a.startOffset=2;a.startedAt=9;a.rate=1;
  a.source={stop:()=>stopped++,disconnect:()=>disconnected++};a.clicks=[{stop:()=>stopped++}];
  a.pause();assert.ok(Math.abs(a.position-2.92)<.02);assert.equal(a.playing,false);assert.equal(a.source,null);assert.equal(stopped,2);assert.equal(disconnected,1);
});
test('count-in keeps playhead at seek position and cancelling does not rewind',()=>{
  const a=new SongAudio();a.context={currentTime:5};a.buffer={duration:60};a.playing=true;a.startOffset=12;a.startedAt=7;a.rate=.5;
  assert.equal(a.time(),12);assert.ok(a.rawTime()<12);a.pause();assert.equal(a.position,12);
});
test('seeking and 0.5 playback remain in source-song seconds',()=>{
  const a=new SongAudio();a.buffer={duration:60};a.seek(25);assert.equal(a.time(),25);
  a.playing=true;a.startOffset=25;a.startedAt=100;a.rate=.5;a.outputContextTime=()=>104;
  assert.equal(a.time(),27);a.pause();assert.equal(a.position,27);a.seek(999);assert.equal(a.time(),60);
});
test('stopping while audio unlocks cannot start an invisible recording',async()=>{
  const a=new SongAudio();let release;a.unlock=()=>new Promise(resolve=>release=resolve);a.buffer={duration:30};
  const pending=a.play();a.pause();release();assert.equal(await pending,false);assert.equal(a.playing,false);
});
test('range endpoint is scheduled on the audio clock; metronome follows origin and speed',async t=>{
  let scheduled,cleared=false;const stops=[],clicks=[];
  t.mock.method(globalThis,'setInterval',fn=>{scheduled=fn;return 1;});
  t.mock.method(globalThis,'clearInterval',()=>{cleared=true;});
  const a=new SongAudio();a.unlock=async()=>{};a.buffer={duration:10};a.position=.8;
  a.context={currentTime:0,destination:{},createGain:()=>({gain:{},connect(){},disconnect(){}}),createBufferSource:()=>({playbackRate:{},connect(){},start(){},stop:when=>stops.push(when),disconnect(){}})};
  a.click=(frequency,when)=>clicks.push(when);
  await a.play({rate:.5,bpm:120,beatOrigin:.2,metronome:true,end:2.3});
  assert.ok(Math.abs(stops[0]-3.08)<1e-8);
  a.context.currentTime=.8;scheduled();assert.ok(Math.abs(clicks[0]-.88)<1e-8);
  a.context.currentTime=2.8;scheduled();assert.ok(Math.abs(clicks.at(-1)-2.88)<1e-8);
  a.pause();assert.equal(cleared,true);assert.equal(a.metronomeTimer,null);
});
// 偽の AudioContext。時計は currentTime だけで進み、出力は0.2秒遅れて聞こえる(Bluetooth相当)。発振器の予約(start/stop)と、つないだ先を記録する。
// setInterval は記録だけにして、tick(秒) でブラウザと同じように止められていないタイマーを呼ぶ。
function rig(t){
  const timers=[],cleared=new Set(),oscillators=[];
  t.mock.method(globalThis,'setInterval',fn=>timers.push(fn));t.mock.method(globalThis,'clearInterval',id=>cleared.add(id));
  const node=extra=>({connect(target){this.out=target;},disconnect(){},...extra});
  const context={currentTime:0,state:'running',outputLatency:.2,destination:{},getOutputTimestamp:()=>({contextTime:context.currentTime-.2,performanceTime:performance.now()}),
    createGain:()=>node({gain:{value:1,setValueAtTime(value){this.value=value;},exponentialRampToValueAtTime(){}}}),createBufferSource:()=>node({playbackRate:{},start(){},stop(){}}),
    createOscillator:()=>{const osc=node({frequency:{setValueAtTime(value){osc.hz=value;},exponentialRampToValueAtTime(){}},start(when){osc.when=when;osc.bookedAt=context.currentTime;},stop(when=0){osc.stopAt=when;}});oscillators.push(osc);return osc;}};
  const a=new SongAudio();a.unlock=async()=>{};a.context=context;a.buffer={duration:10};
  const tick=seconds=>{context.currentTime=seconds;timers.forEach((fn,i)=>{if(!cleared.has(i+1))fn();});};
  // 鳴るときの大きさ = 音ごとの音量 × つないだ先の音量。
  const level=osc=>{let value=1;for(let n=osc.out;n&&n!==context.destination;n=n.out)value*=n.gain.value;return value;};
  return {a,context,oscillators,tick,timers,cleared,level};
}
test('unmuting before the next metronome beat preserves its original time',async t=>{
  const {a,oscillators,tick,level}=rig(t);a.setVolumes(1,0);
  await a.play({bpm:120,metronome:true});
  assert.equal(oscillators.length,0);
  tick(.01);a.setVolumes(1,.5);tick(.025);
  assert.deepEqual(oscillators.map(o=>Math.round(o.when*1000)),[80]);
  assert.ok(level(oscillators[0])>0);a.pause();
});
test('muting the metronome mid-play skips the beats that came due meanwhile; unmuting keeps the next beat on time, without a burst',async t=>{
  const {a,oscillators,tick}=rig(t);
  await a.play({bpm:120,metronome:true});tick(.05);
  // 1倍・0秒から(拍は 0.08+0.5×n 秒に鳴る)。0.05秒で消音: 0.58秒の拍が過ぎても、1秒まで新しい拍は作らない。
  a.setVolumes(1,0);for(let ms=75;ms<=1000;ms+=25)tick(ms/1000);
  assert.deepEqual(oscillators.map(o=>Math.round(o.when*1000)),[80]);
  // 1秒で戻す: 消音中に過ぎた0.58秒の拍はまとめて鳴らさず、まだ先の1.08秒の拍を元の時刻で。
  a.setVolumes(1,.5);tick(1.025);
  assert.deepEqual(oscillators.map(o=>Math.round(o.when*1000)),[80,1080]);
});
test('note taps are booked on the song clock even when output is 0.2 s late: 0.5x, A included, B excluded, chords twice',async t=>{
  const {a,context,oscillators,tick}=rig(t);a.position=.8;context.currentTime=1;
  // 順不同・不正な値まじりで渡す。A(0.8)ちょうどは鳴り、その1ms前とB(2.3)ちょうどは鳴らない。
  await a.play({rate:.5,end:2.3,hits:[{time:2.3,frequency:950},{time:1,frequency:760},{time:2.299,frequency:570},{time:.799,frequency:570},{time:NaN,frequency:570},{time:1.5,frequency:0},null,{time:.8,frequency:950},{time:1,frequency:570}]});
  for(let ms=1000;ms<=5000;ms+=25)tick(ms/1000);
  // 曲は1.08秒に0.8秒の位置から0.5倍で鳴る。音源のt秒は 1.08+(t−0.8)/0.5 秒(聞こえている時刻ではなく、曲と同じ時計)。
  const booked=oscillators.map(o=>[o.hz,o.when]).sort((x,y)=>x[1]-y[1]||x[0]-y[0]);
  assert.deepEqual(booked.map(([hz])=>hz),[950,570,760,570]);
  [1.08,1.48,1.48,4.078].forEach((expected,i)=>assert.ok(Math.abs(booked[i][1]-expected)<1e-9,`${booked[i][1]} ≠ ${expected}`));
  assert.ok(oscillators.every(o=>o.when>=o.bookedAt),'過ぎた時刻へ予約した音がある');
});
test('a 0.2 s stall on the 25 ms timer loses no tap; a long stall skips past taps instead of playing them late',async t=>{
  const {a,oscillators,tick}=rig(t);
  await a.play({hits:Array.from({length:61},(_,i)=>({time:i*.05,frequency:570}))});
  for(let ms=25;ms<=1000;ms+=25)tick(ms/1000);
  for(let ms=1200;ms<=2000;ms+=25)tick(ms/1000);
  // 1倍・0秒から。音源の0.05×i秒は 0.08+0.05×i 秒に鳴る。1.0〜1.2秒に描画などで止まっても、その間の音を含めて1回ずつ。
  const booked=oscillators.map(o=>Math.round(o.when*1000));
  assert.equal(new Set(booked).size,booked.length,'同じ音を2回予約した');
  for(let i=0;80+i*50<=2000;i++)assert.ok(booked.includes(80+i*50),`${(i*.05).toFixed(2)}秒の音を落とした`);
  // 0.9秒止まったあと: 止まっている間に過ぎた音は鳴らさず、まだ先の音(2.85秒〜)から順に。
  tick(2.9);for(let ms=2925;ms<=3200;ms+=25)tick(ms/1000);
  assert.ok(oscillators.every(o=>o.when>=o.bookedAt),'過ぎた音を遅れて鳴らした');
  assert.deepEqual(oscillators.filter(o=>o.bookedAt>=2.9).map(o=>Math.round(o.when*1000)),[2930,2980,3030,3080]);
});
test('with 50000 notes each tick reads only the notes it books, not the whole chart',async t=>{
  const {a,tick}=rig(t);let reads=0;
  await a.play({hits:Array.from({length:50000},(_,i)=>({get time(){reads++;return i/1000;},frequency:570}))});reads=0;
  for(let ms=25;ms<=1000;ms+=25)tick(ms/1000);
  // 1秒ぶん(約1000個)を予約する読み取りだけ。tickごとに全件を見ると 40×50000 回を超える。
  assert.ok(reads<10000,`${reads}回読んだ`);
});
test('pause cancels booked taps and metronome together; seek and replay restart from the new place without doubles',async t=>{
  const {a,context,oscillators,tick,timers,cleared}=rig(t),hits=[0,.25,.5,2,2.25].map(time=>({time,frequency:760}));
  const booked=from=>oscillators.slice(from).map(o=>`${o.hz}@${Math.round(o.when*1000)}`);
  await a.play({bpm:120,metronome:true,hits});tick(.1);
  assert.deepEqual(booked(0),['820@80','760@80','760@330']);
  // 0.1秒で止める: 鳴っている音も0.33秒に予約した音も止まり、そのあと予約は増えない。
  a.pause();const count=oscillators.length;
  assert.ok(timers.length===2&&timers.every((_,i)=>cleared.has(i+1)),'止めたあとも予約が続く');
  assert.ok(oscillators.every(o=>o.stopAt<=.1),'止めたあとに鳴る音が残る');
  tick(.2);tick(.4);assert.equal(oscillators.length,count);
  // 2秒へシークして0.5秒に再生: 2秒ちょうどのノーツと拍から。前の位置の音は予約しない。
  a.seek(2);context.currentTime=.5;await a.play({bpm:120,metronome:true,hits});
  assert.deepEqual(booked(count),['820@580','760@580']);
  // 止めずにもう一度 play(): 前の予約は止まり、2.25秒の音は新しい時刻で1回だけ(古いタイマーが残ると2回になる)。
  const replay=oscillators.length;context.currentTime=.6;await a.play({bpm:120,metronome:true,hits});tick(.7);tick(.8);
  assert.ok(oscillators.slice(0,replay).every(o=>o.stopAt<=.6));
  assert.deepEqual(booked(replay),['820@680','760@680','760@930']);
});
test('the feedback slider reaches taps already booked ahead; no valid hit or a muted tap books nothing',async t=>{
  const {a,oscillators,tick,timers,level}=rig(t);
  await a.play({hits:[{time:NaN,frequency:570},null,{time:1,frequency:-1}]});assert.equal(timers.length,0);
  await a.play({hits:[{time:.1,frequency:570},{time:.15,frequency:760},{time:.5,frequency:950}]});
  // 0.18秒と0.23秒の音は予約済み。そのあと動かした音量(0.3、次に0)が、予約済みの音にもそのまま効く。
  a.setVolumes(1,.3);assert.deepEqual(oscillators.map(o=>+level(o).toFixed(6)),[.0165,.0165]);
  a.setVolumes(1,0);assert.ok(oscillators.every(o=>level(o)===0));
  // 消音中は新しい音を作らない(0.58秒の音)。
  tick(.4);assert.equal(oscillators.length,2);
});

test('unmuting before a future tap keeps that tap on its original scheduled time',async t=>{
  const {a,oscillators,tick,level}=rig(t);a.setVolumes(1,0);
  await a.play({hits:[{time:.1,frequency:570},{time:.15,frequency:760}]});
  tick(.1);assert.equal(oscillators.length,0,'消音中は発振器を作らない');
  a.setVolumes(1,.5);tick(.11);
  assert.deepEqual(oscillators.map(o=>Math.round(o.when*1000)),[180,230],'まだ先の音は消音解除後に元の時刻へ予約できる');
  assert.ok(oscillators.every(o=>level(o)>0),'戻した音量で鳴らない');
});
test('muting mid-play drops the taps that came due meanwhile; unmuting books the taps still ahead on time, without a burst',async t=>{
  const {a,oscillators,tick}=rig(t);
  await a.play({hits:Array.from({length:41},(_,i)=>({time:i*.05,frequency:570}))});tick(.05);
  // 1倍・0秒から(音源の0.05×i秒は 0.08+0.05×i 秒に鳴る)。0.05秒で消音: 予約済みの0.28秒までの音は残り(音量0)、1秒まで新しい音は作らない。
  a.setVolumes(1,0);for(let ms=75;ms<=1000;ms+=25)tick(ms/1000);
  assert.deepEqual(oscillators.map(o=>Math.round(o.when*1000)),[80,130,180,230,280]);
  // 1秒で戻す: 消音中に過ぎた0.33〜0.98秒の音はまとめて鳴らさず、まだ先の1.03秒の音から元の時刻で。
  a.setVolumes(1,.5);tick(1.025);
  assert.deepEqual(oscillators.slice(5).map(o=>Math.round(o.when*1000)),[1030,1080,1130,1180,1230]);
});
