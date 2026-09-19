using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 発光の空メッシュではなく、根を残した珊瑚の形・時刻・設定差を確認する。
public class CoralResponseTests
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
        root = new GameObject("CoralResponseTest");
        response = StageThemeResponse.Create(root.transform, StageTheme.AbyssalRuins, Floor);
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
    static float LaneZ(int lane) => lane == 0 || lane == 3 ? 7.4f : 18.4f;
    static bool InLane(Vector3 point, int lane) => Mathf.Sign(point.x) == (lane < 2 ? -1 : 1) &&
        Mathf.Abs(point.z - LaneZ(lane)) < 1f;

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void SelectedCoralOpensHoldsAndClosesWithFixedRootsAndOtherLanes(int lane)
    {
        response.Tick(5); var rest = surface.vertices;
        var tips = HighestTips(rest, lane); var fixedParts = FixedParts(rest, lane);
        response.OnPerfect(lane); response.Tick(5);
        CollectionAssert.AreEqual(rest, surface.vertices, "成功の瞬間に基礎形状を飛ばさない");
        response.Tick(5.125); AssertTipOpening(rest, surface.vertices, tips, .5f);
        response.Tick(5.25); var open = surface.vertices;
        AssertTipOpening(rest, open, tips, 1);
        int changed = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            if (!InLane(rest[i], lane) || fixedParts.Contains(i))
                Assert.AreEqual(rest[i], open[i], "固定根・枝・非対象列を動かさない");
            if (Vector3.Distance(rest[i], open[i]) > .00001f) changed++;
        }
        Assert.Greater(changed, 400, "対象群の触手が実際にしなる");
        response.Tick(5.5); CollectionAssert.AreEqual(open, surface.vertices, "開いた形を短く保持する");
        response.Tick(5.9); AssertTipOpening(rest, surface.vertices, tips, .5f);
        response.Tick(6.301); AssertRest(rest);
        Assert.AreEqual(0, details.vertexCount, "開閉中も終端も祝福光を生成しない");
        foreach (var point in open)
        {
            Assert.That(Mathf.Abs(point.x), Is.InRange(6.8f, 8.3f));
            Assert.Less(point.y, Floor + 1.16f);
            Assert.IsFalse(float.IsNaN(point.sqrMagnitude) || float.IsInfinity(point.sqrMagnitude));
        }
    }

    [TestCase(false)] [TestCase(true)]
    public void LowReducesOnlyAdditionalOpeningAndPreservesRootsThicknessAndLifetime(bool projector)
    {
        DisplaySettings.SetProjectorModeForTest(projector);
        response.Tick(1); var rest = surface.vertices;
        var tips = HighestTips(rest, 2); var fixedParts = FixedParts(rest, 2);
        var triangles = surface.triangles;
        response.OnPerfect(2); response.Tick(1.25); var full = surface.vertices;
        DisplaySettings.SetReducedEffectsForTest(true); response.Tick(1.25); var low = surface.vertices;
        AssertTipOpening(rest, full, tips, 1); AssertTipOpening(rest, low, tips, .3f);
        foreach (int i in fixedParts) Assert.AreEqual(rest[i], low[i]);
        foreach (var ring in tips)
        {
            var baseCenter = Center(rest, ring); var fullCenter = Center(full, ring); var lowCenter = Center(low, ring);
            Assert.That(Vector3.Distance(lowCenter - baseCenter, (fullCenter - baseCenter) * .3f), Is.LessThan(.00002f));
            foreach (int i in ring)
            {
                Assert.AreEqual(.018f, Vector3.Distance(full[i], fullCenter), .00002f, "通常の先端の肉厚を維持する");
                Assert.AreEqual(.018f, Vector3.Distance(low[i], lowCenter), .00002f, "LOWで触手を細くしない");
            }
        }
        CollectionAssert.AreEqual(triangles, surface.triangles);
        Assert.AreEqual(0, details.vertexCount); Assert.AreEqual(4, response.ActiveLaneMask);
        DisplaySettings.SetReducedEffectsForTest(false); response.Tick(1.25);
        CollectionAssert.AreEqual(full, surface.vertices, "途中の設定切替で周期をやり直さない");
        DisplaySettings.SetReducedEffectsForTest(true); response.Tick(2.301); AssertRest(rest);
    }

    [Test]
    public void RapidPerfectsFinishFirstCycleWithoutRestartExtensionOrQueuedReplay()
    {
        var reference = StageThemeResponse.Create(root.transform, StageTheme.AbyssalRuins, Floor);
        var referenceSurface = MeshOf(reference, "Surface");
        var rest = surface.vertices;
        response.Tick(1); reference.Tick(1); response.OnPerfect(0); reference.OnPerfect(0);
        for (int hit = 1; hit <= 10; hit++)
        {
            double now = 1 + hit * .125;
            response.Tick(now); reference.Tick(now);
            response.OnPerfect(0); response.Tick(now);
            if (hit == 2) { response.OnPerfect(1); reference.OnPerfect(1); }
            CollectionAssert.AreEqual(referenceSurface.vertices, surface.vertices, "125ms連打でも最初の周期を完走する");
        }
        response.Tick(2.301); reference.Tick(2.301);
        Assert.AreEqual(2, response.ActiveLaneMask, "別列の開始時刻は独立する");
        CollectionAssert.AreEqual(referenceSurface.vertices, surface.vertices);
        response.Tick(2.551); AssertRest(rest);
        response.Tick(4); AssertRest(rest);
        response.OnPerfect(0); response.Tick(4.25);
        Assert.AreEqual(1, response.ActiveLaneMask);
        AssertTipOpening(rest, surface.vertices, HighestTips(rest, 0), 1);
    }

    [Test]
    public void FrozenInvalidAndRewoundClockClearAndDisableKeepExactRestBoundaries()
    {
        var rest = surface.vertices;
        response.OnPerfect(3); response.Tick(100);
        Assert.AreEqual(8, response.ActiveLaneMask, "最初のTickの絶対曲時刻で失効させない");
        response.Tick(100.25); var open = surface.vertices;
        foreach (var now in new[] { 100.25, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            response.Tick(now); CollectionAssert.AreEqual(open, surface.vertices);
        }
        Assert.AreEqual(100.25, response.LastTickSeconds);
        response.Tick(2); AssertRest(rest);
        response.OnPerfect(0); response.Tick(2.25); response.Clear(); AssertRest(rest);
        response.Tick(4); response.OnPerfect(1); response.Tick(4.25);
        response.enabled = false; Assert.AreEqual(0, response.ActiveResponseCount);
        response.enabled = true; AssertRest(rest);
        response.Tick(6); response.OnPerfect(2); response.Tick(double.MaxValue); AssertRest(rest);
        response.Tick(-1); AssertRest(rest);
    }

    [Test]
    public void FourSimultaneousCoralsStayNonEmissiveWithinFixedOwnedResources()
    {
        response.Tick(1); var rest = surface.vertices;
        var filters = response.GetComponentsInChildren<MeshFilter>();
        var renderers = response.GetComponentsInChildren<MeshRenderer>();
        var meshes = filters.Select(f => f.sharedMesh).ToArray();
        var materials = renderers.Select(r => r.sharedMaterial).ToArray();
        Assert.AreEqual(2, meshes.Length); Assert.AreEqual(2, materials.Length);
        Assert.Greater(surface.vertexCount, 0); Assert.LessOrEqual(surface.vertexCount, StageThemeResponse.VertexBudget);
        for (int lane = 0; lane < 4; lane++) response.OnPerfect(lane);
        response.Tick(1.25);
        Assert.AreEqual(15, response.ActiveLaneMask); Assert.AreEqual(4, response.ActiveResponseCount);
        for (int lane = 0; lane < 4; lane++) AssertTipOpening(rest, surface.vertices, HighestTips(rest, lane), 1);
        Assert.AreEqual(0, details.vertexCount);
        Assert.IsFalse(response.transform.Find("Details").GetComponent<MeshRenderer>().enabled);
        Assert.AreEqual(0, response.transform.Find("Surface").GetComponent<MeshRenderer>().sharedMaterial.GetFloat("_Emission"));
        Assert.IsEmpty(response.GetComponentsInChildren<Light>());
        Assert.IsEmpty(response.GetComponentsInChildren<ParticleSystem>());
        Assert.IsEmpty(response.GetComponentsInChildren<Rigidbody>());
        Assert.IsEmpty(response.GetComponentsInChildren<Collider>());
        for (int i = 0; i < 2; i++)
        {
            Assert.AreSame(meshes[i], filters[i].sharedMesh); Assert.AreSame(materials[i], renderers[i].sharedMaterial);
        }
        Object.DestroyImmediate(response.gameObject);
        Assert.IsTrue(meshes.All(mesh => mesh == null)); Assert.IsTrue(materials.All(material => material == null));
    }

    void AssertRest(Vector3[] rest)
    {
        Assert.AreEqual(0, response.ActiveResponseCount); Assert.AreEqual(0, response.ActiveLaneMask);
        CollectionAssert.AreEqual(rest, surface.vertices); Assert.AreEqual(0, details.vertexCount);
    }

    // 最上段の触手端を位置で六つに分ける。三角面の生成順には依存しない。
    static List<int[]> HighestTips(Vector3[] rest, int lane)
    {
        var remaining = new HashSet<int>(Enumerable.Range(0, rest.Length).Where(i =>
            InLane(rest[i], lane) && rest[i].y > Floor + 1.10f));
        var result = new List<int[]>();
        while (remaining.Count > 0)
        {
            int seed = remaining.First();
            int[] ring = remaining.Where(i => Vector3.Distance(rest[i], rest[seed]) < .0361f).ToArray();
            Assert.AreEqual(12, ring.Length, "一つの先端断面とキャップを位置から特定する");
            result.Add(ring); remaining.ExceptWith(ring);
        }
        Assert.AreEqual(6, result.Count, "最上段の六本を確認できる");
        return result;
    }

    static HashSet<int> FixedParts(Vector3[] rest, int lane)
    {
        float side = lane < 2 ? -1 : 1;
        var anchor = new Vector3(side * 7.55f, Floor - .34f, LaneZ(lane));
        var centers = new[] { anchor + new Vector3(-side * .20f, .80f, -.20f),
            anchor + new Vector3(side * .22f, 1.03f, .04f), anchor + new Vector3(side * .02f, .69f, .29f) };
        var fixedParts = new HashSet<int>();
        for (int i = 0; i < rest.Length; i++)
        {
            if (!InLane(rest[i], lane)) continue;
            if (rest[i].y < Floor + .30f) fixedParts.Add(i);
            foreach (var center in centers)
            {
                Vector3 delta = rest[i] - center;
                if (delta.y >= -.01f && delta.y <= .06f && new Vector2(delta.x, delta.z).magnitude < .19f)
                    fixedParts.Add(i);
            }
        }
        Assert.Greater(fixedParts.Count, 150, "固定幹と各房の根元を空間から特定する");
        return fixedParts;
    }

    static Vector3 Center(Vector3[] vertices, int[] selected)
    {
        Vector3 value = Vector3.zero;
        foreach (int i in selected) value += vertices[i];
        return value / selected.Length;
    }

    static void AssertTipOpening(Vector3[] rest, Vector3[] current, List<int[]> tips, float opening)
    {
        foreach (var tip in tips)
        {
            Vector3 travel = Center(current, tip) - Center(rest, tip);
            Assert.AreEqual(-.08f * opening, travel.y, .00002f, "先端が一度開いて自然な姿勢へ戻る");
            Assert.AreEqual(.30f * opening, new Vector2(travel.x, travel.z).magnitude, .00002f);
        }
    }
}
