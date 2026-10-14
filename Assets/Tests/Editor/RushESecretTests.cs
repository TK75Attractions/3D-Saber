using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

public class RushESecretTests
{
    [Serializable] sealed class BeatSaberNote { public float _time; public int _lineIndex, _lineLayer, _type; }
    [Serializable] sealed class BeatSaberChart { public BeatSaberNote[] _notes; }

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
        Assert.AreEqual(1158, e.notes.Count, "指定動画の原譜を間引かない");
        Assert.AreEqual(7, e.notes.Count(n => n.IsLong), "要所のみ短いロングへ変更する");
        Assert.AreEqual(1169, e.notes.Sum(n => n.count));
        Assert.AreEqual(4, e.notes.GroupBy(n => n.time).Max(g => g.Count()), "最後の上下2段の両手同時も保持する");
        Assert.AreNotEqual(SongAchievementStore.Key("RushE", "E"), SongAchievementStore.Key("RushE", "Hard"));
        Assert.AreNotEqual(HighScoreStore.Key("RushE", "E"), HighScoreStore.Key("RushE", "Hard"));
        Assert.AreNotEqual(DailyRankingStore.Key("RushE", "E"), DailyRankingStore.Key("RushE", "Hard"));
        Assert.AreEqual(RushESecretState.Purple, GameHUDSkin.DifficultyColor("E"));
    }

    [Test]
    public void EPreservesEveryBeatSaberNoteTimePositionAndHand()
    {
        byte[] raw = File.ReadAllBytes(Path.Combine("Tools", "ChartDesign", "RushE", "BeatSaberSource", "ExpertPlus.dat"));
        using (var sha = System.Security.Cryptography.SHA256.Create())
            Assert.AreEqual("c024ae017deda053cfa8172ac0c0eb41b4e7600499e0934bb2746890be4e6722",
                BitConverter.ToString(sha.ComputeHash(raw)).Replace("-", "").ToLowerInvariant(), "参照版を固定する");
        var source = JsonUtility.FromJson<BeatSaberChart>(System.Text.Encoding.UTF8.GetString(raw))._notes.OrderBy(n => n._time).ToArray();
        var e = ChartLoader.LoadFromStreamingAssets("RushE", "E");
        Assert.AreEqual(source.Length, e.notes.Count);
        for (int i = 0; i < source.Length; i++)
        {
            var original = source[i]; var converted = e.notes[i];
            Assert.AreEqual(original._time * 500.0 - 18263.0 / 11025 * 1000, converted.time, .02, "時刻 / " + i);
            Assert.AreEqual(original._lineIndex - 1.5f, converted.x, .001f, "左右配置 / " + i);
            Assert.AreEqual((original._lineLayer - 1) * .8f, converted.y, .001f, "上下配置 / " + i);
            Assert.AreEqual(original._type == 0 ? SaberHand.Left : SaberHand.Right, SaberHandHelper.FromColor(converted.color), "担当手 / " + i);
            Assert.AreEqual("none", converted.direction);
        }
        Assert.IsTrue(e.notes.Any(n => n.x > 0 && n.color == "blue"), "左手で右側を切るクロスも維持する");
        Assert.IsTrue(e.notes.Any(n => n.x < 0 && n.color == "red"), "右手で左側を切るクロスも維持する");
    }

    [Test]
    public void ELongsFinishBeforeTheNextNoteForThatHand()
    {
        var e = ChartLoader.LoadFromStreamingAssets("RushE", "E");
        foreach (var note in e.notes.Where(n => n.IsLong))
        {
            Assert.That(note.count, Is.InRange(2, 3));
            Assert.That(note.lengthMs, Is.InRange(420f, 700f));
            var next = e.notes.First(n => n.time > note.time && n.color == note.color);
            Assert.GreaterOrEqual(next.time - note.time - note.lengthMs, 250f, "次の同じ手のノーツまで戻し時間を残す");
        }
    }

    [Test]
    public void EEndsOnTheFinalChordWithoutInventedTailCuts()
    {
        var e = ChartLoader.LoadFromStreamingAssets("RushE", "E");
        Assert.AreEqual(136280.27f, e.notes.Max(n => n.time), 1f);
        var lastChord = e.notes.Where(n => Mathf.Abs(n.time - 136280.27f) < 1f).ToArray();
        Assert.AreEqual(4, lastChord.Length);
        Assert.IsTrue(lastChord.Any(n => n.x < 0) && lastChord.Any(n => n.x > 0));
        Assert.IsTrue(lastChord.All(n => n.count == 1 && n.lengthMs == 0));
    }
}
