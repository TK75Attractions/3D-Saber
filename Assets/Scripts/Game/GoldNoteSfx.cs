using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 金ノーツ（CuttableNote.IsGold）を切った瞬間に「豪華なチャイム」を鳴らす。
// A メジャー和音（A4/C#5/E5）の同時発音 + 少し遅れて1オクターブ上のスパークル。
[RequireComponent(typeof(AudioSource))]
public class GoldNoteSfx : MonoBehaviour
{
    [Range(0f, 1f)] public float volume = 0.6f;
    public float chordDuration = 0.55f;
    public float sparkleDelay = 0.16f;
    public float sparkleDuration = 0.35f;

    // A メジャー和音
    public float[] chordFrequencies = { 440f, 554.37f, 659.25f };
    // スパークル：A6 と E6 の2音
    public float[] sparkleFrequencies = { 1318.51f, 1760f };

    private AudioSource source;
    private NoteSpawner bound;
    private readonly Dictionary<long, AudioClip> clipCache = new Dictionary<long, AudioClip>();

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
        // 和音を同時発音（音量は揃いすぎないよう少しデクレッシェンド）
        float[] vols = { 1.0f, 0.85f, 0.7f };
        for (int i = 0; i < chordFrequencies.Length; i++)
        {
            var clip = GetOrCreate(chordFrequencies[i], chordDuration);
            float v = volume * (i < vols.Length ? vols[i] : 0.6f);
            if (clip != null) source.PlayOneShot(clip, v);
        }
        StartCoroutine(PlaySparkleDelayed());
    }

    IEnumerator PlaySparkleDelayed()
    {
        yield return new WaitForSeconds(sparkleDelay);
        float[] vols = { 0.55f, 0.4f };
        for (int i = 0; i < sparkleFrequencies.Length; i++)
        {
            var clip = GetOrCreate(sparkleFrequencies[i], sparkleDuration);
            float v = volume * (i < vols.Length ? vols[i] : 0.4f);
            if (clip != null) source.PlayOneShot(clip, v);
        }
    }

    private AudioClip GetOrCreate(float freq, float duration)
    {
        long key = ((long)Mathf.RoundToInt(freq * 10f) << 16) | (long)Mathf.RoundToInt(duration * 1000f);
        if (clipCache.TryGetValue(key, out var c)) return c;
        c = JudgmentSfx.Beep(freq, duration);
        clipCache[key] = c;
        return c;
    }
}
