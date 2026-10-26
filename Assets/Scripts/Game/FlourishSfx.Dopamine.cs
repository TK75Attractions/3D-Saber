using System;
using UnityEngine;
using static SfxLab;

public static partial class FlourishSfx
{
    static volatile float[] perfectSource;
    public static int PerfectSampleRate { get; private set; } = 48000;

    public static string PerfectCutName(int index) => "PerfectCut.V1." + index;
    public static string FinaleName(VoltageMusicKey key) => "Finale." + key.Tonic + (key.Minor ? ".minor" : ".major");
    public static double PerfectSemitones(int index) => new[] { -1.0, -.35, .35, 1.0 }[index];

    // Resources と GetData は主スレッドだけ。裏の Prewarm は読み取った PCM だけを使う。
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    public static void PreparePerfectSource()
    {
        if (perfectSource == null)
        {
            var clip = Resources.Load<AudioClip>("Audio/SFX/Saber_NoteCut_Perfect");
            if (clip == null || clip.samples <= 0) return;
            var source = new float[clip.samples * clip.channels];
            if (!clip.GetData(source, 0)) return;
            var mono = new float[clip.samples];
            for (int i = 0; i < mono.Length; i++)
            {
                for (int c = 0; c < clip.channels; c++) mono[i] += source[i * clip.channels + c];
                mono[i] /= clip.channels;
            }
            PerfectSampleRate = clip.frequency;
            perfectSource = mono;
        }
        PrewarmPerfectCuts();
    }

    static void PrewarmPerfectCuts()
    {
        var source = perfectSource;
        if (source == null) return;
        for (int i = 0; i < 4; i++)
        {
            int variant = i;
            Cached(PerfectCutName(i), () => PerfectCutVariant(source, variant));
        }
    }

    public static void PrewarmDopamine()
    {
        PrewarmPerfectCuts();
        for (int tonic = 0; tonic < 12; tonic++)
            for (int minor = 0; minor < 2; minor++)
            {
                var key = new VoltageMusicKey(tonic, minor != 0);
                Cached(FinaleName(key), () => FinaleChord(key));
            }
    }

    // 高さを±1半音、長さを約±6%だけ変える。ピークと RMS は元の PCM にそろえる。
    public static float[] PerfectCutVariant(float[] source, int variant)
    {
        if (source == null || source.Length < 2) throw new ArgumentException(nameof(source));
        double rate = Math.Pow(2, PerfectSemitones(variant) / 12);
        var result = new float[Math.Max(2, (int)Math.Round(source.Length / rate))];
        double energy = 0, peak = 0, changedPeak = 0;
        foreach (float value in source)
        {
            peak = Math.Max(peak, Math.Abs(value));
            energy += value * value;
        }
        for (int i = 0; i < result.Length; i++)
        {
            double at = Math.Min(source.Length - 1, i * rate);
            int a = (int)at, b = Math.Min(source.Length - 1, a + 1);
            result[i] = (float)(source[a] + (source[b] - source[a]) * (at - a));
            changedPeak = Math.Max(changedPeak, Math.Abs(result[i]));
        }
        if (peak == 0 || changedPeak == 0) return result;
        double target = energy / source.Length / (peak * peak), lo = .1, hi = 10;
        for (int iteration = 0; iteration < 28; iteration++)
        {
            double power = (lo + hi) * .5, sum = 0;
            foreach (float value in result)
                sum += Math.Pow(Math.Abs(value) / changedPeak, 2 * power);
            if (sum / result.Length > target) lo = power; else hi = power;
        }
        double exponent = (lo + hi) * .5;
        for (int i = 0; i < result.Length; i++)
            result[i] = (float)(Math.Sign(result[i]) * peak *
                Math.Pow(Math.Abs(result[i]) / changedPeak, exponent));
        return result;
    }

    public static float[] FinaleChord(VoltageMusicKey key)
    {
        var notes = key.ChordMidi();
        var layers = new Layer[notes.Length];
        for (int i = 0; i < notes.Length; i++)
            layers[i] = new Layer(Bell(.9, Midi(notes[i]), .3, .3), 0, -i);
        return Finish(FadeTail(Mix(.9, layers), .05), -8);
    }
}
