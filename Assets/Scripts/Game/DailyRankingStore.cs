using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// この端末の同じ曲/難易度の全プレイを集計。人数ではなく延べプレイ数。
// 上位だけを切り捨てると圏外の順位が計算できないため、全件保持する。
// 2026-10-22: 日付で区切らず、これまでの通算で数える(ユーザーの依頼。文化祭の直前に記録を消す)。
// 2026-10-29: 文化祭は運営モードの表(名前空間 "fes"、初めは空)で数えるので、ふだんの表は消さなくてよい。
// 表の day は数え始めた日(結果画面に「○○ から」と出す)。日ごとに戻すなら TryRead の「日付が違えば新しい表」を戻す。
public static class DailyRankingStore
{
    [Serializable]
    public sealed class Entry
    {
        public string runId;
        public int score;
    }

    [Serializable]
    public sealed class Table
    {
        public int version = 1;
        public string day;
        public List<Entry> entries = new List<Entry>();
    }

    public sealed class Result
    {
        public string Day { get; internal set; }
        public int Rank { get; internal set; }
        public int TotalPlays { get; internal set; }
        public int TiedPlays { get; internal set; }
        public long PointsToNext { get; internal set; }
        public bool IsNewBest { get; internal set; }
        public bool Available => Rank > 0;
    }

    // 長さを含め、曲IDや難易度内の区切り文字によるキー衝突を防ぐ。
    // 2026-10-20 に満点を 100000 にそろえたので保存先を v2 に分ける(中身の形式 version は 1 のまま)。
    // 旧 "dailyRanking_v1_" の記録は消さずに残す。
    // 名前空間(運営モード、2026-10-29)を渡さない呼び出しは、今の表示の名前空間(OperatorMode.Namespace)の表。
    // 記録・合流する所は、その回の名前空間(GameSession.RunNamespace・プレイの ns)を渡す。
    public static string Key(string song, string difficulty) => Key(OperatorMode.Namespace, song, difficulty);

    // "" は今までと同じ鍵、"fes" は頭に "fes_"。
    public static string Key(string ns, string song, string difficulty)
    {
        string diff = string.IsNullOrWhiteSpace(difficulty) ? "normal" : difficulty.Trim().ToLowerInvariant();
        return OperatorMode.KeyPrefix(ns) + "dailyRanking_v2_" + (song ?? "").Length + ":" + song + ":" + diff;
    }

    public static string DayKey(DateTime localTime) => localTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static Result Record(string song, string difficulty, string runId, int score, DateTime localTime) =>
        Record(OperatorMode.Namespace, song, difficulty, runId, score, localTime);

    public static Result Record(string ns, string song, string difficulty, string runId, int score, DateTime localTime)
    {
        if (string.IsNullOrWhiteSpace(song) || string.IsNullOrEmpty(runId) || score < 0) return new Result();
        string day = DayKey(localTime);
        try
        {
            if (!TryRead(PlayerPrefs.GetString(Key(ns, song, difficulty), ""), day, out var table))
            {
                // 壊れた記録を空のランキングとして上書きし、偽の1位を出さない。
                Debug.LogWarning("DailyRankingStore: 順位の記録を読み込めないため、順位を表示しません。");
                return new Result();
            }
            bool exists = table.entries.Exists(e => e.runId == runId);
            if (!exists) table.entries.Add(new Entry { runId = runId, score = score });
            var result = Evaluate(table, runId);
            if (!exists)
            {
                PlayerPrefs.SetString(Key(ns, song, difficulty), JsonUtility.ToJson(table));
                PlayerPrefs.Save();
            }
            return result;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("DailyRankingStore: 順位の保存に失敗しました。" + exception.GetType().Name);
            return new Result();
        }
    }

    // 台同士のランキング同期(RankingSync)で、別の台で遊んだ回を足す(順位は評価しない)。同じ runId があれば AlreadyThere。
    // 表の day(数え始めた日)は今のまま。表がまだ無ければ localTime の日から数え始める。
    // 壊れた表は Record と同じく上書きしない(Failed。呼び出し側が適用待ちに残して、あとで足し直す。知らせも呼び出し側)。
    // PlayerPrefs へ書くだけで、ディスクへの保存(PlayerPrefs.Save)は呼び出し側がまとめてする。
    public static RankingMergeResult Merge(string song, string difficulty, string runId, int score, DateTime localTime) =>
        Merge(OperatorMode.Namespace, song, difficulty, runId, score, localTime);

