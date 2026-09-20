using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 実ノーツの判定条件は共有Playテストへ任せ、支持された羽根の形と一周期を確認する。
public class WindVaneResponseTests
{
    const float Floor = -2.5f;
    GameObject root;
    StageThemeResponse response;
    Mesh surface, details;

    [SetUp]
    public void Setup()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        DisplaySettings.SetProjectorModeForTest(false);
        root = new GameObject("WindVaneResponseTest");
        response = StageThemeResponse.Create(root.transform, StageTheme.SkySanctuary, Floor);
        Assert.NotNull(response);
        surface = MeshOf(response, "Surface"); details = MeshOf(response, "Details");
    }

    [TearDown]
    public void Cleanup()
    {
        if (root != null) Object.DestroyImmediate(root);
        DisplaySettings.ResetReducedEffectsCacheForTest();
        DisplaySettings.ResetProjectorModeCacheForTest();
    }

    static Mesh MeshOf(StageThemeResponse value, string name) =>
        value.transform.Find(name).GetComponent<MeshFilter>().sharedMesh;

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void OneVaneTurnsAsASolidAroundItsFixedShaftAndKeepsOtherLanesStill(int lane)
    {
        response.Tick(5);
        var rest = surface.vertices; var originalNormals = surface.normals;
        var topology = surface.triangles; var moving = MovingParts(rest, lane);
        var plate = Plate(rest, lane);
        Vector3 anchor = response.VaneAnchor(lane);
        Assert.AreEqual(new Vector3((lane < 2 ? -1 : 1) * 6.85f, Floor + 2.65f,
            lane == 0 || lane == 3 ? 11.10f : 20.10f), anchor);
        Assert.Greater(Enumerable.Range(0, rest.Length).Count(i => InLane(rest[i], lane) &&
            !moving.Contains(i) && (rest[i].y < anchor.y - .29f || rest[i].y > anchor.y + .49f)), 100,
            "羽根だけでなく支点の上下と取付腕の固定形を持つ");

        response.OnPerfect(lane); response.Tick(5);
        CollectionAssert.AreEqual(rest, surface.vertices, "待機35度から予備動作や瞬間移動を足さない");
        response.Tick(5.10);
        AssertTurn(rest, originalNormals, surface.vertices, surface.normals, moving, lane, 15);
        response.Tick(5.20); var peak = surface.vertices;
        AssertTurn(rest, originalNormals, peak, surface.normals, moving, lane, 30);
        AssertPlateDimensions(peak, plate, lane, 65);
        Assert.AreEqual(1 << lane, response.ActiveLaneMask);
        response.Tick(5.30); CollectionAssert.AreEqual(peak, surface.vertices, "単一の最大姿勢を短く保持する");
        response.Tick(5.625);
        AssertTurn(rest, originalNormals, surface.vertices, surface.normals, moving, lane, 15);
        response.Tick(5.951);
        Assert.AreEqual(0, response.ActiveResponseCount);
        CollectionAssert.AreEqual(rest, surface.vertices);
        CollectionAssert.AreEqual(originalNormals, surface.normals);
        CollectionAssert.AreEqual(topology, surface.triangles);
        Assert.AreEqual(0, details.vertexCount);
    }

    [TestCase(false)] [TestCase(true)]
    public void LowScalesOnlyTheExtraAngleAndKeepsThicknessSupportAndCycle(bool projector)
    {
        DisplaySettings.SetProjectorModeForTest(projector);
        response.Tick(1);
        var rest = surface.vertices; var originalNormals = surface.normals;
        var moving = MovingParts(rest, 2); var plate = Plate(rest, 2);
        response.OnPerfect(2); response.Tick(1.25); var full = surface.vertices;
        AssertTurn(rest, originalNormals, full, surface.normals, moving, 2, 30);
        DisplaySettings.SetReducedEffectsForTest(true); response.Tick(1.25);
        AssertTurn(rest, originalNormals, surface.vertices, surface.normals, moving, 2, 9);
        AssertPlateDimensions(surface.vertices, plate, 2, 44);
        Assert.AreEqual(4, response.ActiveLaneMask);
        Assert.AreEqual(0, details.vertexCount);
        Assert.AreEqual(0, response.transform.Find("Surface").GetComponent<MeshRenderer>().sharedMaterial.GetFloat("_Emission"));
        DisplaySettings.SetReducedEffectsForTest(false); response.Tick(1.25);
        CollectionAssert.AreEqual(full, surface.vertices, "途中の設定変更で位相を再開始しない");
        DisplaySettings.SetReducedEffectsForTest(true); response.Tick(1.951);
        Assert.AreEqual(0, response.ActiveResponseCount);
        CollectionAssert.AreEqual(rest, surface.vertices, "LOWも0.95秒で同じ基礎角へ戻る");
    }

    [Test]
    public void RapidPerfectsDoNotRestartOrQueueWhileTheOtherLaneKeepsItsOwnStart()
    {
        var reference = StageThemeResponse.Create(root.transform, StageTheme.SkySanctuary, Floor);
        var referenceMesh = MeshOf(reference, "Surface");
        var rest = surface.vertices;
        response.Tick(1); reference.Tick(1); response.OnPerfect(0); reference.OnPerfect(0);
        for (int hit = 1; hit <= 7; hit++)
        {
            double now = 1 + hit * .125;
            response.Tick(now); reference.Tick(now);
            response.OnPerfect(0); response.Tick(now);
            if (hit == 2) { response.OnPerfect(1); reference.OnPerfect(1); }
            CollectionAssert.AreEqual(referenceMesh.vertices, surface.vertices,
                "同列125ms連打は最初の回頭を引き延ばさない");
        }
        response.Tick(1.951); reference.Tick(1.951);
        Assert.AreEqual(2, response.ActiveLaneMask);
        CollectionAssert.AreEqual(referenceMesh.vertices, surface.vertices);
        response.Tick(2.201);
        Assert.AreEqual(0, response.ActiveResponseCount); CollectionAssert.AreEqual(rest, surface.vertices);
        response.Tick(3); Assert.AreEqual(0, response.ActiveResponseCount, "入力をあとから予約再生しない");
    }

    [Test]
    public void FirstClockFreezeClearAndDisableRestoreTheNonEmissiveSolid()
    {
        var rest = surface.vertices; var originalNormals = surface.normals;
        response.OnPerfect(3); response.Tick(100); response.Tick(100.20);
        Assert.AreEqual(8, response.ActiveLaneMask);
        var peak = surface.vertices;
        foreach (var time in new[] { 100.20, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            response.Tick(time); CollectionAssert.AreEqual(peak, surface.vertices);
        }
        response.Tick(0); CollectionAssert.AreEqual(rest, surface.vertices);
        Assert.AreEqual(0, response.ActiveResponseCount);
        response.OnPerfect(0); response.Tick(0); response.Tick(.20);
        Assert.AreEqual(1, response.ActiveLaneMask, "同じ0秒を再評価して成功を捨てない");
        CollectionAssert.AreNotEqual(rest, surface.vertices);
        response.Clear(); CollectionAssert.AreEqual(rest, surface.vertices);
        response.OnPerfect(1); response.Tick(10); response.Tick(10.20);
        response.enabled = false; response.enabled = true;
        Assert.AreEqual(0, response.ActiveLaneMask);
        CollectionAssert.AreEqual(rest, surface.vertices);
        CollectionAssert.AreEqual(originalNormals, surface.normals);
        foreach (float age in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, .95f })
            Assert.AreEqual(0, StageThemeResponse.EvaluateVaneTurn(age));
    }

    [Test]
    public void FourVanesKeepFixedGeometryResourcesAndReleaseOnlyTheirOwnAssets()
    {
        var filters = response.GetComponentsInChildren<MeshFilter>();
        var renderers = response.GetComponentsInChildren<MeshRenderer>();
        var meshes = filters.Select(f => f.sharedMesh).ToArray();
        var materials = renderers.Select(r => r.sharedMaterial).ToArray();
        Assert.AreEqual(2, meshes.Length); Assert.AreEqual(2, materials.Length);
        Assert.AreEqual(2496, surface.vertexCount); Assert.LessOrEqual(surface.vertexCount, StageThemeResponse.VertexBudget);
        var sharedOutsideOwner = new Mesh { name = "UnrelatedTestMesh" };
        try
        {
            for (int cycle = 0; cycle < 8; cycle++)
            {
                double start = 2 * cycle;
                response.Tick(start);
                for (int lane = 0; lane < 4; lane++) response.OnPerfect(lane);
                response.Tick(start + .20); Assert.AreEqual(15, response.ActiveLaneMask);
                Assert.AreEqual(2496, surface.vertexCount); Assert.AreEqual(0, details.vertexCount);
                Assert.IsFalse(response.transform.Find("Details").GetComponent<MeshRenderer>().enabled);
                AssertBounds(surface.vertices);
                for (int i = 0; i < filters.Length; i++)
                {
                    Assert.AreSame(meshes[i], filters[i].sharedMesh);
                    Assert.AreSame(materials[i], renderers[i].sharedMaterial);
                }
                response.Tick(start + .951);
            }
            Object.DestroyImmediate(response.gameObject);
            Assert.IsTrue(meshes.All(mesh => mesh == null)); Assert.IsTrue(materials.All(material => material == null));
            Assert.IsTrue(sharedOutsideOwner != null);
        }
        finally { Object.DestroyImmediate(sharedOutsideOwner); }
    }

    bool InLane(Vector3 point, int lane) => Mathf.Sign(point.x) == (lane < 2 ? -1 : 1) &&
        Mathf.Abs(point.z - response.VaneAnchor(lane).z) < 2;

    // 外縁と浅い凸面の中心の深さから羽根を選び、三角形の生成順へ依存しない。
    HashSet<int> Plate(Vector3[] rest, int lane)
    {
        float side = lane < 2 ? -1 : 1;
        Vector3 hub = response.VaneAnchor(lane) + Vector3.up * .10f;
        Vector3 front = Quaternion.Euler(0, side * 35, 0) * Vector3.back;
        var result = new HashSet<int>(Enumerable.Range(0, rest.Length).Where(i => InLane(rest[i], lane) &&
            (Mathf.Abs(Mathf.Abs(Vector3.Dot(rest[i] - hub, front)) - .045f) < .00002f ||
             Mathf.Abs(Mathf.Abs(Vector3.Dot(rest[i] - hub, front)) - .095f) < .00002f)));
        Assert.AreEqual(192, result.Count, "閉じた長円板の前後面と縁が存在する");
        AssertPlateDimensions(rest, result, lane, 35);
        return result;
    }

    HashSet<int> MovingParts(Vector3[] rest, int lane)
    {
        var result = Plate(rest, lane);
        Vector3 hub = response.VaneAnchor(lane) + Vector3.up * .10f;
        Quaternion inverse = Quaternion.Inverse(Quaternion.Euler(0, (lane < 2 ? -1 : 1) * 35, 0));
        for (int i = 0; i < rest.Length; i++)
        {
            if (!InLane(rest[i], lane)) continue;
            Vector3 local = inverse * (rest[i] - hub);
            if (Mathf.Abs(Mathf.Abs(local.y) - .065f) < .00002f &&
                Mathf.Abs(Mathf.Abs(local.z) - .065f) < .00002f) result.Add(i);
        }
        Assert.AreEqual(228, result.Count, "一枚の羽根と軸へつながるハブだけが回頭する");
        return result;
    }

    void AssertTurn(Vector3[] rest, Vector3[] originalNormals, Vector3[] current, Vector3[] currentNormals,
        HashSet<int> moving, int lane, float degrees)
    {
        Vector3 anchor = response.VaneAnchor(lane);
        Quaternion turn = Quaternion.Euler(0, (lane < 2 ? -1 : 1) * degrees, 0);
        Assert.AreEqual(rest.Length, current.Length); AssertBounds(current);
        for (int i = 0; i < rest.Length; i++)
        {
            Vector3 expected = moving.Contains(i) ? anchor + turn * (rest[i] - anchor) : rest[i];
            Assert.That(Vector3.Distance(expected, current[i]), Is.LessThan(.00004f),
                "固定支点・他列は保持し、対象の羽根とハブだけを剛体回頭する");
            Vector3 expectedNormal = moving.Contains(i) ? turn * originalNormals[i] : originalNormals[i];
            Assert.That(Vector3.Distance(expectedNormal, currentNormals[i]), Is.LessThan(.00004f));
        }
    }

    void AssertPlateDimensions(Vector3[] vertices, HashSet<int> plate, int lane, float yawDegrees)
    {
        Quaternion inverse = Quaternion.Inverse(Quaternion.Euler(0, (lane < 2 ? -1 : 1) * yawDegrees, 0));
        Vector3 anchor = response.VaneAnchor(lane);
        Vector3 first = inverse * (vertices[plate.First()] - anchor);
        var bounds = new Bounds(first, Vector3.zero);
        foreach (int i in plate) bounds.Encapsulate(inverse * (vertices[i] - anchor));
        Assert.AreEqual(1.34f, bounds.size.x, .00004f);
        Assert.AreEqual(.76f, bounds.size.y, .00004f);
        Assert.AreEqual(.19f, bounds.size.z, .00004f, "設定や回頭中も浅い凸面の中心の厚みを保つ");
        int center = 0, rim = 0;
        foreach (int i in plate)
        {
            float depth = Mathf.Abs((inverse * (vertices[i] - anchor)).z);
            if (Mathf.Abs(depth - .095f) < .00002f) center++;
            else { Assert.AreEqual(.045f, depth, .00002f, "縁の厚みは増やさない"); rim++; }
        }
        Assert.AreEqual(32, center); Assert.AreEqual(160, rim);
    }

    static void AssertBounds(Vector3[] vertices)
    {
        foreach (var point in vertices)
        {
            Assert.Greater(Mathf.Abs(point.x), 6.6f);
            Assert.Less(point.y, Floor + 3.3f);
            Assert.IsFalse(float.IsNaN(point.sqrMagnitude) || float.IsInfinity(point.sqrMagnitude));
        }
    }
}
