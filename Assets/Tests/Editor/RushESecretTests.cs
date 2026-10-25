using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

public class RushESecretTests
{
    [Test]
    public void AobasouWindUsesTheSharedSecretTableAndLeafAccent()
    {
        var secret = RushESecretState.For("Aobasou");
        Assert.AreSame(RushESecretState.AobasouWind, secret);
        Assert.AreEqual("W", secret.Difficulty);
        Assert.AreSame(secret, RushESecretState.ForDifficulty("w"));
        Assert.AreSame(secret, RushESecretState.ForDifficulty(" W "));
        Assert.AreEqual(RushESecretState.Leaf, GameHUDSkin.DifficultyColor("W"));
        Assert.AreEqual(RushESecretState.Leaf, GameStartCountdown.AccentForDifficulty("w"));
        Assert.AreNotEqual(UISkinPalette.LogoGreen, RushESecretState.Leaf);

        var state = new RushESecretState();
        state.Select("Aobasou");
        for (int i = 0; i < 9; i++)
        {
            Assert.IsTrue(state.TryHit(i * .2));
            Assert.IsFalse(state.Transitioning);
        }
        Assert.IsTrue(state.TryHit(2));
        Assert.IsTrue(state.Reveal());
        Assert.IsTrue(state.Finish());
        Assert.IsTrue(state.Unlocked);
        Assert.AreSame(secret, state.Current);
        state.Select("RushE");
        Assert.IsFalse(state.Unlocked);
    }

