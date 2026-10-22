using TMPro;
using UnityEngine;

// リザルトの音(爽快感カタログ 山4・賞2・賞5)。
//   ・スコアを 1.5 秒でカウントアップし、刻む音と上昇音、止まった瞬間の「チン」
//   ・ランクバッジの着地に合わせて、成績ごとの決めの音
//   ・順位の演出が終わってから、静かなループ曲を小さく流す
// ResultReveal の経過時間で動く。スキップで過ぎた効果音は鳴らさず、上昇音もすぐ止める。
public sealed class ResultSoundtrack : MonoBehaviour
{
    public const float CountSeconds = 1.5f;
    public const float TickInterval = .07f;
    public const float TickVolume = .3f;
    public const float LoopVolume = .13f;
    public const float LoopFadeSeconds = 2.5f;
    // これより大きく時間が飛んだらスキップとみなす。
    public const float SkipJump = .5f;

    ResultReveal reveal;
    TMP_Text scoreText;
    int finalScore;
    float countStart, finishTime, loopStart;
    float lastT = -1f, nextTick;
    float loopStartedAt;
    bool countSkipped;
    AudioSource oneShots, swellSource, loopSource;
    AudioClip tickClip, countSwellClip, countStopClip, finishClip, loopClip;

    public int TickCount { get; private set; }
    public int CountStopCount { get; private set; }
    public bool SwellStarted { get; private set; }
    public bool SwellActive { get; private set; }
    public bool FinishPlayed { get; private set; }
    public int FinishLevel { get; private set; }
    public bool LoopStarted { get; private set; }
    public int ShownScore { get; private set; }
    public float FinishTime => finishTime;
    public float LoopStart => loopStart;
    public float LoopVolumeNow => loopSource != null ? loopSource.volume : 0f;

    public static ResultSoundtrack Build(Transform parent, ResultReveal reveal, TMP_Text score, int finalScore,
        float countStart, float finishTime, float loopStart)
    {
        var go = new GameObject("ResultSoundtrack");
        go.transform.SetParent(parent, false);
        var view = go.AddComponent<ResultSoundtrack>();
        view.Setup(reveal, score, finalScore, countStart, finishTime, loopStart);
        return view;
    }

    void Setup(ResultReveal timeline, TMP_Text score, int final, float start, float finish, float loop)
    {
        reveal = timeline; scoreText = score; finalScore = Mathf.Max(0, final);
        countStart = start; finishTime = finish; loopStart = Mathf.Max(finish + .5f, loop);
        nextTick = countStart;
        FinishLevel = LevelForRank(PlayRankHelper.FromScore(finalScore));
        tickClip = ProceduralSfx.Clip("ResultTick",
            FlourishSfx.Cached("ResultTick", () => ProceduralSfx.Tick()));
        countSwellClip = ProceduralSfx.Clip("ResultCountSwell",
            FlourishSfx.Cached("ResultCountSwell", () => FlourishSfx.CountSwell(CountSeconds)));
        countStopClip = ProceduralSfx.Clip("ResultCountStop",
            FlourishSfx.Cached("ResultCountStop", () => FlourishSfx.CountStop()));
        finishClip = ProceduralSfx.Clip("ResultRankFinish" + FinishLevel,
            FlourishSfx.Cached("ResultRankFinish." + FinishLevel, () => FlourishSfx.RankFinish(FinishLevel)));
        loopClip = ProceduralSfx.Clip("ResultAmbientLoop",
            FlourishSfx.Cached("ResultAmbientLoop", () => ProceduralSfx.AmbientLoop()), ProceduralSfx.LoopRate);
        if (Application.isPlaying)
        {
            oneShots = gameObject.AddComponent<AudioSource>();
            oneShots.playOnAwake = false; oneShots.spatialBlend = 0; oneShots.volume = 1f;
            // 上昇音だけを止められるよう、刻む音とは再生元を分ける。
            swellSource = gameObject.AddComponent<AudioSource>();
            swellSource.playOnAwake = false; swellSource.spatialBlend = 0;
            swellSource.clip = countSwellClip; swellSource.volume = 1f; swellSource.pitch = 1f;
            loopSource = gameObject.AddComponent<AudioSource>();
            loopSource.playOnAwake = false; loopSource.spatialBlend = 0; loopSource.loop = true;
            loopSource.clip = loopClip; loopSource.volume = 0;
        }
        SetScore(0);
        if (reveal != null) reveal.TimeChanged += Tick;
        AudioOutputRecovery.Restored += RestartLoop;
    }

    // 音の出力先が変わると、流していたループ曲も止まる(AudioOutputRecovery)。流し始めていたら、その場で流し直す。
    void RestartLoop()
    {
        if (!LoopStarted || loopSource == null) return;
        loopSource.clip = ProceduralSfx.Revive(ref loopClip);
        loopSource.Play();
    }

