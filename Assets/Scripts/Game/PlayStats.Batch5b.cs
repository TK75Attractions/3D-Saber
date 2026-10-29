using System;

public sealed partial class PlayStats
{
    // 最終結果が MISS 以外のノーツ数。ロングの途中の切りは重ねず、結果が欠けていれば不明。
    public int CutNotesBatch5b
    {
        get
        {
            if (!Complete || notes.Count == 0) return -1;
            int count = 0;
            foreach (var note in notes)
            {
                if (!note.Result.HasValue) return -1;
                if (note.Cut) count++;
            }
            return count;
        }
    }

    public int DurationMsBatch5b(DateTime ended)
    {
        if (!Complete || !StartedAt.HasValue) return -1;
        long start = new DateTimeOffset(StartedAt.Value).ToUnixTimeMilliseconds();
        long end = new DateTimeOffset(ended).ToUnixTimeMilliseconds();
        long duration = end - start;
        return start > 0 && duration > 0 && duration <= AchievementCatalog.Batch5bMaxDurationMs ? (int)duration : -1;
    }

    // 切ったノーツの時刻を全部持つときだけ、最後の一太刀を返す。
    public long LastCutUnixMsBatch5b(DateTime ended)
    {
        if (CutNotesBatch5b <= 0) return -1;
        long end = new DateTimeOffset(ended).ToUnixTimeMilliseconds(), last = -1;
        long start = StartedAt.HasValue ? new DateTimeOffset(StartedAt.Value).ToUnixTimeMilliseconds() : 0;
        foreach (var note in notes)
        {
            if (!note.Cut) continue;
            if (!note.JudgedAt.HasValue) return -1;
            long at = new DateTimeOffset(note.JudgedAt.Value).ToUnixTimeMilliseconds();
            if (at <= 0 || at < start || at > end) return -1;
            last = Math.Max(last, at);
        }
        return last;
    }

    // 同期には同じ回の記録だけを渡す。D の的はノーツ数に足さない。
    public static (int cuts, long lc, int dur) SummaryBatch5b(PlayStats stats, string runId, bool aimMode, DateTime ended)
    {
        if (stats == null || string.IsNullOrEmpty(runId) || stats.RunId != runId)
            return (aimMode ? 0 : -1, -1, -1);
        return (aimMode ? 0 : stats.CutNotesBatch5b,
            aimMode ? -1 : stats.LastCutUnixMsBatch5b(ended), stats.DurationMsBatch5b(ended));
    }
}
