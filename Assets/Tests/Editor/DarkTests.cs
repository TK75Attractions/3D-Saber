using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class DarkRulesTests
{
    static BladeSample Blade(float x, float y)
        => new BladeSample(new Vector2(x, y), new Vector2(x + .4f, y));

    [Test]
    public void DistanceUsesTheWholeSegmentAndItsEndpoints()
    {
        var a = new Vector2(-5, 0);
        var b = new Vector2(5, 0);
        Assert.AreEqual(1f, DarkRules.Distance(new Vector2(4, 1), a, b), 1e-6);
        Assert.AreEqual(2f, DarkRules.Distance(new Vector2(7, 0), a, b), 1e-6);
        Assert.AreEqual(5f, DarkRules.Distance(new Vector2(3, 4), Vector2.zero, Vector2.zero), 1e-6);
    }

    [Test]
    public void LampHasAFullCoreSmoothEdgeAndNonzeroFloor()
    {
        var blade = new BladeSample(new Vector2(-5, 0), new Vector2(5, 0));
        var far = Blade(30, 30);
        float At(float y) => DarkRules.Brightness(1, SaberHand.Right, new Vector2(4, y), blade, far);
        Assert.AreEqual(1, At(DarkRules.FullRadius), 1e-6);
        Assert.AreEqual((1 + DarkRules.NoteMinimum) / 2,
            At((DarkRules.FullRadius + DarkRules.FadeRadius) / 2), 1e-5);
        Assert.AreEqual(DarkRules.NoteMinimum, At(DarkRules.FadeRadius), 1e-6);
        Assert.AreEqual(DarkRules.NoteMinimum, At(100), 1e-6);
        Assert.AreEqual(At(DarkRules.FullRadius - .0001f), At(DarkRules.FullRadius + .0001f), 1e-5);
        Assert.AreEqual(1, DarkRules.Brightness(0, SaberHand.Right, Vector2.zero, far, far), 1e-6);
        Assert.AreEqual(.575f, DarkRules.Brightness(.5f, SaberHand.Right, Vector2.zero, far, far), 1e-6);
    }

    [Test]
    public void LostHandIsRescuedAndEitherLampCanIlluminateGold()
    {
        var far = Blade(30, 30);
        var near = Blade(0, 0);
        Assert.AreEqual(1, DarkRules.Brightness(1, SaberHand.Right, Vector2.zero, default, far));
        Assert.AreEqual(DarkRules.NoteMinimum,
            DarkRules.Brightness(1, SaberHand.Left, Vector2.zero, default, far), 1e-6);
        Assert.AreEqual(1, DarkRules.Brightness(1, SaberHand.Left, Vector2.zero, far, default));
        Assert.AreEqual(1, DarkRules.Brightness(1, SaberHand.Any, Vector2.zero, near, far));
        Assert.AreEqual(1, DarkRules.Brightness(1, SaberHand.Any, Vector2.zero, far, near));
        Assert.AreEqual(1, DarkRules.Brightness(1, SaberHand.Any, Vector2.zero, default, default));
        Assert.AreEqual(1, DarkRules.Brightness(1, SaberHand.Right, Vector2.zero,
            new BladeSample(new Vector2(float.NaN, 0), Vector2.zero), far));
    }

    [Test]
    public void JsonTableInterpolatesAndAnOverlappingRampStartsAtTheCurrentLevel()
    {
        var chart = ChartLoader.Parse("{\"bpm\":90,\"notes\":[],\"dark\":["
            + "{\"time\":3000,\"level\":0,\"ramp\":2000},"
            + "{\"time\":1000,\"level\":1,\"ramp\":4000}]}");
        Assert.AreEqual(2, chart.dark.Count);
        var map = DarkTimeline.Build(chart.dark);
        Assert.NotNull(map);
        Assert.AreEqual(0, map.Evaluate(.999), 1e-6);
        Assert.AreEqual(.5f, map.Evaluate(3), 1e-6);
        Assert.AreEqual(.25f, map.Evaluate(4), 1e-6);
        Assert.AreEqual(0, map.Evaluate(5), 1e-6);
        Assert.AreEqual(.5f, map.Evaluate(3), 1e-6, "巻き戻しても同じ");
        Assert.AreEqual(0, map.Evaluate(double.NaN));
        Assert.IsNull(DarkTimeline.Build(ChartLoader.Parse("{\"notes\":[]}").dark));
    }

    [Test]
    public void InvalidRowsDuplicatesAndShortRampsAreSafe()
    {
        var map = DarkTimeline.Build(new List<DarkPointData> {
            null,
            new DarkPointData { time = float.NaN, level = 1 },
            new DarkPointData { time = 1000, level = .2f, ramp = 0 },
            new DarkPointData { time = 1000, level = 2, ramp = -1 }
        });
        Assert.AreEqual(1, map.Count);
        Assert.AreEqual(0, map.Evaluate(1), 1e-6);
        Assert.AreEqual(.5f, map.Evaluate(1.5), 1e-6);
        Assert.AreEqual(1, map.Evaluate(2), 1e-6);
    }

    [Test]
    public void StageShadersDefaultToTheirOriginalBrightness()
    {
        foreach (string path in new[] {
            "Stage/ScenicSurface", "Stage/ScenicMotes", "Stage/ObsidianMetal",
            "Effects/NoteGuide", "Effects/GameplayCutAccent" })
        {
            var shader = Resources.Load<Shader>(path);
            Assert.NotNull(shader, path);
            var material = new Material(shader);
            try
            {
                Assert.IsTrue(material.HasProperty("_DarkBrightness"), path);
                Assert.AreEqual(1, material.GetFloat("_DarkBrightness"), 1e-6, path);
            }
            finally { Object.DestroyImmediate(material); }
        }
    }
}

