// 外部素材を使わず、ノーツ切断音を決定的な合成で生成する。
const fs = require('node:fs');
const path = require('node:path');

const sampleRate = 48000;
const outDir = path.resolve(__dirname, '../../Outputs/NoteCutSFX');
fs.mkdirSync(outDir, { recursive: true });

function random(seed) {
  let state = seed >>> 0;
  return () => {
    state ^= state << 13;
    state ^= state >>> 17;
    state ^= state << 5;
    return ((state >>> 0) / 4294967296) * 2 - 1;
  };
}

// 帯域を時間とともに下げ、刃が空気を抜ける短い擦過音にする。
class Bandpass {
  constructor() { this.x1 = this.x2 = this.y1 = this.y2 = 0; }
  tick(x, hz, q) {
    const w = 2 * Math.PI * hz / sampleRate;
    const alpha = Math.sin(w) / (2 * q);
    const a0 = 1 + alpha;
    const b0 = alpha / a0;
    const a1 = -2 * Math.cos(w) / a0;
    const a2 = (1 - alpha) / a0;
    const y = b0 * x - b0 * this.x2 - a1 * this.y1 - a2 * this.y2;
    this.x2 = this.x1; this.x1 = x;
    this.y2 = this.y1; this.y1 = y;
    return y;
  }
}

function env(t, attack, decay) {
  return (1 - Math.exp(-t / attack)) * Math.exp(-t / decay);
}

function synthesize({ seconds, decay, peakDb, seed, weight }) {
  const count = Math.round(seconds * sampleRate);
  const samples = new Float64Array(count);
  const rng = random(seed);
  const air = new Bandpass();
  const edge = new Bandpass();
  let phase = 0;
  let bodyPhase = 0;
  let previousInput = 0;
  let previousOutput = 0;
  const dcPole = Math.exp(-2 * Math.PI * 95 / sampleRate);

  for (let i = 0; i < count; i++) {
    const t = i / sampleRate;
    const n = rng();
    const sweepHz = 1500 + 4000 * Math.exp(-t / 0.037);
    const swish = air.tick(n, sweepHz, 0.72) * env(t, 0.0010, decay);
    const impact = edge.tick(n, 2850, 0.85) * env(t, 0.00025, 0.008);

    // 極短い下降成分を重ね、電子音に寄り過ぎない硬い切断感をつける。
    phase += 2 * Math.PI * (650 + 2100 * Math.exp(-t / 0.006)) / sampleRate;
    bodyPhase += 2 * Math.PI * (145 + 170 * Math.exp(-t / 0.010)) / sampleRate;
    const slash = Math.sin(phase) * env(t, 0.0006, 0.009);
    const body = Math.sin(bodyPhase) * env(t, 0.0008, 0.012) * weight;
    const glint = (
      Math.sin(2 * Math.PI * 3250 * t) +
      0.40 * Math.sin(2 * Math.PI * 4597 * t)
    ) * env(t, 0.0012, 0.014);

    let mixed = 1.05 * swish + 0.52 * impact + 0.10 * slash + 0.16 * body + 0.022 * glint;
    mixed = Math.tanh(mixed * 1.2) / 1.2;

    // DCと低い濁りを落とし、開始・終端の不連続をなくす。
    const highPassed = mixed - previousInput + dcPole * previousOutput;
    previousInput = mixed;
    previousOutput = highPassed;
    const attackFade = Math.min(1, t / 0.0003);
    const tailProgress = Math.min(1, Math.max(0, (seconds - t) / 0.025));
    const tailFade = 0.5 - 0.5 * Math.cos(Math.PI * tailProgress);
    samples[i] = highPassed * attackFade * tailFade;
  }

  let peak = 0;
  for (const x of samples) peak = Math.max(peak, Math.abs(x));
  const gain = 10 ** (peakDb / 20) / Math.max(peak, 1e-12);
  for (let i = 0; i < samples.length; i++) samples[i] *= gain;
  samples[0] = samples[samples.length - 1] = 0;
  return samples;
}

function writeWav(name, samples) {
  const bytes = samples.length * 2;
  const wav = Buffer.alloc(44 + bytes);
  wav.write('RIFF', 0);
  wav.writeUInt32LE(36 + bytes, 4);
  wav.write('WAVEfmt ', 8);
  wav.writeUInt32LE(16, 16);
  wav.writeUInt16LE(1, 20);
  wav.writeUInt16LE(1, 22);
  wav.writeUInt32LE(sampleRate, 24);
  wav.writeUInt32LE(sampleRate * 2, 28);
  wav.writeUInt16LE(2, 32);
  wav.writeUInt16LE(16, 34);
  wav.write('data', 36);
  wav.writeUInt32LE(bytes, 40);
  const dither = random(98342);
  for (let i = 0; i < samples.length; i++) {
    const noise = i === 0 || i === samples.length - 1 ? 0 : (dither() + dither()) * 0.5;
    const value = Math.max(-32768, Math.min(32767, Math.round(samples[i] * 32767 + noise)));
    wav.writeInt16LE(value, 44 + i * 2);
  }
  fs.writeFileSync(path.join(outDir, name), wav);
  return checkWav(name, wav);
}

function checkWav(name, wav) {
  let peak = 0, squares = 0, sum = 0, clipped = 0, firstAudible = -1;
  const count = (wav.length - 44) / 2;
  for (let i = 0; i < count; i++) {
    const pcm = wav.readInt16LE(44 + i * 2);
    const x = pcm / 32768;
    if (Math.abs(pcm) >= 32767) clipped++;
    if (firstAudible < 0 && Math.abs(x) > 10 ** (-50 / 20)) firstAudible = i;
    peak = Math.max(peak, Math.abs(x));
    squares += x * x;
    sum += x;
  }
  if (clipped || wav.readInt16LE(44) || wav.readInt16LE(wav.length - 2))
    throw new Error(`${name}: 波形のクリップまたは端点エラー`);
  return {
    file: name,
    format: '48 kHz / PCM 16-bit / mono',
    durationMs: count * 1000 / sampleRate,
    peakDbFS: +(20 * Math.log10(peak)).toFixed(2),
    rmsDbFS: +(20 * Math.log10(Math.sqrt(squares / count))).toFixed(2),
    onsetMs: +(firstAudible * 1000 / sampleRate).toFixed(2),
    dcOffset: +(sum / count).toFixed(7),
    clippedSamples: clipped,
  };
}

const standard = synthesize({ seconds: 0.22, decay: 0.028, peakDb: -3, seed: 73457, weight: 1 });
const rapid = synthesize({ seconds: 0.13, decay: 0.017, peakDb: -5, seed: 18813, weight: 0.45 });
const reports = [
  writeWav('Saber_NoteCut.wav', standard),
  writeWav('Saber_NoteCut_Rapid.wav', rapid),
];
console.log(JSON.stringify(reports, null, 2));
