using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class HeatFeedbackTests
{
    GameObject root;
    SaberCutJudge judge;
    SaberInputBridge bridge;
    HeatMode mode;
    bool oldReduced, oldProjector;

    [SetUp]
    public void SetUp()
    {
        oldReduced = DisplaySettings.ReducedEffects;
        oldProjector = DisplaySettings.ProjectorMode;
        DisplaySettings.SetReducedEffectsForTest(false);
        DisplaySettings.SetProjectorModeForTest(true);
        root = new GameObject("HeatFeedbackTest");
        var tracker = root.AddComponent<SaberTracker>();
        tracker.enabled = false;
        bridge = root.AddComponent<SaberInputBridge>();
        bridge.useInputPoint = bridge.fallbackToMouse = false;
        bridge.bladeColor = Color.red;
        bridge.OverrideBlade(Vector3.down * .8f, Vector3.up * .8f);
        judge = root.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.bladeProvider = bridge;
        judge.hand = SaberHand.Right;
        judge.autonomous = false;
        judge.minCutSpeed = 1;
        judge.maxCutDistance = 5;
        judge.bladeRadius = .1f;
        judge.noteHitRadiusXY = .25f;
        mode = HeatMode.Create(root.transform,
            new ChartData { bpm = 120, heat = new HeatChartData() }, null, judge, null);
        mode.Tick(0, 0);
        Assert.NotNull(mode.View);
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
        // 切った破片はシーンの直下に残るので、ほかのテストへ持ち越さない。
        foreach (var piece in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (piece != null) Object.DestroyImmediate(piece.gameObject);
        DisplaySettings.SetReducedEffectsForTest(oldReduced);
        DisplaySettings.SetProjectorModeForTest(oldProjector);
    }

    CuttableNote Note(bool heat, Vector3 position)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.SetParent(root.transform, false);
        go.transform.position = position;
        var note = go.AddComponent<CuttableNote>();
        note.IsHeat = heat;
        note.IsJudgeable = true;
        note.RequiredHand = SaberHand.Right;
        note.RequiredCutCount = note.RemainingCuts = 8;
        note.HitTime = 0;
        mode.Observe(note);
        return note;
    }

    void Cut(CuttableNote note)
    {
        Assert.IsTrue(note.CutAtSongTime(note.transform.position, Vector3.up * 10,
            CutDirection.None, SaberHand.Right, mode.Session.Time));
    }

    void Swing(Vector3 center)
    {
        judge.bladeProvider = null;
        judge.saber.ResetTo(center - Vector3.right * 2);
        judge.saber.Tick(center, .01f);
        judge.TryCut();
        judge.saber.Tick(center + Vector3.right * 2, .01f);
        judge.TryCut();
    }

    [Test]
    public void ActualMeshShowsZeroToThreeSegmentsAndKeepsTheOriginalColors()
    {
        var note = Note(true, Vector3.right * 1.5f);
        var originalMaterial = note.GetComponent<MeshRenderer>().sharedMaterial;
        Vector3 a = bridge.WorldEndA, b = bridge.WorldEndB, position = note.transform.position;
        var filter = root.transform.Find("HeatMode/HeatVisuals").GetComponent<MeshFilter>();
        var mesh = filter.sharedMesh;
        for (int heat = 0; heat <= 3; heat++)
        {
            if (heat > 0) mode.Session.RecordCut(HeatHand.Right, 0);
            mode.DrawFeedback();
            Assert.AreEqual(heat, mode.View.LitBladeSegments(HeatHand.Right));
            Assert.AreEqual(heat, mode.View.LitGaugeSegments(HeatHand.Right));
            Assert.AreEqual(3, mode.View.GaugeSegments(HeatHand.Right));
            Assert.AreEqual(3, mode.View.GaugeSegments(HeatHand.Left));
            Assert.AreEqual(0, mode.View.LitGaugeSegments(HeatHand.Left));
            Assert.AreEqual(1, mode.View.VisibleFlameNotes);
            Assert.Greater(mesh.vertexCount, 0);
            Assert.AreSame(mesh, filter.sharedMesh);
        }
        Assert.AreEqual(Color.red, bridge.bladeColor);
        Assert.AreEqual(a, bridge.WorldEndA);
        Assert.AreEqual(b, bridge.WorldEndB);
        Assert.AreEqual(position, note.transform.position);
        Assert.AreSame(originalMaterial, note.GetComponent<MeshRenderer>().sharedMaterial);
        mode.Tick(1, 1);
        mode.DrawFeedback();
        Assert.AreEqual(2, mode.View.LitBladeSegments(HeatHand.Right));
        Assert.AreEqual(2, mode.View.LitGaugeSegments(HeatHand.Right));
        Assert.AreEqual(1, mode.View.CoolingRings);
    }

    [Test]
    public void OnlyContactSignalsRejectionAndTheSameNoteSharesOneVisualAndSoundGate()
    {
        var ordinary = Note(false, new Vector3(100, 100));
        for (int i = 0; i < 3; i++) Cut(ordinary);
        var flame = Note(true, Vector3.zero);
        int notifications = 0;
        mode.Rejected += (note, hand, point) => notifications++;
        double coolAt = mode.Session.NextCoolTime(HeatHand.Right);
        mode.DrawFeedback();
        Assert.IsFalse(judge.CanCutNote(flame));
        mode.DrawFeedback();
        Assert.AreEqual(0, notifications);
        Assert.AreEqual(0, mode.Sfx.Played(HeatCue.Reject));

        Swing(Vector3.up * 4);
        judge.hand = SaberHand.Left;
        Swing(Vector3.zero);
        Assert.AreEqual(0, notifications, "遠い振りと手違いは熱の拒否ではない");
        judge.hand = SaberHand.Right;
        Swing(Vector3.zero);
        mode.DrawFeedback();
        Assert.AreEqual(1, notifications);
        Assert.AreEqual(1, mode.View.SparkBursts);
        Assert.AreEqual(1, mode.Sfx.Played(HeatCue.Reject));
        Assert.AreEqual(0, flame.CutsAchieved);
        Assert.AreEqual(0, judge.PendingCount);
        Assert.AreEqual(Vector3.zero, flame.transform.position);
        Assert.AreEqual(coolAt, mode.Session.NextCoolTime(HeatHand.Right));

        mode.Tick(.299, .299);
        Swing(Vector3.zero);
        mode.DrawFeedback();
        Assert.AreEqual(1, mode.Sfx.Played(HeatCue.Reject));
        mode.Tick(.3, .3);
        Swing(Vector3.zero);
        mode.DrawFeedback();
        Assert.AreEqual(2, mode.Sfx.Played(HeatCue.Reject));

        flame.ResetForSpawn();
        flame.IsHeat = flame.IsJudgeable = true;
        flame.RemainingCuts = flame.RequiredCutCount;
        mode.Observe(flame);
        Swing(Vector3.zero);
        mode.DrawFeedback();
        Assert.AreEqual(3, notifications, "再利用したノーツへ前の間引きを持ち越さない");
        Assert.AreEqual(3, mode.Sfx.Played(HeatCue.Reject));
        Assert.AreEqual(1, mode.View.VisibleFlameNotes);
    }

    [Test]
    public void FullSignalsOnlyOnEntryAndEveryCoolingStepIsReportedOnce()
    {
        var ordinary = Note(false, new Vector3(100, 100));
        int full = 0;
        var levels = new List<int>();
        mode.Overheated += hand => { Assert.AreEqual(HeatHand.Right, hand); full++; };
        mode.Cooled += (hand, level) => { Assert.AreEqual(HeatHand.Right, hand); levels.Add(level); };
        for (int i = 0; i < 4; i++) Cut(ordinary);
        mode.DrawFeedback();
        Assert.AreEqual(1, full);
        Assert.AreEqual(1, mode.View.SteamBursts);
        Assert.AreEqual(1, mode.Sfx.Played(HeatCue.Full));

        mode.Tick(1, 1);
        mode.DrawFeedback();
        mode.Tick(1, 1);
        mode.DrawFeedback();
        CollectionAssert.AreEqual(new[] { 2 }, levels);
        Assert.AreEqual(1, mode.Sfx.Played(HeatCue.Cool));
        mode.Tick(3, 3);
        mode.DrawFeedback();
        CollectionAssert.AreEqual(new[] { 2, 1, 0 }, levels);
        mode.Tick(3.1, 3.1);
        mode.DrawFeedback();
        Assert.AreEqual(3, mode.Sfx.Played(HeatCue.Cool));
        Assert.AreEqual(2, mode.View.CoolingRings, "飛んだ二段は輪も短くずらして出す");
        for (int i = 0; i < 3; i++) Cut(ordinary);
        mode.DrawFeedback();
        Assert.AreEqual(2, full, "冷えてから再び上限へ入ればもう一度通知する");

        var view = mode.View;
        var sound = mode.Sfx;
        sound.Schedule(HeatCue.Cool, HeatHand.Right, 4);
        mode.EndPlay();
        mode.EndPlay();
        Assert.IsTrue(view.IsDisposed);
        Assert.AreEqual(0, sound.PendingCount);
        Assert.IsNull(judge.Heat);
        Assert.IsNull(mode.View);
        Assert.IsNull(mode.Sfx);
    }

    [Test]
    public void ReducedEffectsKeepsTheRuleVisibleAndWeakensMotionAndSound()
    {
        Note(true, Vector3.right * 1.5f);
        for (int i = 0; i < 3; i++) mode.Session.RecordCut(HeatHand.Right, 0);
        mode.View.Notify(HeatCue.Reject, HeatHand.Right, 0, Vector3.back);
        mode.View.Notify(HeatCue.Full, HeatHand.Right, 0);
        mode.View.Notify(HeatCue.Cool, HeatHand.Right, 0);
        mode.Sfx.Schedule(HeatCue.Reject, HeatHand.Right, 0);
        mode.DrawFeedback();
        int normalVertices = mode.View.VertexCount;
        float normalGain = mode.Sfx.LastGain;
        DisplaySettings.SetReducedEffectsForTest(true);
        mode.DrawFeedback();
        Assert.AreEqual(3, mode.View.LitBladeSegments(HeatHand.Right));
        Assert.AreEqual(3, mode.View.LitGaugeSegments(HeatHand.Right));
        Assert.AreEqual(1, mode.View.VisibleFlameNotes);
        Assert.AreEqual(1, mode.View.SparkBursts);
        Assert.AreEqual(1, mode.View.SteamBursts);
        Assert.AreEqual(1, mode.View.CoolingRings);
        Assert.Less(mode.View.VertexCount, normalVertices);
        Assert.AreEqual(normalGain * .3f, mode.Sfx.LastGain, .0001f);
        Assert.AreEqual(1, mode.Sfx.Played(HeatCue.Reject), "同じ時計で描き直しても鳴らし直さない");
    }

    [TestCase(HeatCue.Reject, .11f)]
    [TestCase(HeatCue.Full, .18f)]
    [TestCase(HeatCue.Cool, .22f)]
    public void PreparedSoundsAreShortFiniteAndFadeAtBothEnds(HeatCue cue, float seconds)
    {
        var samples = HeatSfx.Samples(cue);
        Assert.AreEqual(Mathf.RoundToInt(seconds * ProceduralSfx.Rate), samples.Length);
        float peak = 0;
        foreach (float sample in samples)
        {
            Assert.IsFalse(float.IsNaN(sample) || float.IsInfinity(sample));
            peak = Mathf.Max(peak, Mathf.Abs(sample));
        }
        Assert.Greater(peak, .05f);
        Assert.LessOrEqual(peak, .27f);
        Assert.AreEqual(0, samples[0], .00001f);
        Assert.AreEqual(0, samples[samples.Length - 1], .00001f);
    }
}
