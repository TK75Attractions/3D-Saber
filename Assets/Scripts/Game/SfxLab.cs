using System;

// 効果音を組み立てる小さな部品(派手さカタログ 2026-10-20 の試聴に使った sfxlab.js の移植)。
// UnityEngine を使わない純関数だけにして、エディターの外でも同じ音を書き出して大きさを確かめられるようにする。
// 乱数は種で決まるので、作り直しても同じ音になる。サンプリング周波数は ProceduralSfx と同じ 44100Hz。
public static class SfxLab
{
    public const int Rate = 44100;
    public enum Wave { Sine, Saw, Square, Triangle }
    public enum Filter { LowPass, HighPass, BandPass }

    // 層を時刻つきで足すときの1枚(start 秒から gainDb で重ねる)。
    public struct Layer
    {
        public float[] Samples;
        public double Start, GainDb;
        public Layer(float[] samples, double start = 0, double gainDb = 0) { Samples = samples; Start = start; GainDb = gainDb; }
    }

    // 乱数の状態はスレッドごとに持つ(起動時に裏のスレッドでまとめて作るため)。どの音も使う前に Reseed する。
    [ThreadStatic] static uint seed;
    public static void Reseed(uint value) { seed = value; }
    public static double Rnd() { seed = seed * 1664525u + 1013904223u; return seed / 4294967296.0 * 2 - 1; }

    public static int Len(double seconds) => Math.Max(1, (int)Math.Round(seconds * Rate));
    public static double DbToGain(double db) => Math.Pow(10, db / 20);
    public static double Midi(double note) => 440 * Math.Pow(2, (note - 69) / 12);

    // ---- 発音 ----

    public static float[] Osc(double seconds, double hz, Wave wave = Wave.Sine, double phase0 = 0) => Osc(seconds, t => hz, wave, phase0);

