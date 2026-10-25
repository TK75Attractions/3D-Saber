using System;
using System.Collections.Generic;

// 覚える譜面のノイズ区間。時計や描画を持たず、譜面を書き換えない。
public static class MemoryVeilRules
{
    public const string RuleCaption = "2小節の見本 → ノイズの中で同じ2小節";
    public const double FadeIn = .12;
    public const double FadeOut = .25;
    // 次の見本のノーツが奥に出る(到着 − approachTime)までにノイズを消し切る。見本は出た瞬間から見せる。
    public const double SpawnClearFactor = 1;
    public const double MinimumSeconds = .3;

    public readonly struct Interval
    {
        public readonly double Start;
        public readonly double End;
        public readonly float Strength;

        public Interval(double start, double end, float strength)
        {
            Start = start;
            End = end;
            Strength = strength;
        }
    }

    // offsetSeconds は NoteSpawner.TotalOffsetSeconds。chart.offsetMs はここで再加算しない。
    public static List<Interval> BuildIntervals(ChartData chart, double offsetSeconds, double approachSeconds)
    {
        var result = new List<Interval>();
        if (chart?.memory?.veils == null || !Finite(offsetSeconds)) return result;
        double approach = Finite(approachSeconds) ? Math.Max(0, approachSeconds) : 0;

        foreach (var veil in chart.memory.veils)
        {
            if (veil == null || !Finite(veil.time) || !Finite(veil.endTime)) continue;
            double start = veil.time / 1000.0;
            double end = veil.endTime / 1000.0;
            double nextVisible = double.PositiveInfinity;

            // 「次の見本」は、まねの区間が終わってからの最初の見えるノーツ。区間の中の veil=0
            // (説明の1回目のまねは見えるまま)で区間を削らない。1ms は小数3桁に丸めた時刻のずれ。
            if (chart.notes != null)
            {
                foreach (var note in chart.notes)
                {
                    if (note == null || note.veil != 0f || !Finite(note.time) || note.time < veil.endTime - 1f) continue;
                    nextVisible = Math.Min(nextVisible, note.TimeSeconds);
                }
            }

            end = Math.Min(end, nextVisible - approach * SpawnClearFactor);
            // ちょうど0.3秒の区間を、浮動小数点の丸めで捨てない。
            if (end - start + 1e-9 < MinimumSeconds) continue;
            float strength = Finite(veil.strength) ? (float)Math.Max(0, Math.Min(1, veil.strength)) : 0;
            result.Add(new Interval(start + offsetSeconds, end + offsetSeconds, strength));
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    // 重なる区間は強い方を使う。加算して、譜面の強さや1を超えない。
    public static float StrengthAt(IReadOnlyList<Interval> intervals, double songSeconds)
    {
        if (intervals == null || !Finite(songSeconds)) return 0;
        double strength = 0;
        foreach (var interval in intervals)
        {
            if (songSeconds <= interval.Start || songSeconds >= interval.End) continue;
            double enter = Smooth((songSeconds - interval.Start) / FadeIn);
            double leave = Smooth((interval.End - songSeconds) / FadeOut);
            strength = Math.Max(strength, interval.Strength * Math.Min(enter, leave));
        }
        return (float)Math.Max(0, Math.Min(1, strength));
    }

    static double Smooth(double value)
    {
        value = Math.Max(0, Math.Min(1, value));
        return value * value * (3 - 2 * value);
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
