using UnityEngine;

// 演出用の効果音をコードで合成する(外部音源は使わない)。爽快感カタログ 山1・山4・サビ中の明るい余韻。
// 波形は純関数(float[])で作り、AudioClip 化は呼び出し側が行い、使い終わったら解放する。
public static class ProceduralSfx
{
    public const int Rate = 44100;

    // サビ入り・ランクの叩きつけ用の「ドン」。重さは倍音で出し、60Hz 未満の超低音は入れない(曲のキックとぶつかるため)。
    public static float[] Don(float seconds = .55f)
    {
        var s = new float[Samples(seconds)];
        double phase = 0;
        uint seed = 7;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float f = Mathf.Lerp(150f, 72f, Mathf.Clamp01(t / .09f));
            phase += 2 * System.Math.PI * f / Rate;
            float body = (float)(System.Math.Sin(phase) * .55 + System.Math.Sin(2 * phase) * .38 + System.Math.Sin(3 * phase) * .2);
            float env = Mathf.Clamp01(t / .003f) * Mathf.Exp(-t * 7.5f);
            float click = Noise(ref seed) * Mathf.Exp(-t * 160f) * .35f;
            s[i] = (body * env + click) * Tail(t, seconds);
        }
        return Normalize(s, .9f);
    }

    // TRACK CLEAR の札: 短く上がる音(下から上へのスイープ+最後のきらめき)。
    public static float[] Rise(float seconds = .62f)
    {
        var s = new float[Samples(seconds)];
        double phase = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float k = Mathf.Clamp01(t / .38f);
            float f = Mathf.Lerp(392f, 1046.5f, k * k);
            phase += 2 * System.Math.PI * f / Rate;
            float sweep = (float)(System.Math.Sin(phase) + .3 * System.Math.Sin(2 * phase)) * Mathf.Clamp01(t / .02f) * (1 - k * .4f);
            float sparkleT = t - .36f;
            float sparkle = sparkleT > 0 ? Bell(sparkleT, 2093f, 1.2f) * .5f : 0;
            s[i] = (sweep * .5f * Mathf.Exp(-Mathf.Max(0, t - .36f) * 9f) + sparkle) * Tail(t, seconds);
        }
        return Normalize(s, .8f);
    }

    // FULL COMBO は金の和音、ALL PERFECT はさらに明るい和音(高い音ときらめきを足す)。
    public static float[] Chord(bool bright, float seconds = 1.5f)
    {
        float[] notes = bright
            ? new[] { 523.25f, 659.25f, 783.99f, 987.77f, 1174.66f, 1567.98f }   // C5 E5 G5 B5 D6 G6
            : new[] { 392.00f, 493.88f, 587.33f, 783.99f };                     // G4 B4 D5 G5
        var s = new float[Samples(seconds)];
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float sum = 0;
            for (int n = 0; n < notes.Length; n++)
            {
                float onset = n * .025f; // 少しずつずらして鳴らす(ストラム)
                if (t < onset) continue;
                sum += Bell(t - onset, notes[n], bright ? 1.6f : 2.1f);
            }
            if (bright) sum += Bell(t, 3135.96f, 4f) * .35f; // きらめき
            s[i] = sum / notes.Length * Tail(t, seconds);
        }
        return Normalize(s, .8f);
    }

    // スコアのカウントアップの刻み音(短いクリック)。
    public static float[] Tick(float seconds = .028f)
    {
        var s = new float[Samples(seconds)];
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            s[i] = (float)System.Math.Sin(2 * System.Math.PI * 2400 * t) * Mathf.Exp(-t * 180f) * Mathf.Clamp01(t / .0008f);
        }
        return Normalize(s, .7f);
    }

    // サビ中に Great/Good のカット音へ足す明るい余韻(Perfect の音には元から入っている)。
    public static float[] Sparkle(float seconds = .26f)
    {
        var s = new float[Samples(seconds)];
        float[] partials = { 2093f, 2637.02f, 3135.96f };
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float sum = 0;
            for (int n = 0; n < partials.Length; n++) sum += Bell(t, partials[n], 11f) * (1 - n * .25f);
            s[i] = sum * Mathf.Clamp01(t / .006f) * Tail(t, seconds);
        }
        return Normalize(s, .6f);
    }

    // カウントイン(3・2・1)の拍の音。立ち上がり3ms・長さ70msの短い音(G5)にして、鳴ったと感じる時刻を拍の頭にそろえる。
    public static float[] CountTick(float seconds = .07f)
    {
        var s = new float[Samples(seconds)];
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float env = Mathf.Clamp01(t / .003f) * Mathf.Exp(-t * 34f);
            float body = Triangle(783.99f, t) + (float)System.Math.Sin(2 * System.Math.PI * 1567.98 * t) * .22f;
            s[i] = body * env * Tail(t, seconds);
        }
        return Normalize(s, .75f);
    }

    // カウントインで数字を斬る音。高い帯域を上へ掃く短いノイズ(90ms)。
    public static float[] CountSwish(float seconds = .09f)
    {
        var s = new float[Samples(seconds)];
        uint seed = 11;
        float low = 0, band = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float k = Mathf.Clamp01(t / seconds);
            // 状態変数フィルタで、中心が 1.5kHz → 5.5kHz へ上がる帯域だけを通す
            float g = 2f * Mathf.Sin(Mathf.PI * Mathf.Lerp(1500f, 5500f, k) / Rate);
            float high = Noise(ref seed) - low - .9f * band;
            band += g * high;
            low += g * band;
            s[i] = band * Mathf.Clamp01(t / .004f) * (1 - k * .7f) * Tail(t, seconds);
        }
        return Normalize(s, .7f);
    }

    // カウントインの START。拍の音より1オクターブ上(G6)で約6倍の長さにし、和音と「ドン」で厚くする(時報の最後の音と同じ考え方)。
    public static float[] CountStart(float seconds = .45f)
    {
        var don = Don(seconds);
        var s = new float[Samples(seconds)];
        float[] chord = { 783.99f, 987.77f, 1174.66f }; // G5 B5 D6
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)Rate;
            float lead = Triangle(1567.98f, t) * Mathf.Clamp01(t / .003f) * Mathf.Exp(-t * 6.5f);
            float sum = 0;
            for (int n = 0; n < chord.Length; n++) sum += Bell(t, chord[n], 5.5f);
            s[i] = (lead * .55f + sum / chord.Length * .6f + don[Mathf.Min(i, don.Length - 1)] * .55f) * Tail(t, seconds);
        }
        return Normalize(s, .88f);
    }

    // リザルトの静かなループ曲(柔らかい和音の4小節)。最後と最初がつながるよう、周波数をループ長で割り切れる値に丸める。
    // 高い音を含まないので LoopRate で作り、正弦は回転の漸化式で進めて読み込み時の負荷を抑える。
    public const int LoopRate = 22050;
    public static float[] AmbientLoop(float seconds = 16f)
    {
        int n = Mathf.Max(1, Mathf.RoundToInt(seconds * LoopRate));
        var s = new float[n];
        // Fmaj7 → G6 → Em7 → Am9(各 seconds/4)
        float[][] chords =
        {
            new[] { 174.61f, 261.63f, 329.63f, 440.00f },
            new[] { 196.00f, 246.94f, 329.63f, 392.00f },
            new[] { 164.81f, 246.94f, 293.66f, 392.00f },
            new[] { 220.00f, 261.63f, 329.63f, 493.88f },
        };
        float segment = seconds / chords.Length;
        for (int c = 0; c < chords.Length; c++)
        {
            // 2本をわずかにずらして重ね、2倍音を少し足した柔らかいパッド。どの周波数もループ長で割り切れる。
            var freqs = new System.Collections.Generic.List<(double hz, double gain)>();
            foreach (float hz in chords[c])
            {
                double f = LoopFrequency(hz, seconds);
                freqs.Add((f, 1.0)); freqs.Add((LoopFrequency(f * 1.004, seconds), .5)); freqs.Add((LoopFrequency(f * 2, seconds), .12));
            }
            float center = c * segment + segment * .5f;
            // 窓は中心±segment。ループの外へはみ出す分は反対側へ回り込む。
            int from = Mathf.FloorToInt((center - segment) * LoopRate), to = Mathf.CeilToInt((center + segment) * LoopRate);
            for (int k = from; k < to; k++)
            {
                int i = ((k % n) + n) % n;
                float t = i / (float)LoopRate;
                float w = ChordWindow(t, c * segment, segment, seconds);
                if (w <= 0) continue;
                // 回り込み境界(i==0)や範囲の先頭では、位相を直接計算して漸化式を始め直す。
                s[i] += (float)(Pad(freqs, i) / chords[c].Length) * w;
            }
        }
        return Normalize(s, .7f);
    }

    // 指定サンプルでのパッドの値。i ごとに直接 sin を呼ぶと重いので、連続した i では回転で進める。
    static int padIndex = -2;
    static double[] padCos, padSin, padStepCos, padStepSin;
    static System.Collections.Generic.List<(double hz, double gain)> padFreqs;
    static double Pad(System.Collections.Generic.List<(double hz, double gain)> freqs, int i)
    {
        if (!ReferenceEquals(freqs, padFreqs) || i != padIndex + 1 || i == 0)
        {
            padFreqs = freqs;
            int m = freqs.Count;
            padCos = new double[m]; padSin = new double[m]; padStepCos = new double[m]; padStepSin = new double[m];
            for (int v = 0; v < m; v++)
            {
                double w = 2 * System.Math.PI * freqs[v].hz / LoopRate;
                padCos[v] = System.Math.Cos(w * i); padSin[v] = System.Math.Sin(w * i);
                padStepCos[v] = System.Math.Cos(w); padStepSin[v] = System.Math.Sin(w);
            }
        }
        else
        {
            for (int v = 0; v < padCos.Length; v++)
            {
                double c = padCos[v] * padStepCos[v] - padSin[v] * padStepSin[v];
                padSin[v] = padSin[v] * padStepCos[v] + padCos[v] * padStepSin[v];
                padCos[v] = c;
            }
        }
        padIndex = i;
        double sum = 0;
        for (int v = 0; v < padSin.Length; v++) sum += padSin[v] * freqs[v].gain;
        return sum;
    }

    // 和音ごとの窓。隣と半分ずつ重ねた余弦の窓で、ループ全体では常に足して1になる(最後の和音は先頭へ回り込む)。
    public static float ChordWindow(float t, float start, float length, float loop)
    {
        float center = start + length * .5f;
        float d = Mathf.Abs(Mathf.Repeat(t - center + loop * .5f, loop) - loop * .5f);
        if (d >= length) return 0;
        return .5f + .5f * Mathf.Cos(Mathf.PI * d / length);
    }

    public static double LoopFrequency(double hz, float loopSeconds) => System.Math.Max(1, System.Math.Round(hz * loopSeconds)) / loopSeconds;

    public static AudioClip Clip(string name, float[] samples, int rate = Rate)
    {
        var clip = AudioClip.Create(name, Mathf.Max(1, samples.Length), 1, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    static int Samples(float seconds) => Mathf.Max(1, Mathf.RoundToInt(seconds * Rate));
    static float Bell(float t, float hz, float decay)
        => (float)(System.Math.Sin(2 * System.Math.PI * hz * t) + .25 * System.Math.Sin(2 * System.Math.PI * hz * 2.01 * t))
           * Mathf.Exp(-t * decay) * Mathf.Clamp01(t / .004f);
    static float Tail(float t, float seconds) => Mathf.Clamp01((seconds - t) / .03f);
    static float Triangle(float hz, float t) { float p = hz * t; p -= Mathf.Floor(p); return 4f * Mathf.Abs(p - .5f) - 1f; }
    static float Noise(ref uint seed) { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / 8388608f - 1f; }
    static float[] Normalize(float[] s, float peak)
    {
        float max = 0;
        foreach (var v in s) max = Mathf.Max(max, Mathf.Abs(v));
        if (max <= 0) return s;
        float g = peak / max;
        for (int i = 0; i < s.Length; i++) s[i] *= g;
        return s;
    }
}
