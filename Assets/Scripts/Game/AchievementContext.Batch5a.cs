using System;
using System.Collections.Generic;

public sealed partial class AchievementContext
{
    // 練習を途中でやめた人も含む、同じ来場者の履歴。分からない回は「勘がいい」を出さない。
    public bool PracticeHistoryKnown, PracticeSeen;

    public bool Batch5aSameScore()
    {
        if (AimMode || !RankingAvailable || Score < 0 || Score >= ScoreManager.MaxScore || Plays == null
            || string.IsNullOrEmpty(RunId) || string.IsNullOrEmpty(SongId) || string.IsNullOrEmpty(Difficulty)) return false;
        foreach (var play in Plays)
            if (play != null && !string.IsNullOrEmpty(play.id) && play.id != RunId
                && (play.ns ?? "") == (Namespace ?? "") && play.song == SongId
                && string.Equals(play.diff, Difficulty, StringComparison.OrdinalIgnoreCase) && play.score == Score)
                return true;
        return false;
    }

    // 5けたの点だけ。先頭の0で埋めず、Dの1000点刻みも使わない。
    int[] Batch5aScoreDigits()
    {
        if (AimMode || Score < AchievementCatalog.PatternScoreMin || Score > AchievementCatalog.PatternScoreMax) return null;
        var counts = new int[10];
        int value = Score;
        for (int i = 0; i < AchievementCatalog.PatternScoreDigits; i++) { counts[value % 10]++; value /= 10; }
        return counts;
    }

    public bool Batch5aEvenScore()
    {
        var counts = Batch5aScoreDigits();
        if (counts == null) return false;
        for (int i = 1; i < counts.Length; i += 2) if (counts[i] != 0) return false;
        return true;
    }

    public bool Batch5aFullHouse()
    {
        var counts = Batch5aScoreDigits();
        if (counts == null) return false;
        bool two = false, three = false;
        foreach (int count in counts) { two |= count == 2; three |= count == 3; }
        return two && three;
    }

    public bool Batch5aFiveKeys()
    {
        var counts = Batch5aScoreDigits();
        if (counts == null) return false;
        for (int i = 1; i <= AchievementCatalog.PatternScoreDigits; i++) if (counts[i] != 1) return false;
        return true;
    }

    // 同じIDは1回だけ。初物・大収穫を含め、数え合う実績はすべて除く。
    IEnumerable<AchievementUnlock> Batch5aSources()
    {
        if (Unlocked == null) yield break;
        var ignored = new HashSet<string>(AchievementCatalog.TallyIds, StringComparer.Ordinal);
        ignored.UnionWith(AchievementCatalog.Batch5aTallyIds);
        ignored.Add(AchievementCatalog.ClearId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unlock in Unlocked)
            if (unlock.Definition != null && !ignored.Contains(unlock.Id) && seen.Add(unlock.Id))
                yield return unlock;
    }

    public bool Batch5aKengou()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unlock in Batch5aSources()) ids.Add(unlock.Id);
        return NoteStats && ids.Contains("ittoryu") && ids.Contains("dual") && ids.Contains("triple");
    }

    public bool Batch5aContents()
    {
        if (Score < 0 || Score >= PlayRankHelper.ScoreLowerBound(PlayRank.B)) return false;
        int count = 0;
        foreach (var unlock in Batch5aSources()) count++;
        return count >= AchievementCatalog.ContentsMinAchievements;
    }

    public bool Batch5aMatchingOrdinals()
    {
        var numbers = new HashSet<int>();
        foreach (var unlock in Batch5aSources())
            if (unlock.Number >= AchievementCatalog.MatchingOrdinalMin && !numbers.Add(unlock.Number)) return true;
        return false;
    }

    public bool Batch5aThreeMedals()
    {
        bool bronze = false, silver = false, gold = false;
        foreach (var unlock in Batch5aSources())
        {
            bronze |= unlock.Tier == AchievementTier.Bronze;
            silver |= unlock.Tier == AchievementTier.Silver;
            gold |= unlock.Tier == AchievementTier.Gold;
        }
        return bronze && silver && gold;
    }

    public bool Batch5aOrdinalSteps()
    {
        var numbers = new HashSet<int>();
        foreach (var unlock in Batch5aSources())
            if (unlock.Number >= AchievementCatalog.StepOrdinalMin) numbers.Add(unlock.Number);
        foreach (int first in numbers)
        {
            if (first > int.MaxValue - (AchievementCatalog.StepOrdinalCount - 1)) continue;
            bool all = true;
            for (int i = 1; i < AchievementCatalog.StepOrdinalCount; i++) all &= numbers.Contains(first + i);
            if (all) return true;
        }
        return false;
    }

    public bool Batch5aTripleGold()
    {
        int count = 0;
        foreach (var unlock in Batch5aSources()) if (unlock.Tier >= AchievementTier.Gold) count++;
        return count >= AchievementCatalog.TripleGoldCount;
    }
}
