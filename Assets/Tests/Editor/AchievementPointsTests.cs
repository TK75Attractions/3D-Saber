using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 実績ポイント(AchievementPoints、2026-10-29)。段の点・合計・2台の記録の中での順位(同点は同じ順位)。
// 段は見直しで変わるので、特定の実績の段を前提にしない(カタログからその場で点を出して比べる)。
public class AchievementPointsTests
{
    const string Key = "Achievements.Log.pointstest";
    string savedKey;
    static readonly long At = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [SetUp]
    public void SetUp()
    {
        savedKey = AchievementStore.Key;
        PlayerPrefs.DeleteKey(Key);
        AchievementStore.UseKeyForTest(Key);
    }

    [TearDown]
    public void TearDown()
    {
        PlayerPrefs.DeleteKey(Key);
        AchievementStore.UseKeyForTest(savedKey);
    }

    static string NewId() => Guid.NewGuid().ToString("N");

    [Test]
    public void EachTierIsWorthItsPoints()
    {
        Assert.AreEqual(1, AchievementPoints.For(AchievementTier.None));
        Assert.AreEqual(3, AchievementPoints.For(AchievementTier.Bronze));
        Assert.AreEqual(6, AchievementPoints.For(AchievementTier.Silver));
        Assert.AreEqual(10, AchievementPoints.For(AchievementTier.Gold));
        Assert.AreEqual(30, AchievementPoints.For(AchievementTier.Rainbow));
        foreach (var definition in AchievementCatalog.All)
            Assert.AreEqual(AchievementPoints.For(definition.Tier), AchievementPoints.For(definition.Id), definition.Id);
        Assert.AreEqual(1, AchievementPoints.For("nosuchach"), "知らない ID は段なしの点");
        var some = AchievementCatalog.All.Take(5).Select(d => d.Id).ToList();
        Assert.AreEqual(some.Sum(AchievementPoints.For), AchievementPoints.Sum(some));
        Assert.AreEqual(0, AchievementPoints.Sum(null));
    }

    [Test]
    public void HighestTierOfARunFramesThePointsCard()
    {
        var top = AchievementCatalog.All.OrderByDescending(d => d.Tier).First();
        var clear = AchievementCatalog.Find(AchievementCatalog.ClearId);
        Assert.AreEqual(top.Tier, AchievementPoints.Highest(new[] { new AchievementUnlock(clear, 3), new AchievementUnlock(top, 1) }));
        Assert.AreEqual(AchievementTier.None, AchievementPoints.Highest(null));
    }

    // 順位はこの名前空間の2台の記録した回の中で(同点は同じ順位、人数はその回を入れた数)。ほかの名前空間は数えない。
    [Test]
    public void TheRankComesFromEveryRecordedRunOfTheSameNamespace()
    {
        string clear = AchievementCatalog.ClearId;
        string rich = AchievementCatalog.All.OrderByDescending(d => d.Tier).First().Id;
        string low = NewId(), mid1 = NewId(), mid2 = NewId(), high = NewId(), other = NewId();
        AchievementStore.Merge("", low, new[] { clear }, "A", At);
        AchievementStore.Merge("", mid1, new[] { clear, rich }, "B", At);
        AchievementStore.Merge("", mid2, new[] { clear, rich }, "A", At);
        AchievementStore.Merge("", high, new[] { clear, rich, "nosuchach" }, "B", At);
        AchievementStore.Merge(OperatorMode.FestivalNamespace, other, new[] { clear, rich, rich + "x" }, "A", At);
        int midPoints = AchievementPoints.Sum(new[] { clear, rich });

        var mid = AchievementPoints.Of("", mid1, null);
        Assert.AreEqual(midPoints, mid.Points);
        Assert.AreEqual(2, mid.Rank, "上は1人(同点の mid2 とは同じ順位)");
        Assert.AreEqual(4, mid.Total, "運営モードの回は数えない");
        Assert.AreEqual(2, AchievementPoints.Of("", mid2, null).Rank);
        Assert.AreEqual(1, AchievementPoints.Of("", high, null).Rank);
        Assert.AreEqual(4, AchievementPoints.Of("", low, null).Rank);

        // 記録に無い回(記録できなかったなど)は、渡した実績で点を出し、人数に足す
        var unseen = AchievementPoints.Of("", NewId(), new[] { clear });
        Assert.AreEqual(AchievementPoints.For(clear), unseen.Points);
        Assert.AreEqual(5, unseen.Total);
        Assert.AreEqual(4, unseen.Rank, "low と同点");

        var fes = AchievementPoints.Of(OperatorMode.FestivalNamespace, other, null);
        Assert.AreEqual(1, fes.Rank);
        Assert.AreEqual(1, fes.Total);
    }

    [Test]
    public void WithoutAnyRecordOnlyThePointsAreKnown()
    {
        var standing = AchievementPoints.Of("", NewId(), new[] { AchievementCatalog.ClearId });
        Assert.AreEqual(AchievementPoints.For(AchievementCatalog.ClearId), standing.Points);
        Assert.AreEqual(0, standing.Rank);
        Assert.AreEqual(0, standing.Total);
        Assert.AreEqual(standing.Points + "pt", AchievementToast.PointsMessage(standing.Points, standing.Rank, standing.Total));
    }
}
