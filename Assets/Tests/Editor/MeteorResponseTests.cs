using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 宇宙の既存四岩だけが分かれ、元の形・法線・所有資源へ戻ることを確認する。
public class MeteorResponseTests
{
    GameObject root;
    FloorRenderer floor;
    ScenicStageWorld world;
    Mesh[] rocks, others;
    Vector3[][] rest, normals, otherVertices;
    Transform[] transforms;

    [SetUp]
    public void Setup()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        DisplaySettings.SetProjectorModeForTest(false);
        root = new GameObject("MeteorResponseTest");
        floor = root.AddComponent<FloorRenderer>(); floor.randomizeOnPlay = false;
        floor.Build(StageTheme.AstralOrbit);
        world = root.GetComponentInChildren<ScenicStageWorld>(); Assert.NotNull(world);
        Assert.IsTrue(world.MeteorResponsesReady);
        string[] names = { "OrbitingRock-1-1", "OrbitingRock-1-2", "OrbitingRock1-2", "OrbitingRock1-1" };
        transforms = names.Select(name => world.transform.Find(name)).ToArray();
        Assert.IsTrue(transforms.All(t => t != null));
        rocks = transforms.Select(t => t.GetComponent<MeshFilter>().sharedMesh).ToArray();
        rest = rocks.Select(m => m.vertices).ToArray(); normals = rocks.Select(m => m.normals).ToArray();
        others = world.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh)
            .Where(m => !rocks.Contains(m)).ToArray();
        otherVertices = others.Select(m => m.vertices).ToArray();
    }

    [TearDown]
    public void Cleanup()
    {
        if (root != null) Object.DestroyImmediate(root);
        DisplaySettings.ResetReducedEffectsCacheForTest();
        DisplaySettings.ResetProjectorModeCacheForTest();
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void EachLaneMovesThreeRigidPiecesAndReturnsToExactlyTheSameMesh(int lane)
    {
        var anchor = world.MeteorAnchor(lane);
        Assert.AreEqual(lane < 2 ? -1 : 1, Mathf.Sign(anchor.x));
        Assert.AreEqual(lane == 0 || lane == 3 ? 11 : 19, anchor.z);
        world.Tick(10); world.OnMeteorPerfect(lane); world.Tick(10.30);
        Assert.AreEqual(1 << lane, world.MeteorLaneMask); Assert.AreEqual(1, world.ActiveMeteorResponseCount);
        Assert.AreEqual(1, world.MeteorOpening(lane));
        for (int other = 0; other < 4; other++)
        {
            if (other == lane) continue;
            CollectionAssert.AreEqual(rest[other], rocks[other].vertices, "非対象の実列の岩は分かれない");
        }
        var moved = rocks[lane].vertices;
        var offsets = new List<Vector3>();
        for (int i = 0; i < moved.Length; i++)
        {
            Vector3 delta = moved[i] - rest[lane][i];
            Assert.AreEqual(.20f, delta.magnitude, .00001f);
            Assert.AreEqual(0, delta.y, .00001f, "ローカルに持ち上げる新しい運動は付けない");
            if (!offsets.Any(v => (v - delta).sqrMagnitude < .00000001f)) offsets.Add(delta);
            Assert.IsTrue(rocks[lane].bounds.Contains(moved[i]), "分離した面が描画boundsから欠けない");
            AssertFinite(moved[i]);
        }
        Assert.AreEqual(3, offsets.Count, "個別粒でなく三つの剛体部分を平行移動する");
        CollectionAssert.AreEqual(normals[lane], rocks[lane].normals);
        world.Tick(11.11); AssertRest();
        AssertOtherGeometry();
    }

    [Test]
    public void OpeningHasOneRiseHoldAndReturnAndRejectsInvalidAges()
    {
        Assert.AreEqual(0, ScenicStageWorld.EvaluateMeteorOpening(0));
        Assert.AreEqual(.5f, ScenicStageWorld.EvaluateMeteorOpening(.12f), .00001f);
        Assert.AreEqual(1, ScenicStageWorld.EvaluateMeteorOpening(.24f));
        Assert.AreEqual(1, ScenicStageWorld.EvaluateMeteorOpening(.42f));
        Assert.AreEqual(.5f, ScenicStageWorld.EvaluateMeteorOpening(.76f), .00001f);
        foreach (float age in new[] { -.1f, 1.10f, 100f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.AreEqual(0, ScenicStageWorld.EvaluateMeteorOpening(age));
    }

    [TestCase(false)] [TestCase(true)]
    public void LowScalesOnlyTheSameLocalGapOnceAndChorusKeepsTheLocalPose(bool projector)
    {
        DisplaySettings.SetProjectorModeForTest(projector);
        world.Tick(10); for (int lane = 0; lane < 4; lane++) world.OnMeteorPerfect(lane);
        world.Tick(10.30, 0, 0); var full = rocks.Select(m => m.vertices).ToArray();
        var positions = transforms.Select(t => t.localPosition).ToArray();
        var rotations = transforms.Select(t => t.localRotation).ToArray();
        DisplaySettings.SetReducedEffectsForTest(true); world.Tick(10.30, 0, 0);
        for (int lane = 0; lane < 4; lane++)
        {
            Assert.AreEqual(.3f, world.MeteorOpening(lane), .00001f);
            var low = rocks[lane].vertices;
            for (int i = 0; i < low.Length; i++)
                Assert.Less((low[i] - rest[lane][i] - (full[lane][i] - rest[lane][i]) * .3f).magnitude,
                    .00001f, "LOWは塊を縮めたり違う方向へ動かさない");
            Assert.AreEqual(positions[lane], transforms[lane].localPosition);
            Assert.AreEqual(rotations[lane], transforms[lane].localRotation);
            CollectionAssert.AreEqual(normals[lane], rocks[lane].normals);
        }
        var lowPose = rocks.Select(m => m.vertices).ToArray();
        world.Tick(10.30, 1, 1);
        for (int lane = 0; lane < 4; lane++) CollectionAssert.AreEqual(lowPose[lane], rocks[lane].vertices);
        DisplaySettings.SetReducedEffectsForTest(false); world.Tick(10.30, 1, 1);
        for (int lane = 0; lane < 4; lane++) CollectionAssert.AreEqual(full[lane], rocks[lane].vertices);
    }

    [Test]
    public void PerfectBeforeFirstClockAndRepeatedZeroAreAcceptedWithoutExtendingTheirCycle()
    {
        world.ClearMeteorResponses(); world.OnMeteorPerfect(0); world.OnMeteorPerfect(0);
        Assert.AreEqual(1, world.ActiveMeteorResponseCount);
        world.Tick(10); Assert.AreEqual(0, world.MeteorOpening(0));
        world.Tick(10.12); Assert.AreEqual(.5f, world.MeteorOpening(0), .00001f);
        world.ClearMeteorResponses(); world.Tick(0); world.OnMeteorPerfect(1);
        world.Tick(0); world.Tick(.12);
        Assert.AreEqual(.5f, world.MeteorOpening(1), .00001f, "同じ0秒の反復で成功を消さない");
        world.ClearMeteorResponses(); world.OnMeteorPerfect(2); world.Tick(100);
        world.Tick(100.12); Assert.AreEqual(.5f, world.MeteorOpening(2), .00001f);
        world.Tick(0); AssertRest();
    }

    [Test]
    public void RapidSameLaneFinishesAndOtherLanesKeepIndependentClocks()
    {
        world.Tick(10); world.OnMeteorPerfect(0);
        world.Tick(10.12); world.OnMeteorPerfect(1);
        for (int step = 1; step <= 8; step++)
        {
            double time = 10 + step * .125;
            world.Tick(time); world.OnMeteorPerfect(0);
        }
        world.Tick(11.11);
        Assert.AreEqual(0, world.MeteorOpening(0));
        Assert.AreEqual(2, world.MeteorLaneMask, "隣の列の別の開始時刻を守る");
        world.Tick(11.23); AssertRest();
        world.Tick(12); AssertRest();
        world.OnMeteorPerfect(3); world.Tick(12.30);
        Assert.AreEqual(8, world.MeteorLaneMask); Assert.AreEqual(1, world.MeteorOpening(3));
    }

    [Test]
    public void FreezeSeekClearAndDisableRestoreOriginalVerticesAndNormals()
    {
        world.Tick(10); world.OnMeteorPerfect(0); world.Tick(10.30);
        var held = rocks[0].vertices; world.Tick(10.30);
        CollectionAssert.AreEqual(held, rocks[0].vertices);
        foreach (double time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        { world.Tick(time); CollectionAssert.AreEqual(held, rocks[0].vertices); }
        world.Tick(9); AssertRest();
        world.OnMeteorPerfect(0); world.Tick(9.30); world.enabled = false; AssertRest();
        world.OnMeteorPerfect(0); world.Tick(10); AssertRest();
        world.enabled = true; world.Tick(10); AssertRest();
        world.OnMeteorPerfect(0); world.Tick(10.30); root.SetActive(false); AssertRest();
        root.SetActive(true); world.Tick(11); AssertRest();
        world.OnMeteorPerfect(0); world.Tick(11.30); world.ClearMeteorResponses(); AssertRest();
        world.OnMeteorPerfect(-1); world.OnMeteorPerfect(4); AssertRest();
    }

    [Test]
    public void RepeatedResponsesKeepTenOriginalRockRenderersAndOneSharedRockMaterial()
    {
        var filters = world.GetComponentsInChildren<MeshFilter>().Where(f => f.name.StartsWith("OrbitingRock")).ToArray();
        Assert.AreEqual(10, filters.Length);
        Assert.AreEqual(4, filters.Count(f => f.sharedMesh.vertexCount == 198));
        Assert.AreEqual(6, filters.Count(f => f.sharedMesh.vertexCount == 126));
        var materials = filters.Select(f => f.GetComponent<Renderer>().sharedMaterial).Distinct().ToArray();
        Assert.AreEqual(1, materials.Length, "分かれる4岩に別の成功発光材質を作らない");
        var allMeshes = world.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh).ToArray();
        var allMaterials = world.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().ToArray();
        for (int step = 0; step < 120; step++)
        {
            world.Tick(10 + step * .1); world.OnMeteorPerfect(step % 4);
            CollectionAssert.AreEqual(allMeshes, world.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh).ToArray());
            CollectionAssert.AreEquivalent(allMaterials, world.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().ToArray());
        }
        world.ClearMeteorResponses(); AssertRest(); AssertOtherGeometry();
        Object.DestroyImmediate(root); root = null;
        Assert.IsTrue(allMeshes.All(m => m == null)); Assert.IsTrue(allMaterials.All(m => m == null));
    }

    [Test]
    public void NonAstralWorldHasNoMeteorResponses()
    {
        var other = new GameObject("NonAstralWorld");
        try
        {
            var otherFloor = other.AddComponent<FloorRenderer>(); otherFloor.randomizeOnPlay = false;
            otherFloor.Build(StageTheme.MoonlitGarden);
            var otherWorld = other.GetComponentInChildren<ScenicStageWorld>();
            Assert.IsFalse(otherWorld.MeteorResponsesReady);
            otherWorld.Tick(10); otherWorld.OnMeteorPerfect(0); otherWorld.Tick(10.3);
            Assert.AreEqual(0, otherWorld.MeteorLaneMask); Assert.AreEqual(0, otherWorld.ActiveMeteorResponseCount);
        }
        finally { Object.DestroyImmediate(other); }
    }

    void AssertRest()
    {
        Assert.AreEqual(0, world.ActiveMeteorResponseCount); Assert.AreEqual(0, world.MeteorLaneMask);
        for (int lane = 0; lane < 4; lane++)
        {
            Assert.AreEqual(0, world.MeteorOpening(lane));
            CollectionAssert.AreEqual(rest[lane], rocks[lane].vertices);
            CollectionAssert.AreEqual(normals[lane], rocks[lane].normals);
        }
    }

    void AssertOtherGeometry()
    { for (int i = 0; i < others.Length; i++) CollectionAssert.AreEqual(otherVertices[i], others[i].vertices); }

    static void AssertFinite(Vector3 value) =>
        Assert.IsFalse(float.IsNaN(value.sqrMagnitude) || float.IsInfinity(value.sqrMagnitude));
}
