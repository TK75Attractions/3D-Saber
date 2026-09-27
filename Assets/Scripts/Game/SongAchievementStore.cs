using System;
using UnityEngine;

// 個人の識別はせず、曲×難易度の達成プレイを延べ人数として記録する。
public static class SongAchievementStore
{
    [Serializable]
    public sealed class Counts
    {
        public int s, sPlus, fc, ap;
        public string lastRunId;
    }
    public static string Key(string song, string difficulty) => "songAchievements_v1_" + song + "::" + difficulty;
    public static Counts Load(string song, string difficulty)
    {
        try
        {
            var value = JsonUtility.FromJson<Counts>(PlayerPrefs.GetString(Key(song, difficulty), "{}")) ?? new Counts();
            value.s = Math.Max(0, value.s); value.sPlus = Math.Max(0, value.sPlus);
            value.fc = Math.Max(0, value.fc); value.ap = Math.Max(0, value.ap);
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
        if (value.lastRunId == runId) return false;
        var rank = PlayRankHelper.FromAccuracy(PlayRankHelper.Accuracy(perfect, great, good, bad, miss));
        bool fullCombo = (long)perfect + great + good + bad > 0 && bad == 0 && miss == 0;
        bool allPerfect = fullCombo && great == 0 && good == 0;
        if (rank >= PlayRank.S) value.s = Increment(value.s);
        if (rank == PlayRank.SPlus) value.sPlus = Increment(value.sPlus);
        if (fullCombo) value.fc = Increment(value.fc);
        if (allPerfect) value.ap = Increment(value.ap);
        value.lastRunId = runId;
        PlayerPrefs.SetString(Key(song, difficulty), JsonUtility.ToJson(value));
        PlayerPrefs.Save();
        return true;
    }
    static int Increment(int value) => value < int.MaxValue ? value + 1 : value;
}
