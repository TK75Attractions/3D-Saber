using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// 曲×難易度ごとのハイスコア上位N件を PlayerPrefs に JSON で保存する薄いストア。
// リザルト画面のランキング表示用。挿入の純粋ロジック(Insert)はテストから直接叩ける。
[Serializable]
public class HighScoreEntry
{
    public int score;
    public string rank;      // "S+" など表示用ラベル
    public float accuracy;   // 0..1
    public string date;      // "yyyy/MM/dd"
    // その回の ID(GameSession.AchievementRunId)。台同士の同期(RankingSync)で同じ回を二重に足さないため。古い記録は空。
    public string runId;
    // 遊び終わった時刻(UTC の Unix ミリ秒)。同点の並びを2台で同じにするため(Compare)。古い記録は 0(一番古い扱い)。
    public long at;
}

[Serializable]
public class HighScoreTable
{
    public List<HighScoreEntry> entries = new List<HighScoreEntry>();
}

public static class HighScoreStore
{
    public const int MaxEntries = 5;

    // 名前空間(運営モード、2026-10-29)を渡さない呼び出しは、今の表示の名前空間(OperatorMode.Namespace)の表(選曲の TOP 5 など)。
    // 記録・合流する所は、その回の名前空間(GameSession.RunNamespace・プレイの ns)を渡す。
    public static string Key(string songId, string difficulty) => Key(OperatorMode.Namespace, songId, difficulty);

    // "" は今までと同じ鍵、"fes" は頭に "fes_"。
    public static string Key(string ns, string songId, string difficulty)
    {
        string diff = string.IsNullOrWhiteSpace(difficulty) ? "normal" : difficulty.Trim().ToLowerInvariant();
        // 2026-10-20 に満点を 100000 にそろえたので保存先を分ける。旧 "hiscore_" の記録は消さずに残す。
        return OperatorMode.KeyPrefix(ns) + "hiscore_v2_" + songId + "_" + diff;
    }

    // 並び: 点の降順 → 遊び終わった時刻の昇順(0 = 古い記録は一番古い扱い)→ runId の昇順。
    // 届いた順に依らないので、台同士で同期しても2台の上位5件が同じ並び・同じ中身になる(2026-10-28)。
    // 同じ台の記録は時刻が増えていくので、前からの「同点は先の記録が上」と同じ結果になる。
    public static int Compare(HighScoreEntry a, HighScoreEntry b)
    {
        int byScore = b.score.CompareTo(a.score);
        if (byScore != 0) return byScore;
        int byTime = Math.Max(0, a.at).CompareTo(Math.Max(0, b.at));
        if (byTime != 0) return byTime;
        return string.CompareOrdinal(a.runId ?? "", b.runId ?? "");
    }

    static readonly IComparer<HighScoreEntry> Order = Comparer<HighScoreEntry>.Create(Compare);

    public static HighScoreTable Load(string songId, string difficulty) => Load(OperatorMode.Namespace, songId, difficulty);

    public static HighScoreTable Load(string ns, string songId, string difficulty) =>
        TryLoad(ns, songId, difficulty, out var table) ? table : new HighScoreTable();

    public static bool TryLoad(string songId, string difficulty, out HighScoreTable table) =>
        TryLoad(OperatorMode.Namespace, songId, difficulty, out table);

