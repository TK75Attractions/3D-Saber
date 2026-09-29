using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// この端末の当日・同じ曲/難易度の全プレイを集計。人数ではなく延べプレイ数。
// 上位だけを切り捨てると圏外の順位が計算できないため、当日分は全件保持する。
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
    public static string Key(string song, string difficulty)
    {
        string diff = string.IsNullOrWhiteSpace(difficulty) ? "normal" : difficulty.Trim().ToLowerInvariant();
        return "dailyRanking_v1_" + (song ?? "").Length + ":" + song + ":" + diff;
    }

    public static string DayKey(DateTime localTime) => localTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static Result Record(string song, string difficulty, string runId, int score, DateTime localTime)
    {
        if (string.IsNullOrWhiteSpace(song) || string.IsNullOrEmpty(runId) || score < 0) return new Result();
        string day = DayKey(localTime);
        try
        {
            if (!TryRead(PlayerPrefs.GetString(Key(song, difficulty), ""), day, out var table))
            {
                // 壊れた記録を空のランキングとして上書きし、偽の1位を出さない。
                Debug.LogWarning("DailyRankingStore: 本日の記録を読み込めないため、順位を表示しません。");
                return new Result();
            }
            bool exists = table.entries.Exists(e => e.runId == runId);
            if (!exists) table.entries.Add(new Entry { runId = runId, score = score });
            var result = Evaluate(table, runId);
            if (!exists)
            {
                PlayerPrefs.SetString(Key(song, difficulty), JsonUtility.ToJson(table));
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

    public static bool TryRead(string json, string day, out Table table)
    {
        table = new Table { day = day };
        if (string.IsNullOrEmpty(json)) return true;
        try
        {
            var saved = JsonUtility.FromJson<Table>(json);
            if (saved == null || saved.version != 1 || !DateTime.TryParseExact(saved.day, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || saved.entries == null) return false;
            // 端末の現地日付を境に切り替える。再起動しても同日なら記録を保持。
            if (saved.day != day) return true;
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
