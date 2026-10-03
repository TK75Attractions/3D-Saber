using System.Collections.Generic;
using UnityEngine;

// ロングノーツの各カットで短い切断音を鳴らす。
// NoteSpawner.OnNoteSpawned で全ノーツを購読し、ロングノーツの OnPartialCut で発火。
// 同梱素材がない場合は従来の上行チャイムへ戻す。
[RequireComponent(typeof(AudioSource))]
public class LongNoteCutSfx : MonoBehaviour
{
    public AudioClip cutClip; // 任意。未指定なら同梱のLong専用の刻む音を使用。
    [Range(0f, 1f)] public float volume = 0.6875f;
    public float baseFrequency = 440f;   // 1カット目の周波数（A4）
    public float toneDurationSec = 0.18f;
    public float pitchPerCutSemitones = 0f; // 0=ペンタトニック表（既定）、>0 ならその半音刻み

    private AudioSource source;
    private AudioClip defaultCutClip;
    private bool defaultClipLoaded;
    private NoteSpawner bound;
    // 同時に進むロングの音程を巻き込まないよう、短い刻み音を独立した4声で鳴らす。
    const int VoiceCount = 4;
    readonly AudioSource[] voices = new AudioSource[VoiceCount];
    readonly HashSet<CuttableNote> tracked = new HashSet<CuttableNote>();
    int nextVoice;
    // 半音指数（ペンタトニック上行）。長すぎる場合は最後の値を保ち続ける。
    private static readonly int[] PentatonicSteps = { 0, 4, 7, 12, 16, 19, 24, 28, 31, 36 };
    // (周波数,長さ) で生成済みクリップをキャッシュして毎回 alloc しない。
    private readonly Dictionary<long, AudioClip> clipCache = new Dictionary<long, AudioClip>();

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
        LoadDefaultCutClip();
        for (int i = 0; i < voices.Length; i++)
        {
            var voice = new GameObject("LongTickVoice" + i);
            voice.transform.SetParent(transform, false);
            voices[i] = voice.AddComponent<AudioSource>();
            voices[i].playOnAwake = false;
        }
    }

    public void Bind(NoteSpawner spawner)
    {
        ResetNotes();
        if (bound != null)
        {
            bound.OnNoteSpawned -= HandleSpawned;
            bound.OnChartReset -= ResetNotes;
        }
        bound = spawner;
        if (bound != null)
        {
            bound.OnNoteSpawned += HandleSpawned;
            bound.OnChartReset += ResetNotes;
        }
    }

    void OnDestroy()
    {
        Bind(null);
        // キャッシュは合成音だけを所有し、外部から借りた素材は含まない。
        foreach (var clip in clipCache.Values) UISkinKit.SafeDestroy(clip);
        clipCache.Clear();
        foreach (var voice in voices) if (voice != null) UISkinKit.SafeDestroy(voice.gameObject);
    }

    private void HandleSpawned(CuttableNote note)
    {
        if (note == null) return;
        if (note.RequiredCutCount <= 1 || !tracked.Add(note)) return; // ロング以外・二重購読は無視
        note.OnPartialCut += HandlePartialCut;
        note.OnRetired += Untrack;
        note.OnMiss += Untrack;
        note.OnCut += HandleFinished;
    }

    void HandleFinished(CuttableNote note, Vector3 point, Vector3 velocity) { Untrack(note); }

    void Untrack(CuttableNote note)
    {
        if (note == null) return;
        note.OnPartialCut -= HandlePartialCut;
        note.OnRetired -= Untrack;
        note.OnMiss -= Untrack;
        note.OnCut -= HandleFinished;
        tracked.Remove(note);
    }

    void ResetNotes()
    {
        foreach (var note in tracked)
        {
            if (note == null) continue;
            note.OnPartialCut -= HandlePartialCut;
            note.OnRetired -= Untrack;
            note.OnMiss -= Untrack;
            note.OnCut -= HandleFinished;
        }
        tracked.Clear();
        StopVoices();
    }

    void StopVoices()
    {
        foreach (var voice in voices) if (voice != null) { voice.Stop(); voice.clip = null; }
        nextVoice = 0;
    }

    void OnDisable() { StopVoices(); }

    private void HandlePartialCut(CuttableNote note, int cutIndex, int total)
    {
        // 最終カット時は JudgmentSfx（または金専用音）が鳴るので、ここはそれ以前のみ。
        if (!isActiveAndEnabled || cutIndex < 0 || cutIndex >= total - 1) return;

        var clip = ClipForCut(cutIndex);
        if (clip == null || source == null) return;
        int slot = nextVoice;
        for (int i = 0; i < voices.Length; i++)
        {
            int candidate = (nextVoice + i) % voices.Length;
            if (!voices[candidate].isPlaying) { slot = candidate; break; }
        }
        var voice = voices[slot];
        nextVoice = (slot + 1) % voices.Length;
        voice.Stop();
        voice.outputAudioMixerGroup = source.outputAudioMixerGroup;
        voice.volume = source.volume * volume;
        voice.mute = source.mute;
        voice.priority = source.priority;
        voice.panStereo = source.panStereo;
        voice.spatialBlend = source.spatialBlend;
        // 合成音のフォールバックは元から上行音階なので二重に音程を上げない。
        voice.pitch = cutClip != null || defaultCutClip != null ? PitchForCut(cutIndex, total) : 1f;
        voice.clip = clip;
        voice.Play();
    }

    // 最初の打撃は原音、最後の途中打撃までに最大4半音。打数が多くても耳障りな高さにしない。
    public static float PitchForCut(int cutIndex, int total)
    {
        float progress = total > 2 ? Mathf.Clamp01(cutIndex / (float)(total - 2)) : 0f;
        return Mathf.Pow(2f, progress * 4f / 12f);
    }

    public AudioClip ClipForCut(int cutIndex)
    {
        if (cutClip != null) return cutClip;
        LoadDefaultCutClip();
        if (defaultCutClip != null) return defaultCutClip;
        return GetOrCreateClip(FrequencyFor(cutIndex), toneDurationSec);
    }

    private void LoadDefaultCutClip()
    {
        if (defaultClipLoaded) return;
        defaultCutClip = Resources.Load<AudioClip>("Audio/SFX/Saber_LongTick");
        defaultClipLoaded = true;
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
