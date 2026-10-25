using System;
using System.Collections.Generic;

// 覚える譜面のノイズ区間。時計や描画を持たず、譜面を書き換えない。
// まねの2小節の間はずっとノイズをかける(2026-10-25 ユーザー「その二小節の間はずっとノイズかけて」)。
// 区間の中で切るノーツ(まね)はノイズの下、それ以外(次の見本)はノイズの上に描くので、
// 区間の終わり近くで奥から出てくる次の見本も見える(MemoryVeilMode が並び順を決める)。
public static class MemoryVeilRules
{
    public const double FadeIn = .12;
    // 区間の終わり(次の見本の頭)でちょうど消え切る。
    public const double FadeOut = .15;
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
    public static List<Interval> BuildIntervals(ChartData chart, double offsetSeconds)
    {
        var result = new List<Interval>();
        if (chart?.memory?.veils == null || !Finite(offsetSeconds)) return result;

        foreach (var veil in chart.memory.veils)
        {
            if (veil == null || !Finite(veil.time) || !Finite(veil.endTime)) continue;
            double start = veil.time / 1000.0;
            double end = veil.endTime / 1000.0;
            // ちょうど0.3秒の区間を、浮動小数点の丸めで捨てない。
            if (end - start + 1e-9 < MinimumSeconds) continue;
            float strength = Finite(veil.strength) ? (float)Math.Max(0, Math.Min(1, veil.strength)) : 0;
            result.Add(new Interval(start + offsetSeconds, end + offsetSeconds, strength));
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    // そのノーツ(届く曲時刻)がノイズの区間の中で切るもの(まね)か。区間の頭ちょうどは中、終わりちょうど(次の見本の頭)は外。
    // 1ms は譜面の時刻を小数3桁に丸めたずれ。
    public static bool Covers(IReadOnlyList<Interval> intervals, double hitSeconds)
    {
        if (intervals == null || !Finite(hitSeconds)) return false;
        foreach (var interval in intervals)
            if (hitSeconds >= interval.Start - .001 && hitSeconds < interval.End - .001) return true;
        return false;
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
