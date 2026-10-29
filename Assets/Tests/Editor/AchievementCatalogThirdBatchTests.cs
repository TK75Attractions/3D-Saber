using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 第3弾の26個。既存の件数・並びのテストはそのままにし、条件と実譜面の場面を確かめる。
public partial class AchievementCatalogTests
{
    static readonly string[] ThirdBatch =
    {
        "goldpair", "regain", "kiriage", "learnpair", "balance", "xcut10", "souage", "trend", "hintkey",
        "urafutari", "pairrainbow", "secretall", "goldcoin", "aobakin", "morningecho", "manicbreath",
        "cradle", "rushe4", "shubu", "gohoubi", "kouyou5", "twinstar", "hyakuren", "m_echo", "e_chords", "meteor",
    };

    static AchievementContext Real3(SecretChart song, string difficulty, JudgmentTier tier = JudgmentTier.Perfect)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", song.SongId, "chart_" + difficulty.ToLowerInvariant() + ".json");
        Assert.IsTrue(File.Exists(path), path);
        var chart = ChartLoader.Parse(File.ReadAllText(path));
        var stage = StagePerformanceTimeline.Load(song.SongId, difficulty);
        return Ctx(c =>
        {
            c.SongId = song.SongId;
            c.Difficulty = difficulty;
            c.Stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(chart, stage), tier);
        });
    }

    // 取れる例は、D・途中からの記録・記録なしでは取れないことも毎回確かめる。
    static void NoteGate3(AchievementContext c, string id)
    {
        Assert.IsTrue(Has(c, id), id);
        var stats = c.Stats;
        c.AimMode = true;
        Assert.IsFalse(Has(c, id), id + ": D");
        c.AimMode = false;
        stats.Complete = false;
        Assert.IsFalse(Has(c, id), id + ": 途中からの記録");
        stats.Complete = true;
        c.Stats = null;
        Assert.IsFalse(Has(c, id), id + ": 記録なし");
        c.Stats = stats;
    }

    static RankingPlay Play3(AchievementContext c, string id, long at, string station = "A") =>
        new RankingPlay { id = id, at = at, st = station, ns = c.Namespace, song = c.SongId, diff = c.Difficulty,
            date = c.EndedLocal.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture) };

    static void Rebuild3(AchievementContext c, JudgmentTier tier = JudgmentTier.Perfect) =>
        c.Stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(c.Stats.Chart, c.Stats.Stage), tier);

    [Test]
    public void ThirdBatchHasTheSpecifiedIdsAndTiers()
    {
        Assert.AreEqual(26, ThirdBatch.Length);
        CollectionAssert.AllItemsAreUnique(ThirdBatch);
        // 2026-10-29 夜の段の見直しで、e_chords・kouyou5 は銅、hyakuren は金、xcut10・souage・rushe4・twinstar はなし。
        var bronze = new[] { "balance", "trend", "hintkey", "urafutari", "pairrainbow", "secretall", "meteor", "e_chords", "kouyou5" };
        foreach (string id in ThirdBatch)
        {
            var d = AchievementCatalog.Find(id);
            Assert.NotNull(d, id);
            Assert.IsTrue(AchievementStore.IsValidId(id));
            Assert.LessOrEqual(id.Length, 12);
            Assert.LessOrEqual(d.Text.Length, 20, id);
            Assert.LessOrEqual(d.Name.Length, 10, id);
            var tier = id == "hyakuren" ? AchievementTier.Gold
                : bronze.Contains(id) ? AchievementTier.Bronze : AchievementTier.None;
            Assert.AreEqual(tier, d.Tier, id);
        }
    }

    [Test]
    public void GoldpairNeedsTwoSuccessfulGoldTapsOnTheirOwnSides()
    {
        var c = Ctx(x => x.Stats = Notes(PlayStatsTests.Note(1000, "gold", x: -1), PlayStatsTests.Note(1001, "gold", x: 1)));
        c.Stats.Record(0, JudgmentTier.Good, null, null, SaberHand.Left, false);
        c.Stats.Record(1, JudgmentTier.Great, null, null, SaberHand.Right, false);
        NoteGate3(c, "goldpair");
        c.Stats.Notes[0].Hand = SaberHand.Right;
        Assert.IsFalse(Has(c, "goldpair"), "反対の手");
        c.Stats.Notes[0].Hand = SaberHand.Left;
        c.Stats.Notes[0].X = 0;
        Assert.IsFalse(Has(c, "goldpair"), "中央は左右の組に数えない");
        c.Stats.Notes[0].X = -1;
        c.Stats.Notes[0].Long = true;
        Assert.IsFalse(Has(c, "goldpair"), "ロングは除く");
        c.Stats.Notes[0].Long = false;
        c.Stats.Record(1, JudgmentTier.Bad);
        Assert.IsFalse(Has(c, "goldpair"));
        c.Stats.Record(1, JudgmentTier.Perfect);
        c.Stats.Chart.notes.Add(PlayStatsTests.Note(1000));
        Rebuild3(c);
        c.Stats.Notes[0].Hand = SaberHand.Left;
        c.Stats.Notes[1].Hand = SaberHand.Right;
        Assert.IsFalse(Has(c, "goldpair"), "3個の組");
        c.Stats = Notes(PlayStatsTests.Note(1000, "gold", x: -1));
        Assert.IsFalse(Has(c, "goldpair"), "片方が無い");
    }

    [Test]
    public void RegainNeedsTwoActualRisesToMaximum()
    {
        var c = Ctx(x => x.Stats = new PlayStats { VoltageMax = VoltageMeter.MaximumLevel, VoltageMaxReaches = 2 });
        Assert.IsTrue(Has(c, "regain"));
        c.Stats.VoltageMaxReaches = 1;
        Assert.IsFalse(Has(c, "regain"), "最高段に1回届いただけ");
        c.Stats = null;
        Assert.IsFalse(Has(c, "regain"));
    }

    [Test]
    public void KiriageNeedsTheUpwardTitleCutByASaber()
    {
        var c = Ctx(x => { x.TitleUpward = true; x.TitleHand = SaberHand.Left; });
        Assert.IsTrue(Has(c, "kiriage"));
        c.TitleHand = SaberHand.Right;
        Assert.IsTrue(Has(c, "kiriage"));
        c.TitleHand = SaberHand.Any;
        Assert.IsFalse(Has(c, "kiriage"));
        c.TitleHand = SaberHand.Left;
        c.TitleUpward = false;
        Assert.IsFalse(Has(c, "kiriage"));
    }

    [Test]
    public void LearnpairUsesAllArrowsAndShortLongsInTheirFirstGroups()
    {
        var c = Ctx(x =>
        {
            x.PracticePairBefore = true;
            x.Stats = Notes(PlayStatsTests.Note(1000, direction: "up"), PlayStatsTests.Note(1000, "blue", "down"),
                PlayStatsTests.Note(1000, "gold"), PlayStatsTests.Note(2000, count: 2),
                PlayStatsTests.Note(2000, "blue", count: 4), PlayStatsTests.Note(3000, direction: "up"));
        });
        PlayStatsTests.AllResults(c.Stats, JudgmentTier.Perfect);
        c.Stats.Record(0, JudgmentTier.Good);
        c.Stats.Record(1, JudgmentTier.Great);
        c.Stats.Record(2, JudgmentTier.Miss);
        c.Stats.Record(5, JudgmentTier.Miss);
        NoteGate3(c, "learnpair");
        c.PracticePairBefore = false;
        Assert.IsFalse(Has(c, "learnpair"));
        c.PracticePairBefore = true;
        c.Stats.Notes[1].WrongSwing = true;
        Assert.IsFalse(Has(c, "learnpair"));
        c.Stats.Notes[1].WrongSwing = false;
        c.Stats.Record(4, JudgmentTier.Miss);
        Assert.IsFalse(Has(c, "learnpair"), "同時のロングを片方逃した");
        c.Stats.Record(4, JudgmentTier.Perfect);
        c.Stats.Record(0, JudgmentTier.Bad);
        Assert.IsFalse(Has(c, "learnpair"));
        c.Stats = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(1000, direction: "up"), PlayStatsTests.Note(2000, count: 5)),
            JudgmentTier.Perfect);
        Assert.IsFalse(Has(c, "learnpair"), "2〜4回のロングが無い");
        c.Stats = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(1000, count: 3)), JudgmentTier.Perfect);
        Assert.IsFalse(Has(c, "learnpair"), "矢印が無い");
    }

    [Test]
    public void BalanceCountsNotesOnceAndIncludesBadCuts()
    {
        var data = Taps(20).Concat(Taps(20, "blue")).Concat(Taps(5, "gold")).ToArray();
        data[0].count = 4;
        var c = Ctx(x => x.Stats = PlayStatsTests.AllResults(Notes(data), JudgmentTier.Bad));
        NoteGate3(c, "balance");
        c.Stats.Record(0, JudgmentTier.Miss);
        Assert.IsFalse(Has(c, "balance"));
        c.Stats.Record(20, JudgmentTier.Miss);
        Assert.IsFalse(Has(c, "balance"), "19個ずつでは足りない");
    }

    [Test]
    public void Xcut10NeedsTenCrossCuts()
    {
        var c = Ctx(x => x.Stats = new PlayStats { CrossCuts = 10 });
        NoteGate3(c, "xcut10");
        c.Stats.CrossCuts = 9;
        Assert.IsFalse(Has(c, "xcut10"));
    }

    [Test]
    public void SouageAlsoRequiresTheExistingPracticeCondition()
    {
        var c = Ctx(x => { x.PracticedBefore = true; x.PracticeAllFirstLoop = true; });
        Assert.IsTrue(Has(c, "souage"));
        c.PracticedBefore = false;
        Assert.IsFalse(Has(c, "souage"));
        c.PracticedBefore = true;
        c.PracticeAllFirstLoop = false;
        Assert.IsFalse(Has(c, "souage"));
    }

    [Test]
    public void TrendUsesTheTwoImmediatePredecessorsAndTheOlderTime()
    {
        var c = Ctx();
        var older = Play3(c, "old1", c.EndedUnixMs - 1800000);
        var newer = Play3(c, "old2", c.EndedUnixMs - 1000);
        var ignored = Play3(c, "other-station", c.EndedUnixMs - 1, "B");
        ignored.song = "別の曲";
        c.Plays = new[] { newer, ignored, older, older };
        Assert.IsTrue(Has(c, "trend"), "30分ちょうど・重複した回は1回");
        older.at--;
        Assert.IsFalse(Has(c, "trend"));
        older.at++;
        newer.song = "別の曲";
        Assert.IsFalse(Has(c, "trend"), "間に別の曲がある");
        newer.song = c.SongId;
        newer.ns = "別モード";
        Assert.IsFalse(Has(c, "trend"));
        newer.ns = c.Namespace;
        older.at = newer.at = c.EndedUnixMs;
        c.Plays = new[] { Play3(c, "zzzz", c.EndedUnixMs), older, newer };
        Assert.IsTrue(Has(c, "trend"), "同時刻は ID が小さい2回だけ");
        c.Plays = new[] { older };
        Assert.IsFalse(Has(c, "trend"), "1回不足");
    }

    [Test]
    public void HintkeyAllowsTheNormalChartButNeedsAHintBeforeOpening()
    {
        var c = Ctx(x => { x.OpenedSecretHere = true; x.HintsBeforeOpen = 1; });
        Assert.IsTrue(Has(c, "hintkey"));
        Song(c, RushESecretState.RushE);
        Assert.IsTrue(Has(c, "hintkey"));
        c.HintsBeforeOpen = 0;
        Assert.IsFalse(Has(c, "hintkey"));
        c.HintsBeforeOpen = 2;
        c.OpenedSecretHere = false;
        Assert.IsFalse(Has(c, "hintkey"));
    }

    [Test]
    public void UrafutariUsesTheNearestNeighborsDifficulty()
    {
        var c = Ctx(x => Song(x, RushESecretState.RushE));
        var p = Play3(c, "neighbor", c.EndedUnixMs - 60000, "B");
        p.song = RushESecretState.MorningMemory.SongId;
        p.diff = RushESecretState.MorningMemory.Difficulty;
        c.Plays = new[] { p };
        Assert.IsTrue(Has(c, "urafutari"), "別の曲でもよい");
        p.at--;
        Assert.IsFalse(Has(c, "urafutari"));
        p.at++;
        p.diff = "Hard";
        Assert.IsFalse(Has(c, "urafutari"));
        p.diff = "M";
        var closer = Play3(c, "closer", c.EndedUnixMs - 1, "B");
        closer.diff = "Normal";
        c.Plays = new[] { p, closer };
        Assert.IsFalse(Has(c, "urafutari"), "遠い裏の回を探し直さない");
        c.Plays = new[] { p };
        c.Difficulty = "Normal";
        Assert.IsFalse(Has(c, "urafutari"));
    }

    [Test]
    public void PairrainbowNeedsBothPeaksAndTheNearestNeighborsAchievement()
    {
        var c = Ctx(x => x.Stats = new PlayStats { VoltageMax = VoltageMeter.MaximumLevel });
        var p = Play3(c, "neighbor", c.EndedUnixMs, "B");
        p.ach.Add("rainbow");
        c.Plays = new[] { p };
        Assert.IsTrue(Has(c, "pairrainbow"));
        c.Stats.VoltageMax--;
        Assert.IsFalse(Has(c, "pairrainbow"));
        c.Stats.VoltageMax++;
        p.ach.Clear();
        Assert.IsFalse(Has(c, "pairrainbow"));
        p.ach.Add("rainbow");
        p.ns = "別モード";
        Assert.IsFalse(Has(c, "pairrainbow"));
        p.ns = c.Namespace;
        p.st = c.Station;
        Assert.IsFalse(Has(c, "pairrainbow"));
        c.Stats = null;
        Assert.IsFalse(Has(c, "pairrainbow"));
    }

    [Test]
    public void SecretallNeedsExactlyThisMissingRegisteredSecret()
    {
        var c = Ctx(x => Song(x, RushESecretState.RushE));
        var plays = new List<RankingPlay>();
        foreach (var chart in RushESecretState.Charts)
        {
            if (chart == RushESecretState.RushE) continue;
            var p = Play3(c, "secret-" + chart.Difficulty, c.EndedUnixMs - 1, "B");
            p.song = chart.SongId;
            p.diff = chart.Difficulty.ToLowerInvariant();
            plays.Add(p);
        }
        Assert.AreEqual(RushESecretState.Charts.Length - 1, plays.Count);
        c.Plays = plays;
        Assert.IsTrue(Has(c, "secretall"));
        var first = plays[0];
        plays.RemoveAt(0);
        Assert.IsFalse(Has(c, "secretall"), "もう1種不足");
        plays.Add(first);
        first.date = c.EndedLocal.AddDays(-1).ToString("yyyy/MM/dd");
        Assert.IsFalse(Has(c, "secretall"));
        first.date = c.EndedLocal.ToString("yyyy/MM/dd");
        first.ns = "別モード";
        Assert.IsFalse(Has(c, "secretall"));
        first.ns = c.Namespace;
        string song = first.song;
        first.song = "別の曲";
        Assert.IsFalse(Has(c, "secretall"), "文字だけが裏でも数えない");
        first.song = song;
        plays.Add(Play3(c, "already", c.EndedUnixMs - 1));
        Assert.IsFalse(Has(c, "secretall"), "この裏も遊ばれている");
        c.Difficulty = "Normal";
        Assert.IsFalse(Has(c, "secretall"));
    }

    [Test]
    public void GoldcoinNeedsFortyGoldNotesOnTheNormalSide()
    {
        var c = Real3(RushESecretState.ElDoradoG, "Normal", JudgmentTier.Miss);
        var gold = c.Stats.Notes.Select((n, k) => k).Where(k => c.Stats.Notes[k].Gold).Take(40).ToArray();
        Assert.AreEqual(40, gold.Length);
        foreach (int k in gold) c.Stats.Record(k, JudgmentTier.Bad);
        NoteGate3(c, "goldcoin");
        c.Stats.Record(gold[0], JudgmentTier.Miss);
        Assert.IsFalse(Has(c, "goldcoin"));
        c.Stats.Record(gold[0], JudgmentTier.Perfect);
        c.Difficulty = RushESecretState.ElDoradoG.Difficulty;
        Assert.IsFalse(Has(c, "goldcoin"));
    }

    [TestCase("Easy")]
    [TestCase("Normal")]
    [TestCase("Hard")]
    [TestCase("W")]
    public void AobakinUsesTheFirstGoldPairAt82286Milliseconds(string difficulty)
    {
        var c = Real3(RushESecretState.AobasouWind, difficulty);
        var group = c.Stats.FirstGroupWith(n => n.Gold);
        Assert.AreEqual(2, group.Count);
        foreach (int k in group)
        {
            Assert.IsTrue(c.Stats.Notes[k].Gold);
            Assert.AreEqual(82.285714, c.Stats.Notes[k].Time, .001);
        }
        NoteGate3(c, "aobakin");
        c.Stats.Record(group[0], JudgmentTier.Great);
        Assert.IsFalse(Has(c, "aobakin"));
        c.Stats.Chart.notes.RemoveAt(c.Stats.Notes[group[0]].Index);
        Rebuild3(c);
        Assert.IsFalse(Has(c, "aobakin"), "金の片方が無い");
        // 双葉を両方消しても、あとの金の組(Hard・W の 109.286 秒)を代わりにしない(点検 E)
        c = Real3(RushESecretState.AobasouWind, difficulty);
        c.Stats.Chart.notes.RemoveAll(n => n.color == "gold" && Math.Abs(n.TimeSeconds + c.Stats.OffsetSeconds - 82.285714) < .01);
        Rebuild3(c);
        Assert.IsFalse(Has(c, "aobakin"), "双葉が無い");
    }

    [TestCase("Easy", 3)]
    [TestCase("Normal", 6)]
    [TestCase("Hard", 7)]
    public void MorningechoHasTheExpectedWindowsAndNeedsMorePerfects(string difficulty, int count)
    {
        var c = Real3(RushESecretState.MorningMemory, difficulty, JudgmentTier.Good);
        var first = c.Stats.WindowAfterSection("第1周", .05, 2.035, count);
        var second = c.Stats.WindowAfterSection("第2周", .05, 2.035, count);
        Assert.AreEqual(count, first.Count);
        Assert.AreEqual(count, second.Count);
        Assert.AreEqual(41.217966, c.Stats.Notes[first[0]].Time, .001);
        Assert.AreEqual(114.438305, c.Stats.Notes[second[0]].Time, .001);
        Assert.IsFalse(Has(c, "morningecho"), "同数");
        c.Stats.Record(second[0], JudgmentTier.Perfect);
        NoteGate3(c, "morningecho");
        c.Stats.Record(first[0], JudgmentTier.Perfect);
        Assert.IsFalse(Has(c, "morningecho"), "同数に戻った");
        c.Stats.Record(first[0], JudgmentTier.Good);
        c.Stats.Record(second[second.Count - 1], JudgmentTier.Bad);
        Assert.IsFalse(Has(c, "morningecho"));
        c.Stats.Notes[first[0]].Result = null;
        Assert.IsFalse(Has(c, "morningecho"), "第1周の記録が欠けている");
        c.Stats.Chart.notes.RemoveAt(c.Stats.Notes[second[0]].Index);
        Rebuild3(c);
        Assert.IsFalse(Has(c, "morningecho"), "窓の1個が欠けている");
        c.Stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(c.Stats.Chart), JudgmentTier.Perfect);
        Assert.IsFalse(Has(c, "morningecho"), "stage が無い");
        c.Difficulty = "M";
        Assert.IsFalse(Has(c, "morningecho"));
    }

    [TestCase("Easy")]
    [TestCase("Normal")]
    public void ManicbreathHasFiveQuietNotesAndTwoGoldNotes(string difficulty)
    {
        var c = Real3(RushESecretState.ManicSonicReverse, difficulty, JudgmentTier.Good);
        var scene = c.Stats.ManicBreathNotes();
        var expected = new[] { 127.5, 130.5, 133.5, 137.5, 140.5, 143.5, 143.5 };
        Assert.AreEqual(7, scene.Count);
        for (int i = 0; i < scene.Count; i++) Assert.AreEqual(expected[i], c.Stats.Notes[scene[i]].Time, .001);
        Assert.IsTrue(c.Stats.Notes[scene[5]].Gold && c.Stats.Notes[scene[6]].Gold);
        NoteGate3(c, "manicbreath");
        c.Stats.Record(scene[0], JudgmentTier.Bad);
        Assert.IsFalse(Has(c, "manicbreath"));
        foreach (int index in scene)
        {
            var missing = Real3(RushESecretState.ManicSonicReverse, difficulty);
            missing.Stats.Chart.notes.RemoveAt(missing.Stats.Notes[index].Index);
            Rebuild3(missing);
            Assert.IsFalse(Has(missing, "manicbreath"), "欠けたノーツ: " + index);
        }
        c = Real3(RushESecretState.ManicSonicReverse, difficulty);
        c.Difficulty = "Hard";
        Assert.IsFalse(Has(c, "manicbreath"));
        c.Difficulty = "R";
        Assert.IsFalse(Has(c, "manicbreath"));
    }

    [Test]
    public void ManicbreathFollowsMarkersAndCanReadAnOlderChartWithoutThem()
    {
        var c = Real3(RushESecretState.ManicSonicReverse, "Normal");
        Assert.IsTrue(c.Stats.Chart.editorMarkers.Any(m => m.name == "BPM60"));
        Assert.IsTrue(c.Stats.Chart.editorMarkers.Any(m => m.name == "再加速"));
        c.Stats.Chart.editorMarkers.Clear();
        Rebuild3(c);
        Assert.IsTrue(Has(c, "manicbreath"), "目印の無い旧譜面は指定時刻");
        c = Real3(RushESecretState.ManicSonicReverse, "Normal");
        foreach (var n in c.Stats.Chart.notes) n.time += 10000;
        foreach (var marker in c.Stats.Chart.editorMarkers) marker.timeMs += 10000;
        c.Stats.Chart.offsetMs += 200;
        Rebuild3(c);
        Assert.IsTrue(Has(c, "manicbreath"), "目印と offsetMs を使う");
        Assert.AreEqual(137.7, c.Stats.Notes[c.Stats.ManicBreathNotes()[0]].Time, .001);
    }

    [Test]
    public void CradleNeedsTenGoldTapsOnEachHandsSide()
    {
        var data = new List<NoteData>();
        for (int i = 0; i < 20; i++) data.Add(PlayStatsTests.Note(1000 + i * 500, "gold", x: i < 10 ? -1 : 1));
        var c = Ctx(x =>
        {
            x.SongId = RushESecretState.YurikagoOvertake.SongId;
            x.Stats = PlayStatsTests.AllResults(Notes(data.ToArray()), JudgmentTier.Bad);
        });
        for (int i = 0; i < 20; i++) c.Stats.Notes[i].Hand = i < 10 ? SaberHand.Left : SaberHand.Right;
        NoteGate3(c, "cradle");
        c.Difficulty = "P";
        Assert.IsTrue(Has(c, "cradle"));
        c.Stats.Notes[0].X = c.Stats.Notes[10].X = 0;
        Assert.IsTrue(Has(c, "cradle"), "中央は実際に切った手で数える");
        c.Stats.Notes[0].X = 1;
        Assert.IsFalse(Has(c, "cradle"), "左手で右側を切った");
        c.Stats.Notes[0].X = 0;
        c.Stats.Notes[0].Long = true;
        Assert.IsFalse(Has(c, "cradle"));
        c.Stats.Notes[0].Long = false;
        c.Stats.Record(19, JudgmentTier.Miss);
        Assert.IsFalse(Has(c, "cradle"), "右が9個");
        c.SongId = "別の曲";
        Assert.IsFalse(Has(c, "cradle"));
    }

    [TestCase("Easy")]
    [TestCase("Normal")]
    [TestCase("Hard")]
    [TestCase("E")]
    public void Rushe4UsesTheFourOpeningNotes(string difficulty)
    {
        var c = Real3(RushESecretState.RushE, difficulty);
        var intro = c.Stats.Notes.Where(n => n.Time < 7.2).ToArray();
        Assert.AreEqual(4, intro.Length);
        NoteGate3(c, "rushe4");
        intro[0].Result = JudgmentTier.Great;
        Assert.IsFalse(Has(c, "rushe4"));
        c.Stats.Chart.notes.RemoveAt(intro[0].Index);
        Rebuild3(c);
        Assert.IsFalse(Has(c, "rushe4"));
    }

    [Test]
    public void Rushe4DoesNotIncludeTheBoundaryAt7200Milliseconds()
    {
        var c = Ctx(x =>
        {
            x.SongId = RushESecretState.RushE.SongId;
            x.Stats = PlayStatsTests.AllResults(Notes(PlayStatsTests.Note(0), PlayStatsTests.Note(1000),
                PlayStatsTests.Note(2000), PlayStatsTests.Note(7199), PlayStatsTests.Note(7200)), JudgmentTier.Perfect);
        });
        c.Stats.Record(4, JudgmentTier.Miss);
        Assert.IsTrue(Has(c, "rushe4"));
        c.Stats.Record(3, JudgmentTier.Miss);
        Assert.IsFalse(Has(c, "rushe4"));
    }

    [TestCase("Easy")]
    [TestCase("Normal")]
    [TestCase("Hard")]
    public void ShubuUsesTheFirstGroupAfterTheMainSection(string difficulty)
    {
        var c = Real3(RushESecretState.PhobiaAvoid, difficulty);
        var group = c.Stats.GroupAfterSection("主部", .05);
        Assert.NotNull(group);
        Assert.AreEqual(2, group.Count);
        NoteGate3(c, "shubu");
        c.Stats.Record(group[0], JudgmentTier.Great);
        Assert.IsFalse(Has(c, "shubu"));
        double first = c.Stats.Notes[group[0]].Time;
        c.Stats.Chart.notes.RemoveAll(n => n.TimeSeconds + c.Stats.OffsetSeconds >= first);
        Rebuild3(c);
        Assert.IsFalse(Has(c, "shubu"), "主部に入る前に切り詰めた");
        // 入口の組の片方・組ごとを消しても、残りやあとの組を代わりにしない(点検 E)
        c = Real3(RushESecretState.PhobiaAvoid, difficulty);
        group = c.Stats.GroupAfterSection("主部", .05);
        c.Stats.Chart.notes.RemoveAt(c.Stats.Notes[group[0]].Index);
        Rebuild3(c);
        Assert.IsFalse(Has(c, "shubu"), "入口の片方が無い");
        c = Real3(RushESecretState.PhobiaAvoid, difficulty);
        group = c.Stats.GroupAfterSection("主部", .05);
        c.Stats.Chart.notes.RemoveAll(n => Math.Abs(n.TimeSeconds + c.Stats.OffsetSeconds - first) < .001);
        Rebuild3(c);
        Assert.IsFalse(Has(c, "shubu"), "入口の組が無い(次の組は始まりから 0.1 秒より後)");
        c = Real3(RushESecretState.PhobiaAvoid, difficulty);
        c.Stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(c.Stats.Chart), JudgmentTier.Perfect);
        Assert.IsFalse(Has(c, "shubu"), "区間が無い");
        c.Difficulty = "D";
        Assert.IsFalse(Has(c, "shubu"));
    }

    [TestCase("Easy")]
    [TestCase("Normal")]
    [TestCase("Hard")]
    public void GohoubiUsesTheLastGoldPair(string difficulty)
    {
        var c = Real3(RushESecretState.BenkyouQuiz, difficulty);
        var group = c.Stats.LastGroupWith(n => n.Gold);
        Assert.AreEqual(2, group.Count);
        foreach (int k in group) Assert.AreEqual(133.245422, c.Stats.Notes[k].Time, .001);
        NoteGate3(c, "gohoubi");
        c.Stats.Record(group[0], JudgmentTier.Great);
        Assert.IsFalse(Has(c, "gohoubi"));
        c.Stats.Chart.notes.RemoveAt(c.Stats.Notes[group[0]].Index);
        Rebuild3(c);
        Assert.IsFalse(Has(c, "gohoubi"), "片方が無い");
        c.Difficulty = "Q";
        Assert.IsFalse(Has(c, "gohoubi"));
    }

    [TestCase("Easy")]
    [TestCase("Normal")]
    [TestCase("Hard")]
    [TestCase("L")]
    public void Kouyou5NeedsEveryOneOfTheFiveSectionEntrances(string difficulty)
    {
        var c = Real3(RushESecretState.EscapadeLong, difficulty);
        var sections = c.Stats.Stage.sections.Where(s => s.label.StartsWith("高揚部", StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(5, sections.Length);
        var scene = c.Stats.GroupsAfterSections("高揚部", .05, 5);
        Assert.GreaterOrEqual(scene.Count, 5);
        foreach (var section in sections)
        {
            var group = c.Stats.GroupFrom(section.startSeconds - .05, false);
            Assert.NotNull(group);
            foreach (int k in group) CollectionAssert.Contains(scene, k);
        }
        NoteGate3(c, "kouyou5");
        c.Stats.Record(scene[0], JudgmentTier.Great);
        Assert.IsFalse(Has(c, "kouyou5"));
        var four = new StagePerformanceTimeline { sections = sections.Take(4).ToArray() };
        c.Stats = PlayStatsTests.AllResults(PlayStatsTests.Stats(c.Stats.Chart, four), JudgmentTier.Perfect);
        Assert.IsFalse(Has(c, "kouyou5"), "区間が4つ");
        c = Real3(RushESecretState.EscapadeLong, difficulty);
        double last = sections.Max(s => s.startSeconds) - .05;
        c.Stats.Chart.notes.RemoveAll(n => n.TimeSeconds + c.Stats.OffsetSeconds > last);
        Rebuild3(c);
        Assert.IsFalse(Has(c, "kouyou5"), "最後の区間の組が無い");
        // 1つ目の高揚部の入口の組だけを消しても、同じ区間のあとの組を代わりにしない(点検 E)
        c = Real3(RushESecretState.EscapadeLong, difficulty);
        var entry = c.Stats.GroupFrom(sections.Min(s => s.startSeconds) - .05, false);
        double entryTime = c.Stats.Notes[entry[0]].Time;
        c.Stats.Chart.notes.RemoveAll(n => Math.Abs(n.TimeSeconds + c.Stats.OffsetSeconds - entryTime) < .001);
        Rebuild3(c);
        var after = c.Stats.GroupFrom(sections.Min(s => s.startSeconds) - .05, false);
        if (after != null && after.Count > 0 && c.Stats.Notes[after[0]].Time > sections.Min(s => s.startSeconds) + AchievementCatalog.SceneEntrySeconds)
            Assert.IsFalse(Has(c, "kouyou5"), "入口の組が無い");
    }

    [TestCase("Hard")]
    [TestCase("V")]
    public void TwinstarNeedsBothMaximumLongs(string difficulty)
    {
        var c = Real3(RushESecretState.ZeroVanish, difficulty);
        var scene = c.Stats.MaximumLongs(20);
        Assert.AreEqual(2, scene.Count);
        foreach (int k in scene)
        {
            Assert.AreEqual(50, c.Stats.Notes[k].Cuts);
            Assert.AreEqual(88.795508, c.Stats.Notes[k].Time, .001);
        }
        NoteGate3(c, "twinstar");
        c.Stats.Record(scene[0], JudgmentTier.Miss);
        Assert.IsFalse(Has(c, "twinstar"));
        c.Stats.Chart.notes.RemoveAt(c.Stats.Notes[scene[0]].Index);
        Rebuild3(c);
        Assert.IsFalse(Has(c, "twinstar"), "1本だけ");
        Assert.IsFalse(Has(Real3(RushESecretState.ZeroVanish, "Easy"), "twinstar"));
        Assert.IsFalse(Has(Real3(RushESecretState.ZeroVanish, "Normal"), "twinstar"));
    }

    [Test]
    public void HyakurenNeedsTheHundredCutLongFinished()
    {
        var c = Real3(RushESecretState.ElDoradoG, "G");
        var longs = c.Stats.Notes.Where(n => n.Long && n.Cuts >= 100).ToArray();
        Assert.AreEqual(1, longs.Length);
        Assert.AreEqual(100, longs[0].Cuts);
        Assert.AreEqual(79.222, longs[0].Time, .001);
        NoteGate3(c, "hyakuren");
        longs[0].Result = JudgmentTier.Miss;
        Assert.IsFalse(Has(c, "hyakuren"));
        longs[0].Result = JudgmentTier.Perfect;
        longs[0].Cuts = 99;
        Assert.IsFalse(Has(c, "hyakuren"));
        longs[0].Cuts = 100;
        c.Difficulty = "Hard";
        Assert.IsFalse(Has(c, "hyakuren"));
        c.Difficulty = "G";
        c.Stats.Chart.notes.RemoveAt(longs[0].Index);
        Rebuild3(c);
        Assert.IsFalse(Has(c, "hyakuren"), "対象が無い");
    }

    [Test]
    public void MemoryEchoUsesTheSixSpecifiedNotesAndTheirMemoryInterval()
    {
        var c = Real3(RushESecretState.MorningMemory, "M", JudgmentTier.Good);
        var scene = c.Stats.MemoryPair(AchievementCatalog.MemoryEchoRecallSeconds, 3);
        var expected = new[] { 16.811186, 18.336610, 18.845085, 20.878983, 22.404407, 22.912881 };
        Assert.AreEqual(6, scene.Count);
        for (int i = 0; i < scene.Count; i++)
        {
            Assert.AreEqual(expected[i], c.Stats.Notes[scene[i]].Time, .001);
            Assert.AreEqual(i >= 3, c.Stats.Notes[scene[i]].Veiled);
        }
        NoteGate3(c, "m_echo");
        c.Stats.Record(scene[3], JudgmentTier.Bad);
        Assert.IsFalse(Has(c, "m_echo"));
        foreach (int k in scene)
        {
            var missing = Real3(RushESecretState.MorningMemory, "M");
            missing.Stats.Chart.notes.RemoveAt(missing.Stats.Notes[k].Index);
            Rebuild3(missing);
            Assert.IsFalse(Has(missing, "m_echo"), "欠けたノーツ: " + k);
        }
        c = Real3(RushESecretState.MorningMemory, "M");
        c.Stats.Chart.memory.veils.Clear();
        Assert.IsFalse(Has(c, "m_echo"), "memory が無い");
        c = Real3(RushESecretState.MorningMemory, "M", JudgmentTier.Miss);
        for (int k = 0; k < 6; k++) c.Stats.Record(k, JudgmentTier.Perfect);
        Assert.IsFalse(Has(c, "m_echo"), "B の指定より前の1組目だけでは取れない");
        c = Real3(RushESecretState.MorningMemory, "M");
        c.Difficulty = "Normal";
        Assert.IsFalse(Has(c, "m_echo"));
    }

    [Test]
    public void EChordsNeedsAllTwelveGoldNotesAtTheSixTimes()
    {
        var c = Real3(RushESecretState.RushE, "E", JudgmentTier.Good);
        var scene = c.Stats.GroupsAtTimes(AchievementCatalog.EChordSeconds, 2, n => n.Gold && !n.Long);
        Assert.AreEqual(12, scene.Count);
        for (int i = 0; i < scene.Count; i++)
            Assert.AreEqual(AchievementCatalog.EChordSeconds[i / 2], c.Stats.Notes[scene[i]].Time, .001);
        Assert.AreEqual(118.382, c.Stats.Notes[scene[10]].Time, .001);
        NoteGate3(c, "e_chords");
        c.Stats.Record(scene[0], JudgmentTier.Bad);
        Assert.IsFalse(Has(c, "e_chords"));
        foreach (int k in scene)
        {
            var missing = Real3(RushESecretState.RushE, "E");
            missing.Stats.Chart.notes.RemoveAt(missing.Stats.Notes[k].Index);
            Rebuild3(missing);
            Assert.IsFalse(Has(missing, "e_chords"), "欠けたノーツ: " + k);
        }
        c = Real3(RushESecretState.RushE, "E");
        c.Difficulty = "Hard";
        Assert.IsFalse(Has(c, "e_chords"));
    }

    [Test]
    public void MeteorCountsActualCutsEvenWhenTheLongEndsInMiss()
    {
        var c = Real3(RushESecretState.EscapadeLong, "L", JudgmentTier.Miss);
        var longs = c.Stats.Notes.Where(n => n.Long && n.Cuts >= 1000).ToArray();
        Assert.AreEqual(1, longs.Length);
        Assert.AreEqual(1000, longs[0].Cuts);
        Assert.AreEqual(5, longs[0].Time, .001);
        longs[0].CutsDone = 500;
        NoteGate3(c, "meteor");
        longs[0].CutsDone = 499;
        Assert.IsFalse(Has(c, "meteor"));
        longs[0].CutsDone = 500;
        longs[0].Cuts = 999;
        Assert.IsFalse(Has(c, "meteor"));
        longs[0].Cuts = 1000;
        c.Difficulty = "Hard";
        Assert.IsFalse(Has(c, "meteor"));
        c.Difficulty = "L";
        c.Stats.Chart.notes.RemoveAt(longs[0].Index);
        Rebuild3(c);
        Assert.IsFalse(Has(c, "meteor"), "対象が無い");
    }
}