    // 周波数は時刻の関数でもよい。のこぎり波と矩形波は polyBLEP で折り返しを抑える。
    public static float[] Osc(double seconds, Func<double, double> hz, Wave wave = Wave.Sine, double phase0 = 0)
    {
        int n = Len(seconds);
        var y = new float[n];
        double phase = phase0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate, f = hz(t), dt = f / Rate;
            phase += dt; phase -= Math.Floor(phase);
            double v;
            switch (wave)
            {
                case Wave.Sine: v = Math.Sin(2 * Math.PI * phase); break;
                case Wave.Saw: v = 2 * phase - 1 - PolyBlep(phase, dt); break;
                case Wave.Square: v = (phase < .5 ? 1 : -1) + PolyBlep(phase, dt) - PolyBlep((phase + .5) % 1, dt); break;
                default: v = 1 - 4 * Math.Abs(phase - .5); break;
            }
            y[i] = (float)v;
        }
        return y;
    }

    static double PolyBlep(double t, double dt)
    {
        if (dt <= 0) return 0;
        if (t < dt) { t /= dt; return t + t - t * t - 1; }
        if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; }
        return 0;
    }

    public static float[] Noise(double seconds)
    {
        var y = new float[Len(seconds)];
        for (int i = 0; i < y.Length; i++) y[i] = (float)Rnd();
        return y;
    }

    // 正弦の和(金属音の部分音など)。位相は乱数で散らす。
    public static float[] Additive(double seconds, (double hz, double amp)[] partials, double detune = 0)
    {
        var y = new float[Len(seconds)];
        foreach (var (hz, amp) in partials)
        {
            double phase0 = Math.Abs(Rnd()) * 6.283;
            for (int i = 0; i < y.Length; i++)
            {
                double t = i / (double)Rate;
                y[i] += (float)(Math.Sin(2 * Math.PI * hz * (1 + detune * Math.Sin(t * 5.1)) * t + phase0) * amp);
            }
        }
        return y;
    }

    // ---- 包絡 ----

    public static float[] Env(float[] x, Func<double, double> envelope)
    {
        for (int i = 0; i < x.Length; i++) x[i] *= (float)envelope(i / (double)Rate);
        return x;
    }

    public static Func<double, double> AdExp(double attack, double decay)
        => t => Math.Min(1, t / Math.Max(attack, 1e-4)) * Math.Exp(-Math.Max(0, t - attack) / decay);

    // 立ち上がって切れる(逆再生シンバル風): 0→1 を power 乗で上げ、最後の release 秒で 0 へ。
    public static Func<double, double> Swell(double seconds, double power = 2.2, double release = .02)
        => t => Math.Pow(Math.Min(1, t / seconds), power) * (t > seconds - release ? Math.Max(0, (seconds - t) / release) : 1);

    // ---- フィルタ(RBJ の biquad。中心周波数を時間で動かせる) ----

    public static float[] Biquad(float[] x, Filter type, Func<double, double> hz, double q = .707)
    {
        var y = new float[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0, last = -1, b0 = 0, b1 = 0, b2 = 0, a1 = 0, a2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double f = Math.Min(Rate * .45, Math.Max(10, hz(i / (double)Rate)));
            if (f != last)
            {
                double w = 2 * Math.PI * f / Rate, c = Math.Cos(w), s = Math.Sin(w), alpha = s / (2 * q);
                double B0, B1, B2, A0 = 1 + alpha, A1 = -2 * c, A2 = 1 - alpha;
                if (type == Filter.LowPass) { B0 = (1 - c) / 2; B1 = 1 - c; B2 = (1 - c) / 2; }
                else if (type == Filter.HighPass) { B0 = (1 + c) / 2; B1 = -(1 + c); B2 = (1 + c) / 2; }
                else { B0 = alpha; B1 = 0; B2 = -alpha; }
                b0 = B0 / A0; b1 = B1 / A0; b2 = B2 / A0; a1 = A1 / A0; a2 = A2 / A0; last = f;
            }
            double v = b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v; y[i] = (float)v;
        }
        return y;
    }

    public static float[] Lp(float[] x, double hz, double q = .707) => Biquad(x, Filter.LowPass, t => hz, q);
    public static float[] Hp(float[] x, double hz, double q = .707) => Biquad(x, Filter.HighPass, t => hz, q);
    public static float[] Bp(float[] x, double hz, double q = .707) => Biquad(x, Filter.BandPass, t => hz, q);
    public static float[] Bp(float[] x, Func<double, double> hz, double q = .707) => Biquad(x, Filter.BandPass, hz, q);

    // ---- 加工 ----

    public static float[] Gain(float[] x, double gain)
    {
        for (int i = 0; i < x.Length; i++) x[i] *= (float)gain;
        return x;
    }

    public static float[] Sat(float[] x, double drive = 2)
    {
        double k = Math.Tanh(drive);
        for (int i = 0; i < x.Length; i++) x[i] = (float)(Math.Tanh(drive * x[i]) / k);
        return x;
    }

    public static float Peak(float[] x)
    {
        float p = 0;
        foreach (var v in x) p = Math.Max(p, Math.Abs(v));
        return p;
    }

    public static float[] Normalize(float[] x, double peak = 1)
    {
        float p = Peak(x);
        return p > 0 ? Gain(x, peak / p) : x;
    }

    // 簡単な残響(並列の櫛形6本+直列のオールパス4本、Schroeder)。長さは元の音+seconds。
    public static float[] Reverb(float[] x, double seconds = 1.2, double wet = .25, double damp = .35)
    {
        int n = x.Length + Len(seconds);
        var sum = new double[n];
        int[] combs = { 1557, 1617, 1491, 1422, 1277, 1356 };
        double feedback = Math.Pow(.001, combs[0] / (double)Rate / seconds);
        foreach (int length in combs)
        {
            var buffer = new double[length];
            int index = 0; double low = 0;
            for (int i = 0; i < n; i++)
            {
                double input = i < x.Length ? x[i] : 0, output = buffer[index];
                low = output * (1 - damp) + low * damp;
                buffer[index] = input + low * feedback;
                index = (index + 1) % length;
                sum[i] += output / combs.Length;
            }
        }
        foreach (int length in new[] { 556, 441, 341, 225 })
        {
            var buffer = new double[length];
            int index = 0;
            for (int i = 0; i < n; i++)
            {
                double held = buffer[index], v = -sum[i] * .5 + held;
                buffer[index] = sum[i] + held * .5;
                index = (index + 1) % length;
                sum[i] = v;
            }
        }
        var result = new float[n];
        for (int i = 0; i < n; i++) result[i] = (float)((i < x.Length ? x[i] : 0) * (1 - wet * .5) + sum[i] * wet);
        return result;
    }

    public static float[] Mix(double seconds, params Layer[] layers)
    {
        var output = new float[Len(seconds)];
        foreach (var layer in layers)
        {
            if (layer.Samples == null) continue;
            int offset = (int)Math.Round(layer.Start * Rate);
            float gain = (float)DbToGain(layer.GainDb);
            for (int i = 0; i < layer.Samples.Length && i + offset < output.Length; i++)
                if (i + offset >= 0) output[i + offset] += layer.Samples[i] * gain;
        }
        return output;
    }

    // ピークを peakDb にそろえ、最後を短くフェードする(終わりのプツッを防ぐ)。
    public static float[] Finish(float[] x, double peakDb = -4, double fade = .012)
    {
        Normalize(x, DbToGain(peakDb));
        int length = Len(fade);
        for (int i = 0; i < length && i < x.Length; i++) x[x.Length - 1 - i] *= i / (float)length;
        return x;
    }

    public static double PeakDb(float[] x) => 20 * Math.Log10(Peak(x) + 1e-12);
}