public class DarkNoteVisualTests
{
    GameObject root;
    NoteSpawner spawner;
    float brightness;

    [SetUp]
    public void SetUp()
    {
        root = new GameObject("DarkNoteTests");
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.transform.SetParent(root.transform, false);
        prefab.AddComponent<CuttableNote>();
        prefab.SetActive(false);
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.simultaneousGuideEnabled = false;
        brightness = 1;
        spawner.BrightnessFor = _ => brightness;
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
    }

    [Test]
    public void BrightnessChangesBodyArrowAndFrameWithoutChangingAlphaOrJudgment()
    {
        var chart = new ChartData { bpm = 90 };
        chart.notes.Add(new NoteData { time = 2000, color = "red", direction = "left", type = "direction" });
        spawner.SetChart(chart);
        spawner.Tick(1.85);
        var note = spawner.LiveNotes[0];
        var body = note.BodyRenderer.sharedMaterial;
        var frame = note.TimingCue.GhostRoot.GetComponentInChildren<MeshRenderer>().sharedMaterial;
        var arrow = note.Arrow.transform.Find("Bars").GetComponent<MeshRenderer>().sharedMaterial;
        Color bodyBefore = body.GetColor("_BaseColor");
        Color frameBefore = frame.GetColor("_BaseColor");
        Color arrowBefore = arrow.GetColor("_BaseColor");
        Vector3 position = note.transform.position;
        bool judgeable = note.IsJudgeable;
        brightness = DarkRules.NoteMinimum;
        spawner.Tick(1.85);
        Assert.AreEqual(bodyBefore.r * brightness, body.GetColor("_BaseColor").r, 1e-5);
        Assert.AreEqual(frameBefore.r * brightness, frame.GetColor("_BaseColor").r, 1e-5);
        Assert.AreEqual(arrowBefore.r * brightness, arrow.GetColor("_BaseColor").r, 1e-5);
        Assert.AreEqual(bodyBefore.a, body.GetColor("_BaseColor").a, 1e-6);
        Assert.AreEqual(frameBefore.a, frame.GetColor("_BaseColor").a, 1e-6);
        Assert.AreEqual(position, note.transform.position);
        Assert.AreEqual(judgeable, note.IsJudgeable);
        Assert.AreEqual(2, note.HitTime, 1e-9);
        brightness = 1;
        spawner.Tick(1.85);
        Assert.AreEqual(bodyBefore.r, body.GetColor("_BaseColor").r, 1e-5);
        Assert.AreEqual(frameBefore.r, frame.GetColor("_BaseColor").r, 1e-5);
    }

