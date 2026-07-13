using System;
using System.Collections.Generic;
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
        string diff = string.IsNullOrEmpty(difficulty) ? "normal" : difficulty.ToLowerInvariant();
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
            PlayerPrefs.SetString(Key(songId, difficulty), JsonUtility.ToJson(table));
            PlayerPrefs.Save();
        }
        return index;
    }

    // スコア降順で挿入。同点は既存(先に出した記録)が上位。maxEntries を超えた分は末尾を捨てる。
    // 挿入位置を返し、圏外なら -1(table は変更しない)。純粋ロジック。
    public static int Insert(HighScoreTable table, HighScoreEntry entry, int maxEntries)
    {
        if (table == null || table.entries == null || entry == null || maxEntries <= 0) return -1;
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
        table.entries.Insert(index, entry);
        while (table.entries.Count > maxEntries) table.entries.RemoveAt(table.entries.Count - 1);
        return index;
    }

    public static void Clear(string songId, string difficulty)
    {
        PlayerPrefs.DeleteKey(Key(songId, difficulty));
        PlayerPrefs.Save();
    }
}
