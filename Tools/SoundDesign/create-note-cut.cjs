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

function finish(samples, peakDb) {
  let peak = 0;
  for (let i = 0; i < samples.length; i++) {
    const t = i / sampleRate;
    const remaining = (samples.length - 1 - i) / sampleRate;
    const fadeIn = Math.min(1, t / 0.0004);
    const fadeOut = 0.5 - 0.5 * Math.cos(Math.PI * Math.min(1, remaining / 0.025));
    samples[i] *= fadeIn * fadeOut;
    peak = Math.max(peak, Math.abs(samples[i]));
  }
  const gain = 10 ** (peakDb / 20) / Math.max(peak, 1e-12);
  for (let i = 0; i < samples.length; i++) samples[i] *= gain;
  return samples;
}

function flick() {
  const samples = new Float64Array(sampleRate * 0.19);
  const rng = random(92313);
  const whip = new Bandpass();
  let phase = 0;
  for (let i = 0; i < samples.length; i++) {
    const t = i / sampleRate;
    // 空気音を高域へ跳ね上げ、短い上昇音で方向へ抜ける感触を作る。
    const hz = 2100 + 4400 * (1 - Math.exp(-t / 0.016));
    const noise = whip.tick(rng(), hz, 1.0) * env(t, 0.0008, 0.026);
    phase += 2 * Math.PI * (1200 + 2200 * (1 - Math.exp(-t / 0.010))) / sampleRate;
    samples[i] = 0.95 * noise + 0.13 * Math.sin(phase) * env(t, 0.001, 0.023);
  }
  return finish(samples, -3.5);
}

function longTick() {
  const samples = new Float64Array(sampleRate * 0.12);
  const rng = random(84901);
  const grit = new Bandpass();
  let phase = 0;
  for (let i = 0; i < samples.length; i++) {
    const t = i / sampleRate;
    phase += 2 * Math.PI * (440 + 180 * Math.exp(-t / 0.004)) / sampleRate;
    const body = (Math.sin(phase) + 0.23 * Math.sin(phase * 2.43)) * env(t, 0.0007, 0.012);
    const texture = grit.tick(rng(), 1650, 1.0) * env(t, 0.0005, 0.010);
    // 通常の長い空気音を避け、細かく刻める「コツ・ジッ」という短い質感にする。
    samples[i] = 0.43 * body + 0.37 * texture;
  }
  return finish(samples, -5);
}

function longFinish() {
  const samples = new Float64Array(sampleRate * 0.28);
  const rng = random(42079);
  const grit = new Bandpass();
  for (let i = 0; i < samples.length; i++) {
    const t = i / sampleRate;
    const release = (Math.sin(2 * Math.PI * 880 * t) + 0.32 * Math.sin(2 * Math.PI * 1320 * t)) * env(t, 0.0008, 0.034);
    const body = Math.sin(2 * Math.PI * 440 * t) * env(t, 0.0007, 0.014);
    samples[i] = 0.21 * release + 0.25 * body + 0.55 * grit.tick(rng(), 2700, 0.8) * env(t, 0.0007, 0.020);
  }
  return finish(samples, -3.5);
}

function gold() {
  const samples = new Float64Array(Math.round(sampleRate * 0.58));
  const rng = random(514929);
  const metal = new Bandpass();
  const ratios = [1, 1.37, 1.83, 2.47, 3.16, 3.87, 4.62];
  const amplitudes = [1, 0.70, 0.54, 0.36, 0.25, 0.18, 0.10];
  const phases = ratios.map(() => rng() * Math.PI);
  let previous = 0;
  let output = 0;
  const dcPole = Math.exp(-2 * Math.PI * 160 / sampleRate);
  for (let i = 0; i < samples.length; i++) {
    const t = i / sampleRate;
    let ring = 0;
    for (let k = 0; k < ratios.length; k++) {
      const hz = 1760 * ratios[k];
      const envelope = env(t, 0.0009, 0.085 / (1 + 0.18 * k));
      // 近い2本の部分音で金属らしい揺らぎを作り、1打の「シャン」にまとめる。
      ring += amplitudes[k] * envelope * (
        Math.sin(2 * Math.PI * hz * t + phases[k]) +
        0.35 * Math.sin(2 * Math.PI * (hz + 9 + k * 3) * t - phases[k])
      );
    }
    const sparkle = metal.tick(rng(), 6100 - 1600 * Math.min(1, t / 0.1), 0.7) * env(t, 0.0007, 0.030);
    const raw = 0.17 * ring + 0.80 * sparkle;
    output = raw - previous + dcPole * output;
    previous = raw;
    samples[i] = output;
  }
  return finish(samples, -3);
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
  writeWav('Saber_FlickCut.wav', flick()),
  writeWav('Saber_LongTick.wav', longTick()),
  writeWav('Saber_LongFinish.wav', longFinish()),
  writeWav('Saber_GoldCut.wav', gold()),
];
console.log(JSON.stringify(reports, null, 2));