    [Test]
    public void VeilAndReuseDoNotRetainDarkness()
    {
        var chart = new ChartData();
        chart.notes.Add(new NoteData { time = 2000, color = "blue", veil = .4f });
        spawner.SetChart(chart);
        spawner.Tick(1.8);
        var note = spawner.LiveNotes[0];
        float alpha = note.BodyRenderer.sharedMaterial.GetColor("_BaseColor").a;
        note.SetDarkBrightness(.15f);
        Assert.AreEqual(.4f, note.Visuals.Veil, 1e-6);
        Assert.AreEqual(alpha, note.BodyRenderer.sharedMaterial.GetColor("_BaseColor").a, 1e-6);
        note.ResetForSpawn();
        note.Visuals.ResetForReuse();
        Assert.AreEqual(1, note.Brightness);
        Assert.AreEqual(1, note.Visuals.Brightness);
        Assert.AreEqual(0, note.Visuals.Veil);
    }
}

public class DarkRefreshTests
{
    GameObject root;
    NoteSpawner spawner;
    BarLineSpawner bars;
    DarkMode mode;
    Material barMaterial;
    Transform barRoot;
    const double TimeAt = 1.06;

    [SetUp]
    public void SetUp()
    {
        root = new GameObject("DarkRefreshTests");
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.transform.SetParent(root.transform, false);
        prefab.SetActive(false);
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.simultaneousGuideEnabled = false;
        spawner.ConfigureFloorGuide(-2.5f);

        var barPrefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        barPrefab.transform.SetParent(root.transform, false);
        barMaterial = new Material(Resources.Load<Shader>("Stage/ObsidianMetal"));
        barMaterial.SetColor("_BaseColor", new Color(.6f, .5f, .4f, .7f));
        barMaterial.SetColor("_EmissionColor", new Color(.2f, .3f, .4f, 1f));
        barPrefab.GetComponent<MeshRenderer>().sharedMaterial = barMaterial;
        barPrefab.SetActive(false);
        barRoot = new GameObject("Bars").transform;
        barRoot.SetParent(root.transform, false);
        bars = root.AddComponent<BarLineSpawner>();
        bars.barLinePrefab = barPrefab;
        bars.root = barRoot;
        bars.DarkBrightness = 1f;

        var chart = new ChartData { bpm = 120 };
        chart.dark.Add(new DarkPointData { time = 0, level = 1, ramp = 1000 });
        chart.notes.Add(new NoteData { time = 500, color = "blue", x = -.7f, count = 3, type = "long" });
        chart.notes.Add(new NoteData { time = 2000, color = "red", x = .7f, direction = "left", veil = .35f });
        spawner.SetChart(chart);
        bars.SetChart(chart);
        mode = DarkMode.Create(root.transform, chart, spawner, null, null, null, bars);
        mode.BladeOverride = _ => new BladeSample(new Vector2(30, 30), new Vector2(31, 30));
    }

    [TearDown]
    public void TearDown()
    {
        if (mode != null) mode.EndPlay();
        if (root != null) Object.DestroyImmediate(root);
        if (barMaterial != null) Object.DestroyImmediate(barMaterial);
    }

