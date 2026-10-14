using System.Linq;
using NUnit.Framework;
using UnityEngine;

public class RushESecretTests
{
    [Test]
    public void TenIndependentHitsUnlockOnlyRushEAndChangingSongResetsEverything()
    {
        var state = new RushESecretState(); state.Select("Other"); Assert.IsFalse(state.TryHit(0));
        state.Select("RushE");
        for (int i = 0; i < 9; i++) { Assert.IsTrue(state.TryHit(i * .2)); Assert.IsFalse(state.Transitioning); }
        Assert.IsFalse(state.TryHit(1.61), "同じ振り・両手同時・クリック重複を数えない");
        Assert.IsFalse(state.TryHit(double.NaN)); Assert.IsFalse(state.TryHit(double.PositiveInfinity));
        Assert.IsTrue(state.TryHit(2)); Assert.IsTrue(state.Transitioning); Assert.IsFalse(state.Unlocked);
        Assert.IsFalse(state.TryHit(3)); Assert.IsFalse(state.Finish());
        Assert.IsTrue(state.Reveal()); Assert.IsFalse(state.Reveal()); Assert.IsTrue(state.Unlocked);
        Assert.IsTrue(state.Finish()); Assert.IsFalse(state.Transitioning);
        state.Select("RushE"); Assert.IsTrue(state.Unlocked);
        state.Select("Other"); Assert.AreEqual(0, state.Hits); Assert.IsFalse(state.Unlocked);
        state.Select("RushE"); Assert.AreEqual(0, state.Hits); Assert.IsFalse(state.Unlocked);
    }
    [Test]
    public void ANewSongCancelsEvenACoveredTransition()
    {
        var s = new RushESecretState(); s.Select("RushE");
        for (int i = 0; i < 10; i++) s.TryHit(i);
        s.Reveal(); s.Select("Epilogue");
        Assert.IsFalse(s.Reveal()); Assert.IsFalse(s.Finish()); Assert.IsFalse(s.Unlocked); Assert.IsFalse(s.Transitioning);
    }
    [Test]
    public void JacketCountsCrossingsNotJitterOrStaleCameraTeleports()
    {
        var stroke = new RushEJacketStroke();
        Assert.IsFalse(stroke.Tick(new Vector2(-1.5f, 0), 0, true));
        Assert.IsTrue(stroke.Tick(new Vector2(1.5f, 0), .05, true));
        Assert.IsTrue(stroke.Tick(new Vector2(-1.5f, 0), .10, true));
        Assert.IsFalse(stroke.Tick(new Vector2(1.5f, 0), .5, true), "古い値の飛び越し");
        stroke.Reset();
        for (int i = 0; i < 80; i++) Assert.IsFalse(stroke.Tick(new Vector2(Mathf.Sin(i) * .1f, 0), i * .016, true));
        Assert.IsFalse(stroke.Tick(new Vector2(float.NaN, 0), 2, true));
        Assert.IsFalse(stroke.Tick(Vector2.zero, 2.01, false));
    }
    [Test]
    public void TilesFullyCoverAtRevealAndHaveClearedBeforeStarting()
    {
        for (int y = 0; y < 9; y++) for (int x = 0; x < 16; x++)
        {
            Assert.AreEqual(0, RushETransitionGraphic.TileScale(0, x, y));
            Assert.AreEqual(1, RushETransitionGraphic.TileScale(RushESecretState.RevealSeconds, x, y));
            Assert.AreEqual(0, RushETransitionGraphic.TileScale(RushESecretState.TransitionSeconds, x, y));
        }
    }
    [Test]
    public void EHasItsOwnDenseChartNoFlicksAndSeparateRecordKeys()
    {
        var e = ChartLoader.LoadFromStreamingAssets("RushE", "E"); var hard = ChartLoader.LoadFromStreamingAssets("RushE", "Hard");
        Assert.AreEqual(10, e.displayLevel); Assert.Greater(e.notes.Sum(n => n.count), hard.notes.Sum(n => n.count) * 2);
        Assert.IsTrue(e.notes.All(n => n.direction == "none" && (n.type == "tap" || n.type == "long")));
        Assert.GreaterOrEqual(e.notes.Count, 1000, "連続する弱い音を休憩用に間引かない");
        Assert.IsTrue(e.notes.All(n => n.type == "tap" && n.count == 1 && n.lengthMs == 0), "発音1回を1ノーツにする");
        Assert.IsTrue(e.notes.GroupBy(n => n.time).All(g => g.Count() <= 2), "和音は左右同時にまとめる");
        Assert.AreNotEqual(SongAchievementStore.Key("RushE", "E"), SongAchievementStore.Key("RushE", "Hard"));
        Assert.AreNotEqual(HighScoreStore.Key("RushE", "E"), HighScoreStore.Key("RushE", "Hard"));
        Assert.AreNotEqual(DailyRankingStore.Key("RushE", "E"), DailyRankingStore.Key("RushE", "Hard"));
        Assert.AreEqual(RushESecretState.Purple, GameHUDSkin.DifficultyColor("E"));
    }

    [TestCase(11488.89f)]
    [TestCase(11628.57f)]
    [TestCase(41904.76f)]
    [TestCase(42099.32f)]
    [TestCase(42483.45f)]
    [TestCase(96545.58f)]
    [TestCase(97618.14f)]
    [TestCase(121244.44f)]
    public void EIncludesQuietAndAcceleratingAudioAttacks(float audioPeakMs)
    {
        // 原版の波形から確認した弱拍・加速音。旧譜面の強度や手の回復による抜けを防ぐ。
        var e = ChartLoader.LoadFromStreamingAssets("RushE", "E");
        Assert.IsTrue(e.notes.Any(n => Mathf.Abs(n.time - audioPeakMs) < 20), audioPeakMs.ToString());
    }

    [Test]
    public void EEndsOnTheFinalChordWithoutInventedTailCuts()
    {
        var e = ChartLoader.LoadFromStreamingAssets("RushE", "E");
        Assert.AreEqual(136280.27f, e.notes.Max(n => n.time), 1f);
        var lastChord = e.notes.Where(n => Mathf.Abs(n.time - 136280.27f) < 1f).ToArray();
        Assert.AreEqual(2, lastChord.Length);
        Assert.IsTrue(lastChord.Any(n => n.x < 0) && lastChord.Any(n => n.x > 0));
        Assert.IsTrue(lastChord.All(n => n.count == 1 && n.lengthMs == 0));
    }
}
