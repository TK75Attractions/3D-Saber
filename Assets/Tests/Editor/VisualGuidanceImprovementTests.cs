using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// シーク・保持中・再利用時の、表示と曲時計の食い違いを実オブジェクトで再現する。
public class VisualGuidanceImprovementTests
{
    readonly List<GameObject> objects = new List<GameObject>();
    GameObject Go(string name)
    {
        var go = new GameObject(name); objects.Add(go); return go;
    }
    [SetUp] public void Setup()
    {
        DisplaySettings.SetProjectorModeForTest(false);
        DisplaySettings.SetReducedEffectsForTest(false);
    }
    [TearDown] public void Cleanup()
    {
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            var go = objects[i]; if (go == null) continue;
            foreach (var component in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component is BarLineSpawner || component is NoteTimingCue || component is ChorusDrop || component is SimultaneousNoteLink)
                    component.GetType().GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(component, null);
            }
            Object.DestroyImmediate(go);
        }
        objects.Clear();
        DisplaySettings.SetProjectorModeForTest(false);
        DisplaySettings.SetReducedEffectsForTest(false);
    }
    BarLineSpawner Bars(float tailMs = 0)
    {
        var sp = Go("Bars").AddComponent<BarLineSpawner>();
        sp.root = sp.transform; sp.approachTime = 1;
        var prefab = Go("BarPrefab");
        var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
        line.name = "Line"; line.transform.SetParent(prefab.transform);
        sp.barLinePrefab = prefab; sp.overrideVisual = true;
        var chart = new ChartData { bpm = 120 };
        chart.notes.Add(new NoteData { time = 1000, count = tailMs > 0 ? 40 : 1, lengthMs = tailMs });
        sp.SetChart(chart); return sp;
    }
    [Test] public void BarLinesContinueThroughAnAuthoredLongTail()
    {
        var sp = Bars(20000); sp.Tick(19);
        Assert.Greater(sp.AliveCount, 0);
    }
    [Test] public void RewindRecreatesTheCurrentMeasureInsteadOfWaitingForTheOldTime()
    {
        var sp = Bars(20000); sp.Tick(18); sp.Tick(0);
        Assert.AreEqual(1, sp.NextIndex); Assert.AreEqual(1, sp.AliveCount);
    }
    [Test] public void ConsecutiveMeasuresReuseObjects()
    {
        var sp = Bars(20000);
        for (int t = 0; t < 24; t++) sp.Tick(t);
        Assert.LessOrEqual(sp.CreatedCount, 3);
    }
    [Test] public void PrefabAccentMaterialIsReusedAndOriginalIsUntouched()
    {
        var sp = Bars(20000); sp.overrideVisual = false; sp.accentEvery = 1;
        Material original = sp.barLinePrefab.GetComponentInChildren<MeshRenderer>().sharedMaterial;
        sp.Tick(0); var accent = sp.GetComponentInChildren<MeshRenderer>().sharedMaterial;
        sp.Tick(4); Assert.AreSame(accent, sp.GetComponentInChildren<MeshRenderer>().sharedMaterial);
        Assert.AreNotSame(original, accent);
        Assert.AreSame(original, sp.barLinePrefab.GetComponentInChildren<MeshRenderer>().sharedMaterial);
    }
    [Test] public void BarOpacityFadesWithoutMutatingSharedStyle()
    {
        var sp = Bars(20000); sp.Tick(1);
        var renderers = sp.GetComponentsInChildren<MeshRenderer>();
        var block = new MaterialPropertyBlock();
        var arriving = Array.Find(renderers, r => r.transform.parent.position.z > 10);
        Assert.NotNull(arriving); arriving.GetPropertyBlock(block);
        Assert.AreEqual(0, block.GetColor("_BaseColor").a, .0001f);
        Assert.AreEqual(sp.lineAlpha, arriving.sharedMaterial.GetColor("_BaseColor").a, .0001f);
        sp.Tick(1.2); arriving.GetPropertyBlock(block);
        Assert.Greater(block.GetColor("_BaseColor").a, 0);
    }
    [Test] public void PooledBarCanReturnToItsOriginalPrefabStyle()
    {
        var sp = Bars(20000); sp.Tick(0); sp.Tick(1.6);
        sp.overrideVisual = false; sp.SetChart(new ChartData { bpm = 120 }); sp.Tick(0);
        var renderer = sp.GetComponentInChildren<MeshRenderer>();
        var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
        Assert.IsTrue(block.isEmpty);
        Assert.AreEqual(sp.barLinePrefab.transform.Find("Line").localScale, renderer.transform.localScale);
    }

    NoteSpawner Floor(int cuts = 1, string direction = "none")
    {
        var sp = Go("FloorOwner").AddComponent<NoteSpawner>();
        var prefab = Go("NotePrefab"); prefab.AddComponent<CuttableNote>();
        sp.notePrefab = prefab; sp.noteRoot = sp.transform; sp.buildTimingCues = false;
        sp.ConfigureFloorGuide(-2.5f);
        var chart = new ChartData();
        chart.notes.Add(new NoteData { time = 1000, x = 1, count = cuts, lengthMs = cuts > 1 ? 10000 : 0, direction = direction });
        sp.SetChart(chart); sp.Tick(.8); return sp;
    }
    [Test] public void LongOnsetUsesADoubleMarkInsteadOfATapMark()
    {
        var tap = Floor(); var hold = Floor(44);
        Assert.Greater(hold.FloorGuide.GetComponent<MeshFilter>().sharedMesh.vertexCount,
            tap.FloorGuide.GetComponent<MeshFilter>().sharedMesh.vertexCount);
    }
    [Test] public void LongTimeGuidePersistsUntilTheAuthoredEnd()
    {
        var sp = Floor(44); sp.Tick(4);
        Assert.AreEqual(1, sp.FloorGuide.LongProgressCount);
        var before = sp.FloorGuide.GetComponent<MeshFilter>().sharedMesh.vertices;
        sp.FloorGuide.Tick(sp, 8);
        var after = sp.FloorGuide.GetComponent<MeshFilter>().sharedMesh.vertices;
        Assert.Less(after[13].x - after[12].x, before[13].x - before[12].x);
        sp.FloorGuide.Tick(sp, 11.1); Assert.AreEqual(0, sp.FloorGuide.LongProgressCount);
    }
    [Test] public void RemainingCutProgressChangesWithoutChangingTheClock()
    {
        var sp = Floor(44); sp.Tick(4);
        var mesh = sp.FloorGuide.GetComponent<MeshFilter>().sharedMesh;
        int full = mesh.vertexCount;
        sp.LiveNotes[0].RemainingCuts = 5; sp.FloorGuide.Tick(sp, 4);
        Assert.Less(mesh.vertexCount, full);
        Assert.AreEqual(1, sp.FloorGuide.LongProgressCount);
    }
    [Test] public void CrowdedGuidesPrioritizeNotesNearestTheJudgmentTime()
    {
        var sp = Floor();
        // List名に依存せず公開リストを通して追加する。
        var live = sp.LiveNotes as IList<CuttableNote>;
        Assert.NotNull(live);
        for (int i = 0; i < 260; i++)
        {
            var n = Go("DenseGuide" + i).AddComponent<CuttableNote>();
            n.HitTime = 1.7; n.transform.position = new Vector3(-1, 0, 15); live.Add(n);
        }
        var urgent = Go("UrgentGuide").AddComponent<CuttableNote>();
        urgent.HitTime = 1; urgent.transform.position = new Vector3(2, 0, 0); live.Add(urgent);
        sp.FloorGuide.Tick(sp, 1);
        var vertices = sp.FloorGuide.GetComponent<MeshFilter>().sharedMesh.vertices;
        Assert.IsTrue(Array.Exists(vertices, v => v.x > 2.5f && v.z < 1 && v.z > -.2f));
        Assert.AreEqual(256, sp.FloorGuide.MarkerCount);
    }
    [Test] public void InactiveNotesLeaveNoPhantomFloorMarkers()
    {
        var sp = Floor(); sp.LiveNotes[0].gameObject.SetActive(false); sp.FloorGuide.Tick(sp, .8);
        Assert.AreEqual(0, sp.FloorGuide.MarkerCount);
    }
    [Test] public void CrowdedGuidesKeepTheCurrentlyHeldLongVisible()
    {
        var sp = Floor(44); sp.Tick(4);
        var live = sp.LiveNotes as IList<CuttableNote>;
        for (int i = 0; i < 260; i++)
        {
            var n = Go("Upcoming" + i).AddComponent<CuttableNote>();
            n.HitTime = 4.5; n.transform.position = new Vector3(-1, 0, 10); live.Add(n);
        }
        sp.FloorGuide.Tick(sp, 4);
        Assert.AreEqual(1, sp.FloorGuide.LongProgressCount);
        Assert.AreEqual(255, sp.FloorGuide.MarkerCount);
    }
    [Test] public void ProjectionModeBroadensTheFixedJudgmentAnchor()
    {
        var sp = Floor(); var mesh = sp.FloorGuide.GetComponent<MeshFilter>().sharedMesh;
        float normal = mesh.vertices[2].z - mesh.vertices[0].z;
        DisplaySettings.SetProjectorModeForTest(true); sp.FloorGuide.Tick(sp, .8);
        Assert.Greater(mesh.vertices[2].z - mesh.vertices[0].z, normal);
    }

    SimultaneousNoteLink Link()
    {
        var a = Go("Left").AddComponent<CuttableNote>(); a.transform.position = Vector3.left * 2;
        var b = Go("Right").AddComponent<CuttableNote>(); b.transform.position = Vector3.right * 2;
        var link = SimultaneousNoteLink.Create(a, b, null); objects.Add(link.gameObject); return link;
    }
    [Test] public void SimultaneousLinkUsesABuildIncludedVertexColorShader()
    {
        var link = Link(); Assert.AreEqual("Saber/Note Guide", link.Line.sharedMaterial.shader.name);
        Assert.AreEqual(1, link.Line.sharedMaterial.GetColor("_BaseColor").a);
    }
    [Test] public void LinkOnlyJoinsTheGapBetweenNoteFaces()
    {
        var link = Link(); Assert.Greater(link.Line.GetPosition(0).x, -2);
        Assert.Less(link.Line.GetPosition(1).x, 2);
        Assert.Greater(link.Line.GetPosition(1).x, link.Line.GetPosition(0).x);
    }
    [Test] public void RetiredLinkIsHiddenBeforeDeferredDestruction()
    {
        var link = Link(); link.noteA.gameObject.SetActive(false);
        Assert.IsFalse(link.Refresh()); Assert.IsFalse(link.Line.enabled);
    }

    NoteTimingCue Cue()
    {
        var go = Go("Cue"); go.AddComponent<CuttableNote>(); var cue = go.AddComponent<NoteTimingCue>();
        cue.Initialize(go.GetComponent<CuttableNote>(), 0); return cue;
    }
    [Test] public void PooledCueCanTurnItsOptionalGhostAndRingOffAndOn()
    {
        var cue = Cue(); cue.buildGhost = false; cue.buildRing = true; cue.Initialize(null, 0);
        Assert.IsFalse(cue.GhostRoot.activeSelf); Assert.NotNull(cue.RingRoot);
        cue.buildGhost = true; cue.buildRing = false; cue.Initialize(null, 0);
        Assert.IsTrue(cue.GhostRoot.activeSelf); Assert.IsFalse(cue.RingRoot.gameObject.activeSelf);
    }
    [Test] public void ProjectionCueStyleDoesNotPermanentlyMutateUserSettings()
    {
        var cue = Cue(); float thickness = cue.ghostThickness, alpha = cue.ghostMaxAlpha;
        DisplaySettings.SetProjectorModeForTest(true); cue.Initialize(null, 0);
        DisplaySettings.SetProjectorModeForTest(false); cue.Initialize(null, 0);
        Assert.AreEqual(thickness, cue.ghostThickness); Assert.AreEqual(alpha, cue.ghostMaxAlpha);
    }
    [Test] public void CueEndFadeUsesTheChartClockAndFreezesOnPause()
    {
        var cue = Cue(); cue.Tick(0, 1, .2f, .2f);
        typeof(CuttableNote).GetProperty("IsCut").SetValue(cue.GetComponent<CuttableNote>(), true);
        cue.Tick(-.1, 1, .2f, .2f);
        var mat = cue.GhostRoot.GetComponentInChildren<MeshRenderer>().sharedMaterial;
        float alpha = mat.GetColor("_BaseColor").a;
        cue.Tick(-.1, 1, .2f, .2f); Assert.AreEqual(alpha, mat.GetColor("_BaseColor").a);
        cue.Tick(-.4, 1, .2f, .2f); Assert.IsFalse(cue.GhostRoot.activeSelf);
    }
    [Test] public void EditingNotePositionMovesBothLandingFrames()
    {
        var cue = Cue(); cue.transform.position = new Vector3(2, -1, 5); cue.Tick(.3, 1, .2f, .2f);
        Assert.AreEqual(2, cue.GhostRoot.transform.position.x);
        Assert.AreEqual(-1, cue.ApproachRoot.transform.position.y);
        Assert.AreEqual(cue.ghostZBias, cue.GhostRoot.transform.position.z);
    }

    ChorusDrop Drop(out AudioSource song, out Transform stage)
    {
        song = Go("Song").AddComponent<AudioSource>(); song.volume = .8f;
        stage = Go("Stage").transform;
        Go("JudgeGate").AddComponent<JudgeGateFrame>().Build(new Vector3(4, 2, 1));
        var drop = Go("Drop").AddComponent<ChorusDrop>(); drop.Setup(Timeline(), song, 120, stage, null); return drop;
    }
    static StagePerformanceTimeline Timeline() => new StagePerformanceTimeline { sections = new[] {
        new StagePerformanceTimeline.Section { startSeconds = 10, endSeconds = 20 },
        new StagePerformanceTimeline.Section { startSeconds = 30, endSeconds = 40 } } };
    [Test] public void ForwardSeekDoesNotPlayOldChorusEntriesOnePerFrame()
    {
        var drop = Drop(out _, out _); drop.Tick(9.9, .02f); drop.Tick(35, .02f); drop.Tick(35.02, .02f);
        Assert.AreEqual(0, drop.FiredCount); Assert.IsFalse(drop.IsDucking);
    }
    [Test] public void PausingFreezesTheChorusRingAndStageKick()
    {
        var drop = Drop(out _, out var stage); drop.Tick(9.98, .02f); drop.Tick(10, .02f);
        float age = drop.RingAge; Vector3 position = stage.position;
        drop.Tick(10, .1f); Assert.AreEqual(age, drop.RingAge); Assert.AreEqual(position, stage.position);
    }
    [Test] public void RebindingRestoresOldSongVolumeAndReplacesItsRing()
    {
        var drop = Drop(out var song, out var stage); drop.Tick(9.7, .02f);
        Assert.Less(song.volume, .8f);
        var next = Go("NextSong").AddComponent<AudioSource>(); next.volume = .6f;
        drop.Setup(Timeline(), next, 120, stage, null);
        Assert.AreEqual(.8f, song.volume, .0001f);
        int rings = 0;
        foreach (var child in GameObject.Find("JudgeGate").GetComponentsInChildren<Transform>(true))
            if (child.name == "ChorusDropRing") rings++;
        Assert.AreEqual(1, rings);
    }
    [Test] public void SwitchingToLowCancelsAlreadyRunningMotionImmediately()
    {
        var drop = Drop(out _, out var stage); Vector3 original = stage.position;
        drop.Tick(9.98, .02f); drop.Tick(10.02, .04f); Assert.Greater(drop.KickOffset, 0);
        DisplaySettings.SetReducedEffectsForTest(true); drop.Tick(10.02, .04f);
        Assert.AreEqual(original, stage.position); Assert.Less(drop.RingAge, 0);
    }
    [Test] public void FeedbackEvictionDoesNotBorrowTheOldPerfectTier()
    {
        var owner = Go("Feedback").AddComponent<NoteSpawner>(); var feedback = GameplayCutFeedback.Create(owner);
        for (int i = 0; i < GameplayCutFeedback.MaxBursts; i++) Judge(feedback, i * 2, JudgmentTier.Perfect);
        feedback.Tick(.04f); Judge(feedback, 40, JudgmentTier.Good);
        var bursts = (Array)typeof(GameplayCutFeedback).GetField("bursts", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(feedback);
        object replacement = bursts.GetValue(0);
        Assert.AreEqual(JudgmentTier.Good, replacement.GetType().GetField("tier").GetValue(replacement));
    }
    void Judge(GameplayCutFeedback feedback, float x, JudgmentTier tier)
    {
        var n = Go("Judged" + x).AddComponent<CuttableNote>(); n.transform.position = new Vector3(x, 0, 0);
        feedback.Track(n); typeof(CuttableNote).GetProperty("IsCut").SetValue(n, true);
        typeof(CuttableNote).GetMethod("NotifyJudgment", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(n, new object[] { tier, n.transform.position, Vector3.right * 5 });
    }
    [Test] public void LongReleaseAddsASongSynchronizedLightCue()
    {
        var chart = new ChartData { offsetMs = -110 };
        chart.notes.Add(new NoteData { time = 1000, count = 44, lengthMs = 20000 });
        var cues = new StageLightCues(chart); Assert.AreEqual(2, cues.Count);
        Assert.Greater(cues.Evaluate(20.89), .99f); Assert.AreEqual(0, cues.Evaluate(10));
    }
    [Test] public void EclipseSelectionIsIndependentOfEqualStartSectionOrder()
    {
        var shortSection = new StagePerformanceTimeline.Section { startSeconds = 10, endSeconds = 20 };
        var longSection = new StagePerformanceTimeline.Section { startSeconds = 10, endSeconds = 40 };
        var a = new StagePerformanceTimeline { sections = new[] { shortSection, longSection } };
        var b = new StagePerformanceTimeline { sections = new[] { longSection, shortSection } };
        Assert.AreEqual(a.EvaluateEclipse(25), b.EvaluateEclipse(25), .0001f);
        Assert.Greater(a.EvaluateEclipse(25), .99f);
    }
}
