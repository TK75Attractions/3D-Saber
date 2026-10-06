import test from 'node:test';
import assert from 'node:assert/strict';
import { encodeWav } from '../dist/wav.js';
test('game-compatible WAV preserves sample rate, length and stereo channel order',()=>{
  const buffer={numberOfChannels:2,length:3,sampleRate:48000,getChannelData:i=>i===0?new Float32Array([1,0,-1]):new Float32Array([-.5,.5,2])};
  const bytes=encodeWav(buffer),v=new DataView(bytes);assert.equal(bytes.byteLength,56);
  assert.equal(new TextDecoder().decode(bytes.slice(0,4)),'RIFF');assert.equal(new TextDecoder().decode(bytes.slice(8,12)),'WAVE');
  assert.equal(v.getUint16(20,true),1);assert.equal(v.getUint16(22,true),2);assert.equal(v.getUint32(24,true),48000);assert.equal(v.getUint32(40,true),12);
  assert.equal(v.getInt16(44,true),32767);assert.equal(v.getInt16(46,true),-16384);assert.equal(v.getInt16(52,true),-32768);assert.equal(v.getInt16(54,true),32767);
});
