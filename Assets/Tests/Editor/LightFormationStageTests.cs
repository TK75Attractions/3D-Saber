using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 曲の長い配列変化を、入力の短い応答やノーツ由来の明滅と混ぜない。
public class LightFormationStageTests
{
    GameObject root;
    FloorRenderer floor;
    PulseArrayStage stage;
    Mesh fixtures, lights, structure;

    [SetUp]
    public void Setup()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        DisplaySettings.SetProjectorModeForTest(false);
        root = new GameObject("LightFormationTest");
        floor = root.AddComponent<FloorRenderer>(); floor.Build(StageTheme.PulseArray);
        stage = root.GetComponentInChildren<PulseArrayStage>();
        fixtures = MeshAt("LampFixtures"); lights = MeshAt("LightBanks"); structure = MeshAt("GraphiteStructure");
    }

    [TearDown]
    public void Cleanup()
    {
        if (root != null) Object.DestroyImmediate(root);
        DisplaySettings.ResetReducedEffectsCacheForTest();
        DisplaySettings.ResetProjectorModeCacheForTest();
    }

    Mesh MeshAt(string name) => stage.transform.Find(name).GetComponent<MeshFilter>().sharedMesh;

    [TestCase(false)] [TestCase(true)]
    public void SixteenFixturesLiftOnlyVerticallyWithTheirLensesAndLeaveTheBeamsFixed(bool projector)
    {
        DisplaySettings.SetProjectorModeForTest(projector);
        floor.Tick(10, .8f, 0);
        var rest = fixtures.vertices; var dark = lights.vertices; var beams = structure.vertices;
        var colorsBefore = lights.colors;
        floor.Tick(10, .8f, 1);
        var raised = fixtures.vertices; var lit = lights.vertices;
        Assert.AreEqual(1, stage.FormationIntensity);
        CollectionAssert.AreEqual(beams, structure.vertices, "固定梁を曲区間で変形しない");
        CollectionAssert.AreEqual(colorsBefore, lights.colors, "灯具の位置評価は色を変えない");
        var lifts = HousingLifts(rest, raised);
        Assert.AreEqual(16, lifts.Count, "左右8組の筐体が独立した位置を保つ");
        foreach (var pair in lifts)
        {
            int opposite = pair.Key < 8 ? pair.Key + 8 : pair.Key - 8;
            Assert.AreEqual(pair.Value, lifts[opposite], .00001f, "左右で同じ時刻の高さを揃える");
            Assert.That(pair.Value, Is.InRange(.1f, 1.1f));
        }
        Assert.Greater(lifts[3], lifts[0], "配列中央の持ち上がりを手前より大きくする");
        AssertLiftOnly(rest, raised);
        AssertLensesFollow(dark, lit, lifts);
        foreach (var point in raised)
        {
            Assert.Greater(Mathf.Abs(point.x), 7.2f, "新しい筐体と支持線を中央へ入れない");
            Assert.That(point.y, Is.InRange(3.8f, 7.3f));
            Assert.That(point.z, Is.InRange(1.3f, 50.8f));
            var bounds = fixtures.bounds; bounds.Expand(.0001f);
            Assert.IsTrue(bounds.Contains(point), "上昇後の描画boundsが筐体を含む");
        }
    }

    [Test]
    public void LowPreservesFixtureWidthAndClockButReducesLiftToThirtyPercent()
    {
        floor.Tick(10, .7f, 0); var rest = fixtures.vertices; var baseLight = lights.vertices;
        floor.Tick(10, .7f, 1); var full = fixtures.vertices; var fullLight = lights.vertices;
        var fullColors = lights.colors;
        DisplaySettings.SetReducedEffectsForTest(true);
        floor.Tick(10, .7f, 1); var low = fixtures.vertices; var lowLight = lights.vertices;
        var lowColors = lights.colors;
        Assert.AreEqual(1, stage.FormationIntensity, "LOWは曲区間の評価値を変更しない");
        Assert.AreEqual(10, stage.LastTickSeconds);
        AssertScaledLift(rest, full, low, .3f);
        AssertScaledLift(baseLight, fullLight, lowLight, .3f);
        int lensVertices = 0;
        for (int i = 0; i < fullLight.Length; i++)
        {
            if (fullLight[i].y - baseLight[i].y > .00001f)
            {
                lensVertices++;
                Assert.AreEqual(fullColors[i].r, lowColors[i].r);
                Assert.AreEqual(fullColors[i].g, lowColors[i].g);
                Assert.AreEqual(fullColors[i].b, lowColors[i].b);
                Assert.AreEqual(fullColors[i].a * .3f, lowColors[i].a, .00001f,
                    "LOWでは新しい灯面だけalphaを30%へ落とす");
            }
            else Assert.AreEqual(fullColors[i], lowColors[i], "既存バンクの光をC22のLOWで変えない");
        }
        Assert.AreEqual(16 * 4, lensVertices);
        DisplaySettings.SetReducedEffectsForTest(false);
        floor.Tick(10, .7f, 1);
        CollectionAssert.AreEqual(full, fixtures.vertices, "LOW解除で同じ時刻の配置へ復帰する");
        CollectionAssert.AreEqual(fullLight, lights.vertices);
    }

    [Test]
    public void SongClockSeekDisableAndChartReloadDoNotCarryAnOldPose()
    {
        var timeline = new StagePerformanceTimeline { sections = new[] {
            new StagePerformanceTimeline.Section { startSeconds = 10, endSeconds = 30, intensity = 1 } } };
        floor.Tick(0, 0, 0); var rest = fixtures.vertices;
        floor.Tick(20, 0, timeline.EvaluateLightFormation(20)); var peak = fixtures.vertices;
        Assert.AreEqual(1, stage.FormationIntensity);
        var peakLight = lights.vertices;
        floor.Tick(20, 0, timeline.EvaluateLightFormation(20));
        CollectionAssert.AreEqual(peak, fixtures.vertices);
        CollectionAssert.AreEqual(peakLight, lights.vertices);
        floor.Tick(40, 0, timeline.EvaluateLightFormation(40));
        CollectionAssert.AreEqual(rest, fixtures.vertices, "区間を飛び越えても復帰する");
        floor.Tick(20, 0, timeline.EvaluateLightFormation(20));
        CollectionAssert.AreEqual(peak, fixtures.vertices, "後方シークも直接その時刻の姿勢を得る");
        floor.enabled = false;
        Assert.AreEqual(0, stage.FormationIntensity);
        CollectionAssert.AreEqual(rest, fixtures.vertices, "親だけの無効化でも筐体を戻す");
        AssertRestLenses(lights.vertices);
        floor.Tick(20, 0, 1);
        CollectionAssert.AreEqual(rest, fixtures.vertices);
        floor.enabled = true; floor.Tick(20, 0, 1);
        stage.enabled = false;
        Assert.AreEqual(0, stage.FormationIntensity);
        CollectionAssert.AreEqual(rest, fixtures.vertices);
        AssertRestLenses(lights.vertices);
        floor.Tick(20, 0, 1);
        CollectionAssert.AreEqual(rest, fixtures.vertices, "無効中は再び動かない");
        stage.enabled = true; floor.Tick(20, 0, 1);
        floor.SetRhythm(new ChartData());
        Assert.AreEqual(0, stage.FormationIntensity);
        CollectionAssert.AreEqual(rest, fixtures.vertices, "譜面の再読込で筐体を戻す");
        AssertRestLenses(lights.vertices);
        floor.Tick(20, 0, 1); root.SetActive(false);
        CollectionAssert.AreEqual(rest, fixtures.vertices, "背景全体の無効化でも筐体を戻す");
        AssertRestLenses(lights.vertices);
    }

    [Test]
    public void InvalidTimesAreIgnoredAndInvalidFormationOrNonpositiveTimeReturnsToRest()
    {
        floor.Tick(0, 0, 0); var rest = fixtures.vertices;
        floor.Tick(20, .5f, 1); var peak = fixtures.vertices; var peakLight = lights.vertices;
        foreach (var time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            floor.Tick(time, 0, 0);
            Assert.AreEqual(20, stage.LastTickSeconds);
            CollectionAssert.AreEqual(peak, fixtures.vertices);
            CollectionAssert.AreEqual(peakLight, lights.vertices);
        }
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f })
        {
            floor.Tick(20, .5f, value);
            Assert.AreEqual(0, stage.FormationIntensity);
            CollectionAssert.AreEqual(rest, fixtures.vertices);
            AssertRestLenses(lights.vertices);
        }
        floor.Tick(20, .5f, 5);
        Assert.AreEqual(1, stage.FormationIntensity);
        CollectionAssert.AreEqual(peak, fixtures.vertices);
        foreach (var time in new[] { 0.0, -1.0 })
        {
            floor.Tick(time, float.NaN, 1);
            Assert.AreEqual(0, stage.FormationIntensity);
            CollectionAssert.AreEqual(rest, fixtures.vertices);
            AssertRestLenses(lights.vertices);
        }
    }

    [Test]
    public void FixturePoseDoesNotDependOnNoteCuesOrChartDensity()
    {
        var sparse = new ChartData(); sparse.notes.Add(new NoteData { time = 10000 });
        floor.SetRhythm(sparse); floor.Tick(10, .8f, .6f);
        var expected = fixtures.vertices; var lenses = lights.vertices;
        var dense = new ChartData();
        for (int i = 0; i < 400; i++) dense.notes.Add(new NoteData { time = 9500 + i * 5 });
        floor.SetRhythm(dense); floor.Tick(10, .8f, .6f);
        CollectionAssert.AreEqual(expected, fixtures.vertices);
        CollectionAssert.AreEqual(lenses, lights.vertices, "cueは明るさだけで、配列や灯面の位置に作用しない");
    }

    [Test]
    public void LongRunningFormationKeepsThreeMeshesTwoMaterialsAndReleasesThem()
    {
        var ownedMeshes = root.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh).ToArray();
        var renderers = root.GetComponentsInChildren<MeshRenderer>();
        var ownedMaterials = renderers.Select(r => r.sharedMaterial).Distinct().ToArray();
        Assert.AreEqual(3, ownedMeshes.Length); Assert.AreEqual(3, renderers.Length); Assert.AreEqual(2, ownedMaterials.Length);
        Assert.AreSame(stage.transform.Find("GraphiteStructure").GetComponent<MeshRenderer>().sharedMaterial,
            stage.transform.Find("LampFixtures").GetComponent<MeshRenderer>().sharedMaterial);
        Assert.IsEmpty(root.GetComponentsInChildren<Collider>());
        Assert.IsEmpty(root.GetComponentsInChildren<Light>());
        Assert.IsEmpty(root.GetComponentsInChildren<ParticleSystem>());
        int fixtureCount = fixtures.vertexCount;
        for (int i = 0; i < 300; i++)
        {
            floor.Tick(i * .8, (i % 5) * .25f, (i % 9) * .125f);
            Assert.AreEqual(fixtureCount, fixtures.vertexCount);
            Assert.LessOrEqual(lights.vertexCount, PulseArrayStage.VertexBudget);
            Assert.AreEqual(3, root.GetComponentsInChildren<MeshRenderer>().Length);
            foreach (var point in fixtures.vertices)
                Assert.IsFalse(float.IsNaN(point.sqrMagnitude) || float.IsInfinity(point.sqrMagnitude));
        }
        CollectionAssert.AreEquivalent(ownedMeshes, root.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh));
        Object.DestroyImmediate(root); root = null;
        Assert.IsTrue(ownedMeshes.All(m => m == null));
        Assert.IsTrue(ownedMaterials.All(m => m == null));
    }

    // 頂点の生成順に依存せず、静止筐体の高さと位置から左右8組を対応させる。
    static int Group(Vector3 point)
    {
        int bank = Mathf.RoundToInt((point.z - 1.55f) / 7);
        Assert.That(bank, Is.InRange(0, 7));
        return bank + (point.x > 0 ? 8 : 0);
    }

    static Dictionary<int, float> HousingLifts(Vector3[] rest, Vector3[] raised)
    {
        var values = new Dictionary<int, float>();
        for (int i = 0; i < rest.Length; i++)
        {
            int group = Group(rest[i]);
            if (rest[i].y > (group % 8 == 1 ? 4.861f : 4.061f)) continue;
            float delta = raised[i].y - rest[i].y;
            if (values.TryGetValue(group, out var previous)) Assert.AreEqual(previous, delta, .00001f);
            else values.Add(group, delta);
        }
        return values;
    }

    static void AssertLiftOnly(Vector3[] rest, Vector3[] raised)
    {
        Assert.AreEqual(rest.Length, raised.Length);
        for (int i = 0; i < rest.Length; i++)
        {
            Assert.AreEqual(rest[i].x, raised[i].x); Assert.AreEqual(rest[i].z, raised[i].z);
            Assert.That(raised[i].y - rest[i].y, Is.InRange(-.00001f, 1.10001f));
        }
    }

    static void AssertLensesFollow(Vector3[] rest, Vector3[] raised, Dictionary<int, float> lifts)
    {
        var groups = new HashSet<int>();
        AssertLiftOnly(rest, raised);
        for (int i = 0; i < rest.Length; i++)
        {
            float delta = raised[i].y - rest[i].y;
            if (delta < .00001f) continue;
            int group = Group(rest[i]); groups.Add(group);
            Assert.AreEqual(lifts[group], delta, .00001f, "灯面を筐体と同じ距離だけ動かす");
        }
        Assert.AreEqual(16, groups.Count);
    }

    static void AssertScaledLift(Vector3[] rest, Vector3[] full, Vector3[] low, float scale)
    {
        Assert.AreEqual(rest.Length, low.Length);
        for (int i = 0; i < rest.Length; i++)
        {
            Assert.AreEqual(full[i].x, low[i].x); Assert.AreEqual(full[i].z, low[i].z);
            Assert.AreEqual((full[i].y - rest[i].y) * scale, low[i].y - rest[i].y, .00001f);
        }
    }

    static void AssertRestLenses(Vector3[] points)
    {
        var groups = new HashSet<int>();
        foreach (var point in points)
        {
            if (Mathf.Abs(Mathf.Abs(point.x) - 7.6f) > .281f) continue;
            int bank = Mathf.RoundToInt((point.z - 1.404f) / 7);
            if (bank < 0 || bank > 7 || Mathf.Abs(point.z - (1.404f + bank * 7)) > .001f) continue;
            float restY = bank == 1 ? 4.75f : 3.95f;
            Assert.That(point.y, Is.InRange(restY - .041f, restY + .041f), "灯面も筐体の原位置へ戻す");
            groups.Add(bank + (point.x > 0 ? 8 : 0));
        }
        Assert.AreEqual(16, groups.Count, "16灯面の原位置を確認する");
    }
}
