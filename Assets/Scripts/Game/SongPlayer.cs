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
    // 音の出力先が変わって音が止まり、読み直した音へ差し替えたとき(AudioOutputRecovery)。再開では止めた位置から鳴らし直す。
    private bool restartOnResume;
    // 鳴らし直しの予約時刻と曲時刻。予約後もシーク先から実サンプルが進むまで待つ。
    private double holdUntilDsp = double.NegativeInfinity;
    private double restartSongTime;
    private int restartSample = -1;
    // 鳴らし直すときの予約の余裕(秒)。
    public const double RestartLeadSeconds = .05;

    public AudioClip Clip
    {
        get { EnsureSource(); return source.clip; }
        set { EnsureSource(); if (source.clip != value) { Stop(); source.clip = value; } }
    }

    // 開始予約中も先読み時計は動く。音が鳴っている状態とは区別する。
    public bool IsScheduled => scheduled;
    public bool IsPlaying => scheduled && !paused && DspNow() >= startDspTime + startOffset;
    public bool IsPaused => paused;
    // 鳴らし直したあと(PlayFrom)、実際の音が進むのを待って時計を止めている間。本編はこの間、判定・譜面の進行・演出の予約も止める
    // (2026-10-23 点検: 止まった時計のまま「今から0.1秒後」と予約したサビの音が、曲のサビより先に鳴った)。
    public bool IsWaitingForAudio
    {
        get { _ = SongTime; return !double.IsNegativeInfinity(holdUntilDsp); }
    }
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
            double now = DspNow();
            if (!double.IsNegativeInfinity(holdUntilDsp))
            {
                if (now < holdUntilDsp) return restartSongTime;
                var clip = source != null ? source.clip : null;
                if (restartSample >= 0 && clip != null && clip.frequency > 0)
                {
                    int sample = source.timeSamples;
                    int advanced = sample - restartSample;
                    // 折り返しをまたいだ進行分を足す。停止で0へ戻っただけなら同期しない。
                    if (source.loop && source.isPlaying && sample > 0 && advanced < 0) advanced += clip.samples;
                    if (advanced > 0)
                        startDspTime = now - (restartSongTime + advanced / (double)clip.frequency);
                    // 初回と同じ上限。残りの長さは今回のシーク先から数える。
                    else if (now - holdUntilDsp <= System.Math.Max(1.0, clip.length - restartSample / (double)clip.frequency + .25))
                        return restartSongTime;
                }
                clockSynchronized = true;
                ClearRestartWait();
            }
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

    // ---- 音の仕組みの作り直しで DSP の時計が跳んだとき(2026-10-28) ----
    // 音の出力先がサンプルレートの違う機器へ変わると、Unity は音の仕組みを作り直し、AudioSettings.dspTime は
    // 「それまでのサンプル数 ÷ 新しいレート」に跳ぶ(48 kHz → 44.1 kHz で通算の +8.8%、戻すと同じだけ後ろへ。PlayMode で実測)。
    // ゲームを30分つけていれば曲の時計が2分半先へ跳び、残りのノーツが全部 MISS になって結果へ進んでいた
    // (ユーザー「演奏中に音の出力先が変わると強制的にリザルト画面に行ってしまってる」)。
    // 作り直しの知らせ(OnAudioConfigurationChanged)が来たら、そのあと最初に時計を読むときに、前に読んだときからの DSP の進みを
    // 実時間の進みと比べ、ずれた分だけ曲の 0 秒の DSP 時刻(と鳴らし直しの予約)をずらして、曲の時計を続ける。
    // ふだんの音の遅れ(作り直しの知らせが無いとき)には触れない。曲の音は AudioOutputRecovery が読み直して今の位置から鳴らす。
    static int audioResets;
    // 作り直しの知らせの通算の数(カウントインの 3・2・1 も同じやり方で跳んだ分をずらす。GameStartCountdown)。
    public static int AudioResetCount => audioResets;
    int seenAudioResets;
    double lastDspRead = double.NaN, lastRealRead;
    // 跳んだ分を直した回数と、最後に直した量(秒。テスト用)。
    public int DspJumpsFollowed { get; private set; }
    public double LastDspJump { get; private set; }

    // 本編の立て直し(AudioOutputRecovery)より先に数えるよう、いちばん早い時点で登録する。
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ListenForAudioReset()
    {
        AudioSettings.OnAudioConfigurationChanged -= CountAudioReset;
        AudioSettings.OnAudioConfigurationChanged += CountAudioReset;
    }

    static void CountAudioReset(bool deviceWasChanged) => audioResets++;

    // 曲の時計に使う DSP の時刻。作り直しのあとの最初の読みで、跳んだ分だけ基準をずらす。
    double DspNow()
    {
        double now = AudioSettings.dspTime;
        double real = Time.realtimeSinceStartupAsDouble;
        if (seenAudioResets != audioResets)
        {
            seenAudioResets = audioResets;
            if (scheduled && !paused && !double.IsNaN(lastDspRead))
            {
                double jump = (now - lastDspRead) - (real - lastRealRead);
                startDspTime += jump;
                if (!double.IsNegativeInfinity(holdUntilDsp)) holdUntilDsp += jump;
                LastDspJump = jump;
                DspJumpsFollowed++;
            }
        }
        lastDspRead = now;
        lastRealRead = real;
        return now;
    }

    // 基準(startDspTime)をいまの DSP の時刻から決め直したとき。前の作り直しの知らせは、もう反映済みとして扱う。
    void MarkDspClock()
    {
        seenAudioResets = audioResets;
        lastDspRead = AudioSettings.dspTime;
        lastRealRead = Time.realtimeSinceStartupAsDouble;
    }

    // テスト用: 音の仕組みの作り直しの知らせが来たことにする(本物は AudioSettings.Reset で起きる)。
    public static void SimulateAudioResetForTest() => CountAudioReset(false);

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
        paused = pausedByApplication = restartOnResume = false;
        ClearRestartWait();
        HeldByTracking = false;
        clockSynchronized = false;
        if (double.IsNaN(dspStartTime) || double.IsInfinity(dspStartTime)) dspStartTime = AudioSettings.dspTime;
        double playAt = System.Math.Max(dspStartTime, AudioSettings.dspTime + 0.01);
        SetStartOffset(offsetSeconds);
        startDspTime = playAt - startOffset;
        MarkDspClock();
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
        paused = pausedByApplication = restartOnResume = false;
        ClearRestartWait();
        HeldByTracking = false;
        startOffset = 0; startSample = 0;
    }

    // OSによる中断と音声を同じ時計で止め、復帰直後の大量MISSを避ける。
    public void Pause()
    {
        if (!scheduled || paused) return;
        pausedSongTime = SongTime;
        pausedBeforeStart = pausedSongTime < startOffset;
        bool waiting = !double.IsNegativeInfinity(holdUntilDsp);
        restartOnResume |= waiting;
        ClearRestartWait();
        paused = true;
        if (source != null)
        {
            // 待機中の予約は取り消し、再開時に保存した曲時刻から予約し直す。
            if (pausedBeforeStart || waiting) source.Stop();
            else source.Pause();
        }
    }

    public void Resume()
    {
        if (!scheduled || !paused) return;
        startDspTime = AudioSettings.dspTime - pausedSongTime;
        MarkDspClock();
        paused = false;
        bool restart = restartOnResume;
        restartOnResume = false;
        if (source != null && source.clip != null)
        {
            if (pausedBeforeStart)
            {
                if (startSample > 0) source.timeSamples = startSample;
                source.PlayScheduled(startDspTime + startOffset);
            }
            else if (restart) PlayFrom(pausedSongTime, AudioSettings.dspTime + RestartLeadSeconds);
            else source.UnPause();
        }
    }

    // 止めている間に音を差し替える(音の出力先が変わって、前の音の中身が消えたとき)。再開で止めた位置から鳴らし直す。
    // 止めていないときは、ふつうに差し替える(Clip と同じく止める)。
    public void ReplaceClipWhilePaused(AudioClip clip)
    {
        EnsureSource();
        if (!scheduled || !paused) { Clip = clip; return; }
        source.Stop();
        ClearRestartWait();
        source.clip = clip;
        restartOnResume = true;
    }

    // 音の出力先が変わって曲の音の中身が消えたあと、読み直した音へ差し替えて、時計は止めずに今の曲時刻の位置から鳴らし直す
    // (2026-10-24 ユーザー「曲止めないで」。読み直す間も時計は DSP で進んでいる)。止めている間は再開で、止めた位置から鳴らす。
    // 鳴らし始める前(カウントインの間)は、予約していた時刻のまま鳴らす。その時刻を過ぎていれば今の位置から。曲の終わりより後ろは差し替えるだけ。
    public void ReplaceClipInPlace(AudioClip clip)
    {
        EnsureSource();
        if (!scheduled) { Clip = clip; return; }
        if (paused) { ReplaceClipWhilePaused(clip); return; }
        double at = SongTime;
        source.Stop();
        ClearRestartWait();
        source.clip = clip;
        if (clip == null || clip.samples <= 0 || clip.frequency <= 0) return;
        double playAt = AudioSettings.dspTime + RestartLeadSeconds;
        if (at < startOffset && startDspTime + startOffset >= playAt)
        {
            if (startSample > 0) source.timeSamples = startSample;
            source.PlayScheduled(startDspTime + startOffset);
            return;
        }
        if (!source.loop && at >= clip.samples / (double)clip.frequency) return;
        PlayFrom(System.Math.Max(at, startOffset), playAt);
    }

    // 鳴らしている途中で音だけが止まったとき(音の出力先が変わった。中身の残っている同梱の音)、今の曲時刻の位置から鳴らし直し、
    // 実サンプルが進むまで時計もその位置で待つ。ループの周回分は曲時刻に残す。止めている間・始まる前・ループしない音の終わりより後ろでは何もしない。
    public void RestartSource()
    {
        EnsureSource();
        var clip = source.clip;
        if (!scheduled || paused || clip == null || clip.samples <= 0 || clip.frequency <= 0) return;
        // 待っている間にまた呼ばれても、待っている位置のまま(先へ動かさない)。
        double at = SongTime;
        if (at < startOffset) return;
        double length = clip.samples / (double)clip.frequency;
        if (!source.loop && at >= length) return;
        source.Stop();
        PlayFrom(at, AudioSettings.dspTime + RestartLeadSeconds);
    }

    // 曲時刻は丸めずに保存し、シーク先からの進行分を足して同期する。音の終了後は予約の余裕だけ待つ。
    void PlayFrom(double songTime, double playAt)
    {
        ClearRestartWait();
        var clip = source.clip;
        startDspTime = playAt - songTime;
        MarkDspClock();
        holdUntilDsp = playAt;
        restartSongTime = songTime;
        clockSynchronized = false;
        if (clip == null || clip.frequency <= 0 || clip.samples <= 0) return;
        double length = clip.samples / (double)clip.frequency;
        double at = source.loop ? songTime % length : songTime;
        if (at < 0 || at >= length) return;
        restartSample = Mathf.Clamp((int)System.Math.Round(at * clip.frequency), 0, clip.samples - 1);
        source.timeSamples = restartSample;
        source.PlayScheduled(playAt);
    }

    void ClearRestartWait()
    {
        holdUntilDsp = double.NegativeInfinity;
        restartSongTime = 0;
        restartSample = -1;
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
