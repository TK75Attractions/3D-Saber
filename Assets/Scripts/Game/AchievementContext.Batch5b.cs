using System;
using System.Collections.Generic;
using System.Globalization;

public sealed partial class AchievementContext
{
    // fes の、今日より前の clear の総数。同期の履歴が欠けていないか照合する。不明は -1。
    public int ClearsBeforeTodayBatch5b = -1;

    public static int CountClearsBeforeTodayBatch5b(string ns, DateTime ended, Func<string, long, int> countSince)
    {
        if (ns != OperatorMode.FestivalNamespace || ended == default || countSince == null) return -1;
        int all = countSince(ns, 0);
        int today = countSince(ns, new DateTimeOffset(ended.Date).ToUnixTimeMilliseconds());
        return all >= 0 && today >= 0 && all >= today ? all - today : -1;
    }

    bool HasHistoryBatch5b => Plays != null && !string.IsNullOrEmpty(RunId) && !string.IsNullOrEmpty(SongId)
        && (Station == "A" || Station == "B") && EndedLocal != default
        && EndedUnixMs > 0 && EndedUnixMs <= RankingSyncLog.MaxUnixMilliseconds;

    IEnumerable<RankingPlay> PlaysBatch5b()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var play in Plays)
            if (play != null && !string.IsNullOrEmpty(play.id) && play.id != RunId
                && (play.ns ?? "") == (Namespace ?? "") && (play.st == "A" || play.st == "B")
                && play.at > 0 && play.at <= RankingSyncLog.MaxUnixMilliseconds && ids.Add(play.id))
                yield return play;
    }

    static bool BeforeBatch5b(RankingPlay play, long at, string id) =>
        play.at < at || (play.at == at && string.CompareOrdinal(play.id, id) < 0);

    // Neighbor と同じく、終了時刻がいちばん近い1回。同じ差なら ID で決める。
    public RankingPlay NeighborBatch5b()
    {
        if (!HasHistoryBatch5b) return null;
        RankingPlay found = null;
        long best = (long)(AchievementCatalog.DuetSeconds * 1000);
        foreach (var play in PlaysBatch5b())
        {
            if (play.st == Station) continue;
            long gap = Math.Abs(EndedUnixMs - play.at);
            if (gap > best || (gap == best && found != null && string.CompareOrdinal(play.id, found.id) >= 0)) continue;
            found = play;
            best = gap;
        }
        return found;
    }

    public bool DaynightBatch5b()
    {
        var other = NeighborBatch5b();
        if (other == null) return false;
        string morning = RushESecretState.MorningMemory.SongId, night = RushESecretState.TwoTwentyThreeLight.SongId;
        return (SongId == morning && other.song == night) || (SongId == night && other.song == morning);
    }

    RankingPlay PreviousBatch5b(string station, long at, string id)
    {
        RankingPlay found = null;
        foreach (var play in PlaysBatch5b())
            if (play.st == station && BeforeBatch5b(play, at, id)
                && (found == null || BeforeBatch5b(found, play.at, play.id))) found = play;
        return found;
    }

    public bool SongswapBatch5b()
    {
        var other = NeighborBatch5b();
        if (other == null || string.IsNullOrEmpty(other.song) || other.song == SongId) return false;
        var mineBefore = PreviousBatch5b(Station, EndedUnixMs, RunId);
        var otherBefore = PreviousBatch5b(other.st, other.at, other.id);
        if (mineBefore == null || otherBefore == null || mineBefore.song != other.song || otherBefore.song != SongId) return false;
        long first = Math.Min(mineBefore.at, otherBefore.at), last = Math.Max(EndedUnixMs, other.at);
        return last - first <= AchievementCatalog.SongswapSeconds * 1000;
    }

    public bool SyncBatch5b()
    {
        var other = NeighborBatch5b();
        return other != null && other.score >= 0 && other.score <= ScoreManager.MaxScore
            && !string.IsNullOrEmpty(Difficulty) && other.song == SongId
            && string.Equals(other.diff, Difficulty, StringComparison.OrdinalIgnoreCase)
            && PlayRankHelper.FromScore(other.score) == Rank;
    }

    // 表裏は条件を満たす相手が窓の中にいればよい(NeighborFinishedSameSong と同じ)。
    public bool OmoteuraBatch5b()
    {
        if (!HasHistoryBatch5b) return false;
        bool ordinary = OrdinaryBatch5b(Difficulty);
        if (!ordinary && !Secret) return false;
        foreach (var play in PlaysBatch5b())
            if (play.st != Station && play.song == SongId
                && Math.Abs(EndedUnixMs - play.at) <= AchievementCatalog.DuetSeconds * 1000
                && ((ordinary && RushESecretState.IsSecretDifficulty(play.diff)) || (Secret && OrdinaryBatch5b(play.diff))))
                return true;
        return false;
    }

    static bool OrdinaryBatch5b(string difficulty) =>
        string.Equals(difficulty, AchievementCatalog.EasyDifficulty, StringComparison.OrdinalIgnoreCase)
        || string.Equals(difficulty, AchievementCatalog.NormalDifficulty, StringComparison.OrdinalIgnoreCase)
        || string.Equals(difficulty, AchievementCatalog.HardDifficulty, StringComparison.OrdinalIgnoreCase);

    bool TodayCompleteBatch5b()
    {
        if (!HasHistoryBatch5b || ClearsToday < 0) return false;
        string today = EndedLocal.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        int count = 0;
        foreach (var play in PlaysBatch5b())
            if (play.date == today && play.ach?.Contains(AchievementCatalog.ClearId) == true) count++;
        return count == ClearsToday;
    }

    bool PastCompleteBatch5b()
    {
        if (!HasHistoryBatch5b || Namespace != OperatorMode.FestivalNamespace || ClearsBeforeTodayBatch5b < 0) return false;
        int count = 0;
        foreach (var play in PlaysBatch5b())
            if (DateTime.TryParseExact(play.date, "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < EndedLocal.Date && play.ach?.Contains(AchievementCatalog.ClearId) == true) count++;
        return count == ClearsBeforeTodayBatch5b;
    }

    public bool HonjitsuBatch5b()
    {
        if (!TodayCompleteBatch5b()) return false;
        string today = EndedLocal.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        foreach (var play in PlaysBatch5b())
            if (play.date == today && play.song == SongId && BeforeBatch5b(play, EndedUnixMs, RunId)) return false;
        return true;
    }

    bool PreviousFestivalDayBatch5b(out DateTime day)
    {
        day = default;
        if (!PastCompleteBatch5b()) return false;
        foreach (var play in PlaysBatch5b())
            if (DateTime.TryParseExact(play.date, "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && date < EndedLocal.Date && date > day) day = date;
        return day != default;
    }

    public bool KinouKoeBatch5b()
    {
        if (!TodayCompleteBatch5b() || !PreviousFestivalDayBatch5b(out var day)) return false;
        string previous = day.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        string today = EndedLocal.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        int before = 0, yesterday = 0;
        foreach (var play in PlaysBatch5b())
        {
            if (play.ach?.Contains(AchievementCatalog.ClearId) != true) continue;
            if (play.date == previous) yesterday++;
            if (play.date == today && BeforeBatch5b(play, EndedUnixMs, RunId)) before++;
        }
        return yesterday > 0 && before == yesterday;
    }

    public bool KinouImaBatch5b()
    {
        if (!PreviousFestivalDayBatch5b(out var day) || string.IsNullOrEmpty(Difficulty)) return false;
        string previous = day.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        foreach (var play in PlaysBatch5b())
            if (play.date == previous && play.song == SongId
                && string.Equals(play.diff, Difficulty, StringComparison.OrdinalIgnoreCase)
                && Math.Abs((RankingSyncLog.LocalTime(play) - day).TotalMilliseconds - EndedLocal.TimeOfDay.TotalMilliseconds)
                    <= AchievementCatalog.KinouWindowMs) return true;
        return false;
    }

    public bool HitoributaiBatch5b()
    {
        if (!TodayCompleteBatch5b() || Stats == null) return false;
        int duration = Stats.DurationMsBatch5b(EndedLocal);
        if (duration <= 0) return false;
        long start = EndedUnixMs - duration;
        string today = EndedLocal.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        int before = 0;
        foreach (var play in PlaysBatch5b())
        {
            if (play.date == today && play.at < start && play.ach?.Contains(AchievementCatalog.ClearId) == true) before++;
            if (play.st == Station || play.at < start) continue;
            // 終了が開始より後なら、相手の開始も調べる。長さが不明なら空席と決めない。
            if (play.dur <= 0 || play.dur > AchievementCatalog.Batch5bMaxDurationMs || play.dur >= play.at
                || play.at - play.dur <= EndedUnixMs) return false;
        }
        return before > AchievementCatalog.HitoributaiPreviousClears;
    }

    public bool ShushifuBatch5b()
    {
        if (!NoteStats) return false;
        var other = NeighborBatch5b();
        long last = Stats.LastCutUnixMsBatch5b(EndedLocal);
        return other != null && other.cuts > 0 && other.lc > 0 && other.lc <= other.at && last > 0
            && Math.Abs(last - other.lc) <= AchievementCatalog.ShushifuWindowMs;
    }

    public bool FushimeBatch5b()
    {
        if (!NoteStats || !TodayCompleteBatch5b() || !PastCompleteBatch5b()) return false;
        int cuts = Stats.CutNotesBatch5b;
        if (cuts <= 0) return false;
        long before = 0;
        foreach (var play in PlaysBatch5b())
        {
            if (!BeforeBatch5b(play, EndedUnixMs, RunId)) continue;
            if (play.cuts < 0) return false;
            before += play.cuts;
        }
        // 「超える」ので、ちょうど5万回はまだ取れず、5万1回になった回で取れる。
        return (before + cuts - 1) / AchievementCatalog.FushimeCuts
            > Math.Max(0L, before - 1) / AchievementCatalog.FushimeCuts;
    }
}
