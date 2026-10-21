using UnityEngine;

// 実際に再生されたサンプル位置にDSP時計を合わせ、曲開始からの秒数を公開する。
// 予約時刻だけでは、開始フレームが重かった場合に音声とのずれが曲末まで残る。
// 頭の無音を飛ばして途中から鳴らすこともできる(仕様面の点検 7、2026-10-21)。そのときも SongTime は音源の頭からの秒のままで、
// 鳴らし始めた瞬間に PlayStartSeconds になる(譜面・演出区間・判定の時刻は変えない)。
[RequireComponent(typeof(AudioSource))]
public class SongPlayer : MonoBehaviour
{
    private AudioSource source;
    private float loudnessBaseVolume = -1;

    // フェードやサビ演出に渡す前の曲別音量。再ロードしても減衰を重ねない。
    public void SetLoudnessGain(float gain)
    {
        EnsureSource();
        if (loudnessBaseVolume < 0) loudnessBaseVolume = source.volume;
        source.volume = loudnessBaseVolume * Mathf.Clamp01(gain);
    }

    // 曲の 0 秒(音源の頭)にあたる DSP 時刻。頭を飛ばすときは、実際に鳴らし始める時刻より startOffset だけ前になる。
    private double startDspTime;
    // 鳴らし始める位置(秒とサンプル)。0 なら従来どおり頭から。
    private double startOffset;
    private int startSample;
    private bool scheduled;
    private bool clockSynchronized;
    private bool paused;
    private bool pausedBeforeStart;
    private bool pausedByApplication;
    private double pausedSongTime;
    public float startDelay = 0.2f;

    public AudioClip Clip
    {
        get { EnsureSource(); return source.clip; }
        set { EnsureSource(); if (source.clip != value) { Stop(); source.clip = value; } }
    }

    // 開始予約中も先読み時計は動く。音が鳴っている状態とは区別する。
    public bool IsScheduled => scheduled;
    public bool IsPlaying => scheduled && !paused && AudioSettings.dspTime >= startDspTime + startOffset;
    public bool IsPaused => paused;
    // 鳴らし始めた位置(音源の頭からの秒)。
    public double PlayStartSeconds => startOffset;
    // 進行バーの終わり(音源の頭からの秒)。0 以下なら音源の長さ。本編が曲ごとの鳴り終わりを入れる。
    public double ProgressEndSeconds { get; set; } = -1;
    // 剣を見失って止めている間(PlayTrackingWatch)。OS の中断から戻っても、これが立っている間は再開しない。
    public bool HeldByTracking { get; set; }

    // 曲開始からの秒数。再生開始前は負の値。
    public double SongTime
    {
        get
        {
            if (!scheduled) return 0.0;
            if (paused) return pausedSongTime;
            double now = AudioSettings.dspTime;
            double elapsed = now - startDspTime;
            if (!clockSynchronized && elapsed >= startOffset && source != null && source.clip != null)
            {
                int sample = source.timeSamples;
                if (sample <= startSample)
                {
                    // 短い音源が最初の読取前に終わった場合や再生装置が無い場合も、曲末へ進む。
                    // 最低1秒か(鳴らす区間の長さ)＋余裕の長い方まで待ち、通常の開始遅延は実サンプルに同期する。
                    // 鳴らし始めの位置から音が進むまでは、時計もその位置で待つ。
                    if (elapsed - startOffset <= System.Math.Max(1.0, source.clip.length - startOffset + .25)) return startOffset;
                    clockSynchronized = true;
                    return elapsed;
                }
                startDspTime = now - sample / (double)source.clip.frequency;
                clockSynchronized = true;
                elapsed = now - startDspTime;
            }
            // 同期後はDSPで連続させ、練習のループや音源終了後の余韻も巻き戻さない。
            return elapsed;
        }
    }

    public double Duration => source != null && source.clip != null ? source.clip.length : 0.0;

    void Awake()
    {
        EnsureSource();
    }

    private void EnsureSource()
    {
        if (source == null) source = GetComponent<AudioSource>();
    }

    public void Play()
    {
        PlayScheduled(AudioSettings.dspTime + startDelay);
    }

    // カウントインなど別演出と同じ DSP 時刻へ、曲開始を正確に揃える。
    // offsetSeconds を渡すと、音源のその位置から鳴らす(dspStartTime は音が鳴り始める時刻)。範囲外・音源なしは頭から。
    public void PlayScheduled(double dspStartTime, double offsetSeconds = 0)
    {
        EnsureSource();
        source.Stop();
        paused = pausedByApplication = false;
        HeldByTracking = false;
        clockSynchronized = false;
        if (double.IsNaN(dspStartTime) || double.IsInfinity(dspStartTime)) dspStartTime = AudioSettings.dspTime;
        double playAt = System.Math.Max(dspStartTime, AudioSettings.dspTime + 0.01);
        SetStartOffset(offsetSeconds);
        startDspTime = playAt - startOffset;
        if (source.clip != null)
        {
            if (startSample > 0) source.timeSamples = startSample;
            source.PlayScheduled(playAt);
        }
        else
        {
            // 音源が無くてもゲームは進行させる（無音プレイ）
            Debug.LogWarning("SongPlayer: clip is null - running silent");
        }
        scheduled = true;
    }

    // 鳴らし始める位置をサンプルにそろえる(丸めた位置を本当の開始位置にする)。最後の1秒より後ろ・音源なし・不正な値は頭から。
    void SetStartOffset(double seconds)
    {
        startOffset = 0; startSample = 0;
        var clip = source != null ? source.clip : null;
        if (clip == null || clip.frequency <= 0 || !(seconds > 0) || double.IsInfinity(seconds)) return;
        double sample = System.Math.Round(seconds * clip.frequency);
        if (sample <= 0 || sample >= clip.samples - clip.frequency) return;
        startSample = (int)sample;
        startOffset = startSample / (double)clip.frequency;
    }

    public void Stop()
    {
        if (source != null) source.Stop();
        scheduled = false;
        clockSynchronized = false;
        paused = pausedByApplication = false;
        HeldByTracking = false;
        startOffset = 0; startSample = 0;
    }

    // OSによる中断と音声を同じ時計で止め、復帰直後の大量MISSを避ける。
    public void Pause()
    {
        if (!scheduled || paused) return;
        pausedSongTime = SongTime;
        pausedBeforeStart = pausedSongTime < startOffset;
        paused = true;
        if (source != null)
        {
            if (pausedBeforeStart) source.Stop();
            else source.Pause();
        }
    }

    public void Resume()
    {
        if (!scheduled || !paused) return;
        startDspTime = AudioSettings.dspTime - pausedSongTime;
        paused = false;
        if (source != null && source.clip != null)
        {
            if (pausedBeforeStart)
            {
                if (startSample > 0) source.timeSamples = startSample;
                source.PlayScheduled(startDspTime + startOffset);
            }
            else source.UnPause();
        }
    }

    void OnApplicationPause(bool value)
    {
        if (value)
        {
            pausedByApplication = scheduled && !paused;
            if (pausedByApplication) Pause();
        }
        else if (pausedByApplication)
        {
            pausedByApplication = false;
            if (!HeldByTracking) Resume();
        }
    }

    void OnDisable() { Stop(); }
}
