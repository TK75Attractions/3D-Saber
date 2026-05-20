using System.Collections.Generic;
using UnityEngine;

// ロングノーツの各カットで上行するチャイム音を鳴らす。
// NoteSpawner.OnNoteSpawned で全ノーツを購読し、ロングノーツの OnPartialCut で発火。
// 音は JudgmentSfx.Beep を使ってペンタトニックの度数で生成。
[RequireComponent(typeof(AudioSource))]
public class LongNoteCutSfx : MonoBehaviour
{
    [Range(0f, 1f)] public float volume = 0.55f;
    public float baseFrequency = 440f;   // 1カット目の周波数（A4）
    public float toneDurationSec = 0.18f;
    public float pitchPerCutSemitones = 0f; // 0=ペンタトニック表（既定）、>0 ならその半音刻み

    private AudioSource source;
    private NoteSpawner bound;
    // 半音指数（ペンタトニック上行）。長すぎる場合は最後の値を保ち続ける。
    private static readonly int[] PentatonicSteps = { 0, 4, 7, 12, 16, 19, 24, 28, 31, 36 };
    // (周波数,長さ) で生成済みクリップをキャッシュして毎回 alloc しない。
    private readonly Dictionary<long, AudioClip> clipCache = new Dictionary<long, AudioClip>();

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
    }

    public void Bind(NoteSpawner spawner)
    {
        if (bound != null)
        {
            bound.OnNoteSpawned -= HandleSpawned;
        }
        bound = spawner;
        if (bound != null)
        {
            bound.OnNoteSpawned += HandleSpawned;
        }
    }

    void OnDestroy()
    {
        Bind(null);
    }

    private void HandleSpawned(CuttableNote note)
    {
        if (note == null) return;
        if (note.RequiredCutCount <= 1) return; // ロング以外は無視
        note.OnPartialCut += HandlePartialCut;
    }

    private void HandlePartialCut(CuttableNote note, int cutIndex, int total)
    {
        // 最終カット時は JudgmentSfx が tier 音を鳴らすので、ここはそれ以前のみ。
        if (cutIndex >= total - 1) return;

        float freq = FrequencyFor(cutIndex);
        var clip = GetOrCreateClip(freq, toneDurationSec);
        if (clip != null) source.PlayOneShot(clip, volume);
    }

    public float FrequencyFor(int cutIndex)
    {
        float semitones;
        if (pitchPerCutSemitones > 0f)
        {
            semitones = pitchPerCutSemitones * cutIndex;
        }
        else
        {
            int i = Mathf.Clamp(cutIndex, 0, PentatonicSteps.Length - 1);
            semitones = PentatonicSteps[i];
        }
        return baseFrequency * Mathf.Pow(2f, semitones / 12f);
    }

    private AudioClip GetOrCreateClip(float freq, float duration)
    {
        long key = ((long)Mathf.RoundToInt(freq * 10f) << 16) | (long)Mathf.RoundToInt(duration * 1000f);
        if (clipCache.TryGetValue(key, out var c)) return c;
        c = JudgmentSfx.Beep(freq, duration);
        clipCache[key] = c;
        return c;
    }
}
