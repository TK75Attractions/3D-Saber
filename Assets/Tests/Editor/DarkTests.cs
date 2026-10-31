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
        // 暗さは見た目の割合。中間の外れ具合 0.5 は見た目 50% → ノーツは線形で 0.5 の3乗。
        Assert.AreEqual(DarkRules.Linear(.5f, DarkRules.NoteGamma),
            At((DarkRules.FullRadius + DarkRules.FadeRadius) / 2), 1e-5);
        Assert.AreEqual(DarkRules.NoteMinimum, At(DarkRules.FadeRadius), 1e-6);
        Assert.AreEqual(DarkRules.NoteMinimum, At(100), 1e-6);
        Assert.AreEqual(At(DarkRules.FullRadius - .0001f), At(DarkRules.FullRadius + .0001f), 1e-5);
        Assert.AreEqual(1, DarkRules.Brightness(0, SaberHand.Right, Vector2.zero, far, far), 1e-6);
        Assert.AreEqual(.125f, DarkRules.Brightness(.5f, SaberHand.Right, Vector2.zero, far, far), 1e-6);
        // 下限は0にしない(着地の枠が比で掛け直す)が、強い発光(本体の6倍)を掛けても見た目5%未満。
        Assert.Greater(DarkRules.NoteMinimum, 0f);
        Assert.Less(DarkRules.NoteMinimum * 6f, DarkRules.Linear(.05f, DarkRules.PerceptualGamma));
    }

    [Test]
    public void DarknessIsAPerceivedFractionNotALinearOne()
    {
        // 2026-10-27「本来暗くなるところが…明るすぎて普通に見えてる」: 旧来の線形 14% は見た目で約4割だった。
        Assert.Greater(Mathf.Pow(DarkRules.StageBrightness(1), 1f / DarkRules.PerceptualGamma), .4f);
        Assert.AreEqual(0, DarkRules.VeilDarkness(0), 1e-6);
        Assert.AreEqual(1, DarkRules.VeilDarkness(1), 1e-6);
        Assert.Greater(DarkRules.VeilDarkness(.96f), .999f, "1回目のサビ(0.96)の外側は線形で 0.1% 未満");
        float previous = -1f;
        for (int i = 0; i <= 20; i++)
        {
            float level = i / 20f, darkness = DarkRules.VeilDarkness(level);
            Assert.Greater(darkness, previous);
            Assert.AreEqual(1f - Mathf.Pow(1f - level, DarkRules.PerceptualGamma), darkness, 1e-5);
            previous = darkness;
        }
        Assert.AreEqual(1, DarkRules.HudBrightness(0), 1e-6);
        Assert.AreEqual(Mathf.Pow(DarkRules.HudMinimum, DarkRules.PerceptualGamma), DarkRules.HudBrightness(1), 1e-6);
        Assert.Less(DarkRules.HudBrightness(1), .01f, "HUD も最暗では見た目 10%");
        Assert.LessOrEqual(DarkRules.Brightness(.96f, SaberHand.Right, new Vector2(4, 4),
            Blade(-4, -4), Blade(-4, -4)), DarkRules.NoteMinimum + 1e-6f);
    }

    [Test]
    public void TheLightNoLongerCoversTheWholeField()
    {
        // Y のノーツは横 ±2.5・縦 -0.375〜0.75。旧 1.8/2.65 では、休めの位置(x=±1.25)に立てた左右の剣だけで全部が明るかった。
        var right = new BladeSample(new Vector2(1.25f, -.6f), new Vector2(1.25f, .6f));
        var far = Blade(30, 30);
        Assert.AreEqual(1, DarkRules.Brightness(1, SaberHand.Right, new Vector2(1.25f, 0), right, far), 1e-6);
        // 右手の光は、左半分のいちばん内側の列(x=-0.9)まで届かない。
        Assert.Less(DarkRules.FadeRadius, 1.25f + .9f);
        Assert.AreEqual(DarkRules.NoteMinimum,
            DarkRules.Brightness(1, SaberHand.Any, new Vector2(-.9f, 0), right, far), 1e-6);
        // 同じ側でも外側の列(x=2.5)はほとんど暗い。
        Assert.Less(DarkRules.Brightness(1, SaberHand.Right, new Vector2(2.5f, 0), right, far), .001f);
        Assert.Less(DarkRules.FullRadius, DarkRules.FadeRadius);
    }

    [Test]
    public void VeilOpensOnlyAroundTheBladesThatAreAvailable()
    {
        var red = new BladeSample(new Vector2(1, -.5f), new Vector2(1, .5f));
        var blue = new BladeSample(new Vector2(-1, -.5f), new Vector2(-1, .5f));
        Assert.AreEqual(0, DarkRules.VeilAlpha(1, new Vector2(1, 0), red, blue), 1e-6);
        Assert.AreEqual(0, DarkRules.VeilAlpha(1, new Vector2(1 + DarkRules.FullRadius, 0), red, blue), 1e-6);
        Assert.AreEqual(.5f, DarkRules.VeilAlpha(1,
            new Vector2(1 + (DarkRules.FullRadius + DarkRules.FadeRadius) / 2, 0), red, blue), 1e-5);
        Assert.AreEqual(1, DarkRules.VeilAlpha(1, new Vector2(1 + DarkRules.FadeRadius, 0), red, blue), 1e-6);
        Assert.AreEqual(.8f, DarkRules.VeilAlpha(.8f, new Vector2(0, 5), red, blue), 1e-6);
        // 見失った剣のまわりは開けない。両方なければ全面が同じ濃さ(局所の明滅を作らない)。
        Assert.AreEqual(1, DarkRules.VeilAlpha(1, new Vector2(1, 0), default, blue), 1e-6);
        Assert.AreEqual(0, DarkRules.VeilAlpha(1, new Vector2(-1, 0), default, blue), 1e-6);
        Assert.AreEqual(.7f, DarkRules.VeilAlpha(.7f, Vector2.zero, default, default), 1e-6);
        Assert.AreEqual(1, DarkRules.VeilAlpha(1, new Vector2(float.NaN, 0), red, blue), 1e-6);
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
    public void NotesAreDrawnAboveTheVeilUntilTheEnd()
    {
        // 暗幕(DarkVeil)はノーツより下。遠いノーツは自分の明るさで黒くなり、近いノーツは暗幕に隠れない。
        spawner.Tick(TimeAt);
        var note = spawner.LiveNotes.Last();
        var parts = note.GetComponentsInChildren<Renderer>(true);
        Assert.IsNotEmpty(parts);
        Assert.IsTrue(parts.All(r => r.sortingOrder == MemoryVeilLayers.AboveNoteSortingOrder));
        Renderer ghost = note.TimingCue != null && note.TimingCue.GhostRoot != null
            ? note.TimingCue.GhostRoot.GetComponentInChildren<Renderer>(true) : null;
        if (ghost != null) Assert.AreEqual(MemoryVeilLayers.AboveNoteSortingOrder, ghost.sortingOrder, "着地の枠も上");
        mode.EndPlay();
        Assert.IsTrue(parts.All(r => r.sortingOrder == 0), "終わったら元の並び順へ戻す");
        if (ghost != null) Assert.AreEqual(0, ghost.sortingOrder);
        Assert.IsNull(mode.Veil);
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

    static double Bar(int n) => 1359.532958984375 + n * 8000.0 / 3;

    // Y の [from, to) 小節のノーツが、表の譜面の shiftBars 小節ずらした所に、同じ順・同じ中身で並ぶ(時刻と拍はずらした分だけ)。
    static void AssertSameNotes(ChartData y, ChartData chart, int from, int to, int shiftBars)
    {
        double shift = shiftBars * 8000.0 / 3;
        string where = from + "〜" + to + " 小節";
        var expected = y.notes.Where(n => n.time >= Bar(from) - 1 && n.time < Bar(to) - 1).OrderBy(n => n.time).ToList();
        var actual = chart.notes.Where(n => n.time >= Bar(from) + shift - 1 && n.time < Bar(to) + shift - 1).OrderBy(n => n.time).ToList();
        Assert.IsNotEmpty(expected, where);
        Assert.AreEqual(expected.Count, actual.Count, where + " の数");
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(expected[i].time + shift, actual[i].time, .05, where + " #" + i);
            Assert.AreEqual(expected[i].beat + shiftBars * 4, actual[i].beat, .001, where + " #" + i);
            var restored = JsonUtility.FromJson<NoteData>(JsonUtility.ToJson(actual[i]));
            restored.time = expected[i].time;
            restored.beat = expected[i].beat;
            Assert.AreEqual(JsonUtility.ToJson(expected[i]), JsonUtility.ToJson(restored), where + " #" + i);
        }
    }

    [Test]
    public void NotesSwitchAtTheFirstChorusAndKeepTheirTiming()
    {
        var light = Read("y");
        Assert.AreEqual(10, light.displayLevel);
        // Y = 元の Normal(24 小節より前)+ 元の Hard(24 小節から)。2026-10-31 に表の譜面だけ 1:51 に縮め
        // (元の 24〜47・56〜63 小節を抜いた)、Y は長い版のまま(音源 audio_y.mp3・演出 stage_y.json)。
        // 縮めた譜面に残った区間とは、ずらした時刻で同じノーツ(境目の 23 小節は Y が Normal から間引いているので比べない)。
        Assert.AreEqual(88, light.notes.Count(n => n.time < Bar(24) - .05));
        Assert.AreEqual(322, light.notes.Count(n => n.time >= Bar(24) - .05));
        AssertSameNotes(light, Read("normal"), 0, 23, 0);
        AssertSameNotes(light, Read("hard"), 48, 56, -24);
        AssertSameNotes(light, Read("hard"), 64, 80, -32);
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

// 2026-10-27 Y の暗幕。画面を黒く覆い、剣のまわりだけ穴を開ける(DarkRules.VeilAlpha と同じ式のシェーダー)。
public class DarkVeilTests
{
    GameObject cameraObject;
    Camera view;

    [SetUp]
    public void SetUp()
    {
        cameraObject = new GameObject("DarkVeilTestCamera");
        view = cameraObject.AddComponent<Camera>();
        view.transform.position = new Vector3(0, 0, -10);
    }

    [TearDown]
    public void TearDown()
    {
        if (cameraObject != null) Object.DestroyImmediate(cameraObject);
    }

    [Test]
    public void ShaderHasTheVeilProperties()
    {
        var shader = Resources.Load<Shader>("Effects/DarkVeil");
        Assert.NotNull(shader);
        var material = new Material(shader);
        try
        {
            foreach (string name in new[] { "_Darkness", "_RedSegment", "_BlueSegment", "_BladeMask", "_Radii", "_JudgeZ" })
                Assert.IsTrue(material.HasProperty(name), name);
            Assert.AreEqual(0, material.GetFloat("_Darkness"), 1e-6, "既定は透明");
        }
        finally { Object.DestroyImmediate(material); }
    }

    [Test]
    public void VeilSitsAboveTheWorldAndBelowNotesAndBlades()
    {
        var veil = DarkVeil.Create(view, 0f);
        Assert.NotNull(veil, "暗幕のshaderが使える");
        try
        {
            Assert.AreEqual(MemoryVeilLayers.NoiseSortingOrder, veil.Renderer.sortingOrder);
            Assert.Less(veil.Renderer.sortingOrder, MemoryVeilLayers.AboveNoteSortingOrder);
            Assert.Less(MemoryVeilLayers.AboveNoteSortingOrder, MemoryVeilLayers.AboveSortingOrder);
            Assert.AreSame(view.transform, veil.Renderer.transform.parent);
            Assert.IsFalse(veil.Visible, "暗さ0では描かない");

            var red = new BladeSample(new Vector2(1, -.5f), new Vector2(1, .5f));
            veil.Show(1f, red, default);
            Assert.IsTrue(veil.Visible);
            var material = veil.Renderer.sharedMaterial;
            Assert.AreEqual(1, material.GetFloat("_Darkness"), 1e-6);
            Assert.AreEqual(new Vector4(1, 0, 0, 0), material.GetVector("_BladeMask"), "見失った青の側は穴を開けない");
            Assert.AreEqual(new Vector4(1, -.5f, 1, .5f), material.GetVector("_RedSegment"));
            Assert.AreEqual(new Vector4(DarkRules.FullRadius, DarkRules.FadeRadius, 0, 0), material.GetVector("_Radii"));
            // 板はカメラの前にあり、画角より広い(撮影で縦横比が変わっても端が欠けない)。
            var bounds = veil.Renderer.GetComponent<MeshFilter>().sharedMesh.bounds;
            Assert.Greater(bounds.center.z, view.nearClipPlane);
            float halfHeight = bounds.center.z * Mathf.Tan(view.fieldOfView * .5f * Mathf.Deg2Rad);
            Assert.Greater(bounds.extents.y, halfHeight * 1.5f);
            Assert.Greater(bounds.extents.x, halfHeight * view.aspect * 1.5f);

            veil.Show(0f, red, default);
            Assert.IsFalse(veil.Visible);
        }
        finally { veil.Dispose(); }
        Assert.AreEqual(0, view.transform.childCount, "片付けで板を残さない");
    }
}
