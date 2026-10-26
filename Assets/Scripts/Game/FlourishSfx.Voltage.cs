using System;
using static SfxLab;

// 音階は曲の調、明るさは倍音で作る。基音も150Hzより上に置く。
public static partial class FlourishSfx
{
    public static int VoltageVoice(int level) => level >= 4 ? 2 : level >= 3 ? 1 : 0;
    public static string VoltageBellName(int midi, int voice) => "VoltageBell." + midi + "." + voice;
    public static string VoltageChordName(VoltageMusicKey key) => "VoltageChord." + key.Tonic + (key.Minor ? ".minor" : ".major");

    public static void PrewarmVoltage()
    {
        // 同じ高さと音色は、曲・歩み・段が違っても同じ作り置きを使う。
        for (int midi = 60; midi <= 100; midi++)
            for (int voice = 0; voice < 3; voice++)
            {
                int m = midi, v = voice;
                Cached(VoltageBellName(m, v), () => VoltageBell(m, v));
            }
        for (int tonic = 0; tonic < 12; tonic++)
            for (int minor = 0; minor < 2; minor++)
            {
                var key = new VoltageMusicKey(tonic, minor != 0);
                Cached(VoltageChordName(key), () => VoltageChord(key));
            }
        Cached("VoltageBreak", VoltageBreak);
    }

    static double VoltagePartial(double hz, double time)
    {
        double phase = 2 * Math.PI * hz * time;
        double harmonic = Math.Max(1, Math.Round(3000 / hz));
        return .22 * Math.Sin(phase) + .65 * Math.Sin(harmonic * phase)
            + .13 * Math.Sin((harmonic + 1) * phase);
    }

    public static float[] VoltageBell(int midi, int voice)
    {
        var samples = new float[Len(.28)];
        double hz = Midi(midi);
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)Rate;
            double value = VoltagePartial(hz, t);
            if (voice >= 1) value += .35 * VoltagePartial(Midi(midi + 7), t);
            if (voice >= 2) value += .12 * Math.Sin(2 * Math.PI * hz * 4 * t) * Math.Exp(-t * 24);
            samples[i] = (float)(value * Math.Min(1, t / .003) * Math.Exp(-t / .055));
        }
        return Finish(FadeTail(samples, .025), -14);
    }

    public static float[] VoltageChord(VoltageMusicKey key)
    {
        var samples = new float[Len(.34)];
        int[] notes = key.ChordMidi();
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)Rate, value = 0;
            foreach (int note in notes) value += VoltagePartial(Midi(note), t);
            samples[i] = (float)(value * Math.Min(1, t / .004) * Math.Exp(-t / .075));
        }
        return Finish(FadeTail(samples, .035), -12);
    }

    public static float[] VoltageBreak()
        => Finish(FadeTail(Mix(.27,
            new Layer(Bell(.18, Midi(88), .035, .5)),
            new Layer(Bell(.18, Midi(81), .035, .5), .075)), .025), -16);
}
