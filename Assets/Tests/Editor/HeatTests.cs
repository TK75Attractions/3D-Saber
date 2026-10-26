using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class HeatRulesTests
{
    static HeatSession Session(HeatChartData settings = null)
        => new HeatSession(settings ?? new HeatChartData(), new ChartTempoMap(120, 0, null));

    [Test]
    public void LoaderOnlyEnablesAnExplicitRootObject()
    {
        Assert.IsNull(ChartLoader.Parse("{\"notes\":[{\"heat\":true}]}").heat);
        Assert.IsNull(ChartLoader.Parse("{\"heat\":null}").heat);
        Assert.IsNull(ChartLoader.Parse("{\"memory\":{\"captions\":[{\"text\":\"heat\"}]}}").heat);
        var chart = ChartLoader.Parse("{\"heat\":{},\"notes\":[{\"color\":\"gold\",\"heat\":true}]}");
        Assert.AreEqual(3, chart.heat.max);
        Assert.AreEqual(2f, chart.heat.coolBeats);
        Assert.AreEqual(1, chart.heat.perCut);
        Assert.IsFalse(chart.heat.blockAll);
        Assert.IsTrue(chart.notes[0].heat);
        Assert.AreEqual("gold", chart.notes[0].color);
    }

    [Test]
    public void LoaderPreservesSettingsAndNormalizesInvalidValues()
    {
        var data = ChartLoader.Parse(
            "{\"heat\":{\"max\":5,\"coolBeats\":0.5,\"perCut\":2,\"blockAll\":true},\"notes\":[{}]}");
        Assert.AreEqual(5, data.heat.max);
        Assert.AreEqual(.5f, data.heat.coolBeats);
        Assert.AreEqual(2, data.heat.perCut);
        Assert.IsTrue(data.heat.blockAll);
        Assert.IsFalse(data.notes[0].heat);
        var normalized = HeatRules.Normalize(new HeatChartData
        {
            max = -1, coolBeats = float.NaN, perCut = 0
        });
        Assert.AreEqual(3, normalized.max);
        Assert.AreEqual(2, normalized.coolBeats);
        Assert.AreEqual(1, normalized.perCut);
        Assert.AreEqual(12, HeatRules.Normalize(new HeatChartData { max = int.MaxValue }).max);
    }

    [Test]
    public void CutsHeatOnlyTheirHandAndBlockedAttemptsDoNotRestartCooling()
    {
        var s = Session();
        Assert.IsTrue(s.TryCut(HeatHand.Right, true, 0));
        Assert.IsTrue(s.TryCut(HeatHand.Right, true, .1));
        Assert.IsTrue(s.TryCut(HeatHand.Right, true, .2));
        Assert.AreEqual(3, s.HeatOf(HeatHand.Right));
        Assert.AreEqual(0, s.HeatOf(HeatHand.Left));
        Assert.IsFalse(s.CanCut(HeatHand.Right, true));
        Assert.IsTrue(s.CanCut(HeatHand.Right, false));
        Assert.IsFalse(s.TryCut(HeatHand.Right, true, .9));
        Assert.AreEqual(1.2, s.NextCoolTime(HeatHand.Right), 1e-8);
        s.Advance(1.199);
        Assert.IsFalse(s.CanCut(HeatHand.Right, true));
        s.Advance(1.2);
        Assert.AreEqual(2, s.HeatOf(HeatHand.Right));
        Assert.IsTrue(s.CanCut(HeatHand.Right, true));
        s.Advance(20);
        Assert.AreEqual(0, s.HeatOf(HeatHand.Right));
    }

    [Test]
    public void OrdinaryCutsAtTheCapRestartTheRestAndBlockAllChangesOnlyTheGate()
    {
        var s = Session(new HeatChartData { max = 3, perCut = 2 });
        s.TryCut(HeatHand.Right, true, 0);
        s.TryCut(HeatHand.Right, true, .1);
        Assert.AreEqual(3, s.HeatOf(HeatHand.Right));
        Assert.IsTrue(s.TryCut(HeatHand.Right, false, .5));
        s.Advance(1.49);
        Assert.AreEqual(3, s.HeatOf(HeatHand.Right));
        s.Advance(1.5);
        Assert.AreEqual(2, s.HeatOf(HeatHand.Right));
        var all = Session(new HeatChartData { max = 1, blockAll = true });
        all.TryCut(HeatHand.Right, false, 0);
        Assert.IsFalse(all.CanCut(HeatHand.Right, false));
        Assert.IsTrue(all.CanCut(HeatHand.Left, false));
    }

    [Test]
    public void CoolingCrossesTempoChangesAndUsesTheOffsetOnce()
    {
        var map = new ChartTempoMap(60, 0, new[]
        {
            new ChartTempoPoint { beat = 0, timeMs = 0 },
            new ChartTempoPoint { beat = 2, timeMs = 2000 },
            new ChartTempoPoint { beat = 4, timeMs = 2600 }
        });
        var s = new HeatSession(new HeatChartData(), map, .75);
        s.RecordCut(HeatHand.Right, 1.75);
        // 拍1から拍3まで休む。途中で60から200 BPMへ変わる。
        Assert.AreEqual(3.05, s.NextCoolTime(HeatHand.Right), 1e-7);
        s.Advance(3.049);
        Assert.AreEqual(1, s.HeatOf(HeatHand.Right));
        s.Advance(3.05);
        Assert.AreEqual(0, s.HeatOf(HeatHand.Right));
    }

    [Test]
    public void SameClockDoesNotCoolTwiceAndInvalidOrReversedClocksDoNotMutate()
    {
        var s = Session();
        s.RecordCut(HeatHand.Left, 0);
        s.RecordCut(HeatHand.Left, 0);
        s.Advance(1);
        s.Advance(1);
        Assert.AreEqual(1, s.HeatOf(HeatHand.Left));
        Assert.IsFalse(s.RecordCut(HeatHand.Left, .5));
        Assert.IsFalse(s.Advance(double.NaN));
        Assert.IsFalse(s.Advance(double.PositiveInfinity));
        Assert.AreEqual(1, s.HeatOf(HeatHand.Left));
        var copy = s.Clone();
        copy.RecordCut(HeatHand.Right, 1);
        Assert.AreEqual(0, s.HeatOf(HeatHand.Right));
    }

    [Test]
    public void AuditFindsAChoiceThatTrapsTheFollowingFixedHand()
    {
        var s = Session(new HeatChartData { max = 1 });
        var cuts = new[]
        {
            new HeatCut(0, HeatHand.Any, true, 0),
            new HeatCut(.1, HeatHand.Right, true, 1)
        };
        var trace = HeatForecast.Simulate(s, cuts);
        Assert.AreEqual(HeatHand.Right, trace[0].Hand);
        Assert.IsFalse(trace[1].Accepted);
        Assert.IsFalse(HeatForecast.FixedHandsBelowMax(trace));
        var audit = HeatForecast.Audit(s, cuts);
        Assert.IsTrue(audit.Complete);
        Assert.AreEqual(true, audit.HasPlayablePath);
        Assert.AreEqual(false, audit.AllChoicesPlayable);
        Assert.AreEqual(false, audit.FixedHandsSafe);
        Assert.AreEqual(1, audit.FirstProblemSource);
    }

    [Test]
    public void AuditDistinguishesSafeImpossibleAndUnfinished()
    {
        var s = Session(new HeatChartData { max = 1 });
        var safe = new[]
        {
            new HeatCut(0, HeatHand.Any, true),
            new HeatCut(1, HeatHand.Right, true)
        };
        var result = HeatForecast.Audit(s, safe);
        Assert.AreEqual(true, result.HasPlayablePath);
        Assert.AreEqual(true, result.AllChoicesPlayable);
        Assert.AreEqual(true, result.FixedHandsSafe);
        var impossible = new[]
        {
            new HeatCut(0, HeatHand.Any, true),
            new HeatCut(.1, HeatHand.Any, true),
            new HeatCut(.2, HeatHand.Any, true)
        };
        Assert.AreEqual(false, HeatForecast.Audit(s, impossible).HasPlayablePath);
        result = HeatForecast.Audit(s, safe, 1);
        Assert.IsFalse(result.Complete);
        Assert.IsNull(result.HasPlayablePath);
        Assert.IsNull(result.FixedHandsSafe);
    }

    [Test]
    public void AuditIncludesBothOrdersOfSimultaneousNotes()
    {
        var cuts = new[]
        {
            new HeatCut(0, HeatHand.Any, true),
            new HeatCut(0, HeatHand.Right, true)
        };
        var result = HeatForecast.Audit(Session(new HeatChartData { max = 1 }), cuts);
        Assert.IsTrue(result.Complete);
        Assert.AreEqual(true, result.HasPlayablePath);
        Assert.AreEqual(false, result.AllChoicesPlayable);
        Assert.AreEqual(false, result.FixedHandsSafe);
    }

    [Test]
    public void FixedHandCheckAlsoWarnsWhenOrdinaryNotesAreNotBlocked()
    {
        var cuts = new[]
        {
            new HeatCut(0, HeatHand.Right, false),
            new HeatCut(.1, HeatHand.Right, false)
        };
        var result = HeatForecast.Audit(Session(new HeatChartData { max = 1 }), cuts);
        Assert.AreEqual(true, result.HasPlayablePath);
        Assert.AreEqual(false, result.FixedHandsSafe);
    }

    [Test]
    public void LongForecastRequiresEveryCutTimeAndAddsOffsetsOnce()
    {
        var chart = new ChartData
        {
            bpm = 120, offsetMs = 250, heat = new HeatChartData(),
            notes = new List<NoteData>
            {
                new NoteData { time = 1000, count = 3, color = "gold", heat = true }
            }
        };
        Assert.Throws<ArgumentException>(() => HeatForecast.FromChart(chart));
        var cuts = HeatForecast.FromChart(chart, .5, (note, cut) => 1 + cut * .1);
        Assert.AreEqual(3, cuts.Count);
        Assert.AreEqual(1.75, cuts[0].Time, 1e-8);
        Assert.AreEqual(1.95, cuts[2].Time, 1e-8);
        Assert.AreEqual(2, cuts[2].CutIndex);
        Assert.AreEqual(HeatHand.Any, cuts[0].RequiredHand);
    }
}

