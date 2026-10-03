using TMPro;
using UnityEngine;

// リザルトの音(爽快感カタログ 山4)。
//   ・スコアを 1.5 秒でカウントアップし、刻む音を鳴らす
//   ・ランクの衝撃波(リング)に合わせて「ドン」
//   ・順位の演出が終わってから、静かなループ曲を小さく流す
// ResultReveal の経過時間で動く。クリック/キーでスキップしたら最終値へ飛び、刻む音とドンは鳴らさない。
public sealed class ResultSoundtrack : MonoBehaviour
{
    public const float CountSeconds = 1.5f;
    public const float TickInterval = .07f;
    public const float TickVolume = .14f;
    public const float DonVolume = .55f;
    public const float LoopVolume = .13f;
    public const float LoopFadeSeconds = 2.5f;
    // これより大きく時間が飛んだらスキップとみなす。
    public const float SkipJump = .5f;

    ResultReveal reveal;
    TMP_Text scoreText;
    int finalScore;
    float countStart, donTime, loopStart;
    float lastT = -1f, nextTick;
    float loopStartedAt;
    AudioSource oneShots, loopSource;
    AudioClip tickClip, donClip, loopClip;

    public int TickCount { get; private set; }
    public bool DonPlayed { get; private set; }
    public bool LoopStarted { get; private set; }
    public int ShownScore { get; private set; }
    public float LoopStart => loopStart;
    public float LoopVolumeNow => loopSource != null ? loopSource.volume : 0f;

    public static ResultSoundtrack Build(Transform parent, ResultReveal reveal, TMP_Text score, int finalScore,
        float countStart, float donTime, float loopStart)
    {
        var go = new GameObject("ResultSoundtrack");
        go.transform.SetParent(parent, false);
        var view = go.AddComponent<ResultSoundtrack>();
        view.Setup(reveal, score, finalScore, countStart, donTime, loopStart);
        return view;
    }

    void Setup(ResultReveal timeline, TMP_Text score, int final, float start, float don, float loop)
    {
        reveal = timeline; scoreText = score; finalScore = Mathf.Max(0, final);
        countStart = start; donTime = don; loopStart = Mathf.Max(don + .5f, loop);
        nextTick = countStart;
        tickClip = ProceduralSfx.Clip("ResultTick", ProceduralSfx.Tick());
        donClip = ProceduralSfx.Clip("ResultRankDon", ProceduralSfx.Don(.6f));
        loopClip = ProceduralSfx.Clip("ResultAmbientLoop", ProceduralSfx.AmbientLoop(), ProceduralSfx.LoopRate);
        if (Application.isPlaying)
        {
            oneShots = gameObject.AddComponent<AudioSource>();
            oneShots.playOnAwake = false; oneShots.spatialBlend = 0;
            loopSource = gameObject.AddComponent<AudioSource>();
            loopSource.playOnAwake = false; loopSource.spatialBlend = 0; loopSource.loop = true;
            loopSource.clip = loopClip; loopSource.volume = 0;
        }
        SetScore(0);
        if (reveal != null) reveal.TimeChanged += Tick;
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

        if (!skipped)
        {
            // 数字が動いている間だけ刻む。上がるほど少し高くする。
            while (t >= nextTick && nextTick < countStart + CountSeconds && finalScore > 0)
            {
                if (nextTick > countStart) PlayTick((nextTick - countStart) / CountSeconds);
                nextTick += TickInterval;
            }
            if (!DonPlayed && previous < donTime && t >= donTime)
            {
                DonPlayed = true;
                if (oneShots != null) { oneShots.pitch = 1f; oneShots.PlayOneShot(donClip, DonVolume); }
            }
        }
        else
        {
            // スキップ: 最終値へ飛ぶ。過ぎた刻む音とドンは鳴らさない。
            nextTick = countStart + CountSeconds;
        }

        if (!LoopStarted && t >= loopStart)
        {
            LoopStarted = true;
            loopStartedAt = Time.unscaledTime;
            if (loopSource != null) loopSource.Play();
        }
        if (LoopStarted && loopSource != null)
            loopSource.volume = LoopVolume * Mathf.Clamp01((Time.unscaledTime - loopStartedAt) / LoopFadeSeconds);
    }

    void PlayTick(float progress)
    {
        TickCount++;
        if (oneShots == null) return;
        oneShots.pitch = Mathf.Lerp(.9f, 1.35f, Mathf.Clamp01(progress));
        oneShots.PlayOneShot(tickClip, TickVolume);
    }

    void SetScore(int value)
    {
        ShownScore = value;
        if (scoreText != null) scoreText.text = value.ToString("N0");
    }

    void OnDestroy()
    {
        if (reveal != null) reveal.TimeChanged -= Tick;
        UISkinKit.SafeDestroy(tickClip);
        UISkinKit.SafeDestroy(donClip);
        UISkinKit.SafeDestroy(loopClip);
    }
}