    [Test]
    public void BenkyouMemoryUsesTheSharedSecretTableAndAmberAccent()
    {
        var secret = RushESecretState.For("BenkyouRemaster");
        Assert.AreSame(RushESecretState.BenkyouMemory, secret);
        Assert.AreEqual("M", secret.Difficulty);
        Assert.AreSame(secret, RushESecretState.ForDifficulty("m"));
        Assert.AreSame(secret, RushESecretState.ForDifficulty(" M "));
        Assert.AreEqual(RushESecretState.Amber, GameHUDSkin.DifficultyColor("M"));
        Assert.AreEqual(RushESecretState.Amber, GameStartCountdown.AccentForDifficulty("m"));
        CollectionAssert.AreEqual(new[]
        {
            RushESecretState.RushE, RushESecretState.ElDoradoG,
            RushESecretState.PhobiaAvoid, RushESecretState.BenkyouMemory,
            RushESecretState.AobasouWind
        }, RushESecretState.Charts);

        var state = new RushESecretState();
        state.Select("BenkyouRemaster");
        for (int i = 0; i < 9; i++)
        {
            Assert.IsTrue(state.TryHit(i * .2));
            Assert.IsFalse(state.Transitioning);
        }
        Assert.IsTrue(state.TryHit(2));
        Assert.IsTrue(state.Reveal());
        Assert.IsTrue(state.Finish());
        Assert.IsTrue(state.Unlocked);
        Assert.AreSame(secret, state.Current);
        state.Select("RushE");
        Assert.IsFalse(state.Unlocked);
        Assert.AreSame(RushESecretState.RushE, state.Current);
    }

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
    public void APauseOfResetSecondsStartsTheCountOver()
    {
        var s = new RushESecretState(); s.Select("RushE");
        for (int i = 0; i < 9; i++) Assert.IsTrue(s.TryHit(i * .5));
        double t = 4 + RushESecretState.ResetSeconds;
        Assert.IsTrue(s.TryHit(t)); Assert.AreEqual(1, s.Hits, "迷う間の横切りを貯めない"); Assert.IsFalse(s.Transitioning);
        for (int i = 0; i < 9; i++) { t += RushESecretState.ResetSeconds - .01; Assert.IsTrue(s.TryHit(t)); }
        Assert.AreEqual(10, s.Hits, "間が ResetSeconds 未満なら続けて数える"); Assert.IsTrue(s.Transitioning);
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
    public void CameraSamplesSurviveRepeatedFramesAndResetAfterTrackingLoss()
    {
        var stroke = new RushECameraStroke();
        var left = new Vector2(-1.5f, 0); var right = new Vector2(1.5f, 0);
        Assert.IsFalse(stroke.Tick(left, 1, true));
        for (int i = 0; i < 8; i++) Assert.IsFalse(stroke.Tick(left, 1, true));
        Assert.IsTrue(stroke.Tick(right, 1.05, true), "カメラより描画が速くても1振りを認識する");
        for (int i = 0; i < 8; i++) Assert.IsFalse(stroke.Tick(right, 1.05, true));
        Assert.IsTrue(stroke.Tick(left, 1.10, true));
        Assert.IsFalse(stroke.Tick(right, 1.4, true), "受信間隔が長い座標飛びは数えない");
        Assert.IsFalse(stroke.Tick(right, 1.4, false));
        Assert.IsFalse(stroke.Tick(left, 1.45, true), "追跡再開の最初の座標は始点だけに使う");
        Assert.IsFalse(stroke.Tick(right, 1.44, true), "受信時計が戻った場合も始点を捨てる");
        Assert.IsFalse(stroke.Tick(left, double.NaN, true));
        Assert.IsFalse(stroke.Tick(left, 2, true));
        Assert.IsTrue(stroke.Tick(right, 2.05, true));
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
        Assert.AreEqual(171, e.notes.Count(n => n.color == "gold"));
        Assert.AreEqual(159, e.notes.Count(n => n.time >= 125718f));
        Assert.IsTrue(e.notes.Where(n => n.time >= 125718f).All(n => n.color == "gold"), "終盤ラッシュを金へ変更する");
        var accents = e.notes.Where(n => n.color == "gold" && n.time < 125718f).GroupBy(n => n.time).ToArray();
        Assert.AreEqual(6, accents.Length, "山場とは別に途中6組だけ金を追加する");
        Assert.IsTrue(accents.All(g => g.Count() == 2 && g.Any(n => n.x < 0) && g.Any(n => n.x > 0)));
        Assert.IsTrue(e.notes.Where(n => n.IsLong).All(n => n.color != "gold"), "既存ロングの担当手を保つ");
        Assert.AreEqual(4, e.notes.GroupBy(n => n.time).Max(g => g.Count()), "最後の上下2段の両手同時も保持する");
        Assert.AreNotEqual(SongAchievementStore.Key("RushE", "E"), SongAchievementStore.Key("RushE", "Hard"));
        Assert.AreNotEqual(HighScoreStore.Key("RushE", "E"), HighScoreStore.Key("RushE", "Hard"));
        Assert.AreNotEqual(DailyRankingStore.Key("RushE", "E"), DailyRankingStore.Key("RushE", "Hard"));
        Assert.AreEqual(RushESecretState.Purple, GameHUDSkin.DifficultyColor("E"));
    }

    // 2026-10-24: ElDorado の G。E と同じく10回切ると出る。長い版(3分43秒)の Hard を全部金色にした譜面と、G 専用の音源・演出・音量そろえ。
    [Test]
    public void ElDoradoUnlocksGWithTheSameTenCutsAndOtherSongsStayLocked()
    {
        var state = new RushESecretState(); state.Select("ElDorado");
        Assert.AreSame(RushESecretState.ElDoradoG, state.Current);
        for (int i = 0; i < 9; i++) { Assert.IsTrue(state.TryHit(i * .2)); Assert.IsFalse(state.Transitioning); }
        Assert.IsTrue(state.TryHit(2)); Assert.IsTrue(state.Transitioning);
        Assert.IsTrue(state.Reveal()); Assert.IsTrue(state.Finish()); Assert.IsTrue(state.Unlocked);
        state.Select("RushE"); Assert.AreSame(RushESecretState.RushE, state.Current); Assert.IsFalse(state.Unlocked, "曲を変えたら最初から");
        state.Select("Epilogue"); Assert.IsNull(state.Current); Assert.IsFalse(state.TryHit(10), "隠し譜面の無い曲は数えない");
        Assert.AreSame(RushESecretState.ElDoradoG, RushESecretState.ForDifficulty("g"));
        Assert.AreSame(RushESecretState.RushE, RushESecretState.ForDifficulty("E"));
        Assert.IsFalse(RushESecretState.IsSecretDifficulty("Hard"));
        Assert.AreEqual(UISkinPalette.NoteGold, GameHUDSkin.DifficultyColor("G"), "G は金");
        Assert.AreEqual(UISkinPalette.NoteGold, GameStartCountdown.AccentForDifficulty("G"));
        Assert.AreEqual(RushESecretState.Purple, GameStartCountdown.AccentForDifficulty("E"), "E は今までの紫");
        var restored = new RushESecretState(); restored.Select("ElDorado"); restored.RestoreUnlocked();
        Assert.IsTrue(restored.Unlocked, "判定調整との往復で G のまま戻る");
        var none = new RushESecretState(); none.Select("Epilogue"); none.RestoreUnlocked();
        Assert.IsFalse(none.Unlocked);
    }

    [Test]
    public void GIsTheLongHardChartWithEveryNoteGold()
    {
        var g = ChartLoader.LoadFromStreamingAssets("ElDorado", "G");
        Assert.AreEqual(392, g.notes.Count, "長い版の Hard の全ノーツ");
        Assert.IsTrue(g.notes.All(n => n.color == "gold"), "全部金(ノーツ・フリック・ロング)");
        Assert.AreEqual(218, g.notes.Count(n => n.type == "tap"));
        Assert.AreEqual(127, g.notes.Count(n => n.type == "direction"));
        Assert.AreEqual(47, g.notes.Count(n => n.IsLong));
        Assert.IsTrue(g.notes.Where(n => n.type == "direction").All(n => n.direction != "none"), "矢印の向きはそのまま");
        Assert.AreEqual(100f, g.bpm); Assert.AreEqual(0f, g.offsetMs); Assert.AreEqual(10, g.displayLevel);
        Assert.AreEqual(217346f, g.notes.Max(n => n.time + n.lengthMs), 1f, "長い版の最後まで");
        // ロングは長さ(時間)を変えずに回数だけ5倍(同日ユーザー指示)。長さの無かったロングも、今までの長さ(回数-1)×0.7秒を書いてある。
        var longs = g.notes.Where(n => n.IsLong).ToArray();
        Assert.IsTrue(longs.All(n => n.count >= 10 && n.count % 5 == 0), "ロングの回数は元の5倍");
        Assert.AreEqual(790, longs.Sum(n => n.count), "元の158回の5倍");
        Assert.IsTrue(longs.All(n => n.lengthMs > 0), "回数を増やしても長さは伸ばさない");
        Assert.AreEqual(33, longs.Count(n => n.count == 10 && Mathf.Approximately(n.lengthMs, 700f)), "長さの無かった2回のロングは、10回・0.7秒");
        Assert.AreEqual(1, longs.Count(n => n.count == 100 && Mathf.Approximately(n.lengthMs, 9600f)), "いちばん長い20回・9.6秒は100回・9.6秒");
        Assert.AreEqual(1135, g.notes.Sum(n => n.count), "切る回数の合計");
        Assert.AreEqual(257, ChartLoader.LoadFromStreamingAssets("ElDorado", "Hard").notes.Count, "ふつうの Hard(短い版)は変えない");
        Assert.AreNotEqual(SongAchievementStore.Key("ElDorado", "G"), SongAchievementStore.Key("ElDorado", "Hard"));
        Assert.AreNotEqual(HighScoreStore.Key("ElDorado", "G"), HighScoreStore.Key("ElDorado", "Hard"));
        Assert.AreNotEqual(DailyRankingStore.Key("ElDorado", "G"), DailyRankingStore.Key("ElDorado", "Hard"));
    }

    [Test]
    public void GPlaysTheLongAudioWithItsOwnTimingAndLoudness()
    {
        string folder = Path.Combine(Application.streamingAssetsPath, "Songs", "ElDorado");
        Assert.AreEqual("_g", SongLoudness.VariantSuffix(folder, "G"));
        Assert.AreEqual("", SongLoudness.VariantSuffix(folder, "Hard"), "ほかの難易度は今までの音源");
        var g = SongLoudness.CandidatesInFolder(folder, "G").First();
        Assert.AreEqual("audio_g.mp3", Path.GetFileName(g.Path));
        Assert.AreEqual(Mathf.Pow(10, -5.03f / 20), g.Gain, 1e-4f, "長い版は −12.97 LUFS なので −5.03 dB で、ほかの曲と同じ −18 LUFS");
        var hard = SongLoudness.CandidatesInFolder(folder, "Hard").First();
        Assert.AreEqual("audio.mp3", Path.GetFileName(hard.Path));
        Assert.AreEqual(Mathf.Pow(10, -3.79f / 20), hard.Gain, 1e-4f);
        Assert.AreEqual("loudness_g.json", SongLoudness.MetadataFileFor("audio_g.mp3"));
        Assert.AreEqual("loudness.json", SongLoudness.MetadataFileFor("audio.mp3"));
        var stage = StagePerformanceTimeline.Load("ElDorado", "G");
        Assert.AreEqual(167.4, stage.previewStartSeconds, 1e-6, "長い版の最終ピークから試聴");
        Assert.AreEqual(4, stage.sections.Length, "長い版のピーク(短い版で切った第1ピークを含む)");
        Assert.IsTrue(SongLoudness.SourceMatches(folder, stage.audioSha256, "G"), "演出の時刻は長い版の音源のもの");
        Assert.IsFalse(SongLoudness.SourceMatches(folder, stage.audioSha256, "Hard"));
        var normal = StagePerformanceTimeline.Load("ElDorado", "Hard");
        Assert.AreEqual(100.203, normal.previewStartSeconds, 1e-6, "ふつうの難易度は今までの stage.json");
        Assert.AreEqual(normal.stageTheme, stage.stageTheme, "背景は同じ");
    }

    // Eの番号 → 採用された音合わせの時刻(ms)。10/22分と10/23のz06〜z08。
    static readonly Dictionary<int, double> AudioAligned = new Dictionary<int, double>
    {
        [54] = 16001.0,
        [391] = 67877.0,
        [402] = 68244.0,
        [690] = 94131.0,
        [261] = 50138.0,
        [262] = 50138.0,
        [924] = 118382.0,
        [925] = 118382.0,
        [1136] = 134770.0,
    };

    [Test]
    public void EPreservesEveryBeatSaberNoteTimePositionAndNonGoldHand()
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
            double expected = original._time * 500.0 - 18263.0 / 11025 * 1000;
            // ユーザーが採用した音合わせだけを辞書で限定する。同時ノーツも全件保持する。
            if (AudioAligned.TryGetValue(i, out double aligned))
            {
                Assert.AreEqual(aligned, converted.time, .02, "音の頭へ動かした時刻 / " + i);
                // z08だけは選択Bが約42.24ms前。全ノーツの許容幅は広げない。
                Assert.LessOrEqual(Math.Abs(converted.time - expected),
                    i == 1136 ? 43.0 : 40.0, "承認された移動幅 / " + i);
            }
            else Assert.AreEqual(expected, converted.time, .02, "時刻 / " + i);
            Assert.AreEqual(original._lineIndex - 1.5f, converted.x, .001f, "左右配置 / " + i);
            Assert.AreEqual((original._lineLayer - 1) * .8f, converted.y, .001f, "上下配置 / " + i);
            var expectedHand = converted.color == "gold" ? SaberHand.Any : original._type == 0 ? SaberHand.Left : SaberHand.Right;
            Assert.AreEqual(expectedHand, SaberHandHelper.FromColor(converted.color), "金以外の担当手 / " + i);
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
