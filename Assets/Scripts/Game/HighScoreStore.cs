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
}

[Serializable]
public class HighScoreTable
{
    public List<HighScoreEntry> entries = new List<HighScoreEntry>();
}

public static class HighScoreStore
{
    public const int MaxEntries = 5;

    public static string Key(string songId, string difficulty)
    {
        string diff = string.IsNullOrWhiteSpace(difficulty) ? "normal" : difficulty.Trim().ToLowerInvariant();
        return "hiscore_" + songId + "_" + diff;
    }

    public static HighScoreTable Load(string songId, string difficulty)
    {
        string json = PlayerPrefs.GetString(Key(songId, difficulty), "");
        if (string.IsNullOrEmpty(json)) return new HighScoreTable();
        try
        {
            var table = JsonUtility.FromJson<HighScoreTable>(json);
            if (table == null || table.entries == null) return new HighScoreTable();
            // 一部が不正・順不同でも有効な記録を残す。同点の記録順は変えない。
            // 読み取りだけでは保存先を書き換えず、次のRecordで保存する。
            table.entries = table.entries.Where(e => e != null && e.score >= 0)
                .OrderByDescending(e => e.score).Take(MaxEntries).Select(Snapshot).ToList();
            return table;
        }
        catch (Exception)
        {
            return new HighScoreTable();
        }
    }

    // 記録して保存。挿入位置(0始まり)を返す。上位N圏外なら -1(保存もしない)。
    public static int Record(string songId, string difficulty, HighScoreEntry entry, out HighScoreTable table)
    {
        table = Load(songId, difficulty);
        int index = Insert(table, entry, MaxEntries);
        if (index >= 0)
        {
            try
            {
                PlayerPrefs.SetString(Key(songId, difficulty), JsonUtility.ToJson(table));
                PlayerPrefs.Save();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("HighScoreStore: 記録を保存できませんでした。" + exception.GetType().Name);
                // 成績画面は表示し続けるが、保存成功を表す挿入番号は返さない。
                return -1;
            }
        }
        return index;
    }

    // スコア降順で挿入。同点は既存(先に出した記録)が上位。maxEntries を超えた分は末尾を捨てる。
    // 挿入位置を返し、圏外なら -1(table は変更しない)。純粋ロジック。
    public static int Insert(HighScoreTable table, HighScoreEntry entry, int maxEntries)
    {
        if (table == null || table.entries == null || entry == null || entry.score < 0 || maxEntries <= 0) return -1;
        table.entries = table.entries.Where(e => e != null && e.score >= 0).OrderByDescending(e => e.score).ToList();
        int index = table.entries.Count;
        for (int i = 0; i < table.entries.Count; i++)
        {
            if (entry.score > table.entries[i].score)
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
        accuracy = float.IsNaN(entry.accuracy) || float.IsInfinity(entry.accuracy) ? 0 : Mathf.Clamp01(entry.accuracy)
    };

    public static void Clear(string songId, string difficulty)
    {
        PlayerPrefs.DeleteKey(Key(songId, difficulty));
        PlayerPrefs.Save();
    }
}
