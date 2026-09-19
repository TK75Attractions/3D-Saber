using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class StageReactiveEffectsTests
{
    readonly List<GameObject> objects = new List<GameObject>();
    readonly List<CuttableNote> notes = new List<CuttableNote>();
    NoteSpawner spawner;
    StageReactiveEffects effects;
    FloorRenderer stage;
    ScoreManager score;
    SongPlayer clock;

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
        clock = root.AddComponent<SongPlayer>();
        score = root.AddComponent<ScoreManager>(); score.songPlayer = clock; score.Bind(spawner);
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
    void Cut(int index, double error = 0)
    {
        typeof(SongPlayer).GetField("scheduled", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(clock, true);
        typeof(SongPlayer).GetField("clockSynchronized", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(clock, true);
        typeof(SongPlayer).GetField("startDspTime", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(clock, AudioSettings.dspTime - notes[index].HitTime - error);
        notes[index].Cut(notes[index].transform.position, Vector3.up * 8, CutDirection.None, notes[index].RequiredHand);
    }

    [Test]
    public void RapidPerfectsWeaveOnlyTheirActualLaneAndEachStitchExpiresIndependently()
    {
        Spawn(N(1000), N(1140), N(1280));
        Cut(0); Assert.AreEqual(0, effects.ActiveStitchCount);
        effects.Tick(1.14); Cut(1);
        Assert.AreEqual(1, effects.ActiveStitchCount);
        Assert.AreEqual(1 << StageReactiveEffects.FloorLaneForX(-1), effects.ActiveWeaveLaneMask);
        effects.Tick(1.28); Cut(2); Assert.AreEqual(2, effects.ActiveStitchCount);
        effects.Tick(1.64); Assert.AreEqual(1, effects.ActiveStitchCount, "次の成功で古い節を延命しない");
        effects.Tick(1.79); Assert.AreEqual(0, effects.ActiveStitchCount);
    }

    [TestCase(false)] [TestCase(true)]
    public void WeaveCannotSkipAnUnresolvedOrMissedPredecessor(bool miss)
    {
        Spawn(N(1000), N(1080), N(1160));
        Cut(0);
        if (miss) notes[1].MarkMiss();
        Cut(2);
        Assert.AreEqual(0, effects.ActiveStitchCount);
        if (!miss) { Cut(1); Assert.AreEqual(0, effects.ActiveStitchCount, "後から確定しても過去へ連結しない"); }
    }

    [Test]
    public void GreatSimultaneousAndLongNotesBreakTheWeave()
    {
        Spawn(N(1000), N(1100), N(1200));
        Cut(0); Cut(1, .10); Cut(2); Assert.AreEqual(0, effects.ActiveStitchCount);
        notes.Clear(); Spawn(N(1000), N(1000), N(1120));
        Cut(0); Cut(1); Cut(2); Assert.AreEqual(0, effects.ActiveStitchCount);
        notes.Clear(); Spawn(N(1000), N(1100, "blue", 2), N(1200));
        Cut(0); Cut(1); Cut(1); Cut(2); Assert.AreEqual(0, effects.ActiveStitchCount);
        notes.Clear(); Spawn(N(1000), N(1230));
        Cut(0); Cut(1); Assert.AreEqual(0, effects.ActiveStitchCount);
    }

    [Test]
    public void OtherLaneDoesNotBreakContinuityAndWeaveIsBoundedAndClearedOnRewind()
    {
        Spawn(N(1000), N(1060, "red"), N(1120));
        Cut(0); Cut(1); Cut(2); Assert.AreEqual(1, effects.ActiveStitchCount);
        effects.Tick(.5); Assert.AreEqual(0, effects.ActiveStitchCount);
        notes.Clear();
        var entries = new NoteData[40];
        for (int i = 0; i < entries.Length; i++) entries[i] = N(1000 + i * 20);
        Spawn(entries);
        for (int i = 0; i < entries.Length; i++) Cut(i);
        Assert.AreEqual(StageReactiveEffects.StitchesPerLane, effects.ActiveStitchCount);
        effects.Tick(1.1);
        Assert.LessOrEqual(effects.GetComponent<MeshFilter>().sharedMesh.vertexCount, StageReactiveEffects.VertexBudget);
        effects.enabled = false; Assert.AreEqual(0, effects.ActiveStitchCount);
        effects.enabled = true; spawner.SetChart(new ChartData());
        Assert.AreEqual(0, effects.ActiveStitchCount);
    }

    [Test]
    public void ReenableStartsFreshWeaveWithAlreadySpawnedNotes()
    {
        Spawn(N(1000), N(1100), N(1200)); Cut(0);
        effects.enabled = false; effects.enabled = true;
        Cut(1); Assert.AreEqual(0, effects.ActiveStitchCount);
        Cut(2); Assert.AreEqual(1, effects.ActiveStitchCount);
    }

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
    public void LongStaysDarkUntilPerfectCompletionAndTimeoutNeverReleases()
    {
        Spawn(N(1000, "blue", 3)); Cut(0);
        Assert.AreEqual(0, effects.ActiveWaveCount);
        Assert.AreEqual(0, effects.GetComponentInChildren<StageThemeResponse>().ActiveResponseCount);
        Cut(0); Assert.AreEqual(0, effects.ReleaseCount); Assert.AreEqual(0, effects.ActiveWaveCount);
        Assert.AreEqual(0, effects.GetComponentInChildren<StageThemeResponse>().ActiveResponseCount);
        Cut(0); Assert.AreEqual(1, effects.ReleaseCount); Assert.AreEqual(JudgmentTier.Perfect, score.LastTier);
        Assert.AreEqual(1, effects.GetComponentInChildren<StageThemeResponse>().ActiveResponseCount);
        notes.Clear(); Spawn(N(1000, "red", 3)); Cut(0); notes[0].MarkMiss();
        Assert.AreEqual(0, effects.ReleaseCount); Assert.AreEqual(0, effects.ActiveWaveCount);
        Assert.AreEqual(0, effects.GetComponentInChildren<StageThemeResponse>().ActiveResponseCount);
    }

    [TestCase(0, JudgmentTier.Perfect, 1)]
    [TestCase(.10, JudgmentTier.Great, 0)]
    [TestCase(.15, JudgmentTier.Good, 0)]
    [TestCase(.19, JudgmentTier.Bad, 0)]
    [TestCase(.40, JudgmentTier.Miss, 0)]
    [TestCase(-.055, JudgmentTier.Great, 0)]
    [TestCase(-.075, JudgmentTier.Good, 0)]
    [TestCase(-.095, JudgmentTier.Bad, 0)]
    [TestCase(-.15, JudgmentTier.Miss, 0)]
    public void OnlyFinalPerfectJudgmentLightsStageAndCutSparks(double error, JudgmentTier tier, int count)
    {
        Spawn(N()); Cut(0, error); effects.Tick(1.1);
        Assert.AreEqual(tier, score.LastTier);
        Assert.AreEqual(count, effects.ActiveWaveCount);
        Assert.AreEqual(count, spawner.GetComponentInChildren<GameplayCutFeedback>().ActiveCount);
        Assert.AreEqual(count, effects.GetComponentInChildren<StageThemeResponse>().ActiveResponseCount,
            "Obsidianのラッチも最終Perfectと同じ結果を返す");
        if (count == 0) Assert.AreEqual(0, effects.GetComponent<MeshFilter>().sharedMesh.vertexCount);
    }

    [TestCase(1)] [TestCase(3)]
    public void DirectionDowngradeBlocksBothAccentsEvenAfterLongCompletion(int cuts)
    {
        var n = N(1000, "blue", cuts); n.direction = "right";
        Spawn(n); for (int i = 0; i < cuts; i++) Cut(0);
        Assert.AreEqual(JudgmentTier.Great, score.LastTier);
        Assert.AreEqual(0, effects.ActiveWaveCount); Assert.AreEqual(0, effects.ReleaseCount);
        Assert.AreEqual(0, spawner.GetComponentInChildren<GameplayCutFeedback>().ActiveCount);
        Assert.AreEqual(0, effects.GetComponentInChildren<StageThemeResponse>().ActiveResponseCount);
    }

    [TestCase(0, 0)] [TestCase(1, 0)] [TestCase(2, 1)] [TestCase(3, 1)]
    [TestCase(4, 2)] [TestCase(5, 2)] [TestCase(6, 3)] [TestCase(7, 3)]
    public void EveryTwoNoteColumnsLightExactlyOneFloorLane(int column, int lane)
    {
        var n = N(1000, column < 4 ? "red" : "blue");
        n.x = Mathf.Lerp(-2.5f, 2.5f, column / 7f);
        Spawn(n); Cut(0); effects.Tick(1.12);
        Assert.AreEqual(1 << lane, effects.ActiveFloorLaneMask, "担当手と配置の左右が逆でも配置に従う");
        Assert.AreEqual(1 << lane, effects.GetComponentInChildren<StageThemeResponse>().ActiveLaneMask);
        int floorVertices = 0;
        foreach (var v in effects.GetComponent<MeshFilter>().sharedMesh.vertices)
            if (v.y <= stage.floorY + .6f && Mathf.Abs(v.x) < 5.8f)
            {
                floorVertices++;
                Assert.That(v.x, Is.InRange(-6f + lane * 3, -3f + lane * 3));
            }
        Assert.Greater(floorVertices, 0);
    }

    [Test]
    public void CoordinatesRespectScaleAndGoldHandDoesNotMoveTheLane()
    {
        var n = N(1000, "gold"); n.x = -1;
        var chart = new ChartData { coordScale = 2 }; chart.notes.Add(n);
        spawner.SetChart(chart); spawner.Tick(1); effects.Tick(1);
        notes[0].RequiredHand = SaberHand.Right; Cut(0);
        Assert.AreEqual(1, effects.ActiveFloorLaneMask);
        Assert.AreEqual(-1, StageReactiveEffects.FloorLaneForX(float.NaN));
        Assert.AreEqual(0, StageReactiveEffects.FloorLaneForX(-100));
        Assert.AreEqual(3, StageReactiveEffects.FloorLaneForX(100));
    }

    [Test]
    public void PairNeedsTwoPerfectsAndKeepsBothActualPositions()
    {
        var a = N(); a.x = -2.5f; var b = N(1000, "red"); b.x = -.8f;
        Spawn(a, b); Cut(0); Cut(1);
        Assert.AreEqual(1, effects.PairCount); Assert.AreEqual(3, effects.ActiveFloorLaneMask);
        notes.Clear(); Spawn(a, b); Cut(0); Cut(1, .15);
        Assert.AreEqual(0, effects.PairCount); Assert.AreEqual(1, effects.ActiveWaveCount);
        Assert.AreEqual(1, effects.ActiveFloorLaneMask);
    }

    [Test]
    public void UnjudgedCutDoesNotUseAnEarlierPerfectAndChorusStillOpens()
    {
        Spawn(N()); score.RegisterHit(JudgmentTier.Perfect);
        score.Bind(null); // 既に生まれたノーツへの既存購読は残るので、新しい譜面で確認する。
        notes.Clear(); Spawn(N()); Cut(0);
        Assert.AreEqual(0, effects.ActiveWaveCount);
        Assert.AreEqual(0, spawner.GetComponentInChildren<GameplayCutFeedback>().ActiveCount);
        effects.Tick(10); Assert.AreEqual(1, effects.Presentation.impact);
        Assert.Greater(effects.GetComponent<MeshFilter>().sharedMesh.vertexCount, 0);
    }

    [Test]
    public void WrongHandAndUntouchedMissDoNotEmit()
    {
        Spawn(N());
        notes[0].Cut(Vector3.zero, Vector3.up * 8, CutDirection.None, SaberHand.Right);
        Assert.AreEqual(0, effects.ActiveWaveCount);
        notes[0].MarkMiss(); Assert.AreEqual(0, effects.ActiveWaveCount);
        Assert.AreEqual(0, effects.GetComponentInChildren<StageThemeResponse>().ActiveResponseCount);
    }

    [TestCase(StageTheme.ObsidianRelay, true)] [TestCase(StageTheme.VioletVault, false)]
    [TestCase(StageTheme.AbyssalRuins, true)]
    public void ThemeResponseReplacesOnlyTheSupportedThemeSideWaveAndKeepsTheCommonFloor(StageTheme theme, bool replaced)
    {
        Object.DestroyImmediate(effects.gameObject);
        stage.Build(theme);
        effects = StageReactiveEffects.Create(stage, spawner, Timeline());
        Spawn(N()); Cut(0); effects.Tick(1.12);
        var response = effects.GetComponentInChildren<StageThemeResponse>();
        Assert.AreEqual(replaced, response != null && response.ReplacesSideResponse);
        Assert.AreEqual(2, effects.ActiveFloorLaneMask);
        int floorVertices = 0, sideVertices = 0;
        foreach (var point in effects.GetComponent<MeshFilter>().sharedMesh.vertices)
        {
            if (point.y < stage.floorY + .7f) floorVertices++;
            else sideVertices++;
        }
        Assert.Greater(floorVertices, 0, "専用演出の有無によらず共通床の反応を維持する");
        if (replaced) Assert.AreEqual(0, sideVertices, "素材反応へ置換した側面に旧成功波を重ねない");
        else Assert.Greater(sideVertices, 0, "対象外背景の既存側面波を弱めない");
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
