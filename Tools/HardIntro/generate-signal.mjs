// JARL和文表とITUの点・線・空白比を使う。Web版と同じ文章を16.55秒で送信する。
import fs from 'node:fs';
import {fileURLToPath} from 'node:url';
import {READING,MESSAGE,makeTransmission} from './wabun.mjs';
const transmission=makeTransmission(READING,.55,17.1);
const rate=48000,count=rate*24,data=Buffer.alloc(44+count*2);
data.write('RIFF',0);data.writeUInt32LE(data.length-8,4);data.write('WAVEfmt ',8);data.writeUInt32LE(16,16);
data.writeUInt16LE(1,20);data.writeUInt16LE(1,22);data.writeUInt32LE(rate,24);data.writeUInt32LE(rate*2,28);
data.writeUInt16LE(2,32);data.writeUInt16LE(16,34);data.write('data',36);data.writeUInt32LE(count*2,40);
const envelope=[[0,.006],[1,.01],[3.7,.10],[4.4,.12],[5.2,.026],[14,.017],[17.8,.022],[19.5,0],[24,0]];
let eventIndex=0,envIndex=0,seed=57201,previous=0,highpass=0,peak=0;
const alpha=1/(1+2*Math.PI*2100/rate);
for(let i=0;i<count;i++){
  const t=i/rate;
  while(eventIndex<transmission.events.length&&t>=transmission.events[eventIndex].start+transmission.events[eventIndex].duration)eventIndex++;
  const event=transmission.events[eventIndex];let tone=0;
  if(event&&t>=event.start){const at=t-event.start,edge=.0025;const key=Math.min(1,at/edge,(event.duration-at)/edge);tone=.24*key*Math.sin(2*Math.PI*720*t);}
  while(envIndex<envelope.length-2&&t>envelope[envIndex+1][0])envIndex++;
  const a=envelope[envIndex],b=envelope[envIndex+1],gain=a[1]+(b[1]-a[1])*(t-a[0])/(b[0]-a[0]);
  seed=(Math.imul(seed,1664525)+1013904223)>>>0;const white=seed/2147483648-1;
  highpass=alpha*(highpass+white-previous);previous=white;
  const sample=.46*(tone+gain*highpass);peak=Math.max(peak,Math.abs(sample));
  data.writeInt16LE(Math.round(Math.max(-1,Math.min(1,sample))*32767),44+i*2);
}
const output=fileURLToPath(new URL('../../Assets/Resources/UI/HardIntro/MorseSignal.wav',import.meta.url));
fs.writeFileSync(output,data);
console.log(JSON.stringify({output,message:MESSAGE,reading:READING,events:transmission.events.length,unit:transmission.unit,peak,duration:24}));
