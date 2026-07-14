using UnityEngine;

// 金ノーツ（CuttableNote.IsGold）を切った瞬間の専用カット音。
// お試し仕様(2026-07-14): 1600Hz→900Hz の下降スイープ + 2500Hz のベル音、150ms。
// 通常ノーツのスイープ(1200→700Hz/70ms)より高く長く、「特別に豪華な一閃」に聞こえる設計。
// 金ノーツのカット時は JudgmentSfx 側が通常音をスキップするので、この音だけが鳴る。
[RequireComponent(typeof(AudioSource))]
public class GoldNoteSfx : MonoBehaviour
{
    [Range(0f, 1f)] public float volume = 0.5f;

    [Header("カット音合成(変更すると次回再生時に作り直す)")]
    public float sweepFromHz = 1600f;
    public float sweepToHz = 900f;
    public float durationSec = 0.15f;
    public float bellHz = 2500f;
    [Range(0f, 1f)] public float bellLevel = 0.55f;

    private AudioSource source;
    private NoteSpawner bound;
    private AudioClip clip;
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
        EnsureClip();
        if (clip != null) source.PlayOneShot(clip, volume);
    }

    private void EnsureClip()
    {
        // シーンに旧版がシリアライズされていた場合、新フィールドは 0 で来るので既定値へ戻す
        if (sweepFromHz < 100f) sweepFromHz = 1600f;
        if (sweepToHz < 100f) sweepToHz = 900f;
        if (durationSec <= 0.02f) durationSec = 0.15f;
        if (bellHz < 100f) bellHz = 2500f;
        if (bellLevel <= 0.01f) bellLevel = 0.55f;

        float key = sweepFromHz * 1000f + sweepToHz * 10f + durationSec + bellHz * 0.001f + bellLevel;
        if (clip != null && Mathf.Approximately(key, builtKey)) return;
        builtKey = key;
        clip = BuildGoldCutClip(sweepFromHz, sweepToHz, durationSec, bellHz, bellLevel);
    }

    // 純粋関数: 下降スイープ + ベル(高音サイン減衰)を1クリップに合成する。テストから直接呼べる。
    public static AudioClip BuildGoldCutClip(
        float sweepFromHz, float sweepToHz, float duration, float bellHz, float bellLevel)
    {
        const int sampleRate = 44100;
        int samples = Mathf.Max(1, (int)(sampleRate * duration));
        var data = new float[samples];
        double sweepPhase = 0.0;
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            float k = duration > 0f ? Mathf.Clamp01(t / duration) : 1f;
            float attack = Mathf.Clamp01(t / 0.003f);

            // 下降スイープ(位相連続)
            float freq = Mathf.Lerp(sweepFromHz, sweepToHz, k);
            sweepPhase += 2.0 * Mathf.PI * freq / sampleRate;
            float sweep = (float)System.Math.Sin(sweepPhase) * (1f - k);

            // ベル: 指数減衰する高音サイン(τ = 長さの1/3)
            float bellEnv = Mathf.Exp(-t / Mathf.Max(0.01f, duration / 3f));
            float bell = Mathf.Sin(2f * Mathf.PI * bellHz * t) * bellEnv * bellLevel;

            data[i] = (sweep * 0.7f + bell * 0.5f) * attack;
        }

        // ピーク正規化(クリップ歪み防止)
        float peak = 0f;
        for (int i = 0; i < samples; i++) peak = Mathf.Max(peak, Mathf.Abs(data[i]));
        if (peak > 0.0001f)
        {
            float gain = 0.9f / peak;
            for (int i = 0; i < samples; i++) data[i] *= gain;
        }

        var result = AudioClip.Create("gold_cut", samples, 1, sampleRate, false);
        result.SetData(data, 0);
        return result;
    }
}
