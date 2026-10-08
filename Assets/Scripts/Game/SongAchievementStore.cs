using System;
using System.Collections.Generic;
using UnityEngine;

// 個人の識別はせず、曲×難易度の達成プレイを延べ人数として記録する。
public static class SongAchievementStore
{
    [Serializable]
    public sealed class Counts
    {
        public int s, sPlus, fc, ap;
        public string lastRunId;
        public List<string> recentRunIds = new List<string>();
    }
    public static string Key(string song, string difficulty)
    {
        // 標準難易度の既存キーは維持し、大小文字や前後の空白で実績が分裂しないようにする。
        string value = string.IsNullOrWhiteSpace(difficulty) ? "normal" : difficulty.Trim().ToLowerInvariant();
        switch (value)
        {
            case "easy": value = "Easy"; break;
            case "normal": value = "Normal"; break;
            case "hard": value = "Hard"; break;
        }
        return "songAchievements_v1_" + song + "::" + value;
    }
    public static Counts Load(string song, string difficulty)
    {
        try
        {
            var value = JsonUtility.FromJson<Counts>(PlayerPrefs.GetString(Key(song, difficulty), "{}")) ?? new Counts();
            value.s = Math.Max(0, value.s); value.sPlus = Math.Max(0, value.sPlus);
            value.fc = Math.Max(0, value.fc); value.ap = Math.Max(0, value.ap);
            if (value.recentRunIds == null) value.recentRunIds = new List<string>();
            if (!string.IsNullOrEmpty(value.lastRunId) && !value.recentRunIds.Contains(value.lastRunId))
                value.recentRunIds.Add(value.lastRunId);
            return value;
        }
        catch (ArgumentException) { return new Counts(); }
    }
    public static bool Record(string song, string difficulty, string runId, int perfect, int great, int good, int bad, int miss)
    {
        if (string.IsNullOrEmpty(song) || string.IsNullOrEmpty(difficulty) || string.IsNullOrEmpty(runId)) return false;
        if (perfect < 0 || great < 0 || good < 0 || bad < 0 || miss < 0) return false;
        if ((long)perfect + great + good + bad + miss == 0) return false;
        var value = Load(song, difficulty);
        if (value.recentRunIds.Contains(runId)) return false;
        var rank = PlayRankHelper.FromAccuracy(PlayRankHelper.Accuracy(perfect, great, good, bad, miss));
        bool fullCombo = (long)perfect + great + good + bad > 0 && bad == 0 && miss == 0;
        bool allPerfect = fullCombo && great == 0 && good == 0;
        if (rank >= PlayRank.S) value.s = Increment(value.s);
        if (rank == PlayRank.SPlus) value.sPlus = Increment(value.sPlus);
        if (fullCombo) value.fc = Increment(value.fc);
        if (allPerfect) value.ap = Increment(value.ap);
        value.lastRunId = runId;
        value.recentRunIds.Add(runId);
        // 直近の画面再生成・再通知を吸収し、長期利用でも保存容量を一定に保つ。
        if (value.recentRunIds.Count > 128) value.recentRunIds.RemoveRange(0, value.recentRunIds.Count - 128);
        try
        {
            PlayerPrefs.SetString(Key(song, difficulty), JsonUtility.ToJson(value));
            PlayerPrefs.Save();
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("SongAchievementStore: 実績を保存できませんでした。" + exception.GetType().Name);
            return false;
        }
    }
    static int Increment(int value) => value < int.MaxValue ? value + 1 : value;
}
