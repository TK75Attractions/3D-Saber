using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 金ノーツを切った瞬間に、同梱の短い金属音「シャン」を単独で鳴らす。
// 同梱音源が無い場合は従来の合成金属音を使用する。
// 2026-10-20(派手さカタログ 斬2「会心の一撃」): 本編では Perfect と Great だけ、専用の「キィン」(FlourishSfx.GoldCut)を鳴らす。
// 金を切ると G → B → D と上がる。4拍金を切らないか、Bad・Miss で戻す(普通の成功では戻さない)。
// 0.15秒遅れてスコアの所で「チャリン」。Good と Bad は JudgmentSfx が判定ごとの普通の切断音を鳴らす。
// 判定の窓に入る0.2秒前の「チリッ」は NoteTimingCue.GoldOmen を受けて鳴らす。
[RequireComponent(typeof(AudioSource))]
public class GoldNoteSfx : MonoBehaviour
{
    public AudioClip cutClip; // 未指定なら同梱の金ノーツ専用音。
    [Range(0f, 1f)] public float volume = 0.9375f;

    [Header("シング合成(パラメタを変えると次回再生時に作り直す)")]
    public float baseFrequency = 2093f;   // C7 付近の金属基音
    public float ringDuration = 0.85f;    // 金属リングの減衰時間(秒)
    public float swishDuration = 0.09f;   // 斬撃ノイズの長さ(秒)
    public float shimmerHz = 7f;          // きらめき(振幅うなり)の速さ
    public float secondHitDelay = 0.13f;  // 2打目(しゃん・しゃん)の間隔(秒)
    [Range(0f, 1f)] public float secondHitLevel = 0.55f;

