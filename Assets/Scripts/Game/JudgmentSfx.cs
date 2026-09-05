using UnityEngine;

// ScoreManager.OnJudgment を購読し、ティアごとに判定音を鳴らす。
// AudioClip が未指定なら同梱の切断音を使い、素材が無い場合だけ合成音へ戻す。
[RequireComponent(typeof(AudioSource))]
public class JudgmentSfx : MonoBehaviour
{
    public ScoreManager scoreManager;

    [Header("Tier clips (任意)")]
    public AudioClip perfectClip;
    public AudioClip greatClip;
    public AudioClip goodClip;
    public AudioClip badClip;
    public AudioClip missClip;

    [Header("Volume")]
    [Range(0f, 1f)] public float volume = 0.6f;

    private AudioSource source;
    private AudioClip defaultCutClip;
    private bool defaultClipLoaded;
    // 自動生成したビープ音をキャッシュ
    private AudioClip genPerfect, genGreat, genGood, genBad, genMiss;

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
        LoadDefaultCutClip();
    }

    void OnEnable()
    {
        if (scoreManager != null) scoreManager.OnJudgment += OnJudgment;
    }

    void OnDisable()
    {
        if (scoreManager != null) scoreManager.OnJudgment -= OnJudgment;
    }

    private void OnJudgment(JudgmentTier tier, int award)
    {
        // 金ノーツのカット時も判定音は鳴らす(GoldNoteSfx のシャリーンが上に重なる従来仕様)
        AudioClip clip = ClipFor(tier);
        if (clip == null) return;
        source.PlayOneShot(clip, volume);
    }

    public AudioClip ClipFor(JudgmentTier tier)
    {
        // Inspectorで指定した判定音を優先。Missには切断音を使わない。
        AudioClip cut = tier == JudgmentTier.Miss ? null : LoadDefaultCutClip();
        switch (tier)
        {
            case JudgmentTier.Perfect: return perfectClip != null ? perfectClip : (cut != null ? cut : (genPerfect ??= Beep(880f, 0.16f)));
            case JudgmentTier.Great:   return greatClip   != null ? greatClip   : (cut != null ? cut : (genGreat   ??= Beep(660f, 0.14f)));
            case JudgmentTier.Good:    return goodClip    != null ? goodClip    : (cut != null ? cut : (genGood    ??= Beep(440f, 0.12f)));
            case JudgmentTier.Bad:     return badClip     != null ? badClip     : (cut != null ? cut : (genBad     ??= Beep(220f, 0.10f)));
            default:                   return missClip    != null ? missClip    : (genMiss    ??= Buzz(110f, 0.18f));
        }
    }

    private AudioClip LoadDefaultCutClip()
    {
        if (!defaultClipLoaded)
        {
            defaultCutClip = Resources.Load<AudioClip>("Audio/SFX/Saber_NoteCut");
            defaultClipLoaded = true;
        }
        return defaultCutClip;
    }

    // 純粋関数: fromHz→toHz へ滑らかに下降(位相連続)するスイープ音を作る。
    // カット音の「シュッ」とした質感用。エンベロープは 3ms アタック + 線形減衰。
    public static AudioClip Sweep(float fromHz, float toHz, float duration)
    {
        const int sampleRate = 44100;
        int samples = Mathf.Max(1, (int)(sampleRate * duration));
        var clip = AudioClip.Create($"sweep_{fromHz:F0}_{toHz:F0}", samples, 1, sampleRate, false);
        var data = new float[samples];
        double phase = 0.0;
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            float k = duration > 0f ? Mathf.Clamp01(t / duration) : 1f;
            float freq = Mathf.Lerp(fromHz, toHz, k);
            phase += 2.0 * Mathf.PI * freq / sampleRate;
            float attack = Mathf.Clamp01(t / 0.003f);
            float decay = 1f - k;
            data[i] = (float)System.Math.Sin(phase) * attack * decay * 0.6f;
        }
        clip.SetData(data, 0);
        return clip;
    }

    // 純粋関数：周波数と長さからシンプルなサイン波 + 簡易エンベロープでクリップを作る。
    public static AudioClip Beep(float frequency, float duration)
    {
        const int sampleRate = 44100;
        int samples = Mathf.Max(1, (int)(sampleRate * duration));
        var clip = AudioClip.Create($"beep_{frequency:F0}", samples, 1, sampleRate, false);
        var data = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            // 立ち上がり 5ms、減衰は線形
            float attack = Mathf.Clamp01(t / 0.005f);
            float decay = Mathf.Clamp01(1f - t / duration);
            float env = attack * decay;
            data[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * env * 0.5f;
        }
        clip.SetData(data, 0);
        return clip;
    }

    public static AudioClip Buzz(float frequency, float duration)
    {
        const int sampleRate = 44100;
        int samples = Mathf.Max(1, (int)(sampleRate * duration));
        var clip = AudioClip.Create($"buzz_{frequency:F0}", samples, 1, sampleRate, false);
        var data = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            float attack = Mathf.Clamp01(t / 0.005f);
            float decay = Mathf.Clamp01(1f - t / duration);
            float env = attack * decay;
            // ノコギリ波風で「鈍い」音にする
            float saw = 2f * (frequency * t - Mathf.Floor(frequency * t + 0.5f));
            data[i] = saw * env * 0.4f;
        }
        clip.SetData(data, 0);
        return clip;
    }
}