public class HeatModeTests
{
    GameObject root;

    [TearDown]
    public void Cleanup()
    {
        if (root != null) UnityEngine.Object.DestroyImmediate(root);
    }

    SaberCutJudge Judge()
    {
        root = new GameObject("HeatTest");
        var tracker = root.AddComponent<SaberTracker>();
        tracker.enabled = false;
        var judge = root.AddComponent<SaberCutJudge>();
        judge.autonomous = false;
        judge.saber = tracker;
        judge.hand = SaberHand.Right;
        judge.minCutSpeed = 1;
        judge.maxCutDistance = 5;
        judge.bladeRadius = .2f;
        judge.noteHitRadiusXY = .3f;
        return judge;
    }

    CuttableNote Note(bool heat)
    {
        var go = new GameObject("HeatTestNote");
        go.transform.SetParent(root.transform);
        var note = go.AddComponent<CuttableNote>();
        note.RequiredCutCount = note.RemainingCuts = 8;
        note.RequiredHand = SaberHand.Right;
        note.IsJudgeable = true;
        note.IsHeat = heat;
        return note;
    }

    [Test]
    public void AcceptedLongCutsHeatAndTheActualJudgeRejectsThenRecovers()
    {
        var judge = Judge();
        var note = Note(true);
        var mode = HeatMode.Create(root.transform,
            new ChartData { bpm = 120, heat = new HeatChartData { max = 2 } },
            null, judge, null, false);
        mode.Observe(note);
        mode.Tick(.2, .2);
        note.CutAtSongTime(Vector3.zero, Vector3.right * 10, CutDirection.None, SaberHand.Right, .2);
        note.CutAtSongTime(Vector3.zero, Vector3.right * 10, CutDirection.None, SaberHand.Right, .2);
        Assert.AreEqual(2, mode.Session.HeatOf(HeatHand.Right));
        Assert.IsFalse(judge.CanCutNote(note));

        judge.saber.ResetTo(Vector3.left * 2);
        judge.saber.Tick(Vector3.zero, .1f);
        judge.TryCut();
        judge.saber.Tick(Vector3.right * 2, .1f);
        judge.TryCut();
        Assert.AreEqual(2, note.CutsAchieved);
        Assert.AreEqual(0, judge.PendingCount);

        mode.Tick(1.2, 1.2);
        Assert.IsTrue(judge.CanCutNote(note));
        judge.saber.ResetTo(Vector3.left * 2);
        judge.saber.Tick(Vector3.zero, .1f);
        judge.TryCut();
        judge.saber.Tick(Vector3.right * 2, .1f);
        judge.TryCut();
        Assert.AreEqual(3, note.CutsAchieved);
        Assert.AreEqual(2, mode.Session.HeatOf(HeatHand.Right));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BlockAllIsReflectedWithoutChangingStunAndEndPlayRemovesTheGate(bool blockAll)
    {
        var judge = Judge();
        var note = Note(false);
        var mode = HeatMode.Create(root.transform,
            new ChartData { bpm = 120, heat = new HeatChartData { max = 1, blockAll = blockAll } },
            null, judge, null, false);
        mode.Observe(note);
        mode.Tick(0, 0);
        note.CutAtSongTime(Vector3.zero, Vector3.right, CutDirection.None, SaberHand.Right, 0);
        Assert.AreEqual(!blockAll, judge.CanCutNote(note));
        Assert.IsFalse(judge.Stunned);
        judge.Stunned = true;
        mode.EndPlay();
        Assert.IsNull(judge.Heat);
        Assert.IsTrue(judge.Stunned);
        judge.Stunned = false;
        Assert.IsTrue(judge.CanCutNote(note));
    }

    [Test]
    public void TimeoutDoesNotHeatAndPoolReuseClearsTheMarkAndSubscriptions()
    {
        var judge = Judge();
        var note = Note(true);
        var mode = HeatMode.Create(root.transform,
            new ChartData { bpm = 120, heat = new HeatChartData() },
            null, judge, null, false);
        mode.Observe(note);
        mode.Tick(0, 0);
        note.CutAtSongTime(Vector3.zero, Vector3.right, CutDirection.None, SaberHand.Right, 0);
        note.MarkMiss();
        Assert.AreEqual(1, mode.Session.HeatOf(HeatHand.Right));
        note.ResetForSpawn();
        Assert.IsFalse(note.IsHeat);
        note.RemainingCuts = note.RequiredCutCount;
        note.IsJudgeable = true;
        note.CutAtSongTime(Vector3.zero, Vector3.right, CutDirection.None, SaberHand.Right, 0);
        Assert.AreEqual(1, mode.Session.HeatOf(HeatHand.Right));
    }

    [Test]
    public void SpawnerCopiesTheMarkAndAutomaticallyConnectsTheMode()
    {
        var judge = Judge();
        var spawner = root.AddComponent<NoteSpawner>();
        spawner.buildTimingCues = false;
        spawner.simultaneousGuideEnabled = false;
        spawner.noteRoot = root.transform;
        var prefab = new GameObject("HeatPrefab");
        prefab.transform.SetParent(root.transform);
        prefab.AddComponent<CuttableNote>();
        prefab.SetActive(false);
        spawner.notePrefab = prefab;
        var chart = new ChartData
        {
            bpm = 120, heat = new HeatChartData(),
            notes = new List<NoteData>
            {
                new NoteData { time = 1000, count = 3, color = "gold", heat = true }
            }
        };
        spawner.SetChart(chart);
        var mode = HeatMode.Create(root.transform, chart, spawner, judge, null, false);
        spawner.Tick(.9);
        var note = spawner.LiveNotes[0];
        Assert.IsTrue(note.IsHeat);
        Assert.AreEqual(SaberHand.Any, note.RequiredHand);
        mode.Tick(1, 1);
        note.CutAtSongTime(Vector3.zero, Vector3.right, CutDirection.None, SaberHand.Right, 1);
        Assert.AreEqual(1, mode.Session.HeatOf(HeatHand.Right));
        spawner.SetChart(new ChartData());
        Assert.IsNull(judge.Heat);
    }

    [Test]
    public void MissingSettingsDoNotCreateAMode()
    {
        var judge = Judge();
        Assert.IsNull(HeatMode.Create(root.transform, new ChartData(), null, judge, null, false));
        Assert.IsNull(judge.Heat);
    }
}