    [Test]
    public void PlayingRefreshesAfterMovementAndPauseOnlyChangesAppearance()
    {
        spawner.Tick(TimeAt);
        var note = spawner.LiveNotes.Last();
        var mesh = spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh;
        var bright = mesh.colors;
        int calls = 0;
        spawner.BrightnessFor = n => { calls++; return mode.BrightnessFor(n); };

        mode.Tick(TimeAt, refreshNotes: false);
        Assert.AreEqual(0, calls);
        Assert.AreEqual(1f, note.Brightness);
        CollectionAssert.AreEqual(bright, mesh.colors, "再生中は DarkMode が床を描き直さない");

        spawner.Tick(TimeAt);
        Assert.AreEqual(spawner.AliveCount, calls, "位置更新後に各ノーツを一度だけ更新する");
        Assert.AreEqual(DarkRules.NoteMinimum, note.Brightness, 1e-6);
        var dark = mesh.colors;
        Assert.AreNotEqual(bright[bright.Length - 1], dark[dark.Length - 1]);
        Vector3 position = note.transform.position;
        var vertices = mesh.vertices;
        var triangles = mesh.triangles;
        bool judgeable = note.IsJudgeable;
        int next = spawner.NextIndex, alive = spawner.AliveCount;
        int misses = 0;
        spawner.OnNoteMissed += _ => misses++;

        // 時計を進めず手を見失わせ、停止中にも救済と床の色が反映されることを確かめる。
        mode.BladeOverride = _ => default(BladeSample);
        mode.Tick(TimeAt);
        Assert.AreEqual(1f, note.Brightness);
        Assert.AreEqual(bright[bright.Length - 1], mesh.colors[bright.Length - 1]);
        Assert.AreEqual(position, note.transform.position);
        Assert.AreEqual(judgeable, note.IsJudgeable);
        Assert.IsFalse(note.IsMissed);
        Assert.AreEqual(next, spawner.NextIndex);
        Assert.AreEqual(alive, spawner.AliveCount);
        Assert.AreEqual(0, misses);
        CollectionAssert.AreEqual(vertices, mesh.vertices);
        CollectionAssert.AreEqual(triangles, mesh.triangles);
        spawner.BrightnessFor = null;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EndPlayRestoresMeshAndPropertyBlocksWithoutAdvancing(bool overrideVisual)
    {
        bars.overrideVisual = overrideVisual;
        spawner.Tick(TimeAt);
        bars.Tick(TimeAt);
        var mesh = spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh;
        var bright = mesh.colors;
        var renderers = barRoot.GetComponentsInChildren<MeshRenderer>();
        Assert.IsNotEmpty(renderers);
        var barColors = renderers.Select(r => ReadColor(r, "_BaseColor")).ToArray();
        var emissions = renderers.Select(r => ReadColor(r, "_EmissionColor")).ToArray();
        if (overrideVisual)
            Assert.IsTrue(renderers.Where((r, i) => barColors[i].a > 0
                && barColors[i].a < r.sharedMaterial.GetColor("_BaseColor").a).Any(),
                "出現中のフェードも復帰後に保存する");

        mode.Tick(TimeAt, refreshNotes: false);
        spawner.Tick(TimeAt);
        bars.Tick(TimeAt);
        Assert.Less(mesh.colors[0].r, bright[0].r);
        for (int i = 0; i < renderers.Length; i++)
            AssertColor(DarkRules.Rgb(barColors[i], DarkRules.StageMinimum),
                ReadColor(renderers[i], "_BaseColor"));

        var vertices = mesh.vertices;
        var triangles = mesh.triangles;
        var notes = spawner.LiveNotes.ToArray();
        var positions = notes.Select(n => n.transform.position).ToArray();
        var judgeable = notes.Select(n => n.IsJudgeable).ToArray();
        var barPositions = renderers.Select(r => r.transform.position).ToArray();
        int nextNote = spawner.NextIndex, nextBar = bars.NextIndex;
        int liveBars = bars.AliveCount, createdBars = bars.CreatedCount, pooledBars = bars.PooledCount;
        int markers = spawner.FloorGuide.MarkerCount, progress = spawner.FloorGuide.LongProgressCount;
        int misses = 0;
        spawner.OnNoteMissed += _ => misses++;

        // EndPlay のあとに Tick を呼ばず、実際に送られている色を読む。
        mode.EndPlay();
        mode.EndPlay();
        Assert.IsNull(spawner.BrightnessFor);
        CollectionAssert.AreEqual(bright, mesh.colors);
        CollectionAssert.AreEqual(vertices, mesh.vertices);
        CollectionAssert.AreEqual(triangles, mesh.triangles);
        CollectionAssert.AreEqual(positions, notes.Select(n => n.transform.position).ToArray());
        CollectionAssert.AreEqual(judgeable, notes.Select(n => n.IsJudgeable).ToArray());
        CollectionAssert.AreEqual(barPositions, renderers.Select(r => r.transform.position).ToArray());
        Assert.IsTrue(notes.All(n => n.Brightness == 1f && !n.IsMissed));
        Assert.AreEqual(nextNote, spawner.NextIndex);
        Assert.AreEqual(notes.Length, spawner.AliveCount);
        Assert.AreEqual(nextBar, bars.NextIndex);
        Assert.AreEqual(liveBars, bars.AliveCount);
        Assert.AreEqual(createdBars, bars.CreatedCount);
        Assert.AreEqual(pooledBars, bars.PooledCount);
        Assert.AreEqual(markers, spawner.FloorGuide.MarkerCount);
        Assert.AreEqual(progress, spawner.FloorGuide.LongProgressCount);
        Assert.AreEqual(0, misses);
        for (int i = 0; i < renderers.Length; i++)
        {
            AssertColor(barColors[i], ReadColor(renderers[i], "_BaseColor"));
            if (renderers[i].sharedMaterial.HasProperty("_EmissionColor"))
                AssertColor(emissions[i], ReadColor(renderers[i], "_EmissionColor"));
        }

        spawner.FloorGuide.Clear();
        spawner.FloorGuide.RestoreDarkBrightness();
        Assert.AreEqual(0, mesh.vertexCount, "消したガイドの色を復活させない");
        Assert.IsEmpty(mesh.colors);
    }

    static Color ReadColor(Renderer renderer, string property)
    {
        var block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block);
        return block.GetColor(property);
    }

