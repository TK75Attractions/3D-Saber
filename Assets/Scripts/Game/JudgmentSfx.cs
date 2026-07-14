using UnityEngine;

// ScoreManager.OnJudgment を購読し、ティアごとに判定音を鳴らす。
// AudioClip が割り付けられていなければ手続き的にビープ音を生成して使う。
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
    // 自動生成したビープ音をキャッシュ
    private AudioClip genPerfect, genGreat, genGood, genBad, genMiss;

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
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
        // 金ノーツのカットは GoldNoteSfx 専用音のみ鳴らす(通常カット音を重ねない)
        if (scoreManager != null && scoreManager.LastCutWasGold && tier != JudgmentTier.Miss) return;
        AudioClip clip = ClipFor(tier);
        if (clip == null) return;
        source.PlayOneShot(clip, volume);
    }

    public AudioClip ClipFor(JudgmentTier tier)
    {
        // 通常ノーツのカット音: 1200Hz→700Hz の下降スイープ 70ms(斬撃感のお試し仕様)。
        // ティアの違いは HUD 側で見せるため、ヒット音は共通。Miss だけ従来のブザー。
        switch (tier)
        {
            case JudgmentTier.Perfect: return perfectClip != null ? perfectClip : (genPerfect ??= Sweep(1200f, 700f, 0.07f));
            case JudgmentTier.Great:   return greatClip   != null ? greatClip   : (genGreat   ??= Sweep(1200f, 700f, 0.07f));
            case JudgmentTier.Good:    return goodClip    != null ? goodClip    : (genGood    ??= Sweep(1200f, 700f, 0.07f));
            case JudgmentTier.Bad:     return badClip     != null ? badClip     : (genBad     ??= Sweep(1200f, 700f, 0.07f));
            default:                   return missClip    != null ? missClip    : (genMiss    ??= Buzz(110f, 0.18f));
        }
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
