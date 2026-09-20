using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 曲時計で通過する一体の形と資源を検証する。判定・譜面・曲区間の選択は別テストで扱う。
public class MarinePassageTests
{
    GameObject root;
    FloorRenderer floor;
    AbyssalPassageStage stage;
    Mesh mesh;
    Material material;
    Mesh[] otherMeshes;
    Material[] otherMaterials;

    [SetUp]
    public void Setup()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        DisplaySettings.SetProjectorModeForTest(false);
        root = new GameObject("MarinePassTest");
        floor = root.AddComponent<FloorRenderer>(); floor.randomizeOnPlay = false;
        floor.Build(StageTheme.AbyssalRuins);
        otherMeshes = root.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh).Distinct().ToArray();
        otherMaterials = root.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().ToArray();
        stage = AbyssalPassageStage.Create(root.transform, floor.floorY); Assert.NotNull(stage);
        mesh = stage.GetComponent<MeshFilter>().sharedMesh;
        material = stage.GetComponent<Renderer>().sharedMaterial;
    }

    [TearDown]
    public void Cleanup()
    {
        if (root != null) Object.DestroyImmediate(root);
        DisplaySettings.ResetReducedEffectsCacheForTest();
        DisplaySettings.ResetProjectorModeCacheForTest();
    }

    [Test]
    public void TravelKeepsMovingForwardAndJoinsWithoutAnInstantSpeedChange()
    {
        float previous = AbyssalPassageStage.EvaluateTravel(0);
        Assert.AreEqual(0, previous);
        float previousZ = AbyssalPassageStage.EvaluateCenter(0, floor.floorY).z;
        for (int i = 1; i <= 1400; i++)
        {
            float age = i * .01f;
            float travel = AbyssalPassageStage.EvaluateTravel(age);
            Assert.Greater(travel, previous, "通常再生で停止・逆走・周回しない");
            Vector3 center = AbyssalPassageStage.EvaluateCenter(age, floor.floorY);
            Assert.Greater(center.z, previousZ, "手前へ迫る航路に変えない");
            Assert.Less(center.x, -6f);
            Assert.AreEqual(floor.floorY + 2.6f, center.y, .0001f);
            Vector3 forward = AbyssalPassageStage.EvaluateForward(age);
            AssertFinite(forward); Assert.AreEqual(1, forward.magnitude, .0001f);
            Assert.Greater(forward.z, 0, "頭を手前へ反転させない");
            previous = travel; previousZ = center.z;
        }
        Assert.AreEqual(1, previous, .00001f);
        const float h = .001f;
        foreach (float join in new[] { 3f, 11f })
        {
            float at = AbyssalPassageStage.EvaluateTravel(join);
            float left = (at - AbyssalPassageStage.EvaluateTravel(join - h)) / h;
            float right = (AbyssalPassageStage.EvaluateTravel(join + h) - at) / h;
            Assert.Greater(left, 0); Assert.Greater(right, 0);
            Assert.AreEqual(left, right, .002f, "速度の継ぎ目に段差を作らない");
        }
    }

    [Test]
    public void WholeCreatureStaysOutsideTheCentralCorridorAndKeepsItsSolidBody()
    {
        var body = mesh.vertices.Take(stage.BodyVertexCount).ToArray();
        var bodyNormals = mesh.normals.Take(stage.BodyVertexCount).ToArray();
        var triangles = mesh.triangles;
        foreach (float age in new[] { 0f, .15f, .6f, 1.5f, 3f, 5f, 7f, 9f, 11f, 12.5f, 13.7f, 13.999f })
        {
            stage.SetPass(age); Assert.IsTrue(stage.IsVisible);
            Assert.AreEqual(age, stage.PassAge);
            var vertices = mesh.vertices;
            CollectionAssert.AreEqual(body, vertices.Take(stage.BodyVertexCount).ToArray(), "胴体を膨張や成功光へ変えない");
            CollectionAssert.AreEqual(bodyNormals, mesh.normals.Take(stage.BodyVertexCount).ToArray());
            CollectionAssert.AreEqual(triangles, mesh.triangles);
            var bounds = mesh.bounds; bounds.Expand(.0001f);
            foreach (Vector3 point in vertices)
            {
                AssertFinite(point); Assert.IsTrue(bounds.Contains(point), "動く鰭も同じ描画boundsへ収まる");
                Vector3 world = stage.transform.TransformPoint(point);
                Assert.Less(world.x, -6f, "全身を片側のノーツ帯外へ保つ");
                Assert.Greater(world.y, floor.floorY + 1.5f, "低い珊瑚や床の反応へ下りない");
                Assert.Less(world.y, floor.floorY + 3.3f, "既存魚群の中心より下で通過する");
            }
        }
        Assert.AreEqual(0, material.GetFloat("_Emission"));
        Assert.AreEqual(0, material.GetFloat("_Caustics"));
        Assert.AreEqual(0, material.GetFloat("_Sway"));
        Assert.AreEqual(Color.black, material.GetColor("_AccentColor"));
        Assert.AreEqual(1, material.GetColor("_BaseColor").a);
    }

    [TestCase(0f, 5f)]
    [TestCase(14f, 16f)]
    public void ClosedEndpointFitsBehindTheExistingPillar(float age, float pillarZ)
    {
        stage.Clear(); var closed = mesh.vertices;
        var closedBounds = new Bounds(closed[0], Vector3.zero);
        foreach (var point in closed) closedBounds.Encapsulate(point);
        Assert.LessOrEqual(closedBounds.size.x, 1.12f + .0001f);
        Assert.LessOrEqual(closedBounds.size.y, .90f + .0001f);
        Assert.LessOrEqual(closedBounds.size.z, 2.80f + .0001f);
        Vector3 center = AbyssalPassageStage.EvaluateCenter(age, floor.floorY);
        Quaternion rotation = Quaternion.LookRotation(AbyssalPassageStage.EvaluateForward(age), Vector3.up);
        // GamePlayManagerがGame.unityのuseSlightTopDownViewで設定する実プレイ位置。
        Vector3 eye = new Vector3(0, 2.35f, -7);
        foreach (int sx in new[] { -1, 1 }) foreach (int sy in new[] { -1, 1 }) foreach (int sz in new[] { -1, 1 })
        {
            Vector3 corner = closedBounds.center + Vector3.Scale(closedBounds.extents, new Vector3(sx, sy, sz));
            Vector3 world = center + rotation * corner;
            Assert.IsTrue(IntersectsPillarBeforePoint(eye, world, pillarZ),
                "閉形の包囲箱の全8隅が柱の保守的内接円柱の裏に収まる: " + world);
        }
    }

    [Test]
    public void LowKeepsTravelAndRequiredFoldWhileOnlyReducingTheSecondaryFinRoll()
    {
        stage.SetPass(4.6f); var neutralNormals = mesh.normals;
        stage.SetPass(5.75f); var fullVertices = mesh.vertices; var fullNormals = mesh.normals;
        Vector3 position = stage.LocalCenter; Quaternion rotation = stage.transform.localRotation;
        Color color = material.GetColor("_BaseColor"); float opening = stage.FoldOpening;
        DisplaySettings.SetReducedEffectsForTest(true); stage.SetPass(5.75f);
        Assert.AreEqual(position, stage.LocalCenter); Assert.AreEqual(rotation, stage.transform.localRotation);
        Assert.AreEqual(opening, stage.FoldOpening); Assert.AreEqual(1, opening);
        Assert.AreEqual(color, material.GetColor("_BaseColor"));
        CollectionAssert.AreEqual(fullVertices.Take(stage.BodyVertexCount).ToArray(),
            mesh.vertices.Take(stage.BodyVertexCount).ToArray());
        CollectionAssert.AreNotEqual(fullVertices.Skip(stage.BodyVertexCount).ToArray(),
            mesh.vertices.Skip(stage.BodyVertexCount).ToArray(), "二次鰭の動きだけはLOWで小さくなる");
        int fin = stage.BodyVertexCount;
        float fullAngle = Mathf.Abs(Vector2.SignedAngle(XY(neutralNormals[fin]), XY(fullNormals[fin])));
        float lowAngle = Mathf.Abs(Vector2.SignedAngle(XY(neutralNormals[fin]), XY(mesh.normals[fin])));
        Assert.Greater(fullAngle, 1f);
        Assert.AreEqual(fullAngle * .3f, lowAngle, .002f, "同じ全開姿勢を基準に副次回転だけ30%にする");
        DisplaySettings.SetReducedEffectsForTest(false); stage.SetPass(5.75f);
        CollectionAssert.AreEqual(fullVertices, mesh.vertices);
        foreach (float age in new[] { 0f, 1.5f, 7f, 12.5f, 13.999f })
        {
            stage.SetPass(age); position = stage.LocalCenter; rotation = stage.transform.localRotation; opening = stage.FoldOpening;
            DisplaySettings.SetReducedEffectsForTest(true); stage.SetPass(age);
            Assert.AreEqual(position, stage.LocalCenter); Assert.AreEqual(rotation, stage.transform.localRotation);
            Assert.AreEqual(opening, stage.FoldOpening, "入退場に必要な折畳みを縮めない");
            DisplaySettings.SetReducedEffectsForTest(false);
        }
    }

    [Test]
    public void RepeatedClockAndSeekingReproduceTheSamePoseWithoutAPlaybackHistory()
    {
        stage.SetPass(7); var middle = mesh.vertices; var normals = mesh.normals;
        Vector3 position = stage.LocalCenter; Quaternion rotation = stage.transform.localRotation;
        stage.SetPass(7); CollectionAssert.AreEqual(middle, mesh.vertices);
        stage.SetPass(12); stage.SetPass(2); stage.Clear(); stage.SetPass(7);
        CollectionAssert.AreEqual(middle, mesh.vertices); CollectionAssert.AreEqual(normals, mesh.normals);
        Assert.AreEqual(position, stage.LocalCenter); Assert.AreEqual(rotation, stage.transform.localRotation);
        Assert.AreEqual(stage.transform.TransformPoint(Vector3.zero), stage.WorldCenter);
    }

    [Test]
    public void InvalidAgeDisableAndClearHideThePassageWithoutReplayingItsPreviousPose()
    {
        foreach (float age in new[] { -1f, 14f, 15f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            stage.SetPass(7); stage.SetPass(age); AssertHidden();
        }
        stage.SetPass(7); stage.enabled = false; AssertHidden();
        stage.SetPass(7); AssertHidden(); stage.enabled = true; AssertHidden();
        stage.SetPass(7); root.SetActive(false); AssertHidden(); root.SetActive(true); AssertHidden();
        stage.SetPass(7); stage.Clear(); stage.Clear(); AssertHidden();
        stage.SetPass(7); Assert.IsTrue(stage.IsVisible);
    }

    [Test]
    public void RepeatedPosesReuseOneOwnedMeshMaterialAndDestroyReleasesOnlyThoseResources()
    {
        Assert.IsNull(AbyssalPassageStage.Create(null, floor.floorY));
        Assert.IsNull(AbyssalPassageStage.Create(root.transform, float.NaN));
        Assert.AreSame(stage, AbyssalPassageStage.Create(root.transform, floor.floorY));
        Assert.AreEqual(1, stage.GetComponentsInChildren<MeshFilter>().Length);
        Assert.AreEqual(1, stage.GetComponentsInChildren<Renderer>().Length);
        Assert.That(stage.VertexCount, Is.InRange(1, 1024));
        Assert.Greater(stage.BodyVertexCount, 0); Assert.Less(stage.BodyVertexCount, stage.VertexCount);
        Assert.IsEmpty(stage.GetComponentsInChildren<Collider>());
        Assert.IsEmpty(stage.GetComponentsInChildren<Rigidbody>());
        Assert.IsEmpty(stage.GetComponentsInChildren<ParticleSystem>());
        Assert.IsEmpty(stage.GetComponentsInChildren<Light>());
        int count = mesh.vertexCount;
        for (int i = 0; i < 100; i++)
        {
            stage.SetPass(i % 15);
            Assert.AreEqual(count, mesh.vertexCount);
            Assert.AreSame(mesh, stage.GetComponent<MeshFilter>().sharedMesh);
            Assert.AreSame(material, stage.GetComponent<Renderer>().sharedMaterial);
        }
        stage.Clear(); Assert.AreEqual(count, mesh.vertexCount);
        Object.DestroyImmediate(stage.gameObject);
        Assert.IsTrue(mesh == null); Assert.IsTrue(material == null);
        Assert.IsTrue(otherMeshes.All(m => m != null)); Assert.IsTrue(otherMaterials.All(m => m != null));
        Object.DestroyImmediate(root); root = null;
        Assert.IsTrue(otherMeshes.All(m => m == null)); Assert.IsTrue(otherMaterials.All(m => m == null));
    }

    bool IntersectsPillarBeforePoint(Vector3 eye, Vector3 point, float pillarZ)
    {
        Vector3 ray = point - eye;
        Vector2 d = new Vector2(ray.x, ray.z), offset = new Vector2(eye.x + 8.2f, eye.z - pillarZ);
        // 最上部半径 .6*.84 の十二角柱へ内接する円より小さい保守値。
        const float radius = .486f;
        float a = Vector2.Dot(d, d), b = 2 * Vector2.Dot(offset, d), c = offset.sqrMagnitude - radius * radius;
        float discriminant = b * b - 4 * a * c;
        if (discriminant < 0) return false;
        float t = (-b - Mathf.Sqrt(discriminant)) / (2 * a);
        float y = eye.y + ray.y * t;
        return t > 0 && t < 1 && y > floor.floorY && y < floor.floorY + 4.7f;
    }

    void AssertHidden()
    {
        Assert.IsFalse(stage.IsVisible); Assert.AreEqual(-1, stage.PassAge); Assert.AreEqual(0, stage.FoldOpening);
        Assert.IsFalse(stage.GetComponent<Renderer>().enabled);
    }
    static Vector2 XY(Vector3 value) => new Vector2(value.x, value.y);
    static void AssertFinite(Vector3 value) => Assert.IsFalse(float.IsNaN(value.sqrMagnitude) || float.IsInfinity(value.sqrMagnitude));
}
