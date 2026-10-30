using System;
using System.Collections.Generic;
using NUnit.Framework;

// 記録から外す回(2026-10-30 夕、v2.6): 運営モードで 10/30 16:25〜24:00(日本時間)に遊び終わった回は、表と実績に入れない。
public class RankingRemovalsTests
{
    static readonly long At1625 = new DateTimeOffset(2026, 10, 30, 16, 25, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
    static readonly long Midnight = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();

    [SetUp] public void EnableWindow() { RankingRemovals.WindowEnabledForTest = true; }
    [TearDown] public void ResetWindow() { RankingRemovals.WindowEnabledForTest = null; }

    static RankingPlay Play(string id, string ns, long at) => new RankingPlay { id = id, song = "logic", diff = "Hard", score = 50000, rank = "B", acc = .5f, ns = ns, at = at };

    [Test]
    public void OnlyOperatorModePlaysFrom1625ToMidnightAreRemoved()
    {
        Assert.IsTrue(RankingRemovals.InRemovedWindow("fes", At1625));
        Assert.IsTrue(RankingRemovals.InRemovedWindow("fes", Midnight - 1));
        Assert.IsFalse(RankingRemovals.InRemovedWindow("fes", At1625 - 1), "16:24:59.999 は運営の回");
        Assert.IsFalse(RankingRemovals.InRemovedWindow("fes", Midnight), "次の日は外さない");
        Assert.IsFalse(RankingRemovals.InRemovedWindow("", At1625 + 60000), "ふだんのモードは対象外");
    }

    [Test]
    public void APlayFoundByTimeIsAlsoRemovedByItsId()
    {
        string id = "window-test-" + Guid.NewGuid().ToString("N");
        Assert.IsFalse(RankingRemovals.IsRemoved(id));
        Assert.IsTrue(RankingRemovals.IsRemoved(Play(id, "fes", At1625 + 1000)));
        Assert.IsTrue(RankingRemovals.IsRemoved(id), "実績の数など ID だけで引く所でも外れる");
    }

    [Test]
    public void WithoutDropsWindowPlaysAndKeepsTheRest()
    {
        var keep = Play("keep-" + Guid.NewGuid().ToString("N"), "fes", At1625 - 60000);
        var drop = Play("drop-" + Guid.NewGuid().ToString("N"), "fes", At1625 + 60000);
        var ordinary = Play("ordinary-" + Guid.NewGuid().ToString("N"), "", At1625 + 60000);
        var kept = RankingRemovals.Without(new List<RankingPlay> { keep, drop, ordinary });
        CollectionAssert.AreEqual(new[] { keep, ordinary }, kept);
    }

    [Test]
    public void TheWindowIsOffInTheEditorByDefault()
    {
        RankingRemovals.WindowEnabledForTest = null;
        Assert.IsFalse(RankingRemovals.InRemovedWindow("fes", At1625 + 1000), "Editor・テストの回は今の時刻で作るので外さない");
    }
}
