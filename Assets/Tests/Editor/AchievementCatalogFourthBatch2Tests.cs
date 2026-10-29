using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// 第4弾(2陣目)。数え合いは実際の解除番号を使い、本番の保存キーには触れない。
public class AchievementCatalogFourthBatch2Tests
{
    const string Key = "Achievements.Log.v1.__FourthBatch2Test";
    static readonly DateTime Noon = new DateTime(2026, 10, 30, 11, 0, 0, DateTimeKind.Local);
    readonly Dictionary<FieldInfo, object> session = new Dictionary<FieldInfo, object>();
    string savedKey;

    [SetUp]
    public void SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) session[field] = field.GetValue(null);
        savedKey = AchievementStore.Key;
        PlayerPrefs.DeleteKey(Key);
        AchievementStore.UseKeyForTest(Key);
        GameSession.FinalAchievements = new List<string>();
        AchievementRunInfo.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        PlayerPrefs.DeleteKey(Key);
        PlayerPrefs.Save();
        AchievementStore.UseKeyForTest(savedKey);
        AchievementRunInfo.Clear();
        foreach (var entry in session) entry.Key.SetValue(null, entry.Value);
        session.Clear();
    }

    static AchievementContext Ctx(PlayStats stats = null) => new AchievementContext
    {
        SongId = "__FourthBatch2Test", Difficulty = "Normal", Score = 30123,
        Stats = stats, Station = "A", Namespace = "", RunId = Guid.NewGuid().ToString("N"),
        StartedLocal = Noon.AddMinutes(-2), EndedLocal = Noon,
        EndedUnixMs = new DateTimeOffset(Noon).ToUnixTimeMilliseconds(), ClearsToday = 3,
        PracticeHistoryKnown = true,
    };

    static NoteData N(float ms, string color = "red", string direction = "none", int cuts = 1, float x = 0, float y = 0) =>
        PlayStatsTests.Note(ms, color, direction, cuts, x, y);

    static NoteData[] Taps(int count) => Enumerable.Range(0, count).Select(i => N(1000 + i * 500)).ToArray();

    static PlayStats Stats(params NoteData[] data)
    {
        var s = PlayStatsTests.Stats(PlayStatsTests.Chart(data));
        s.PlayStartSeconds = 0;
        s.PlayEndSeconds = 1000;
        for (int k = 0; k < s.Notes.Count; k++)
        {
            s.Record(k, JudgmentTier.Perfect);
            s.Notes[k].CutsDone = s.Notes[k].Cuts;
        }
        return s;
    }

    static bool Has(AchievementContext c, string id)
    {
        var definition = AchievementCatalog.Find(id);
        Assert.NotNull(definition, id);
        return definition.Met(c);
    }

    static void Gate(AchievementContext c, string id)
    {
        Assert.IsTrue(Has(c, id), id);
        c.AimMode = true;
        Assert.IsFalse(Has(c, id), id + ": D");
        c.AimMode = false;
        c.Stats.Complete = false;
        Assert.IsFalse(Has(c, id), id + ": 途中からの記録");
        c.Stats.Complete = true;
        var stats = c.Stats;
        c.Stats = null;
        Assert.IsFalse(Has(c, id), id + ": 記録なし");
        c.Stats = stats;
    }

    [Test]
    public void GoodstartIncludesTheWholeTenthGroup()
    {
        var data = Taps(11);
        data[10].time = data[9].time + .5f;
        var c = Ctx(Stats(data));
        Gate(c, "goodstart");
        c.Stats.Notes[10].Result = JudgmentTier.Bad;
        Assert.IsFalse(Has(c, "goodstart"));
        Assert.IsFalse(Has(Ctx(Stats(Taps(9))), "goodstart"));
    }

    [Test]
    public void KangaiiRequiresKnownUnpracticedHistoryAndAllFirstArrows()
    {
        var c = Ctx(Stats(N(1000), N(2000, direction: "right"), N(2000.5f, "blue", "left"), N(3000, direction: "up")));
        c.Stats.Notes[1].Result = JudgmentTier.Good;
        c.Stats.Notes[3].Result = JudgmentTier.Miss;
        Gate(c, "kangaii");
        c.Stats.Notes[2].WrongSwing = true;
        Assert.IsFalse(Has(c, "kangaii"));
        c.Stats.Notes[2].WrongSwing = false;
        c.Stats.Notes[2].Result = JudgmentTier.Bad;
        Assert.IsFalse(Has(c, "kangaii"));
        c.Stats.Notes[2].Result = JudgmentTier.Perfect;
        c.PracticeSeen = true;
        Assert.IsFalse(Has(c, "kangaii"));
        c.PracticeSeen = false;
        c.PracticeHistoryKnown = false;
        Assert.IsFalse(Has(c, "kangaii"));
        Assert.IsFalse(Has(Ctx(Stats(N(1000))), "kangaii"));
    }

    [Test]
    public void PracticeHistoryIncludesAnInterruptedPracticeAndSurvivesRecordedSongs()
    {
        Assert.IsTrue(AchievementRunInfo.Batch5aPracticeHistoryKnown);
        AchievementRunInfo.ObserveBatch5aPractice(true);
        Assert.IsFalse(AchievementRunInfo.Batch5aPracticeSeen, "範囲合わせだけ");
        AchievementRunInfo.PracticeFinished(false, "skipped", null);
        Assert.IsTrue(AchievementRunInfo.Batch5aPracticeSeen);
        Assert.IsFalse(AchievementRunInfo.PracticePending);
        AchievementRunInfo.BeginRun("batch5a-first");
        AchievementRunInfo.RunRecorded("batch5a-first");
        AchievementRunInfo.BeginRun("batch5a-second");
        Assert.IsTrue(AchievementRunInfo.Batch5aPracticeSeen);
        AchievementRunInfo.BeginVisitor();
        Assert.IsFalse(AchievementRunInfo.Batch5aPracticeSeen);
    }

    [Test]
    public void OwariyoshiNeedsAnOpeningFailureAndTheWholeFinalGroupPerfect()
    {
        var c = Ctx(Stats(N(1000), N(1000.5f, "blue"), N(2000), N(2000.5f, "blue")));
        c.Stats.Notes[0].Result = JudgmentTier.Miss;
        Gate(c, "owariyoshi");
        c.Stats.Notes[3].Result = JudgmentTier.Great;
        Assert.IsFalse(Has(c, "owariyoshi"));
        c.Stats.Notes[3].Result = JudgmentTier.Perfect;
        c.Stats.Notes[0].Result = JudgmentTier.Perfect;
        Assert.IsFalse(Has(c, "owariyoshi"));
    }

    [Test]
    public void BouncebackChecksEveryRecoveryAndDoesNotCountTheLastFailure()
    {
        var c = Ctx(Stats(Taps(7)));
        foreach (int k in new[] { 0, 2, 4, 6 }) c.Stats.Notes[k].Result = JudgmentTier.Miss;
        Gate(c, "bounceback");
        c.Stats.Notes[3].Result = JudgmentTier.Great;
        Assert.IsFalse(Has(c, "bounceback"));
        c.Stats.Notes[3].Result = JudgmentTier.Perfect;
        c.Stats.Notes[4].Result = JudgmentTier.Perfect;
        Assert.IsFalse(Has(c, "bounceback"), "末尾のミスを足して3回にはしない");
        var simultaneous = Ctx(Stats(N(1000), N(1000.5f, "blue"), N(2000), N(3000), N(4000)));
        simultaneous.Stats.Notes[0].Result = simultaneous.Stats.Notes[1].Result = simultaneous.Stats.Notes[3].Result = JudgmentTier.Bad;
        Gate(simultaneous, "bounceback");
    }

    static NoteData[] LongTransitions(int count)
    {
        var data = new List<NoteData>();
        for (int i = 0; i < count; i++)
        {
            var n = N(1000 + i * 4000, cuts: 2);
            n.lengthMs = 1000;
            data.Add(n);
            data.Add(N(3000 + i * 4000));
        }
        return data.ToArray();
    }

    [Test]
    public void ZanshinNeedsThreeDistinctScenesAndEveryLongAndSuccessor()
    {
        var c = Ctx(Stats(LongTransitions(3)));
        Gate(c, "zanshin");
        c.Stats.Notes[2].CutsDone--;
        Assert.IsFalse(Has(c, "zanshin"));
        c.Stats.Notes[2].CutsDone++;
        c.Stats.Notes[5].Result = JudgmentTier.Great;
        Assert.IsFalse(Has(c, "zanshin"));
        Assert.IsFalse(Has(Ctx(Stats(LongTransitions(2))), "zanshin"));
        var data = LongTransitions(3);
        data[5].time += 1;
        Assert.IsFalse(Has(Ctx(Stats(data)), "zanshin"), "1秒を超えたら対象の場面でない");
        var together = new[] { N(1000, cuts: 2), N(1000, "blue", cuts: 2), N(1000, "gold", cuts: 2), N(3000) };
        foreach (var n in together.Take(3)) n.lengthMs = 1000;
        Assert.IsFalse(Has(Ctx(Stats(together)), "zanshin"), "同時のロング3個は1場面");
    }

    [Test]
    public void GoldperfectNeedsThreeGoldNotesAndAllPerfectIncludingLongs()
    {
        var c = Ctx(Stats(N(1000, "gold"), N(2000, "gold"), N(3000, "gold", cuts: 2)));
        Gate(c, "goldperfect");
        c.Stats.Notes[2].Result = JudgmentTier.Miss;
        Assert.IsFalse(Has(c, "goldperfect"));
        Assert.IsFalse(Has(Ctx(Stats(N(1000, "gold"), N(2000, "gold"))), "goldperfect"));
    }

    [Test]
    public void YosumiUsesChartExtremaAndNeedsFourSeparateCorners()
    {
        var data = new[] { N(1000, x: -2, y: -1), N(2000, x: 2, y: -1), N(3000, x: -2, y: 1), N(4000, x: 2, y: 1) };
        var c = Ctx(Stats(data));
        Gate(c, "yosumi");
        c.Stats.Notes[3].Result = JudgmentTier.Great;
        Assert.IsFalse(Has(c, "yosumi"));
        Assert.IsFalse(Has(Ctx(Stats(data.Take(3).ToArray())), "yosumi"));
        Assert.IsFalse(Has(Ctx(Stats(N(1000), N(2000))), "yosumi"), "同じ場所を四すみにしない");
    }

    static AchievementContext Quiet(int quietNotes)
    {
        var data = new List<NoteData>();
        for (int i = 0; i < quietNotes; i++) data.Add(N(1000 + i * 500));
        data.Add(N(21000));
        var stage = new StagePerformanceTimeline
        {
            sections = new[]
            {
                new StagePerformanceTimeline.Section { startSeconds = 0, endSeconds = 10, intensity = .1f },
                new StagePerformanceTimeline.Section { startSeconds = 20, endSeconds = 30, intensity = .8f },
            },
        };
        var s = PlayStatsTests.Stats(PlayStatsTests.Chart(data.ToArray()), stage);
        s.PlayStartSeconds = 0; s.PlayEndSeconds = 40;
        PlayStatsTests.AllResults(s, JudgmentTier.Perfect);
        s.Notes[s.Notes.Count - 1].Result = JudgmentTier.Miss;
        return Ctx(s);
    }

    [Test]
    public void SasayakiSelectsTheQuietestSectionBeforeCheckingNotes()
    {
        var c = Quiet(5);
        Gate(c, "sasayaki");
        c.Stats.Notes[0].Result = JudgmentTier.Great;
        Assert.IsFalse(Has(c, "sasayaki"));
        Assert.IsFalse(Has(Quiet(4), "sasayaki"));
        Assert.IsFalse(Has(Ctx(Stats(Taps(5))), "sasayaki"), "stageなし");
        c = Quiet(5);
        c.Stats.PlayStartSeconds = 1;
        Assert.IsFalse(Has(c, "sasayaki"), "静かな場面の一部が欠ける");
        c = Quiet(5);
        c.Stats.Stage.sections[1].intensity = .1f;
        Assert.IsTrue(Has(c, "sasayaki"), "同じ強度なら早い区間");
        c.Stats.Stage.sections[1].intensity = .05f;
        Assert.IsFalse(Has(c, "sasayaki"), "弱い区間が5個未満でも次点へ替えない");
    }

    [Test]
    public void Top5UsesAvailableAllTimeRankingWithTwentyPlays()
    {
        var c = Ctx();
        c.RankingAvailable = true; c.TotalPlays = 20; c.DailyRank = 5;
        Assert.IsTrue(Has(c, "top5"));
        c.TotalPlays = 19;
        Assert.IsFalse(Has(c, "top5"));
        c.TotalPlays = 20; c.DailyRank = 6;
        Assert.IsFalse(Has(c, "top5"));
        c.DailyRank = 0;
        Assert.IsFalse(Has(c, "top5"));
        c.DailyRank = 1; c.RankingAvailable = false;
        Assert.IsFalse(Has(c, "top5"));
    }

    [Test]
    public void KannonAcceptsOutwardDiagonalsInOneGroup()
    {
        var c = Ctx(Stats(N(1000, "red", "up-right"), N(1000.5f, "blue", "down-left")));
        Gate(c, "kannon");
        c.Stats.Notes[1].Result = JudgmentTier.Great;
        Assert.IsFalse(Has(c, "kannon"));
        Assert.IsFalse(Has(Ctx(Stats(N(1000, "red", "left"), N(1000, "blue", "right"))), "kannon"));
        Assert.IsFalse(Has(Ctx(Stats(N(1000, "red", "right"), N(1002, "blue", "left"))), "kannon"));
    }

    static AchievementContext ArrowRun(int count)
    {
        var data = new List<NoteData>();
        for (int i = 0; i < count; i++)
        {
            data.Add(N(1000 + i * 500, "red", "up"));
            data.Add(N(1000.5f + i * 500, "blue", "up"));
        }
        return Ctx(Stats(data.ToArray()));
    }

    [Test]
    public void YatsugiCountsWholeArrowGroupsAndTheWholeRun()
    {
        var c = ArrowRun(6);
        Gate(c, "yatsugi");
        Assert.IsFalse(Has(ArrowRun(5), "yatsugi"));
        c = ArrowRun(7);
        c.Stats.Notes[0].Result = JudgmentTier.Great;
        Assert.IsFalse(Has(c, "yatsugi"), "一続きの先頭を除いて6回にしない");
        c = ArrowRun(6);
        c.Stats.Notes[5].Arrow = false;
        Assert.IsFalse(Has(c, "yatsugi"));
    }

    [Test]
    public void RhythmOkNeedsMeasuredCutsAndBothRates()
    {
        var c = Ctx(Stats(Taps(25)));
        for (int i = 0; i < 20; i++)
        {
            c.Stats.Notes[i].ErrorMs = 10;
            c.Stats.Notes[i].Result = i < 16 ? JudgmentTier.Perfect : JudgmentTier.Great;
        }
        for (int i = 20; i < 25; i++) c.Stats.Notes[i].Result = JudgmentTier.Miss;
        Gate(c, "rhythm_ok");
        c.Stats.Notes[0].Result = JudgmentTier.Great;
        Assert.IsFalse(Has(c, "rhythm_ok"), "8割未満");
        c.Stats.Notes[0].Result = JudgmentTier.Perfect;
        c.Stats.Notes[0].ErrorMs = null;
        Assert.IsFalse(Has(c, "rhythm_ok"), "測定19個");
        c.Stats.Notes[0].ErrorMs = double.NaN;
        Assert.IsFalse(Has(c, "rhythm_ok"));
        c.Stats.Notes[0].ErrorMs = 10;
        c.Stats.Notes[24].Result = JudgmentTier.Good;
        Assert.IsFalse(Has(c, "rhythm_ok"), "MISSが2割未満");
    }

    [Test]
    public void KaikinAllowsBadButDoesNotRequireIt()
    {
        var c = Ctx(Stats(Taps(4)));
        Gate(c, "kaikin");
        for (int i = 0; i < 3; i++) c.Stats.Notes[i].Result = JudgmentTier.Bad;
        Assert.IsTrue(Has(c, "kaikin"));
        c.Stats.Notes[3].Result = JudgmentTier.Miss;
        Assert.IsFalse(Has(c, "kaikin"));
        c.Stats.Notes[3].Result = null;
        Assert.IsFalse(Has(c, "kaikin"));
        Assert.IsFalse(Has(Ctx(Stats()), "kaikin"));
    }

    [Test]
    public void SanshuNeedsThreePerfectNotesOfEachKind()
    {
        var data = new List<NoteData>();
        for (int i = 0; i < 3; i++)
        {
            data.Add(N(1000 + i * 3000, "gold"));
            data.Add(N(2000 + i * 3000, direction: "up"));
            data.Add(N(3000 + i * 3000, cuts: 2));
        }
        var c = Ctx(Stats(data.ToArray()));
        Gate(c, "sanshu");
        foreach (int k in new[] { 0, 1, 2 })
        {
            c.Stats.Notes[k].Result = JudgmentTier.Great;
            Assert.IsFalse(Has(c, "sanshu"));
            c.Stats.Notes[k].Result = JudgmentTier.Perfect;
        }
        Assert.IsFalse(Has(Ctx(Stats(data.Take(8).ToArray())), "sanshu"));
    }

    static AchievementContext Chords(int count)
    {
        var data = new List<NoteData> { N(500) };
        for (int i = 0; i < count; i++)
        {
            data.Add(N(1000 + i * 500));
            data.Add(N(1000.5f + i * 500, "blue"));
        }
        var c = Ctx(Stats(data.ToArray()));
        c.Stats.Notes[0].Result = JudgmentTier.Miss;
        return c;
    }

    [Test]
    public void ChordsRequiresAllFiveOrMoreChordsButNotSingleNotes()
    {
        var c = Chords(5);
        Gate(c, "chords");
        c.Stats.Notes[2].Result = JudgmentTier.Great;
        Assert.IsFalse(Has(c, "chords"));
        Assert.IsFalse(Has(Chords(4), "chords"));
    }

    static AchievementContext Measured(int count, double error)
    {
        var c = Ctx(Stats(Taps(count)));
        foreach (var n in c.Stats.Notes) n.ErrorMs = error;
        return c;
    }

    [Test]
    public void YushuNeedsAUniqueAbsoluteMinimumInTheFinalGroupAndThirtyMeasurements()
    {
        var c = Measured(30, -10);
        c.Stats.Notes[29].ErrorMs = -1;
        Gate(c, "yushu");
        c.Stats.Notes[0].ErrorMs = 1;
        Assert.IsFalse(Has(c, "yushu"), "同じ最小値は除く");
        c.Stats.Notes[0].ErrorMs = .5;
        Assert.IsFalse(Has(c, "yushu"));
        c.Stats.Notes[0].ErrorMs = null;
        Assert.IsFalse(Has(c, "yushu"), "測定29個");
        c.Stats.Notes[0].ErrorMs = double.PositiveInfinity;
        Assert.IsFalse(Has(c, "yushu"));
        c.Stats.Notes[0].ErrorMs = 10;
        c.Stats.Notes[29].ErrorMs = null;
        Assert.IsFalse(Has(c, "yushu"), "最後を測れていない");
        var data = Taps(31);
        data[30].time = data[29].time + .5f;
        c = Ctx(Stats(data));
        foreach (var n in c.Stats.Notes) n.ErrorMs = 10;
        c.Stats.Notes[29].ErrorMs = 1;
        Assert.IsTrue(Has(c, "yushu"), "最後の組のどれか1つ");
    }

    [Test]
    public void SteadyUsesPopulationDeviationAndOneHundredFiniteMeasurements()
    {
        var c = Measured(100, 0);
        for (int i = 0; i < 100; i++) c.Stats.Notes[i].ErrorMs = i % 2 == 0 ? -15 : 15;
        Gate(c, "steady");
        c.Stats.Notes[0].ErrorMs = -16;
        Assert.IsFalse(Has(c, "steady"));
        c.Stats.Notes[0].ErrorMs = double.NaN;
        Assert.IsFalse(Has(c, "steady"));
        c.Stats.Notes[0].ErrorMs = null;
        Assert.IsFalse(Has(c, "steady"));
        Assert.IsFalse(Has(Measured(99, 0), "steady"));
        Assert.IsTrue(Has(Measured(100, 40), "steady"), "平均のずれとばらつきは別");
    }

    [Test]
    public void AllgoodNeedsTwentyNotesAndOnlyGood()
    {
        var c = Ctx(Stats(Taps(20)));
        PlayStatsTests.AllResults(c.Stats, JudgmentTier.Good);
        Gate(c, "allgood");
        foreach (var tier in new[] { JudgmentTier.Perfect, JudgmentTier.Great, JudgmentTier.Bad, JudgmentTier.Miss })
        {
            c.Stats.Notes[0].Result = tier;
            Assert.IsFalse(Has(c, "allgood"));
        }
        c = Ctx(Stats(Taps(19)));
        PlayStatsTests.AllResults(c.Stats, JudgmentTier.Good);
        Assert.IsFalse(Has(c, "allgood"));
    }

    [Test]
    public void GobugobuUsesTheResultScreenCountsAndExcludesD()
    {
        var c = Ctx(Stats(Taps(40)));
        c.Perfect = c.Great = 20;
        Gate(c, "gobugobu");
        c.Great = 21;
        Assert.IsFalse(Has(c, "gobugobu"));
        c.Perfect = c.Great = 19;
        Assert.IsFalse(Has(c, "gobugobu"));
    }

    [TestCase("evenscore", 24860, 24861)]
    [TestCase("fullhouse", 55522, 55523)]
    [TestCase("fivekeys", 35142, 35141)]
    public void DigitPatternsRequireFiveDigitsAndExcludeD(string id, int yes, int no)
    {
        var c = Ctx();
        c.Score = yes;
        Assert.IsTrue(Has(c, id));
        c.AimMode = true;
        Assert.IsFalse(Has(c, id));
        c.AimMode = false; c.Score = no;
        Assert.IsFalse(Has(c, id));
        foreach (int score in new[] { -1, 0, 8888, 100000 })
        {
            c.Score = score;
            Assert.IsFalse(Has(c, id));
        }
    }

    [Test]
    public void DoutenRequiresAnotherRunOnTheSameChartAndNamespace()
    {
        var c = Ctx();
        c.RankingAvailable = true;
        var other = new RankingPlay { id = "another", song = c.SongId, diff = "normal", ns = "", score = c.Score, st = "B" };
        c.Plays = new[] { other };
        Assert.IsTrue(Has(c, "douten"));
        other.st = "A";
        Assert.IsTrue(Has(c, "douten"), "同じ台の過去の人もよい");
        other.id = c.RunId;
        Assert.IsFalse(Has(c, "douten"));
        other.id = "another"; other.diff = "Hard";
        Assert.IsFalse(Has(c, "douten"));
        other.diff = "normal"; other.song = "else";
        Assert.IsFalse(Has(c, "douten"));
        other.song = c.SongId; other.ns = OperatorMode.FestivalNamespace;
        Assert.IsFalse(Has(c, "douten"));
        other.ns = ""; other.score++;
        Assert.IsFalse(Has(c, "douten"));
        other.score = c.Score; c.RankingAvailable = false;
        Assert.IsFalse(Has(c, "douten"));
        c.RankingAvailable = true; c.AimMode = true;
        Assert.IsFalse(Has(c, "douten"));
        c.AimMode = false; c.Score = other.score = ScoreManager.MaxScore;
        Assert.IsFalse(Has(c, "douten"));
    }

    static List<AchievementUnlock> Record(AchievementContext c) => AchievementTracker.Record(c);
    static bool Contains(List<AchievementUnlock> unlocks, string id) => unlocks.Exists(u => u.Id == id);

    static void Seed(string id, int count)
    {
        for (int i = 0; i < count; i++)
            AchievementStore.Merge("", Guid.NewGuid().ToString("N"), new[] { id }, "B", new DateTimeOffset(Noon.AddDays(-1)).ToUnixTimeMilliseconds());
    }

    [TestCase(true)]
    [TestCase(false)]
    public void NakamiCountsThreeOrdinaryAchievementsAfterUnlocking(bool enough)
    {
        var c = Ctx();
        c.PracticedBefore = true;
        c.MaxCombo = 50;
        c.OpenedSecretHere = enough;
        Assert.AreEqual(enough, Contains(Record(c), "nakami"));
        c.RunId = Guid.NewGuid().ToString("N");
        c.Score = PlayRankHelper.ScoreLowerBound(PlayRank.B);
        c.OpenedSecretHere = true;
        Assert.IsFalse(Contains(Record(c), "nakami"));
    }

    [TestCase(9, true)]
    [TestCase(8, false)]
    public void SoroibanUsesRealOrdinalsAndStartsAtTen(int previous, bool expected)
    {
        Seed("practice", previous);
        Seed("combo50", previous);
        var c = Ctx();
        c.PracticedBefore = true; c.MaxCombo = 50;
        Assert.AreEqual(expected, Contains(Record(c), "soroiban"));
    }

    static AchievementContext Medals(bool bronze, bool thirdGold)
    {
        var c = Ctx();
        c.Score = 95123; c.Hit = 10; c.Perfect = 9; c.Great = 1;
        c.MaxCombo = bronze ? 100 : 50;
        c.CutCount = thirdGold ? AchievementCatalog.PricePerPlay : 10;
        return c;
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Medal3UsesCurrentTiersAndDoesNotCountFirstever(bool bronze)
    {
        var unlocks = Record(Medals(bronze, false));
        Assert.AreEqual(bronze, Contains(unlocks, "medal3"));
        Assert.IsTrue(Contains(unlocks, "firstever"));
    }

    [TestCase(9, true)]
    [TestCase(8, false)]
    public void AchstepsUsesThreeDifferentAchievementsWithConsecutiveRealOrdinals(int previous, bool expected)
    {
        Seed("practice", previous);
        Seed("combo50", previous + 1);
        Seed("sottoji", previous + 2);
        var c = Ctx();
        c.PracticedBefore = true; c.MaxCombo = 50; c.OpenedSecretHere = true;
        Assert.AreEqual(expected, Contains(Record(c), "achsteps"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TriplegoldNeedsThreeOrdinaryGoldOrRainbowAchievements(bool third)
    {
        var c = Medals(true, third);
        var unlocks = Record(c);
        Assert.AreEqual(third, Contains(unlocks, "triplegold"));
        Assert.IsTrue(Contains(unlocks, "medal3"));
    }

    static AchievementContext SwordStyles(bool triple)
    {
        var data = new List<NoteData>();
        for (int i = 0; i < 10; i++) data.Add(N(1000 + i * 500));
        for (int i = 0; i < 10; i++) data.Add(N(7000 + i * 500, "blue"));
        for (int i = 0; i < (triple ? 3 : 2); i++) data.Add(N(14000, "gold"));
        var c = Ctx(Stats(data.ToArray()));
        c.Stats.ActionsComplete = true;
        for (int k = 0; k < c.Stats.Notes.Count; k++)
        {
            var n = c.Stats.Notes[k];
            c.Stats.AcceptCut(k, 0, 1, n.Blue ? SaberHand.Left : SaberHand.Right, n.Time, new SaberCutRecord());
        }
        return c;
    }

    [TestCase(true)]
    [TestCase(false)]
    public void KengouUsesTheThreeActuallyUnlockedStyles(bool triple)
    {
        var c = SwordStyles(triple);
        var unlocks = Record(c);
        Assert.IsTrue(Contains(unlocks, "ittoryu"));
        Assert.IsTrue(Contains(unlocks, "dual"));
        Assert.AreEqual(triple, Contains(unlocks, "triple"));
        Assert.AreEqual(triple, Contains(unlocks, "kengou"));
    }

    [Test]
    public void TalliesUseOneSnapshotAndRepeatedRecordKeepsNumbersAndThreeSaveLimit()
    {
        var c = Medals(true, true);
        int before = AchievementStore.WriteCountForTest;
        var first = Record(c);
        Assert.IsTrue(Contains(first, "medal3") && Contains(first, "triplegold"));
        Assert.LessOrEqual(AchievementStore.WriteCountForTest - before, 3);
        Assert.IsFalse(c.Unlocked.Any(u => AchievementCatalog.Batch5aTallyIds.Contains(u.Id)));
        before = AchievementStore.WriteCountForTest;
        var again = Record(c);
        CollectionAssert.AreEqual(first.Select(u => (u.Id, u.Number)), again.Select(u => (u.Id, u.Number)));
        Assert.AreEqual(before, AchievementStore.WriteCountForTest);
        CollectionAssert.AllItemsAreUnique(GameSession.FinalAchievements);
    }
}
