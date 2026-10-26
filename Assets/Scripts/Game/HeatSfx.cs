using System;
using UnityEngine;

public enum HeatCue { Reject, Full, Cool }

// 波形と8声を開始時に作る。曲中は固定長の予約枠を使い、コルーチンや音源を増やさない。
public sealed class HeatSfx : IDisposable
{
    struct Request { public bool active; public HeatCue cue; public HeatHand hand; public double at; }
    readonly GameObject root;
    readonly AudioSource[] voices = new AudioSource[8];
    readonly AudioClip[] clips = new AudioClip[3];
    readonly Request[] requests = new Request[32];
    readonly int[] played = new int[3];
    int nextRequest, nextVoice;
    bool disposed;

    // EditModeでは出音の代わりに、実際に声へ渡す再生要求を数える。
    public int Played(HeatCue cue) => played[(int)cue];
    public float LastGain { get; private set; }
    public int PendingCount
    {
        get { int count = 0; foreach (var item in requests) if (item.active) count++; return count; }
    }

    public HeatSfx(Transform parent)
    {
        root = new GameObject("HeatAudio");
        root.transform.SetParent(parent, false);
        for (int i = 0; i < voices.Length; i++)
        {
            voices[i] = root.AddComponent<AudioSource>();
            voices[i].playOnAwake = false;
            voices[i].spatialBlend = 0;
            voices[i].loop = false;
        }
        for (int i = 0; i < clips.Length; i++)
        {
            clips[i] = ProceduralSfx.Clip("Heat" + (HeatCue)i, Samples((HeatCue)i));
            clips[i].hideFlags = HideFlags.DontSave;
        }
    }

    public void Schedule(HeatCue cue, HeatHand hand, double time)
    {
        if (disposed || !HeatRules.Finite(time)) return;
        requests[nextRequest] = new Request { active = true, cue = cue, hand = hand, at = time };
        nextRequest = (nextRequest + 1) % requests.Length;
    }

    public void Tick(double time)
    {
        if (disposed || !HeatRules.Finite(time)) return;
        LastGain = .7f * DisplaySettings.AccentScale;
        foreach (var voice in voices) voice.volume = LastGain;
        for (int i = 0; i < requests.Length; i++)
        {
            var item = requests[i];
            if (!item.active || item.at > time) continue;
            requests[i].active = false;
            // 大きく時刻が飛んだあとは、過ぎた演出をまとめて鳴らさない。
            if (time - item.at > .5) continue;
            var source = voices[nextVoice];
            nextVoice = (nextVoice + 1) % voices.Length;
            source.Stop();
            source.clip = ProceduralSfx.Revive(ref clips[(int)item.cue]);
            source.panStereo = item.hand == HeatHand.Left ? -.22f : .22f;
            source.pitch = 1;
            played[(int)item.cue]++;
            if (Application.isPlaying) source.Play();
        }
    }

    public static float[] Samples(HeatCue cue)
    {
        // 拒否=短い金属のジュッ、上限=低めの蒸気、冷却=柔らかいシュー。
        float seconds = cue == HeatCue.Reject ? .11f : cue == HeatCue.Full ? .18f : .22f;
        var samples = new float[Mathf.RoundToInt(seconds * ProceduralSfx.Rate)];
        uint seed = 8917u + (uint)cue * 113u;
        float low = 0, peak = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            seed = unchecked(seed * 1664525u + 1013904223u);
            float noise = (seed >> 8) / 8388608f - 1f;
            low += .18f * (noise - low);
            float t = i / (float)ProceduralSfx.Rate;
            float k = i / (float)(samples.Length - 1);
            float envelope = Mathf.Clamp01(t / .003f) * Mathf.Pow(1 - k, 1.8f);
            float value = cue == HeatCue.Reject
                ? .65f * (noise - low) + .35f * Mathf.Sin(2 * Mathf.PI * 1760 * t) * Mathf.Exp(-t * 60)
                : cue == HeatCue.Full
                    ? low + .18f * Mathf.Sin(2 * Mathf.PI * 330 * t) * Mathf.Exp(-t * 30)
                    : .65f * low + .1f * (noise - low);
            samples[i] = value * envelope;
            peak = Mathf.Max(peak, Mathf.Abs(samples[i]));
        }
        float target = cue == HeatCue.Reject ? .26f : cue == HeatCue.Full ? .2f : .14f;
        float gain = peak > 0 ? target / peak : 0;
        for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
        return samples;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Array.Clear(requests, 0, requests.Length);
        foreach (var voice in voices) if (voice != null) voice.Stop();
        root.SetActive(false);
        UISkinKit.SafeDestroy(root);
        foreach (var clip in clips) UISkinKit.SafeDestroy(clip);
    }
}
