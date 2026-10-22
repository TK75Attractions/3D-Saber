import { clamp } from './core.js';
export class SongAudio {
  constructor() { this.context = null; this.buffer = null; this.source = null; this.playing = false; this.position = 0; this.rate = 1; this.startOffset = 0; this.startedAt = 0; this.clicks = []; this.playRequest = 0; this.songVolume = 1; this.feedbackVolume = 1; this.songGain = null; this.feedbackGain = null; }
  setVolumes(song, feedback) {
    this.songVolume = Number.isFinite(song) ? clamp(song, 0, 1) : 1;
    this.feedbackVolume = Number.isFinite(feedback) ? clamp(feedback, 0, 1) : 1;
    if (this.songGain) this.songGain.gain.value = this.songVolume;
    if (this.feedbackGain) this.feedbackGain.gain.value = this.feedbackVolume;
  }
  async unlock() {
    if (!this.context) {
      const Audio = window.AudioContext || window.webkitAudioContext;
      if (!Audio) throw new Error('このブラウザは音声編集に対応していません。SafariかChromeで開いてください。');
      this.context = new Audio({ latencyHint: 'interactive' });
    }
    if (this.context.state !== 'running') await this.context.resume();
  }
  async decode(blob) {
    await this.unlock();
    if (blob.size > 100 * 1024 * 1024) throw new Error('音源は100MB以内にしてください。');
    let decoded;
    try { decoded = await this.context.decodeAudioData(await blob.arrayBuffer()); }
    catch { throw new Error('この音源を再生できません。MP3・M4A・WAVで試してください。'); }
    if (decoded.duration > 20 * 60) throw new Error('音源は20分以内にしてください。');
    return decoded;
  }
  get duration() { return this.buffer?.duration || 0; }
  // 出力デバイスが鳴らしているサンプルの時刻へ、入力イベントの時計を合わせる。
  outputContextTime(eventTime = performance.now()) {
    const ctx = this.context;
    if (!ctx) return 0;
    const now = performance.now();
    let event = eventTime;
    if (!Number.isFinite(event) || Math.abs(event - now) > 60000) event = now;
    if (ctx.getOutputTimestamp) {
      const stamp = ctx.getOutputTimestamp();
      if (stamp.contextTime > 0 && stamp.performanceTime > 0 && Math.abs(now - stamp.performanceTime) < 1000) {
        return stamp.contextTime + (event - stamp.performanceTime) / 1000;
      }
    }
    return ctx.currentTime - (ctx.outputLatency || ctx.baseLatency || 0) + (event - now) / 1000;
  }
  rawTime(eventTime) {
    return this.playing ? this.startOffset + (this.outputContextTime(eventTime) - this.startedAt) * this.rate : this.position;
  }
  time(eventTime) { return clamp(this.rawTime(eventTime), this.playing ? this.startOffset : 0, this.duration); }
  // hits は確認用のタップ音(音源上の秒 time と高さ frequency)。曲と同じ音声の時計に予約する。
  async play({ rate = 1, countBeats = 0, bpm = 120, metronome = false, beatOrigin = 0, end = this.duration, hits = [] } = {}) {
    const request = ++this.playRequest;
    await this.unlock();
    if (request !== this.playRequest) return false;
    if (!this.buffer) throw new Error('先に音源を開いてください。');
    this.pause();
    if (this.position >= this.duration - .01) this.position = 0;
    this.rate = rate; this.startOffset = this.position;
    // 並べ替えは開始時刻を決める前に済ませる(長い譜面でも、頭の音の予約に間に合う)。
    const queue = (Array.isArray(hits) ? hits : []).filter(hit => Number.isFinite(hit?.time) && Number.isFinite(hit?.frequency) && hit.frequency > 0).sort((a, b) => a.time - b.time);
    const beatWall = 60 / bpm / rate;
    this.startedAt = this.context.currentTime + .08 + countBeats * beatWall;
    this.source = this.context.createBufferSource(); this.source.buffer = this.buffer;
    this.source.playbackRate.value = rate;
    this.songGain = this.context.createGain(); this.songGain.gain.value = this.songVolume;
    this.source.connect(this.songGain); this.songGain.connect(this.context.destination);
    this.source.start(this.startedAt, this.position); this.playing = true;
    this.endPosition = clamp(end, this.position, this.duration);
    this.source.stop(this.startedAt + (this.endPosition - this.position) / rate);
    for (let i = 0; i < countBeats; i++) this.click(i === 0 ? 1000 : 680, this.context.currentTime + .08 + i * beatWall, .09);
    if (metronome) {
      const beatSong = 60 / bpm;
      let beat = Math.ceil((this.position - beatOrigin) / beatSong - 1e-8);
      const schedule = () => {
        const now = this.context.currentTime;
        // 非表示などで遅れても、過去のクリックをまとめて鳴らさない。
        beat = Math.max(beat, Math.ceil((this.startOffset + (now - this.startedAt) * rate - beatOrigin) / beatSong - 1e-8));
        let songTime = beatOrigin + beat * beatSong;
        while (songTime < this.endPosition) {
          const when = this.startedAt + (songTime - this.startOffset) / rate;
          if (when > now + .12) break;
          // 消音中は、まだ先の拍を作らずにそこで待つ。過ぎた拍は上の追いつきで飛ばすので、戻してもまとめて鳴らない。
          if (when >= now && this.feedbackVolume === 0) break;
          if (when >= now) this.click(820, when, .035);
          songTime = beatOrigin + (++beat) * beatSong;
        }
      };
      schedule(); this.metronomeTimer = setInterval(schedule, 25);
    }
    if (queue.length) {
      let next = queue.findIndex(hit => hit.time >= this.startOffset - 1e-9);
      if (next < 0) next = queue.length;
      // 曲と同じ時計なので、出力の遅れ(Bluetoothなど)は曲と同じだけ加わり、耳では揃う。聞こえている時刻(time())で先読みしない。
      // 0.25秒先まで予約し、描画などで0.2秒止まっても落とさない。止める・シークでは pause が予約ごと止める。
      const scheduleHits = () => {
        const now = this.context.currentTime;
        for (; next < queue.length && queue[next].time < this.endPosition; next++) {
          const when = this.startedAt + (queue[next].time - this.startOffset) / rate;
          if (when > now + .25) break;
          // 消音中は、まだ先の音を作らずにそこで待つ。鳴る前に音量を戻せば、次の予約で元の時刻に鳴る。
          if (when >= now && this.feedbackVolume === 0) break;
          // 遅れて過ぎた音は飛ばす(まとめて鳴らさない)。同じ時刻のノーツはそれぞれ鳴らす。
          if (when >= now) this.click(queue[next].frequency, when);
        }
      };
      scheduleHits(); this.hitTimer = setInterval(scheduleHits, 25);
    }
    return true;
  }
  pause() {
    this.playRequest++;
    clearInterval(this.metronomeTimer); this.metronomeTimer = null;
    clearInterval(this.hitTimer); this.hitTimer = null;
    if (this.playing) this.position = Math.max(this.startOffset, this.time());
    this.playing = false;
    if (this.source) { try { this.source.stop(); } catch {} this.source.disconnect(); this.source = null; }
    if (this.songGain) { this.songGain.disconnect(); this.songGain = null; }
    for (const click of this.clicks) { try { click.stop(); } catch {} }
    this.clicks = [];
  }
  seek(value) { this.pause(); this.position = clamp(value, 0, this.duration); }
  click(frequency = 600, when, volume = .055) {
    if (!this.context || this.context.state !== 'running' || this.feedbackVolume === 0) return;
    // 音量は共通の出口でかける(曲の songGain と同じ形)。先に予約した音にも、動かした音量がすぐ届く。
    if (!this.feedbackGain) { this.feedbackGain = this.context.createGain(); this.feedbackGain.gain.value = this.feedbackVolume; this.feedbackGain.connect(this.context.destination); }
    const start = when ?? this.context.currentTime;
    const osc = this.context.createOscillator(), gain = this.context.createGain();
    osc.type = 'sine'; osc.frequency.setValueAtTime(frequency, start); osc.frequency.exponentialRampToValueAtTime(frequency * .6, start + .045);
    gain.gain.setValueAtTime(volume, start); gain.gain.exponentialRampToValueAtTime(.0001, start + .055);
    osc.connect(gain); gain.connect(this.feedbackGain); osc.start(start); osc.stop(start + .06);
    this.clicks.push(osc); osc.onended = () => { osc.disconnect(); gain.disconnect(); this.clicks = this.clicks.filter(x => x !== osc); };
  }
}