    // 保存値を読む。無ければ空の表で true、壊れていれば(JSON として読めない)空の表で false(上書きしないために区別する)。
    // 一部が不正・順不同でも有効な記録を残す(true)。並びが同じ記録の順は変えない。読むだけでは保存先を書き換えない。
    public static bool TryLoad(string ns, string songId, string difficulty, out HighScoreTable table)
    {
        table = new HighScoreTable();
        string json = PlayerPrefs.GetString(Key(ns, songId, difficulty), "");
        if (string.IsNullOrEmpty(json)) return true;
        try
        {
            var saved = JsonUtility.FromJson<HighScoreTable>(json);
            if (saved == null || saved.entries == null) return false;
            saved.entries = saved.entries.Where(e => e != null && e.score >= 0)
                .OrderBy(e => e, Order).Take(MaxEntries).Select(Snapshot).ToList();
            table = saved;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 記録して保存。挿入位置(0始まり)を返す。同じ回(runId)がもうあればその位置。上位N圏外・壊れた表・保存の失敗なら -1(保存もしない)。
    public static int Record(string songId, string difficulty, HighScoreEntry entry, out HighScoreTable table) =>
        Record(OperatorMode.Namespace, songId, difficulty, entry, out table);

    public static int Record(string ns, string songId, string difficulty, HighScoreEntry entry, out HighScoreTable table)
    {
        RecordRun(ns, songId, difficulty, entry, out table, out int index);
        return index;
    }

    public static RankingMergeResult RecordRun(string songId, string difficulty, HighScoreEntry entry, out HighScoreTable table, out int index) =>
        RecordRun(OperatorMode.Namespace, songId, difficulty, entry, out table, out index);

    // 1回分を残して保存する(結果画面)。台同士の同期の足し直し(MergeAll)と同じ決まりで、結果を返す。
    // Added(index に挿入位置)・AlreadyThere(同じ runId がもうある。足し直しと重なっても二重にしない)・
    // OutsideTop(上位N圏外。保存しない)・Failed(表が壊れていて上書きしない、または保存の失敗。呼び出し側が足し直しへ回す)。
    public static RankingMergeResult RecordRun(string ns, string songId, string difficulty, HighScoreEntry entry, out HighScoreTable table, out int index)
    {
        index = -1;
        if (!TryLoad(ns, songId, difficulty, out table) || entry == null || entry.score < 0) return RankingMergeResult.Failed;
        int existing = IndexOfRun(table, entry.runId);
        if (existing >= 0)
        {
            index = existing;
            return RankingMergeResult.AlreadyThere;
        }
        int inserted = Insert(table, entry, MaxEntries);
        if (inserted < 0) return RankingMergeResult.OutsideTop;
        try
        {
            PlayerPrefs.SetString(Key(ns, songId, difficulty), JsonUtility.ToJson(table));
            PlayerPrefs.Save();
        }
        catch (Exception exception)
        {
            Debug.LogWarning("HighScoreStore: 記録を保存できませんでした。" + exception.GetType().Name);
            // 成績画面は表示し続けるが、保存成功を表す挿入番号は返さない。
            return RankingMergeResult.Failed;
        }
        index = inserted;
        return RankingMergeResult.Added;
    }

    // 同じ回(runId)の記録の位置。runId の無い記録(古い記録・テスト)は比べない。
    static int IndexOfRun(HighScoreTable table, string runId) =>
        string.IsNullOrEmpty(runId) ? -1 : table.entries.FindIndex(e => e.runId == runId);

    // 台同士のランキング同期(RankingSync)で、別の台で遊んだ回を足す。同じ runId が表にあれば足さない(AlreadyThere)。
    // 無ければ Insert と同じ決まり(Compare の並び・上位5件)で足す(Added。上位5件に入らなければ OutsideTop)。
    // 壊れた表は上書きしない(Failed。呼び出し側が適用待ちに残して、あとで足し直す)。
    // PlayerPrefs へ書くだけで、ディスクへの保存(PlayerPrefs.Save)は呼び出し側がまとめてする(1コマに1回まで)。
    public static RankingMergeResult Merge(string songId, string difficulty, HighScoreEntry entry) =>
        Merge(OperatorMode.Namespace, songId, difficulty, entry);

    public static RankingMergeResult Merge(string ns, string songId, string difficulty, HighScoreEntry entry) =>
        MergeAll(ns, songId, difficulty, new[] { entry })[0];

    public static RankingMergeResult[] MergeAll(string songId, string difficulty, IList<HighScoreEntry> entries) =>
        MergeAll(OperatorMode.Namespace, songId, difficulty, entries);

    // 同じ曲×難易度へ何回分かをまとめて足す(表の読み書きは1回)。結果は entries と同じ順。
    // 並びは届いた順に依らないので、まとめて足しても1回ずつ足しても同じ表になる。
    public static RankingMergeResult[] MergeAll(string ns, string songId, string difficulty, IList<HighScoreEntry> entries)
    {
        var results = new RankingMergeResult[entries?.Count ?? 0];
        if (results.Length == 0) return results;
        if (string.IsNullOrEmpty(songId) || !TryLoad(ns, songId, difficulty, out var table))
        {
            for (int i = 0; i < results.Length; i++) results[i] = RankingMergeResult.Failed;
            return results;
        }
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in table.entries)
            if (!string.IsNullOrEmpty(existing.runId)) present.Add(existing.runId);
        bool inserted = false;
        for (int i = 0; i < results.Length; i++)
        {
            var entry = entries[i];
            if (entry == null || string.IsNullOrEmpty(entry.runId) || entry.score < 0) { results[i] = RankingMergeResult.Failed; continue; }
            if (!present.Add(entry.runId)) { results[i] = RankingMergeResult.AlreadyThere; continue; }
            if (Insert(table, entry, MaxEntries) >= 0) { results[i] = RankingMergeResult.Added; inserted = true; }
            else results[i] = RankingMergeResult.OutsideTop;
        }
        // あとの回に押し出されて上位5件から外れた回は OutsideTop にする。
        for (int i = 0; i < results.Length; i++)
            if (results[i] == RankingMergeResult.Added && IndexOfRun(table, entries[i].runId) < 0)
                results[i] = RankingMergeResult.OutsideTop;
        if (!inserted) return results;
        try
        {
            PlayerPrefs.SetString(Key(ns, songId, difficulty), JsonUtility.ToJson(table));
        }
        catch (Exception)
        {
            for (int i = 0; i < results.Length; i++)
                if (results[i] == RankingMergeResult.Added || results[i] == RankingMergeResult.OutsideTop) results[i] = RankingMergeResult.Failed;
        }
        return results;
    }

    // Compare の並びで挿入。同点で時刻も同じ(古い記録どうしなど)なら既存(先に出した記録)が上位。maxEntries を超えた分は末尾を捨てる。
    // 挿入位置を返し、圏外なら -1(table は変更しない)。純粋ロジック。
    public static int Insert(HighScoreTable table, HighScoreEntry entry, int maxEntries)
    {
        if (table == null || table.entries == null || entry == null || entry.score < 0 || maxEntries <= 0) return -1;
        table.entries = table.entries.Where(e => e != null && e.score >= 0).OrderBy(e => e, Order).ToList();
        int index = table.entries.Count;
        for (int i = 0; i < table.entries.Count; i++)
        {
            if (Compare(entry, table.entries[i]) < 0)
            {
                index = i;
                break;
            }
        }
        if (index >= maxEntries) return -1;
        table.entries.Insert(index, Snapshot(entry));
        while (table.entries.Count > maxEntries) table.entries.RemoveAt(table.entries.Count - 1);
        return index;
    }

    // 結果を保存した後に呼び出し元がEntryを書き換えても、ランキングの内容は変えない。
    static HighScoreEntry Snapshot(HighScoreEntry entry) => new HighScoreEntry
    {
        score = entry.score,
        rank = entry.rank,
        date = entry.date,
        runId = entry.runId,
        at = entry.at,
        accuracy = float.IsNaN(entry.accuracy) || float.IsInfinity(entry.accuracy) ? 0 : Mathf.Clamp01(entry.accuracy)
    };

    public static void Clear(string songId, string difficulty) => Clear(OperatorMode.Namespace, songId, difficulty);

    public static void Clear(string ns, string songId, string difficulty)
    {
        PlayerPrefs.DeleteKey(Key(ns, songId, difficulty));
        PlayerPrefs.Save();
    }
}
