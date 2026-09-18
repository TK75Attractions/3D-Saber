using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class StageReactiveEffectsTests
{
    readonly List<GameObject> objects = new List<GameObject>();
    readonly List<CuttableNote> notes = new List<CuttableNote>();
    NoteSpawner spawner;
    StageReactiveEffects effects;
    FloorRenderer stage;

    [SetUp]
    public void Setup()
    {
        var root = new GameObject("StageReactionTest"); objects.Add(root);
        stage = root.AddComponent<FloorRenderer>();
        var prefab = new GameObject("ReactionNotePrefab"); objects.Add(prefab);
        prefab.AddComponent<CuttableNote>();
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.buildTimingCues = false;
        spawner.OnNoteSpawned += n => { notes.Add(n); objects.Add(n.gameObject); };
        effects = StageReactiveEffects.Create(stage, spawner, Timeline());
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var item in objects) if (item != null) Object.DestroyImmediate(item);
        objects.Clear(); notes.Clear();
    }

    static StagePerformanceTimeline Timeline() => new StagePerformanceTimeline {
        sections = new[] { new StagePerformanceTimeline.Section { startSeconds = 10, endSeconds = 20 } }
    };

    void Spawn(params NoteData[] entries)
    {
        var chart = new ChartData(); chart.notes.AddRange(entries);
        spawner.SetChart(chart); spawner.Tick(1); effects.Tick(1);
    }
    static NoteData N(float ms = 1000, string color = "blue", int count = 1) =>
        new NoteData { time = ms, type = count > 1 ? "long" : "tap", color = color, count = count, x = color == "blue" ? -1 : 1 };
    void Cut(int index) => notes[index].Cut(notes[index].transform.position, Vector3.up * 8, CutDirection.None, notes[index].RequiredHand);

    [Test]
    public void AuthoredEntranceContractsThenOpensWithoutChangingLegacyCurve()
    {
        var t = Timeline();
        Assert.AreEqual(0, t.EvaluatePresentation(7).anticipation);
        Assert.Greater(t.EvaluatePresentation(9.5).anticipation, .8f);
        Assert.Greater(t.EvaluatePresentation(9.95).hush, .7f);
        Assert.AreEqual(0, t.Evaluate(10));
        Assert.AreEqual(1, t.EvaluatePresentation(10).impact);
        Assert.AreEqual(0, t.EvaluatePresentation(11).impact);
        t.sections[0].intensity = .45f;
        Assert.AreEqual(0, t.EvaluatePresentation(10).impact, "助走区間をサビの入口と誤認しない");
        Assert.AreEqual(0, t.EvaluatePresentation(double.NaN).opening);
        t.sections[0].intensity = float.NaN;
        Assert.AreEqual(0, t.EvaluatePresentation(10).impact);
    }

    [Test]
    public void AuthoredSimultaneousCutsMergeButNearbyDifferentBeatsDoNot()
    {
        Spawn(N(), N(1000, "red")); Cut(0); Cut(1);
        Assert.AreEqual(1, effects.PairCount); Assert.AreEqual(1, effects.ActiveWaveCount);
        notes.Clear(); Spawn(N(), N(1050, "red")); Cut(0); Cut(1);
        Assert.AreEqual(0, effects.PairCount); Assert.AreEqual(2, effects.ActiveWaveCount);
    }

    [Test]
    public void SameHandOrLatePairDoesNotGetTwoHandCelebration()
    {
        Spawn(N(), N()); Cut(0); Cut(1); Assert.AreEqual(0, effects.PairCount);
        notes.Clear(); Spawn(N(), N(1000, "red")); Cut(0); effects.Tick(1.2); Cut(1);
        Assert.AreEqual(0, effects.PairCount);
    }

    [Test]
    public void LongChargesAndOnlyTrueCompletionReleases()
    {
        Spawn(N(1000, "blue", 3)); Cut(0);
        Assert.That(effects.LeftCharge, Is.EqualTo(1f / 3).Within(.001));
        Cut(0); Assert.AreEqual(0, effects.ReleaseCount);
        Cut(0); Assert.AreEqual(1, effects.ReleaseCount); Assert.AreEqual(0, effects.LeftCharge);
        notes.Clear(); Spawn(N(1000, "red", 3)); Cut(0); notes[0].MarkMiss();
        Assert.AreEqual(0, effects.ReleaseCount); Assert.AreEqual(0, effects.RightCharge);
    }

    [Test]
    public void WrongHandAndUntouchedMissDoNotEmit()
    {
        Spawn(N());
        notes[0].Cut(Vector3.zero, Vector3.up * 8, CutDirection.None, SaberHand.Right);
        Assert.AreEqual(0, effects.ActiveWaveCount);
        notes[0].MarkMiss(); Assert.AreEqual(0, effects.ActiveWaveCount);
    }

    [Test]
    public void SongClockFreezesWavesAndResetDetachesNotes()
    {
        Spawn(N(), N(1200, "red")); Cut(0); effects.Tick(1.1);
        var mesh = effects.GetComponent<MeshFilter>().sharedMesh;
        var before = mesh.vertices; effects.Tick(1.1); CollectionAssert.AreEqual(before, mesh.vertices);
        effects.Tick(2); Assert.AreEqual(0, effects.ActiveWaveCount);
        // 大きなフレーム間隔でも、生存ノーツへの購読を失わない。
        Cut(1); Assert.AreEqual(1, effects.ActiveWaveCount);
        spawner.SetChart(new ChartData()); Assert.AreEqual(0, effects.ActiveWaveCount);
        Assert.False(effects.GetComponent<MeshRenderer>().enabled);
    }

    [Test]
    public void DenseCutsRemainBoundedAndOutsideTheReadingCorridor()
    {
        var entries = new List<NoteData>();
        for (int i = 0; i < 40; i++) entries.Add(N(1000 + i * 15, i % 2 == 0 ? "blue" : "red"));
        Spawn(entries.ToArray());
        for (int i = 0; i < notes.Count; i++) Cut(i);
        Assert.LessOrEqual(effects.ActiveWaveCount, StageReactiveEffects.MaxWaves);
        effects.Tick(1.1);
        var mesh = effects.GetComponent<MeshFilter>().sharedMesh;
        Assert.That(mesh.vertexCount, Is.GreaterThan(0).And.LessThanOrEqualTo(StageReactiveEffects.VertexBudget));
        foreach (var v in mesh.vertices)
            Assert.True(v.y <= stage.floorY + .6f || Mathf.Abs(v.x) >= StageReactiveEffects.CorridorHalfWidth, v.ToString());
        effects.Tick(4); Assert.AreEqual(0, effects.ActiveWaveCount);
    }

    [TestCase(9.8)] [TestCase(10.0)] [TestCase(10.3)] [TestCase(11.0)]
    public void EntranceBeamsStayOutsideTheReadingCorridor(double time)
    {
        Spawn(N(1000, "blue", 4)); Cut(0); Cut(0); Cut(0);
        effects.Tick(time);
        foreach (var v in effects.GetComponent<MeshFilter>().sharedMesh.vertices)
            Assert.True(v.y <= stage.floorY + .6f || Mathf.Abs(v.x) >= StageReactiveEffects.CorridorHalfWidth, v.ToString());
    }

    [TestCase(StageTheme.ObsidianRelay)] [TestCase(StageTheme.VioletVault)]
    public void FloorWaveClearsTheRaisedMetalDeck(StageTheme theme)
    {
        Object.DestroyImmediate(effects.gameObject);
        stage.Build(theme);
        effects = StageReactiveEffects.Create(stage, spawner, Timeline());
        Spawn(N()); Cut(0); effects.Tick(1.12);
        int floorVertices = 0;
        foreach (var v in effects.GetComponent<MeshFilter>().sharedMesh.vertices)
            if (Mathf.Abs(v.x) < StageReactiveEffects.CorridorHalfWidth)
            {
                floorVertices++;
                Assert.That(v.y, Is.GreaterThan(stage.floorY + .4f).And.LessThan(stage.floorY + .6f));
            }
        Assert.Greater(floorVertices, 0);
    }

    [Test]
    public void DisableAndReenableKeepLiveNoteSubscriptionsAndDestroyReleasesResources()
    {
        Spawn(N(), N(1200, "red")); Cut(0);
        effects.enabled = false; Assert.AreEqual(0, effects.ActiveWaveCount);
        effects.enabled = true; Cut(1); Assert.AreEqual(1, effects.ActiveWaveCount);
        var mesh = effects.GetComponent<MeshFilter>().sharedMesh;
        var material = effects.GetComponent<MeshRenderer>().sharedMaterial;
        Object.DestroyImmediate(effects.gameObject);
        Assert.True(mesh == null); Assert.True(material == null);
    }
}
