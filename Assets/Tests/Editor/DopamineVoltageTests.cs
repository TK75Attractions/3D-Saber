using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 採点経路・純粋な規則・共有する光の受付を確かめる。
public class DopamineVoltageTests
{
    readonly List<GameObject> created = new List<GameObject>();
    bool savedLow;
    int savedSongs;

    [SetUp]
    public void SetUp()
    {
        savedLow = DisplaySettings.ReducedEffects;
        savedSongs = VisitorPlays.SongsPerVisitor;
        DisplaySettings.SetReducedEffectsForTest(false);
        VisitorPlays.SetSongsPerVisitorForTest(2);
        VisitorPlays.BeginVisitor();
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = created.Count - 1; i >= 0; i--)
            if (created[i] != null) Object.DestroyImmediate(created[i]);
        created.Clear();
        DisplaySettings.SetReducedEffectsForTest(savedLow);
        VisitorPlays.BeginVisitor();
        VisitorPlays.SetSongsPerVisitorForTest(savedSongs);
    }

    GameObject Make(string name)
    {
        var go = new GameObject(name);
        created.Add(go);
        return go;
    }

    [Test]
    public void EveryComboUnitProducesOneStepAndEightRaisesALevel()
    {
        var meter = new VoltageMeter();
        var steps = new List<int>();
        meter.Changed += c => steps.Add(c.StepNumber);
        meter.ObserveCombo(7, false);
        Assert.AreEqual(0, meter.Level);
        Assert.AreEqual(7, meter.Steps);
        meter.ObserveCombo(8, false);
        Assert.AreEqual(1, meter.Level);
        Assert.Zero(meter.Steps);
        Assert.AreEqual(1, meter.MaxLevelReached);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, steps);
        meter.ObserveCombo(8, false);
        Assert.AreEqual(8, steps.Count, "同じコンボの通知を二重に数えない");
    }

    [Test]
    public void LevelFourWrapsOnlyItsSteps()
    {
        var meter = new VoltageMeter(4);
        int raised = 0;
        meter.Changed += c => { if (c.Raised) raised++; };
        meter.ObserveCombo(24, false);
        Assert.AreEqual(4, meter.Level);
        Assert.Zero(meter.Steps);
        Assert.AreEqual(4, meter.MaxLevelReached);
        Assert.Zero(raised, "V4の8歩を段上がりとして再発火しない");
    }

    [TestCase(JudgmentTier.Bad)]
    [TestCase(JudgmentTier.Miss)]
    public void BadAndMissDropOneLevelAndClearSteps(JudgmentTier tier)
    {
        var score = Make("VoltageScore").AddComponent<ScoreManager>();
        var meter = new VoltageMeter(3);
        using (new VoltageScoreBinding(score, meter))
        {
            score.RegisterHit(JudgmentTier.Perfect);
            score.RegisterHit(JudgmentTier.Perfect);
            score.RegisterHit(tier);
            Assert.Zero(score.Combo);
            Assert.AreEqual(2, meter.Level);
            Assert.Zero(meter.Steps);
            Assert.AreEqual(3, meter.MaxLevelReached);
            for (int i = 0; i < 5; i++) score.RegisterHit(tier);
            Assert.Zero(meter.Level);
            Assert.Zero(meter.Steps);
        }
    }

    [Test]
    public void InitialLevelIsBoundedAndMissAtZeroCannotGoNegative()
    {
        Assert.Zero(new VoltageMeter(-5).Level);
        Assert.AreEqual(4, new VoltageMeter(20).Level);
        var meter = new VoltageMeter();
        meter.ObserveCombo(3, false);
        meter.ObserveCombo(0, true);
        meter.ObserveCombo(0, true);
        Assert.Zero(meter.Level);
        Assert.Zero(meter.Steps);
    }

    [Test]
    public void LongPartialCutsAndFinalCutUseTheExistingScoreEvents()
    {
        var score = Make("LongScore").AddComponent<ScoreManager>();
        var note = Make("LongNote").AddComponent<CuttableNote>();
        note.RequiredCutCount = note.RemainingCuts = 3;
        typeof(ScoreManager).GetMethod("HandleSpawned", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(score, new object[] { note });
        var meter = new VoltageMeter();
        using (new VoltageScoreBinding(score, meter))
        {
            note.Cut(Vector3.zero, Vector3.up * 8);
            Assert.AreEqual(1, meter.Steps);
            note.Cut(Vector3.zero, Vector3.up * 8);
            Assert.AreEqual(2, meter.Steps);
            note.Cut(Vector3.zero, Vector3.up * 8);
            Assert.AreEqual(3, meter.Steps);
            Assert.AreEqual(3, score.Combo);
        }
    }

    [Test]
    public void ObserverDoesNotChangeScoreComboJudgmentsOrMaximum()
    {
        var plain = Make("PlainScore").AddComponent<ScoreManager>();
        var observed = Make("ObservedScore").AddComponent<ScoreManager>();
        var meter = new VoltageMeter();
        using (new VoltageScoreBinding(observed, meter))
        {
            for (int i = 0; i < 40; i++)
            {
                JudgmentTier tier = i == 19 ? JudgmentTier.Bad : i == 29 ? JudgmentTier.Miss : JudgmentTier.Perfect;
                plain.RegisterHit(tier);
                observed.RegisterHit(tier);
                Assert.AreEqual(plain.Score, observed.Score);
                Assert.AreEqual(plain.ScoreWithCombo, observed.ScoreWithCombo);
                Assert.AreEqual(plain.Combo, observed.Combo);
                Assert.AreEqual(plain.MaxCombo, observed.MaxCombo);
            }
            plain.FinalizeScore(); observed.FinalizeScore();
            Assert.AreEqual(plain.Score, observed.Score);
            Assert.AreEqual(plain.JudgedNoteCount, observed.JudgedNoteCount);
            Assert.AreEqual(plain.BadCount, observed.BadCount);
            Assert.AreEqual(plain.MissCount, observed.MissCount);
        }
        observed.Reset();
        for (int i = 0; i < 32; i++) observed.RegisterHit(JudgmentTier.Perfect);
        observed.FinalizeScore();
        Assert.AreEqual(100000, observed.Score);
        Assert.AreEqual(100000, ScoreManager.MaxScore);
    }

    [Test]
    public void DisposingBindingStopsObservation()
    {
        var score = Make("DetachedScore").AddComponent<ScoreManager>();
        var meter = new VoltageMeter();
        var binding = new VoltageScoreBinding(score, meter);
        binding.Dispose(); binding.Dispose();
        score.RegisterHit(JudgmentTier.Perfect);
        Assert.Zero(meter.Steps);
    }

    [Test]
    public void MajorMinorAndMissingKeysHaveTheSpecifiedEightNotes()
    {
        var major = VoltageMusicKey.Parse("G major");
        var minor = VoltageMusicKey.Parse("D minor");
        int[] majorNotes = new int[8], minorNotes = new int[8];
        for (int i = 0; i < 8; i++)
        {
            majorNotes[i] = major.StepMidi(i + 1, 0);
            minorNotes[i] = minor.StepMidi(i + 1, 0);
            Assert.AreEqual(majorNotes[i] + 12, major.StepMidi(i + 1, 2));
        }
        CollectionAssert.AreEqual(new[] { 67, 69, 71, 74, 76, 79, 81, 83 }, majorNotes);
        CollectionAssert.AreEqual(new[] { 62, 65, 67, 69, 72, 74, 77, 79 }, minorNotes);
        CollectionAssert.AreEqual(new[] { 62, 65, 69, 74 }, minor.ChordMidi());
        foreach (string value in new[] { null, "", "unknown" })
        {
            var fallback = VoltageMusicKey.Parse(value);
            Assert.AreEqual(7, fallback.Tonic);
            Assert.False(fallback.Minor);
        }
        Assert.AreEqual(1, VoltageMusicKey.Parse("C# major").Tonic);
        Assert.AreEqual(10, VoltageMusicKey.Parse("Bb major").Tonic);
        var parsed = JsonUtility.FromJson<StagePerformanceTimeline>("{\"key\":\"D minor\"}");
        Assert.True(VoltageMusicKey.Parse(parsed.key).Minor);
        Assert.AreEqual(7, VoltageMusicKey.Parse(JsonUtility.FromJson<StagePerformanceTimeline>("{}").key).Tonic);
    }

    [TestCase("2_23_AM", 2, true)]
    [TestCase("Andalusia", 7, false)]
    [TestCase("Aobasou", 1, false)]
    [TestCase("BenkyouRemaster", 0, false)]
    [TestCase("ElDorado", 5, false)]
    [TestCase("Epilogue", 0, false)]
    [TestCase("escapade", 7, false)]
    [TestCase("logic", 0, true)]
    [TestCase("ManicSonic", 5, true)]
    [TestCase("Morning", 2, false)]
    [TestCase("Phobia PARTⅠ", 5, true)]
    [TestCase("RushE", 9, true)]
    [TestCase("揺籠", 10, false)]
    [TestCase("製作中", 2, false)]
    public void SongMetadataContainsTheRequestedKey(string song, int tonic, bool minor)
    {
        var key = VoltageMusicKey.Parse(StagePerformanceTimeline.Load(song).key);
        Assert.AreEqual(tonic, key.Tonic);
        Assert.AreEqual(minor, key.Minor);
    }

    [Test]
    public void DifficultySpecificTimelineInheritsTheCommonKey()
    {
        Assert.AreEqual("F major", StagePerformanceTimeline.Load("ElDorado", "G").key);
        Assert.AreEqual(7, VoltageMusicKey.Parse(StagePerformanceTimeline.Load("NewSong").key).Tonic);
    }

    [Test]
    public void DenseSoundsArePairedAndNeverExceedEightInAnySecond()
    {
        var paired = new VoltageSoundLimiter();
        Assert.True(paired.TryPlay(0));
        Assert.False(paired.TryPlay(.05));
        Assert.True(paired.TryPlay(.10));
        Assert.False(paired.TryPlay(.15));

        var limiter = new VoltageSoundLimiter();
        var played = new List<double>();
        for (int i = 0; i < 240; i++)
        {
            double time = i * .025;
            if (limiter.TryPlay(time, i % 8 == 7)) played.Add(time);
        }
        foreach (double start in played)
        {
            int count = 0;
            foreach (double time in played) if (time >= start && time <= start + 1) count++;
            Assert.LessOrEqual(count, 8);
        }
        var sparse = new VoltageSoundLimiter();
        for (int i = 0; i < 20; i++) Assert.True(sparse.TryPlay(i * .2));
    }

    [Test]
    public void FourthLargeFlashIsDroppedAndClockRewindDoesNotRefillTheBudget()
    {
        var budget = new LargeFlashBudget();
        Assert.True(budget.TryTake(0));
        Assert.True(budget.TryTake(.2));
        Assert.True(budget.TryTake(.4));
        Assert.False(budget.TryTake(.6));
        Assert.False(budget.TryTake(.1));
        Assert.False(budget.TryTake(1));
        Assert.AreEqual(3, budget.AcceptedCount);
        Assert.True(budget.TryTake(1.000001));
        Assert.AreEqual(4, budget.AcceptedCount);
    }

    [Test]
    public void LargeFlashesStayWithinThreeForEveryMovingWindow()
    {
        var budget = new LargeFlashBudget();
        var accepted = new List<double>();
        for (int i = 0; i < 400; i++)
        {
            double time = i * .017;
            if (budget.TryTake(time)) accepted.Add(time);
        }
        foreach (double start in accepted)
        {
            int count = 0;
            foreach (double time in accepted) if (time >= start && time <= start + 1) count++;
            Assert.LessOrEqual(count, 3);
        }
    }

    [TestCase(LargeFlashKind.Full)]
    [TestCase(LargeFlashKind.Edge)]
    public void FlashRectanglesExcludeTheGatePlusTwentyFourPixels(LargeFlashKind kind)
    {
        var gate = new FlashRect(.3, .3, .7, .7);
        var protectedGate = LargeFlashLayout.ProtectedGate(gate);
        Assert.AreEqual(24.0 / 1920, gate.Left - protectedGate.Left, 1e-12);
        Assert.AreEqual(24.0 / 1080, gate.Bottom - protectedGate.Bottom, 1e-12);
        var pieces = new List<FlashRect>();
        LargeFlashLayout.Build(kind, protectedGate, pieces);
        double area = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            Assert.Zero(FlashRect.Intersection(pieces[i], protectedGate).Area);
            area += pieces[i].Area;
            for (int j = i + 1; j < pieces.Count; j++)
                Assert.Zero(FlashRect.Intersection(pieces[i], pieces[j]).Area);
        }
        Assert.AreEqual(kind == LargeFlashKind.Full ? 1 - protectedGate.Area : 1 - .84 * .84, area, 1e-10);
        LargeFlashLayout.Build(kind, new FlashRect(-1, -1, 2, 2), pieces);
        Assert.IsEmpty(pieces);
    }

    [Test]
    public void SecondSongStartsWarmOnlyAfterAQualifyingCompletedFirstRun()
    {
        Assert.Zero(VisitorPlays.StartingVoltageLevel);
        VisitorPlays.RecordVoltagePeak("first", 3);
        Assert.Zero(VisitorPlays.StartingVoltageLevel);
        VisitorPlays.CompleteSong("first");
        Assert.AreEqual(1, VisitorPlays.StartingVoltageLevel);
        Assert.False(VisitorPlays.CompleteSong("first"));
        Assert.AreEqual(1, VisitorPlays.StartingVoltageLevel);
        VisitorPlays.CompleteSong("second");
        Assert.Zero(VisitorPlays.StartingVoltageLevel);
        VisitorPlays.BeginVisitor();
        Assert.Zero(VisitorPlays.StartingVoltageLevel);
    }

    [Test]
    public void LowPeakAbortedRunAndSingleSongModeDoNotCarry()
    {
        VisitorPlays.RecordVoltagePeak("aborted", 4);
        VisitorPlays.RecordVoltagePeak("completed", 2);
        VisitorPlays.CompleteSong("completed");
        Assert.Zero(VisitorPlays.StartingVoltageLevel);
        VisitorPlays.BeginVisitor();
        VisitorPlays.SetSongsPerVisitorForTest(1);
        VisitorPlays.RecordVoltagePeak("single", 4);
        VisitorPlays.CompleteSong("single");
        Assert.Zero(VisitorPlays.StartingVoltageLevel);
    }

    [Test]
    public void VoltageSoundsUseThePrewarmedNamesAndBakedLevels()
    {
        FlourishSfx.PrewarmGameplay();
        var key = VoltageMusicKey.Parse("D minor");
        for (int level = 0; level <= 4; level++)
            for (int step = 1; step <= 8; step++)
            {
                string name = FlourishSfx.VoltageBellName(key.StepMidi(step, level), FlourishSfx.VoltageVoice(level));
                Assert.True(FlourishSfx.TryGetCached(name, out var samples), name);
                Assert.AreEqual(-14, SfxLab.PeakDb(samples), .2);
            }
        Assert.True(FlourishSfx.TryGetCached(FlourishSfx.VoltageChordName(key), out _));
        Assert.True(FlourishSfx.TryGetCached("VoltageBreak", out var broken));
        Assert.AreEqual(-16, SfxLab.PeakDb(broken), .2);
        CollectionAssert.AreEqual(FlourishSfx.VoltageBell(74, 2), FlourishSfx.VoltageBell(74, 2));

        var bell = FlourishSfx.VoltageBell(60, 0);
        var low = SfxLab.Lp(SfxLab.Lp(bell, 150), 150);
        double allEnergy = 0, lowEnergy = 0;
        for (int i = 0; i < bell.Length; i++)
        {
            allEnergy += bell[i] * bell[i];
            lowEnergy += low[i] * low[i];
        }
        Assert.Less(lowEnergy / allEnergy, .01);
    }

    [Test]
    public void SeededFragmentsAndTrailMultiplierAreReproducible()
    {
        var a = new VoltageRandom("song", "run");
        var b = new VoltageRandom("song", "run");
        var c = new VoltageRandom("song", "other");
        bool differs = false;
        for (int i = 0; i < 30; i++)
        {
            float x = a.Next();
            Assert.AreEqual(x, b.Next());
            differs |= x != c.Next();
        }
        Assert.True(differs);
        Assert.AreEqual(SaberBladeVisual.LifetimeFor(.7f, 0) * 1.6f,
            SaberBladeVisual.LifetimeFor(.7f, 4), 1e-6);
    }

    [Test]
    public void OverlappingBackgroundPushesReturnWithoutMovingTheGate()
    {
        var stage = Make("Background").transform;
        stage.position = new Vector3(0, -1, 3);
        Vector3 original = stage.position;
        var gate = Make("UnmovedGate").transform;
        Vector3 gatePosition = gate.position;
        var drop = Make("PushController").AddComponent<ChorusDrop>();
        drop.Setup(new StagePerformanceTimeline(), null, 120, stage, null);
        drop.Tick(0, 0);
        drop.PushBackground(.15f, .08f);
        drop.Tick(.04, .04f);
        Assert.Greater(stage.position.z, original.z);
        drop.PushBackground(.14f, .12f);
        drop.Tick(.08, .04f);
        Vector3 frozen = stage.position;
        drop.Tick(.08, 1);
        Assert.AreEqual(frozen, stage.position, "同じ曲時計では実時間のdtを使わない");
        drop.Tick(.3, .22f);
        Assert.AreEqual(original, stage.position);
        Assert.AreEqual(gatePosition, gate.position);
        DisplaySettings.SetReducedEffectsForTest(true);
        drop.PushBackground(.15f, .08f);
        drop.Tick(.35, .05f);
        Assert.AreEqual(original, stage.position);
    }
}
