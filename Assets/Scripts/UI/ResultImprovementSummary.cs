using System;
using System.Globalization;
using UnityEngine;

// 成績の次の目標を判定ルールから求める。得点やランクそのものは変更しない。
public static class ResultImprovementSummary
{
    public static long JudgedCount(int perfect, int great, int good, int bad, int miss)
        => (long)Math.Max(0, perfect) + Math.Max(0, great) + Math.Max(0, good) + Math.Max(0, bad) + Math.Max(0, miss);

    public static string Percentage(int count, long total)
        => total <= 0 ? "--" : (Math.Min(total, Math.Max(0, count)) * 100.0 / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static string NextRank(float accuracy, long judged)
    {
        if (judged <= 0) return "判定記録がありません";
        accuracy = float.IsNaN(accuracy) || float.IsInfinity(accuracy) ? 0 : Mathf.Clamp01(accuracy);
        if (!PlayRankHelper.TryNextRank(PlayRankHelper.FromAccuracy(accuracy), out var next)) return "最高ランク S+ 達成";
        double gap = Math.Ceiling(Math.Max(0, PlayRankHelper.LowerBound(next) - accuracy) * 10000 - .001) / 100;
        return PlayRankHelper.Label(next) + " まで精度あと " + gap.ToString("0.00", CultureInfo.InvariantCulture) + " ポイント";
    }

    public static string Achievement(int perfect, int great, int good, int bad, int miss)
    {
        long total = JudgedCount(perfect, great, good, bad, miss);
        if (total == 0) return "譜面を最後までプレイして記録を作ろう";
        long breaks = (long)Math.Max(0, bad) + Math.Max(0, miss);
        if (breaks > 0) return "FCを目指そう：BAD + MISS が " + breaks.ToString("N0") + " 個";
        long remaining = (long)Math.Max(0, great) + Math.Max(0, good);
        return remaining > 0 ? "FULL COMBO！ APまであと " + remaining.ToString("N0") + " 個のPERFECT" : "ALL PERFECT！ すべて最高判定";
    }

    public static string BestDifference(int score, int previousBest, bool hasRecord)
    {
        if (!hasRecord) return "FIRST RECORD";
        long difference = (long)score - previousBest;
        return difference == 0 ? "自己ベストと同点" : (difference > 0 ? "自己ベスト +" : "自己ベストまで ")
            + Math.Abs(difference).ToString("N0") + " 点";
    }

    public static string Difficulty(string name)
    {
        if (string.IsNullOrEmpty(name)) return "CHART --";
        return string.Equals(name, "Hard", StringComparison.OrdinalIgnoreCase) ? "MASTER / HARD" : name.ToUpperInvariant();
    }
}
