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