    public static RankingMergeResult Merge(string ns, string song, string difficulty, string runId, int score, DateTime localTime) =>
        MergeAll(ns, song, difficulty, new[] { new Entry { runId = runId, score = score } }, localTime)[0];

    // 同じ曲×難易度へ何回分かをまとめて足す(表の読み書きは1回)。結果は entries と同じ順。
    public static RankingMergeResult[] MergeAll(string song, string difficulty, IList<Entry> entries, DateTime localTime) =>
        MergeAll(OperatorMode.Namespace, song, difficulty, entries, localTime);

    public static RankingMergeResult[] MergeAll(string ns, string song, string difficulty, IList<Entry> entries, DateTime localTime)
    {
        var results = new RankingMergeResult[entries?.Count ?? 0];
        if (results.Length == 0) return results;
        bool readable = false;
        Table table = null;
        try { readable = !string.IsNullOrWhiteSpace(song) && TryRead(PlayerPrefs.GetString(Key(ns, song, difficulty), ""), DayKey(localTime), out table); }
        catch (Exception) { readable = false; }
        if (!readable)
        {
            for (int i = 0; i < results.Length; i++) results[i] = RankingMergeResult.Failed;
            return results;
        }
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in table.entries) present.Add(existing.runId);
        bool added = false;
        for (int i = 0; i < results.Length; i++)
        {
            var entry = entries[i];
            if (entry == null || string.IsNullOrEmpty(entry.runId) || entry.score < 0) { results[i] = RankingMergeResult.Failed; continue; }
            if (!present.Add(entry.runId)) { results[i] = RankingMergeResult.AlreadyThere; continue; }
            table.entries.Add(new Entry { runId = entry.runId, score = entry.score });
            results[i] = RankingMergeResult.Added;
            added = true;
        }
        if (!added) return results;
        try
        {
            PlayerPrefs.SetString(Key(ns, song, difficulty), JsonUtility.ToJson(table));
        }
        catch (Exception)
        {
            for (int i = 0; i < results.Length; i++)
                if (results[i] == RankingMergeResult.Added) results[i] = RankingMergeResult.Failed;
        }
        return results;
    }

    // 記録から外す回(RankingRemovals)を表から除く。除いたら true。表が無い・読めないときは何もしない(上書きしない)。
    // 残りが無くなれば鍵ごと消す(数え始めた日も次の回から)。PlayerPrefs へは書くだけ(ディスクへの保存は呼び出し側)。
    public static bool RemoveRun(string ns, string song, string difficulty, string runId)
    {
        string key = Key(ns, song, difficulty);
        string json = PlayerPrefs.GetString(key, "");
        if (string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(json) || !TryRead(json, "", out var table)) return false;
        if (table.entries.RemoveAll(e => e.runId == runId) == 0) return false;
        if (table.entries.Count == 0) PlayerPrefs.DeleteKey(key);
        else PlayerPrefs.SetString(key, JsonUtility.ToJson(table));
        return true;
    }

    public static bool TryRead(string json, string day, out Table table)
    {
        table = new Table { day = day };
        if (string.IsNullOrEmpty(json)) return true;
        try
        {
            var saved = JsonUtility.FromJson<Table>(json);
            if (saved == null || saved.version != 1 || !DateTime.TryParseExact(saved.day, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || saved.entries == null) return false;
            // 日付が変わっても同じ表に足していく(通算)。再起動しても記録を保持。
            var ids = new HashSet<string>();
            foreach (var entry in saved.entries)
                if (entry == null || string.IsNullOrEmpty(entry.runId) || entry.score < 0 || !ids.Add(entry.runId)) return false;
            table = saved;
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    public static Result Evaluate(Table table, string runId)
    {
        var current = table.entries.Find(e => e.runId == runId);
        if (current == null) return new Result();
        var result = new Result { Day = table.day, Rank = 1, TotalPlays = table.entries.Count };
        int previousBest = -1;
        foreach (var entry in table.entries)
        {
            if (entry == current) break;
            previousBest = Math.Max(previousBest, entry.score);
        }
        // 重複登録時も全件を比較する。新記録の判定だけは今回より前の記録を使う。
        result.Rank = 1;
        result.TiedPlays = 0;
        long nextScore = long.MaxValue;
        foreach (var entry in table.entries)
        {
            if (entry.score > current.score) { result.Rank++; nextScore = Math.Min(nextScore, entry.score); }
            if (entry.score == current.score) result.TiedPlays++;
        }
        result.PointsToNext = nextScore == long.MaxValue ? 0 : nextScore - current.score + 1;
        result.IsNewBest = previousBest >= 0 && current.score > previousBest;
        return result;
    }
}
