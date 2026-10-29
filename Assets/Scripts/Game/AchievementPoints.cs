using System;
using System.Collections.Generic;

// 実績ポイント(2026-10-29 ユーザー「実績について、なし1pt 銅3pt 銀6pt 金10pt 虹30ptとしたときの合計をその獲得ポイントとし、
// 楽曲終了後の実績のポップアップの最後にOOpt O位/O人っていう同じようなポップアップを出して」)。
// その回に取った実績の段(AchievementCatalog の Definition.Tier をその場で読む)の点の合計。知らない ID(新しい版の台から来た分)は段なしの点。
// 順位は、同じ名前空間(ふだん/運営モード)で2台が記録した回(AchievementStore の回ごとの実績)の点と比べる。同点は同じ順位、人数は回の数。
// 段の見直しで段が変わったら、前の回の点も今の段で数え直す(記録には段を残さない)。
public static class AchievementPoints
{
    public const int NonePoints = 1, BronzePoints = 3, SilverPoints = 6, GoldPoints = 10, RainbowPoints = 30;

    public struct Standing
    {
        public int Points;
        // 順位と人数(その回を入れた数)。記録が読めないときは 0(点だけ出す)。
        public int Rank, Total;
    }

    public static int For(AchievementTier tier)
    {
        switch (tier)
        {
            case AchievementTier.Bronze: return BronzePoints;
            case AchievementTier.Silver: return SilverPoints;
            case AchievementTier.Gold: return GoldPoints;
            case AchievementTier.Rainbow: return RainbowPoints;
            default: return NonePoints;
        }
    }

    public static int For(string achId)
    {
        var definition = AchievementCatalog.Find(achId);
        return For(definition != null ? definition.Tier : AchievementTier.None);
    }

    public static int Sum(IEnumerable<string> achIds)
    {
        int total = 0;
        if (achIds != null)
            foreach (string id in achIds)
                if (!string.IsNullOrEmpty(id)) total += For(id);
        return total;
    }

    // その回でいちばん高い段(ポイントのカードのふち)。
    public static AchievementTier Highest(IEnumerable<AchievementUnlock> unlocks)
    {
        var highest = AchievementTier.None;
        if (unlocks != null)
            foreach (var unlock in unlocks)
                if (unlock.Tier > highest) highest = unlock.Tier;
        return highest;
    }

    // 名前空間 ns の回 runId の点と順位。点はその回の記録から(記録に無ければ fallback の実績から)。
    public static Standing Of(string ns, string runId, IEnumerable<string> fallback)
    {
        var runs = AchievementStore.RunsIn(ns);
        var standing = new Standing
        {
            Points = runId != null && runs.TryGetValue(runId, out var mine) ? Sum(mine) : Sum(fallback),
        };
        if (runs.Count == 0) return standing;
        int higher = 0;
        foreach (var pair in runs)
            if (pair.Key != runId && Sum(pair.Value) > standing.Points) higher++;
        standing.Rank = higher + 1;
        standing.Total = runs.Count + (runId != null && runs.ContainsKey(runId) ? 0 : 1);
        return standing;
    }
}
