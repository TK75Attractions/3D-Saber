using System.Collections;
using UnityEngine;

// 金ノーツ（CuttableNote.IsGold）を切った瞬間に「シャリーン」と鳴らす。
// 以前はサイン波の和音チャイムだったが「セイバー感が無い」との評のため、
// 斬撃ノイズ(スウィッシュ) + 非整数倍音の金属リング(うなり付き)の合成音に刷新。
// しゃんしゃん感を出すため、少し高い2打目を短いディレイで重ねる。
[RequireComponent(typeof(AudioSource))]
public class GoldNoteSfx : MonoBehaviour
{
    [Range(0f, 1f)] public float volume = 0.65f;

    [Header("シング合成(パラメタを変えると次回再生時に作り直す)")]
    public float baseFrequency = 2093f;   // C7 付近の金属基音
    public float ringDuration = 0.85f;    // 金属リングの減衰時間(秒)
    public float swishDuration = 0.09f;   // 斬撃ノイズの長さ(秒)
    public float shimmerHz = 7f;          // きらめき(振幅うなり)の速さ
    public float secondHitDelay = 0.13f;  // 2打目(しゃん・しゃん)の間隔(秒)
    [Range(0f, 1f)] public float secondHitLevel = 0.55f;

    private AudioSource source;
    private NoteSpawner bound;
    private AudioClip firstClip;
    private AudioClip secondClip;
    private float builtKey;

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
    }

    public void Bind(NoteSpawner spawner)
    {
        if (bound != null) bound.OnNoteSpawned -= HandleSpawned;
        bound = spawner;
        if (bound != null) bound.OnNoteSpawned += HandleSpawned;
    }

    void OnDestroy()
    {
        Bind(null);
    }

    private void HandleSpawned(CuttableNote note)
    {
        if (note == null || !note.IsGold) return;
        note.OnCut += HandleCut;
    }

    private void HandleCut(CuttableNote note, Vector3 point, Vector3 velocity)
    {
        PlayLuxury();
    }

    public void PlayLuxury()
    {
        if (source == null) source = GetComponent<AudioSource>();
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
