using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class DailyRankingStoreTests
{
    string song;
    readonly DateTime day = new DateTime(2026, 9, 30, 12, 0, 0);

    [SetUp] public void Setup() { song = "__daily_" + Guid.NewGuid().ToString("N"); }
    [TearDown] public void Cleanup()
    {
        foreach (string difficulty in new[] { "Normal", "Hard" }) PlayerPrefs.DeleteKey(DailyRankingStore.Key(song, difficulty));
        PlayerPrefs.Save();
    }
    DailyRankingStore.Result Record(string id, int score, DateTime? at = null, string difficulty = "Normal")
        => DailyRankingStore.Record(song, difficulty, id, score, at ?? day);

    [Test] public void FirstPlayIsOneOfOneAndNotAFabricatedNewBest()
    {
        var value = Record("first", 100);
        Assert.AreEqual(1, value.Rank); Assert.AreEqual(1, value.TotalPlays);
        Assert.False(value.IsNewBest); Assert.AreEqual(0, value.PointsToNext);
    }
    [Test] public void TiesUseCompetitionRankingAndRequireOnePointToOvertake()
    {
        Record("a", 100); var tied = Record("b", 100); var third = Record("c", 90);
        Assert.AreEqual(1, tied.Rank); Assert.AreEqual(2, tied.TiedPlays); Assert.False(tied.IsNewBest);
        Assert.AreEqual(3, third.Rank); Assert.AreEqual(11, third.PointsToNext);
    }
    [Test] public void RanksBeyondHistoricalTopFiveStayExact()
    {
        for (int i = 0; i < 30; i++) Record("r" + i, 100 + i);
        var value = Record("last", 50);
        Assert.AreEqual(31, value.Rank); Assert.AreEqual(31, value.TotalPlays); Assert.AreEqual(51, value.PointsToNext);
    }
    [Test] public void RepeatedRunIsNotInsertedOrReplaced()
    {
        Record("a", 100); Record("b", 200);
        var value = Record("a", 10000);
        Assert.AreEqual(2, value.TotalPlays); Assert.AreEqual(2, value.Rank);
    }
    [Test] public void NewBestIsStrictlyHigher()
    {
        Record("a", 100); var value = Record("b", 101);
        Assert.True(value.IsNewBest); Assert.AreEqual(1, value.Rank);
    }
    // 2026-10-22: 日付で区切らず通算で数える(ユーザーの依頼)。Day は数え始めた日のまま。
    [Test] public void LaterDaysKeepCountingFromTheFirstDay()
    {
        Record("a", 100, day.Date.AddHours(23).AddMinutes(59));
        Assert.AreEqual(2, Record("b", 90, day.Date.AddHours(23).AddMinutes(59).AddSeconds(59)).Rank);
        var next = Record("c", 10, day.Date.AddDays(1));
        Assert.AreEqual(3, next.Rank); Assert.AreEqual(3, next.TotalPlays); Assert.AreEqual("2026-09-30", next.Day);
        var later = Record("d", 200, day.Date.AddDays(30));
        Assert.AreEqual(1, later.Rank); Assert.AreEqual(4, later.TotalPlays); Assert.True(later.IsNewBest);
        Assert.AreEqual("2026-09-30", later.Day);
    }

    // ルーレットは何人でも 100位あたりから数を下げ、1位より上は空ける。
    [Test] public void WheelCountsDownFromAboutOneHundredAndLeavesAboveFirstEmpty()
    {
        Assert.AreEqual("100", DailyRankingWheel.CellText(1, DailyRankingWheel.SpinFrom - 1, 0));
        Assert.AreEqual("1", DailyRankingWheel.CellText(1, 0, 0));
        Assert.AreEqual("2", DailyRankingWheel.CellText(1, 0, 1));
        Assert.AreEqual("", DailyRankingWheel.CellText(1, 0, -1));
        Assert.AreEqual("", DailyRankingWheel.CellText(1, 0, -2));
        Assert.AreEqual("1", DailyRankingWheel.CellText(3, 0, -2));
    }
    [Test] public void SongAndDifficultyAreSeparateAndDifficultyCaseIsNormalized()
    {
        Record("a", 100);
        Assert.AreEqual(1, Record("b", 50, difficulty: "Hard").Rank);
        Assert.AreEqual(2, Record("c", 50, difficulty: "NORMAL").Rank);
        Assert.AreNotEqual(DailyRankingStore.Key("a:b", "c"), DailyRankingStore.Key("a", "b:c"));
    }
    [Test] public void ZeroScoreAndMaximumScoreDoNotOverflowTheGap()
    {
        Record("max", int.MaxValue);
        var value = Record("zero", 0);
        Assert.AreEqual(2, value.Rank); Assert.AreEqual(2147483648L, value.PointsToNext);
    }
    [Test] public void InvalidInputsNeverCreateARecord()
    {
        Assert.False(Record("negative", -1).Available);
        Assert.False(Record(null, 100).Available);
        Assert.False(DailyRankingStore.Record(null, "Normal", "id", 100, day).Available);
        Assert.False(PlayerPrefs.HasKey(DailyRankingStore.Key(song, "Normal")));
    }
    [TestCase("not json")]
    [TestCase("{}")]
    [TestCase("{\"version\":1,\"day\":\"2026-09-30\",\"entries\":[null]}")]
    public void InvalidHistoryIsPreservedWithoutClaimingFirstPlace(string json)
    {
        PlayerPrefs.SetString(DailyRankingStore.Key(song, "Normal"), json);
        LogAssert.Expect(LogType.Warning, "DailyRankingStore: 順位の記録を読み込めないため、順位を表示しません。");
        Assert.False(Record("new", 100).Available);
        Assert.AreEqual(json, PlayerPrefs.GetString(DailyRankingStore.Key(song, "Normal")));
    }
}