    static int LevelForRank(PlayRank rank)
    {
        switch (rank)
        {
            case PlayRank.SPlus: return 4;
            case PlayRank.S: return 3;
            case PlayRank.A: return 2;
            case PlayRank.B: return 1;
            default: return 0;
        }
    }

    // 経過時間 t でのカウントアップの値。出だしは速く、最後はゆっくり止まる。純関数。
    public static int CountedScore(float t, int final, float start, float seconds)
    {
        if (final <= 0 || t <= start) return 0;
        float p = seconds > 0 ? Mathf.Clamp01((t - start) / seconds) : 1f;
        if (p >= 1f) return final;
        float ease = 1f - (1f - p) * (1f - p) * (1f - p);
        return Mathf.Clamp(Mathf.RoundToInt(final * ease), 0, final);
    }

    public void Tick(float t)
    {
        // 時間が戻る呼び出し(外からのスキップの後など)では表示を巻き戻さない。
        if (lastT >= 0 && t < lastT) return;
        bool skipped = lastT >= 0 ? t - lastT > SkipJump : t > countStart + SkipJump;
        float previous = lastT;
        lastT = t;

        int shown = CountedScore(t, finalScore, countStart, CountSeconds);
        if (shown != ShownScore) SetScore(shown);

        float countEnd = countStart + CountSeconds;
        if (!skipped)
        {
            if (!countSkipped && finalScore > 0)
            {
                if (!SwellStarted && previous < countStart && t >= countStart && t < countEnd)
                    StartSwell();

                // 数字が動いている間だけ刻む。上がるほど少し高くする。
                while (t >= nextTick && nextTick < countEnd)
                {
                    if (nextTick > countStart) PlayTick((nextTick - countStart) / CountSeconds);
                    nextTick += TickInterval;
                }
                if (CountStopCount == 0 && previous < countEnd && t >= countEnd)
                {
                    StopSwell();
                    CountStopCount++;
                    PlayAccent(ref countStopClip);
                }
            }
            if (!FinishPlayed && previous < finishTime && t >= finishTime)
            {
                FinishPlayed = true;
                PlayAccent(ref finishClip);
            }
        }
        else
        {
            // スキップ後に時計が進んでも、カウントの終止音を後追いで鳴らさない。
            countSkipped = true;
            nextTick = countEnd;
            StopSwell();
        }

        if (!LoopStarted && t >= loopStart)
        {
            LoopStarted = true;
            loopStartedAt = Time.unscaledTime;
            if (loopSource != null) { loopSource.clip = ProceduralSfx.Revive(ref loopClip); loopSource.Play(); }
        }
        if (LoopStarted && loopSource != null)
            loopSource.volume = LoopVolume * Mathf.Clamp01((Time.unscaledTime - loopStartedAt) / LoopFadeSeconds);
    }

    void StartSwell()
    {
        SwellStarted = true;
        SwellActive = true;
        if (swellSource != null) { swellSource.clip = ProceduralSfx.Revive(ref countSwellClip); swellSource.Play(); }
    }

    void StopSwell()
    {
        SwellActive = false;
        if (swellSource != null) swellSource.Stop();
    }

    // 作った音は鳴らす前に中身を確かめる(音の出力先が変わると消えるため。ProceduralSfx.Revive)。
    void PlayAccent(ref AudioClip clip)
    {
        if (oneShots == null) return;
        oneShots.pitch = 1f;
        oneShots.PlayOneShot(ProceduralSfx.Revive(ref clip), 1f);
    }

    void PlayTick(float progress)
    {
        TickCount++;
        if (oneShots == null) return;
        oneShots.pitch = Mathf.Lerp(.9f, 1.35f, Mathf.Clamp01(progress));
        oneShots.PlayOneShot(ProceduralSfx.Revive(ref tickClip), TickVolume);
    }

    void SetScore(int value)
    {
        ShownScore = value;
        if (scoreText != null) scoreText.text = value.ToString("N0");
    }

    void OnDestroy()
    {
        if (reveal != null) reveal.TimeChanged -= Tick;
        AudioOutputRecovery.Restored -= RestartLoop;
        StopSwell();
        if (oneShots != null) oneShots.Stop();
        if (loopSource != null) loopSource.Stop();
        UISkinKit.SafeDestroy(tickClip);
        UISkinKit.SafeDestroy(countSwellClip);
        UISkinKit.SafeDestroy(countStopClip);
        UISkinKit.SafeDestroy(finishClip);
        UISkinKit.SafeDestroy(loopClip);
    }
}