    public const float CoinDelaySeconds = .15f;
    public const int CriticalSteps = 3;
    private AudioSource source, coinSource;
    private readonly AudioClip[] criticalClips = new AudioClip[CriticalSteps];
    private AudioClip coinClip, omenClip;
    private readonly AudioClip[] pairBells = new AudioClip[CriticalSteps];
    private readonly GoldChordGate chordGate = new GoldChordGate();
    private readonly Dictionary<CuttableNote, int> tracked = new Dictionary<CuttableNote, int>(128);
    public int MainSoundCount { get; private set; }
    public int PairBellCount { get; private set; }
    public int CoinCount { get; private set; }
    private ScoreManager boundScore;
    readonly GoldStaircase staircase = new GoldStaircase();
    ChartData tempoChart;
    ChartTempoMap tempo;
    private int criticalStep => staircase.NextStep;
    public int StairCount => staircase.Count;
    public int CriticalCount { get; private set; }
    public int OmenCount { get; private set; }
    // 次の会心の音の段(0〜2)。
    public int CriticalStep => criticalStep;
    // Perfect/Great で切った金ノーツ(切った位置, 段)。背景を金に寄せる・星の輪を出す演出が聞く。
    public event System.Action<Vector3, int> OnCritical;
    private AudioClip defaultCutClip;
    private bool defaultClipLoaded;
    private NoteSpawner bound;
    private AudioClip firstClip;
    private AudioClip secondClip;
    private float builtKey;

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
        ResolveCutClip();
        if (!Application.isPlaying) return;
        // 曲中に合成して処理落ちしないよう、読み込みの時点で作っておく。
        for (int i = 0; i < CriticalSteps; i++)
        {
            int step = i;
            criticalClips[i] = ProceduralSfx.Clip("GoldCritical" + i, FlourishSfx.Cached("GoldCut" + i, () => FlourishSfx.GoldCut(step)));
            pairBells[i] = ProceduralSfx.Clip("GoldPairBell" + i, FlourishSfx.Cached("GoldPairBell" + i, () => FlourishSfx.GoldPairBell(step)));
        }
        coinClip = ProceduralSfx.Clip("GoldCoinDelayed", FlourishSfx.Cached("GoldCoinDelayed", FlourishSfx.GoldCoinDelayed));
        omenClip = ProceduralSfx.Clip("GoldOmen", FlourishSfx.Cached("GoldOmen", FlourishSfx.GoldOmen));
        coinSource = gameObject.AddComponent<AudioSource>();
        coinSource.playOnAwake = false; coinSource.spatialBlend = 0; coinSource.clip = coinClip;
    }

    public static bool IsCritical(JudgmentTier tier) => tier == JudgmentTier.Perfect || tier == JudgmentTier.Great;

    public void Bind(NoteSpawner spawner)
    {
        ResetNotes();
        if (bound != null)
        {
            bound.OnNoteSpawned -= HandleSpawned;
            bound.OnChartReset -= ResetNotes;
        }
        NoteTimingCue.GoldOmen -= HandleOmen;
        bound = spawner;
        if (bound != null)
        {
            bound.OnNoteSpawned += HandleSpawned;
            bound.OnChartReset += ResetNotes;
            NoteTimingCue.GoldOmen += HandleOmen;
            foreach (var note in bound.LiveNotes) HandleSpawned(note);
        }
    }

    // 失敗だけで段を戻す。期限は曲時計から別に確かめる。
    public void BindScore(ScoreManager score)
    {
        if (boundScore != null) boundScore.OnJudgment -= HandleAnyJudgment;
        boundScore = score;
        if (boundScore != null) boundScore.OnJudgment += HandleAnyJudgment;
    }

    void HandleAnyJudgment(JudgmentTier tier, int award)
    {
        staircase.Judge(tier);
    }

    void OnDestroy()
    {
        Bind(null);
        BindScore(null);
        UISkinKit.SafeDestroy(coinSource);
        coinSource = null;
        ReleaseGeneratedClips();
        for (int i = 0; i < criticalClips.Length; i++) { UISkinKit.SafeDestroy(criticalClips[i]); criticalClips[i] = null; }
        for (int i = 0; i < pairBells.Length; i++) { UISkinKit.SafeDestroy(pairBells[i]); pairBells[i] = null; }
        UISkinKit.SafeDestroy(coinClip);
        UISkinKit.SafeDestroy(omenClip);
        coinClip = omenClip = null;
    }

    private void ReleaseGeneratedClips()
    {
        UISkinKit.SafeDestroy(firstClip);
        UISkinKit.SafeDestroy(secondClip);
        firstClip = secondClip = null;
    }

    private void HandleSpawned(CuttableNote note)
    {
        if (note == null || !note.IsGold || note.IsFinalized || tracked.ContainsKey(note)) return;
        chordGate.Reserve(bound != null ? bound.TotalNoteCount : 16);
        tracked.Add(note, chordGate.Register(note.HitTime));
        note.OnJudged += HandleJudged;
        note.OnRetired += Untrack;
        note.OnMiss += Untrack;
    }

    void Untrack(CuttableNote note)
    {
        if (note == null) return;
        note.OnJudged -= HandleJudged;
        note.OnRetired -= Untrack;
        note.OnMiss -= Untrack;
        tracked.Remove(note);
    }

    void ResetNotes()
    {
        foreach (var pair in tracked)
        {
            var note = pair.Key;
            if (note == null) continue;
            note.OnJudged -= HandleJudged;
            note.OnRetired -= Untrack;
            note.OnMiss -= Untrack;
        }
        tracked.Clear();
        chordGate.Reset();
        staircase.Reset();
        tempoChart = null; tempo = null;
        if (coinSource != null) coinSource.Stop();
    }

    // 無効中の予約音は残さない。組の履歴は復帰まで維持し、同じ組の硬貨を再発行しない。
    void OnDisable() { StopSounds(); }

    // 曲の中断でも、遅れて鳴る硬貨音を残さない。
    public void StopSounds()
    {
        StopAllCoroutines();
        if (source != null) source.Stop();
        if (coinSource != null) coinSource.Stop();
    }

    private void HandleJudged(CuttableNote note, JudgmentTier tier, Vector3 point, Vector3 velocity)
    {
        if (note == null || !tracked.TryGetValue(note, out int group)) return;
        Untrack(note);
        if (!isActiveAndEnabled || note.IsMissed || !note.IsCut) return;
        double cutTime = note.LastCutSongTime ?? CurrentSongTime;
        double beat = BeatAt(CurrentSongTime);
        staircase.Tick(beat);
        if (!IsCritical(tier))
        {
            staircase.Judge(tier);
            if (tier == JudgmentTier.Good) staircase.Gold(beat, false);
            return;
        }
        PlayCritical(point, chordGate.Decide(group, cutTime, criticalStep));
    }

    double CurrentSongTime => boundScore != null && boundScore.songPlayer != null ? boundScore.songPlayer.SongTime : 0;

    double BeatAt(double time)
    {
        var chart = bound != null ? bound.CurrentChart : null;
        if (tempo == null || !ReferenceEquals(chart, tempoChart))
        {
            tempoChart = chart;
            tempo = new ChartTempoMap(chart != null ? chart.bpm : 120, chart != null ? chart.beatZeroMs : 0, chart?.editorTempoMap);
        }
        return tempo.BeatAt((time - (bound != null ? bound.TotalOffsetSeconds : 0)) * 1000);
    }

    public void TickStaircase(double songTime) => staircase.Tick(BeatAt(songTime));

    public void PlayCritical(Vector3 point)
    {
        TickStaircase(CurrentSongTime);
        PlayCritical(point, new GoldChordGate.Decision {
            main = true, coin = true, advance = true, step = Mathf.Clamp(criticalStep, 0, CriticalSteps - 1)
        });
    }

    void PlayCritical(Vector3 point, GoldChordGate.Decision decision)
    {
        if (!isActiveAndEnabled) return;
        if (source == null) source = GetComponent<AudioSource>();
        int step = decision.step;
        staircase.Gold(BeatAt(CurrentSongTime), decision.advance);
        CriticalCount++;
        if (decision.main)
        {
            MainSoundCount++;
            AudioClip clip = cutClip != null ? cutClip : ProceduralSfx.Revive(ref criticalClips[step]);
            if (clip != null) source.PlayOneShot(clip, volume);
            else PlayLuxury();
        }
        if (decision.bell)
        {
            PairBellCount++;
            var bell = ProceduralSfx.Revive(ref pairBells[step]);
            if (source != null && bell != null) source.PlayOneShot(bell, 1f);
        }
        if (decision.coin)
        {
            CoinCount++;
            // 0.15秒の無音を持つ音源を重ね、別の組の予約を上書きしない。
            if (coinSource != null && coinClip != null)
                coinSource.PlayOneShot(ProceduralSfx.Revive(ref coinClip), 1f);
        }
        // 見た目の通知は切れた各ノーツに残す。採点・振動の経路は変更しない。
        OnCritical?.Invoke(point, step);
    }

    void HandleOmen(CuttableNote note)
    {
        if (note == null || bound == null || note.JudgmentOwner != bound) return;
        OmenCount++;
        if (source != null && omenClip != null) source.PlayOneShot(ProceduralSfx.Revive(ref omenClip), 1f);
    }

    public AudioClip ResolveCutClip()
    {
        if (cutClip != null) return cutClip;
        if (!defaultClipLoaded)
        {
            defaultCutClip = Resources.Load<AudioClip>("Audio/SFX/Saber_GoldCut");
            defaultClipLoaded = true;
        }
        return defaultCutClip;
    }

    public void PlayLuxury()
    {
        if (source == null) source = GetComponent<AudioSource>();
        AudioClip cut = ResolveCutClip();
        if (cut != null)
        {
            source.PlayOneShot(cut, volume);
            return;
        }
        EnsureClips();
        if (firstClip != null) source.PlayOneShot(firstClip, volume);
        if (secondClip != null && isActiveAndEnabled) StartCoroutine(PlaySecondDelayed());
    }

    IEnumerator PlaySecondDelayed()
    {
        yield return new WaitForSeconds(secondHitDelay);
        source.PlayOneShot(secondClip, volume * secondHitLevel);
    }

    private void EnsureClips()
    {
        // シーンに旧版がシリアライズされていた場合、新フィールドは 0 で来るので既定値へ戻す。
        if (baseFrequency < 100f) baseFrequency = 2093f;
        if (ringDuration <= 0.05f) ringDuration = 0.85f;
        if (swishDuration <= 0.005f) swishDuration = 0.09f;
        if (shimmerHz <= 0.5f) shimmerHz = 7f;
        if (secondHitDelay <= 0.01f) secondHitDelay = 0.13f;
        if (secondHitLevel <= 0.01f) secondHitLevel = 0.55f;

        float key = baseFrequency * 1000f + ringDuration * 100f + swishDuration * 10f + shimmerHz;
        if (firstClip != null && Mathf.Approximately(key, builtKey)) return;
        // 作り直す前の遅延音と合成バッファを残さない。共有AudioSourceは停止しない。
        StopAllCoroutines();
        ReleaseGeneratedClips();
        builtKey = key;
        firstClip = BuildShingClip("gold_shing", baseFrequency, ringDuration, swishDuration, shimmerHz, 12345);
        secondClip = BuildShingClip("gold_shing2",
            baseFrequency * 1.22f, ringDuration * 0.6f, swishDuration * 0.6f, shimmerHz * 1.3f, 54321);
    }

    // 純粋関数: 斬撃ノイズ + 非整数倍音の金属リングで「シャリーン」を合成する。テストから直接呼べる。
    // 整数倍音を避けた部分音(ベル/ブレード系)と、部分音ごとに位相をずらした振幅うなりが
    // 「しゃんしゃん」した金属のきらめきを作る。
    public static AudioClip BuildShingClip(
        string name, float baseFreq, float ringDuration, float swishDuration, float shimmerHz, int seed)
    {
        const int sampleRate = 44100;
        float total = Mathf.Max(0.1f, ringDuration + 0.05f);
        int samples = Mathf.Max(1, (int)(sampleRate * total));
        var data = new float[samples];

        float[] ratios = { 1.00f, 1.34f, 1.71f, 2.15f, 2.64f, 3.36f };
        float[] amps = { 1.00f, 0.72f, 0.55f, 0.40f, 0.28f, 0.18f };
        var phases = new float[ratios.Length];
        var rng = new System.Random(seed);
        for (int k = 0; k < phases.Length; k++) phases[k] = (float)(rng.NextDouble() * Mathf.PI * 2.0);

        float prevNoise = 0f;
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            float attack = Mathf.Clamp01(t / 0.003f);

            // 金属リング: 高次部分音ほど速く減衰
            float ring = 0f;
            for (int k = 0; k < ratios.Length; k++)
            {
                float decay = Mathf.Exp(-t * (3.0f + 2.2f * k) / Mathf.Max(0.05f, ringDuration));
                float shimmer = 1f + 0.35f * Mathf.Sin(2f * Mathf.PI * shimmerHz * t + phases[k]);
                ring += amps[k] * decay * shimmer * Mathf.Sin(2f * Mathf.PI * baseFreq * ratios[k] * t + phases[k]);
            }

            // 斬撃スウィッシュ: ホワイトノイズの1次差分(簡易ハイパス)を鋭く減衰
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            float highPassed = noise - prevNoise;
            prevNoise = noise;
            float swish = highPassed * Mathf.Exp(-t / Mathf.Max(0.005f, swishDuration * 0.45f)) * 0.9f;

            data[i] = (ring * 0.32f + swish * 0.5f) * attack;
        }

        // ピーク正規化(クリップ歪み防止)
        float peak = 0f;
        for (int i = 0; i < samples; i++) peak = Mathf.Max(peak, Mathf.Abs(data[i]));
        if (peak > 0.0001f)
        {
            float gain = 0.9f / peak;
            for (int i = 0; i < samples; i++) data[i] *= gain;
        }

        var clip = AudioClip.Create(name, samples, 1, sampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