    static void AssertColor(Color expected, Color actual)
    {
        Assert.AreEqual(expected.r, actual.r, 1e-5f);
        Assert.AreEqual(expected.g, actual.g, 1e-5f);
        Assert.AreEqual(expected.b, actual.b, 1e-5f);
        Assert.AreEqual(expected.a, actual.a, 1e-6f);
    }
}

public class TwoTwentyThreeDarkChartTests
{
    static ChartData Read(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "2_23_AM", "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path), "gen_dark.py --write で生成してください: " + path);
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    [Test]
    public void NotesSwitchAtTheFirstChorusAndKeepTheirTiming()
    {
        var light = Read("y");
        Assert.AreEqual(10, light.displayLevel);
        MixedSecretChartAssertions.Check("2_23_AM", light, "y", 65359.532958984375, 88, 322);
        Assert.AreEqual(142, light.notes.Count(n => n.IsDirection));
        Assert.AreEqual(20, light.notes.Count(n => n.IsLong));
        Assert.AreEqual(75, light.notes.Count(n => n.color == "gold"));
        Assert.IsTrue(light.notes.All(n => n.vanish == "" && n.veil == 0 && !n.HasWind));
    }

    [Test]
    public void SectionsFollowTheBarsAndEndInDaylight()
    {
        var chart = Read("y");
        int[] bars = { 0, 4, 8, 16, 24, 32, 36, 38, 40, 48, 56, 64, 68 };
        float[] levels = { 0, .30f, .60f, .72f, .96f, .08f, .46f, .82f, .68f, 1, .76f, .38f, 0 };
        int[] ramps = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 3 };
        Assert.AreEqual(bars.Length, chart.dark.Count);
        double bar = 240000.0 / chart.bpm;
        for (int i = 0; i < bars.Length; i++)
        {
            var point = chart.dark[i];
            Assert.AreEqual(chart.beatZeroMs + bars[i] * bar, point.time, .03);
            Assert.AreEqual(levels[i], point.level, 1e-6);
            Assert.AreEqual(ramps[i] * bar, point.ramp, .03);
        }
        var map = DarkTimeline.Build(chart.dark);
        Assert.AreEqual(1, map.Evaluate(134), 1e-6);
        Assert.AreEqual(0, map.Evaluate(191), 1e-6);
    }

    [Test]
    public void YUsesTheSharedSecretTableAndWarmLampAccent()
    {
        var secret = RushESecretState.For("2_23_AM");
        Assert.AreSame(RushESecretState.TwoTwentyThreeLight, secret);
        Assert.AreEqual("Y", secret.Difficulty);
        Assert.AreSame(secret, RushESecretState.ForDifficulty(" y "));
        Assert.AreEqual(RushESecretState.Lamp, GameHUDSkin.DifficultyColor("Y"));
        Assert.AreEqual(RushESecretState.Lamp, GameStartCountdown.AccentForDifficulty("y"));
        var state = new RushESecretState();
        state.Select("2_23_AM");
        for (int i = 0; i < 10; i++) Assert.IsTrue(state.TryHit(i * .2));
        Assert.IsTrue(state.Reveal());
        Assert.IsTrue(state.Finish());
        Assert.AreSame(secret, state.Current);
        Assert.IsTrue(state.Unlocked);
    }
}
