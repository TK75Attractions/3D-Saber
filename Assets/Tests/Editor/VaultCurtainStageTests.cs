using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 幕は曲区間の姿勢だけを担当し、床の判定反応や固定支持部を変えない。
public class VaultCurtainStageTests
{
    GameObject root;
    FloorRenderer floor;
    VioletCurtainStage stage;
    Mesh curtain;
    Mesh[] fixedMeshes;
    Vector3[][] fixedVertices;

    [SetUp]
    public void Setup()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        DisplaySettings.SetProjectorModeForTest(false);
        root = new GameObject("VaultCurtainTest");
        floor = root.AddComponent<FloorRenderer>(); floor.randomizeOnPlay = false;
        floor.Build(StageTheme.VioletVault);
        stage = root.GetComponentInChildren<VioletCurtainStage>(); Assert.NotNull(stage);
        curtain = stage.GetComponentInChildren<MeshFilter>().sharedMesh;
        fixedMeshes = root.GetComponentsInChildren<MeshFilter>()
            .Where(f => !f.transform.IsChildOf(stage.transform)).Select(f => f.sharedMesh).ToArray();
        fixedVertices = fixedMeshes.Select(m => m.vertices).ToArray();
    }

    [TearDown]
    public void Cleanup()
    {
        if (root != null) Object.DestroyImmediate(root);
        DisplaySettings.ResetReducedEffectsCacheForTest();
        DisplaySettings.ResetProjectorModeCacheForTest();
    }

    [TestCase(false)] [TestCase(true)]
    public void FourWindowsFoldSimultaneouslyToRevealTheirCentresWhileFixedArchitectureStaysStill(bool projector)
    {
        DisplaySettings.SetProjectorModeForTest(projector);
        floor.Tick(20, .8f, 0, 0); var rest = curtain.vertices;
        var colors = curtain.colors; var triangles = curtain.triangles;
        floor.Tick(20, .8f, 0, 1); var open = curtain.vertices;
        Assert.AreEqual(4, stage.WindowCount);
        Assert.AreEqual(1, stage.Opening);
        CollectionAssert.AreNotEqual(rest, open);
        CollectionAssert.AreEqual(colors, curtain.colors, "折り畳みに成功色やフラッシュを付けない");
        CollectionAssert.AreEqual(triangles, curtain.triangles);
        var movements = new float[stage.WindowCount];
        var groups = new int[stage.WindowCount];
        var restGaps = Enumerable.Repeat(float.MaxValue, stage.WindowCount).ToArray();
        var openGaps = Enumerable.Repeat(float.MaxValue, stage.WindowCount).ToArray();
        for (int i = 0; i < rest.Length; i++)
        {
            int window = NearestWindow(rest[i]);
            Vector3 center = stage.WindowCenter(window);
            movements[window] += (open[i] - rest[i]).magnitude; groups[window]++;
            restGaps[window] = Mathf.Min(restGaps[window], Mathf.Abs(rest[i].z - center.z));
            openGaps[window] = Mathf.Min(openGaps[window], Mathf.Abs(open[i].z - center.z));
            Assert.Greater(Mathf.Abs(open[i].x), 6f, "幕が中央ノーツ帯へせり出さない");
            Assert.AreEqual(Mathf.Sign(rest[i].x), Mathf.Sign(open[i].x));
            AssertFinite(open[i]);
            var bounds = curtain.bounds; bounds.Expand(.0001f);
            Assert.IsTrue(bounds.Contains(open[i]), "開いた幕を描画boundsが含む");
        }
        for (int window = 0; window < stage.WindowCount; window++)
        {
            Assert.Greater(groups[window], 0);
            Assert.Greater(movements[window] / groups[window], .05f, "四窓のどれも同時に折り畳まれる");
            Assert.Greater(openGaps[window], restGaps[window] + .2f, "中央の被覆が減り、暗い奥面を見せる");
            int opposite = Enumerable.Range(0, stage.WindowCount).Single(w =>
                Mathf.Abs(stage.WindowCenter(w).z - stage.WindowCenter(window).z) < .01f &&
                Mathf.Sign(stage.WindowCenter(w).x) != Mathf.Sign(stage.WindowCenter(window).x));
            Assert.AreEqual(movements[window] / groups[window], movements[opposite] / groups[opposite], .0001f,
                "左右で位相や折り量をずらさない");
        }
        AssertFixedArchitecture();
    }

    [Test]
    public void LowUsesTheSameThirtyPercentPoseWithoutChangingThicknessColorOrClock()
    {
        floor.Tick(20, .5f, 0, .3f); var thirty = curtain.vertices;
        var triangles = curtain.triangles; var colors = curtain.colors;
        var material = stage.GetComponentInChildren<Renderer>().sharedMaterial;
        Color fabricColor = material.GetColor("_BaseColor"), emission = material.GetColor("_EmissionColor");
        floor.Tick(20, .5f, 0, 1); var full = curtain.vertices;
        DisplaySettings.SetReducedEffectsForTest(true);
        floor.Tick(20, .5f, 0, 1);
        Assert.AreEqual(.3f, stage.Opening, .00001f);
        Assert.AreEqual(20, stage.LastTickSeconds);
        CollectionAssert.AreEqual(thirty, curtain.vertices, "LOWは同じ折り形状の開度だけ30%にする");
        CollectionAssert.AreEqual(triangles, curtain.triangles);
        CollectionAssert.AreEqual(colors, curtain.colors, "LOWで幕を透明にしたり色を変えたりしない");
        Assert.AreEqual(fabricColor, material.GetColor("_BaseColor"));
        Assert.AreEqual(1, material.GetColor("_BaseColor").a);
        Assert.AreEqual(emission, material.GetColor("_EmissionColor"));
        Assert.AreEqual(Color.black, emission, "幕へ成功光を追加しない");
        DisplaySettings.SetReducedEffectsForTest(false); floor.Tick(20, .5f, 0, 1);
        CollectionAssert.AreEqual(full, curtain.vertices);
        AssertFixedArchitecture();
    }

    [Test]
    public void ClockFreezeSeekReloadAndOwnerDisableRestoreClosedPose()
    {
        var timeline = new StagePerformanceTimeline { sections = new[] {
            new StagePerformanceTimeline.Section { startSeconds = 10, endSeconds = 30, intensity = 1 } } };
        floor.Tick(0, 0, 0, 0); var rest = curtain.vertices;
        floor.Tick(20, 0, 0, timeline.EvaluateVaultCurtain(20)); var peak = curtain.vertices;
        Assert.AreEqual(1, stage.Opening);
        floor.Tick(20, 0, 0, timeline.EvaluateVaultCurtain(20));
        CollectionAssert.AreEqual(peak, curtain.vertices);
        floor.Tick(40, 0, 0, timeline.EvaluateVaultCurtain(40));
        CollectionAssert.AreEqual(rest, curtain.vertices);
        floor.Tick(20, 0, 0, timeline.EvaluateVaultCurtain(20));
        CollectionAssert.AreEqual(peak, curtain.vertices, "シークは通過順に依存しない");
        floor.enabled = false; AssertClosed(rest);
        floor.Tick(20, 0, 0, 1); AssertClosed(rest);
        floor.enabled = true; floor.Tick(20, 0, 0, 1);
        stage.enabled = false; AssertClosed(rest);
        floor.Tick(20, 0, 0, 1); AssertClosed(rest);
        stage.enabled = true; floor.Tick(20, 0, 0, 1);
        floor.SetRhythm(new ChartData()); AssertClosed(rest);
        floor.Tick(20, 0, 0, 1); root.SetActive(false); AssertClosed(rest);
        root.SetActive(true); floor.Tick(20, 0, 0, 1);
        CollectionAssert.AreEqual(peak, curtain.vertices);
        stage.Clear(); AssertClosed(rest);
    }

    [Test]
    public void InvalidClockIsIgnoredButInvalidOpeningAndNonpositiveClockCloseTheCurtain()
    {
        var rest = curtain.vertices;
        floor.Tick(20, .5f, 0, 1); var peak = curtain.vertices;
        foreach (var time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            floor.Tick(time, 0, 0, 0); stage.Tick(time, 0);
            Assert.AreEqual(20, stage.LastTickSeconds);
            CollectionAssert.AreEqual(peak, curtain.vertices);
        }
        foreach (var opening in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f })
        {
            floor.Tick(20, .5f, 0, 1); floor.Tick(20, .5f, 0, opening);
            Assert.AreEqual(0, stage.Opening); CollectionAssert.AreEqual(rest, curtain.vertices);
        }
        floor.Tick(20, .5f, 0, 5);
        Assert.AreEqual(1, stage.Opening); CollectionAssert.AreEqual(peak, curtain.vertices);
        foreach (var time in new[] { 0.0, -1.0 })
        {
            floor.Tick(20, .5f, 0, 1); floor.Tick(time, .5f, 0, 1);
            Assert.AreEqual(0, stage.Opening); CollectionAssert.AreEqual(rest, curtain.vertices);
        }
    }

    [Test]
    public void NoteDensityAndOtherMusicEffectsDoNotChangeCurtainPose()
    {
        var sparse = new ChartData(); sparse.notes.Add(new NoteData { time = 10000 });
        floor.SetRhythm(sparse); floor.Tick(20, 0, 0, .6f); var expected = curtain.vertices;
        var dense = new ChartData();
        for (int i = 0; i < 400; i++) dense.notes.Add(new NoteData { time = 19000 + i * 5 });
        floor.SetRhythm(dense); floor.Tick(20, 1, 1, .6f);
        CollectionAssert.AreEqual(expected, curtain.vertices,
            "譜面密度・入口の強度・他背景の灯具値から幕を追加変形しない");
        AssertFixedArchitecture();
    }

    [Test]
    public void RepeatedEvaluationKeepsOneOwnedMeshMaterialAndNoNewSimulationObjects()
    {
        Assert.AreSame(stage, VioletCurtainStage.Ensure(floor), "Ensureの再呼出で重複を作らない");
        var filters = stage.GetComponentsInChildren<MeshFilter>();
        var renderers = stage.GetComponentsInChildren<Renderer>();
        Assert.AreEqual(1, filters.Length); Assert.AreEqual(1, renderers.Length);
        var ownedMaterial = renderers[0].sharedMaterial;
        Assert.AreEqual(1, renderers.SelectMany(r => r.sharedMaterials).Distinct().Count());
        int count = curtain.vertexCount;
        Assert.That(count, Is.InRange(1, 4096));
        Assert.IsEmpty(stage.GetComponentsInChildren<Collider>());
        Assert.IsEmpty(stage.GetComponentsInChildren<Light>());
        Assert.IsEmpty(stage.GetComponentsInChildren<ParticleSystem>());
        for (int i = 0; i < 120; i++)
        {
            floor.Tick(i * .8, (i % 5) * .25f, 0, (i % 9) * .125f);
            Assert.AreEqual(count, curtain.vertexCount);
            Assert.AreSame(curtain, stage.GetComponentInChildren<MeshFilter>().sharedMesh);
            Assert.AreSame(ownedMaterial, stage.GetComponentInChildren<Renderer>().sharedMaterial);
            foreach (var point in curtain.vertices) AssertFinite(point);
        }
        Assert.AreEqual(1, stage.GetComponentsInChildren<Renderer>().Length);
        AssertFixedArchitecture();
    }

    [Test]
    public void DestroyingCurtainReleasesOnlyItsOwnResourcesAndLeavesFloorArchitectureAlive()
    {
        var ownedMaterial = stage.GetComponentInChildren<Renderer>().sharedMaterial;
        var otherMaterials = root.GetComponentsInChildren<Renderer>()
            .Where(r => !r.transform.IsChildOf(stage.transform)).SelectMany(r => r.sharedMaterials).Distinct().ToArray();
        floor.Tick(20, .8f, 0, 1);
        Object.DestroyImmediate(stage.gameObject);
        Assert.IsTrue(curtain == null); Assert.IsTrue(ownedMaterial == null);
        Assert.IsTrue(fixedMeshes.All(m => m != null)); Assert.IsTrue(otherMaterials.All(m => m != null));
        AssertFixedArchitecture();
        Assert.DoesNotThrow(() => floor.Tick(21, 0, 0, 1));
        Object.DestroyImmediate(root); root = null;
        Assert.IsTrue(fixedMeshes.All(m => m == null)); Assert.IsTrue(otherMaterials.All(m => m == null));
    }

    int NearestWindow(Vector3 point)
    {
        int nearest = -1; float distance = float.MaxValue;
        for (int i = 0; i < stage.WindowCount; i++)
        {
            var center = stage.WindowCenter(i);
            if (Mathf.Sign(point.x) != Mathf.Sign(center.x)) continue;
            float candidate = Mathf.Abs(point.z - center.z);
            if (candidate < distance) { distance = candidate; nearest = i; }
        }
        Assert.GreaterOrEqual(nearest, 0); return nearest;
    }

    void AssertClosed(Vector3[] rest)
    {
        Assert.AreEqual(0, stage.Opening); Assert.AreEqual(0, stage.LastTickSeconds);
        CollectionAssert.AreEqual(rest, curtain.vertices);
    }

    void AssertFixedArchitecture()
    {
        for (int i = 0; i < fixedMeshes.Length; i++)
            CollectionAssert.AreEqual(fixedVertices[i], fixedMeshes[i].vertices,
                "固定レール・支持・奥窓・床を幕の折り畳みで変えない");
    }

    static void AssertFinite(Vector3 value) =>
        Assert.IsFalse(float.IsNaN(value.sqrMagnitude) || float.IsInfinity(value.sqrMagnitude));
}
