using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 着座する主片と、接触後の爪の反動を分けて確認する。生成順ではなく静止位置で部品を選ぶ。
public class LatchResponseTests
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
        root = new GameObject("LatchResponseTest");
        response = StageThemeResponse.Create(root.transform, StageTheme.ObsidianRelay, Floor);
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
    static float LaneZ(int lane) => lane == 0 || lane == 3 ? 4.4f : 9.4f;

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void SelectedLatchSeatsAtItsFixedStopThenReturnsWithoutMovingTheTrayOrAnotherLane(int lane)
    {
        response.Tick(5);
        var rest = surface.vertices; var marks = details.vertices; var colors = details.colors;
        var slug = SlugIndices(rest, lane); var paws = PawIndices(rest, lane);
        response.OnPerfect(lane);
        response.Tick(5.08);
        AssertSlugTravel(rest, surface.vertices, slug, lane, .324f);
        AssertUnchangedExcept(rest, surface.vertices, slug);
        response.Tick(5.12);
        AssertSlugTravel(rest, surface.vertices, slug, lane, .36f);
        AssertUnchangedExcept(rest, surface.vertices, slug);
        AssertContact(surface.vertices, slug);
        AssertMarkFollows(marks, details.vertices, lane, .36f);
        CollectionAssert.AreEqual(colors, details.colors, "接触に追加フラッシュを付けない");
        response.Tick(5.20);
        AssertContact(surface.vertices, slug);
        var peak = surface.vertices;
        Assert.Greater(Travel(rest, peak, paws), .01f, "接触後に同じ装置の爪だけが一度反動する");
        var moving = new HashSet<int>(slug); moving.UnionWith(paws);
        AssertUnchangedExcept(rest, peak, moving);
        response.Tick(5.32);
        AssertContact(surface.vertices, slug);
        Assert.Less(Travel(rest, surface.vertices, paws), Travel(rest, peak, paws));
        response.Tick(5.34);
        foreach (int index in paws) Assert.That(Vector3.Distance(rest[index], surface.vertices[index]), Is.LessThan(.00001f));
        response.Tick(5.51);
        AssertSlugTravel(rest, surface.vertices, slug, lane, .18f);
        response.Tick(5.701);
        Assert.AreEqual(0, response.ActiveResponseCount); Assert.AreEqual(0, response.ActiveLaneMask);
        CollectionAssert.AreEqual(rest, surface.vertices);
        CollectionAssert.AreEqual(marks, details.vertices);
        CollectionAssert.AreEqual(colors, details.colors);
        foreach (var point in peak)
        {
            Assert.Greater(Mathf.Abs(point.x), 6f);
            Assert.IsFalse(float.IsNaN(point.sqrMagnitude) || float.IsInfinity(point.sqrMagnitude));
        }
    }

    [TestCase(false)] [TestCase(true)]
    public void LowKeepsPrimaryContactAndOnlyReducesPawMotionAndDetailAlpha(bool projector)
    {
        DisplaySettings.SetProjectorModeForTest(projector);
        response.Tick(1);
        var rest = surface.vertices; var slug = SlugIndices(rest, 2); var paws = PawIndices(rest, 2);
        response.OnPerfect(2); response.Tick(1.20);
        var full = surface.vertices; var fullMarks = details.vertices; var fullColors = details.colors;
        DisplaySettings.SetReducedEffectsForTest(true); response.Tick(1.20);
        var low = surface.vertices; var lowColors = details.colors;
        AssertContact(full, slug); AssertContact(low, slug);
        foreach (int index in slug) Assert.AreEqual(full[index], low[index], "LOWでも主片を受け口まで運ぶ");
        Assert.That(Travel(rest, low, paws) / Travel(rest, full, paws), Is.InRange(.297f, .303f));
        // 回転角を30%にするので、頂点の弦長比は丸め誤差を含む狭い範囲で確認する。
        var moving = new HashSet<int>(slug); moving.UnionWith(paws);
        AssertUnchangedExcept(rest, low, moving);
        CollectionAssert.AreEqual(fullMarks, details.vertices, "LOWで刻線の幅と主片への追従を変えない");
        Assert.AreEqual(fullColors.Length, lowColors.Length);
        for (int i = 0; i < fullColors.Length; i++)
        {
            Assert.AreEqual(fullColors[i].r, lowColors[i].r);
            Assert.AreEqual(fullColors[i].g, lowColors[i].g);
            Assert.AreEqual(fullColors[i].b, lowColors[i].b);
            Assert.AreEqual(fullColors[i].a * .3f, lowColors[i].a, .00001f);
        }
        Assert.AreEqual(4, response.ActiveLaneMask);
        DisplaySettings.SetReducedEffectsForTest(false); response.Tick(1.20);
        CollectionAssert.AreEqual(full, surface.vertices);
        CollectionAssert.AreEqual(fullColors, details.colors);
        DisplaySettings.SetReducedEffectsForTest(true); response.Tick(1.701);
        Assert.AreEqual(0, response.ActiveResponseCount);
        CollectionAssert.AreEqual(rest, surface.vertices, "LOWでも同じ0.70秒で帰還を完了する");
    }

    [Test]
    public void RapidPerfectsFinishTheFirstCycleWhileAnotherLaneCanStartIndependently()
    {
        var reference = StageThemeResponse.Create(root.transform, StageTheme.ObsidianRelay, Floor);
        var referenceSurface = MeshOf(reference, "Surface");
        response.Tick(1); reference.Tick(1); response.OnPerfect(0); reference.OnPerfect(0);
        for (int hit = 1; hit <= 5; hit++)
        {
            double now = 1 + hit * .125;
            response.Tick(now); reference.Tick(now);
            response.OnPerfect(0); response.Tick(now);
            if (hit == 2) { response.OnPerfect(1); reference.OnPerfect(1); }
            CollectionAssert.AreEqual(referenceSurface.vertices, surface.vertices, "同列125ms連打で位相を巻き戻さない");
        }
        response.Tick(1.701); reference.Tick(1.701);
        Assert.AreEqual(2, response.ActiveLaneMask, "別列の周期を最初の列へ合わせて短縮しない");
        CollectionAssert.AreEqual(referenceSurface.vertices, surface.vertices);
        response.Tick(1.951); Assert.AreEqual(0, response.ActiveResponseCount);
        response.Tick(3); Assert.AreEqual(0, response.ActiveResponseCount, "稼働中の入力を遅延再生しない");
        response.OnPerfect(0); response.Tick(3.12);
        Assert.AreEqual(1, response.ActiveLaneMask);
        AssertContact(surface.vertices, SlugIndices(referenceSurface.vertices, 0));
    }

    [Test]
    public void FreezeInvalidTimeRewindAndClearRestoreExactRestGeometry()
    {
        var rest = surface.vertices; var marks = details.vertices;
        response.OnPerfect(3); response.Tick(100);
        Assert.AreEqual(8, response.ActiveLaneMask, "初回Tick前の成功を絶対時刻で失効させない");
        response.Tick(100.2); var peak = surface.vertices; var peakMarks = details.vertices;
        foreach (var now in new[] { 100.2, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            response.Tick(now);
            CollectionAssert.AreEqual(peak, surface.vertices);
            CollectionAssert.AreEqual(peakMarks, details.vertices);
        }
        Assert.AreEqual(100.2, response.LastTickSeconds);
        response.Tick(2); AssertRest(rest, marks);
        response.OnPerfect(0); response.Tick(2.2); response.Clear(); AssertRest(rest, marks);
        response.Tick(4); response.OnPerfect(1); response.Tick(4.2);
        response.enabled = false;
        Assert.AreEqual(0, response.ActiveResponseCount);
        response.enabled = true; AssertRest(rest, marks);
        response.Tick(6); response.OnPerfect(2); response.Tick(double.MaxValue);
        AssertRest(rest, marks);
    }

    [Test]
    public void AllFourSimultaneousPerfectsKeepSeparateContactsAndFixedResourceLimits()
    {
        response.Tick(1); var rest = surface.vertices;
        var meshes = response.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh).ToArray();
        var materials = response.GetComponentsInChildren<MeshRenderer>().Select(r => r.sharedMaterial).ToArray();
        Assert.AreEqual(2, meshes.Length); Assert.AreEqual(2, materials.Length);
        for (int lane = 0; lane < 4; lane++) response.OnPerfect(lane);
        response.Tick(1.12);
        Assert.AreEqual(15, response.ActiveLaneMask); Assert.AreEqual(4, response.ActiveResponseCount);
        for (int lane = 0; lane < 4; lane++) AssertContact(surface.vertices, SlugIndices(rest, lane));
        Assert.IsEmpty(response.GetComponentsInChildren<Collider>());
        Assert.IsEmpty(response.GetComponentsInChildren<Rigidbody>());
        Assert.IsEmpty(response.GetComponentsInChildren<Light>());
        Assert.IsEmpty(response.GetComponentsInChildren<ParticleSystem>());
        foreach (var mesh in meshes) Assert.LessOrEqual(mesh.vertexCount, StageThemeResponse.VertexBudget);
        Object.DestroyImmediate(response.gameObject);
        Assert.IsTrue(meshes.All(mesh => mesh == null));
        Assert.IsTrue(materials.All(material => material == null));
    }

    void AssertRest(Vector3[] rest, Vector3[] marks)
    {
        Assert.AreEqual(0, response.ActiveResponseCount); Assert.AreEqual(0, response.ActiveLaneMask);
        CollectionAssert.AreEqual(rest, surface.vertices);
        CollectionAssert.AreEqual(marks, details.vertices);
    }

    static HashSet<int> SlugIndices(Vector3[] rest, int lane)
    {
        var found = new HashSet<int>();
        for (int i = 0; i < rest.Length; i++)
        {
            var point = rest[i];
            if (Mathf.Sign(point.x) != (lane < 2 ? -1 : 1) || Mathf.Abs(point.z - LaneZ(lane)) > .201f) continue;
            if (Mathf.Abs(Mathf.Abs(point.x) - 6.36f) <= .181f && Mathf.Abs(point.y - (Floor + 1.065f)) <= .141f)
                found.Add(i);
        }
        Assert.AreEqual(36, found.Count, "静止時の金属片の六面を位置から識別する");
        return found;
    }

    static HashSet<int> PawIndices(Vector3[] rest, int lane)
    {
        var found = new HashSet<int>();
        for (int i = 0; i < rest.Length; i++)
        {
            var point = rest[i]; float dz = Mathf.Abs(point.z - LaneZ(lane));
            if (Mathf.Sign(point.x) != (lane < 2 ? -1 : 1) || dz < .224f || dz > .276f) continue;
            if (Mathf.Abs(point.x) >= 6.759f && Mathf.Abs(point.x) <= 7.041f &&
                point.y >= Floor + .969f && point.y <= Floor + 1.191f) found.Add(i);
        }
        Assert.AreEqual(72, found.Count, "同じ装置内の前後の二爪を位置から識別する");
        return found;
    }

    static void AssertUnchangedExcept(Vector3[] rest, Vector3[] current, HashSet<int> moving)
    {
        Assert.AreEqual(rest.Length, current.Length);
        for (int i = 0; i < rest.Length; i++)
            if (!moving.Contains(i)) Assert.That(Vector3.Distance(rest[i], current[i]), Is.LessThan(.00001f),
                "台座・ガイド・固定接触面と別列は静止する");
    }

    static void AssertSlugTravel(Vector3[] rest, Vector3[] current, HashSet<int> slug, int lane, float distance)
    {
        float side = lane < 2 ? -1 : 1;
        foreach (int index in slug)
        {
            Assert.AreEqual(distance, (current[index].x - rest[index].x) * side, .00002f);
            Assert.AreEqual(rest[index].y, current[index].y); Assert.AreEqual(rest[index].z, current[index].z);
        }
    }

    static void AssertContact(Vector3[] points, HashSet<int> slug)
    {
        float outer = slug.Max(index => Mathf.Abs(points[index].x));
        float centerZ = (slug.Max(index => points[index].z) + slug.Min(index => points[index].z)) * .5f;
        float side = Mathf.Sign(points[slug.First()].x);
        var receiver = points.Where(point => Mathf.Sign(point.x) == side &&
            Mathf.Abs(Mathf.Abs(point.z - centerZ) - .23f) < .00002f &&
            Mathf.Abs(point.x) >= 6.899f && Mathf.Abs(point.x) <= 7.121f &&
            point.y >= Floor + .924f && point.y <= Floor + 1.206f).ToArray();
        Assert.GreaterOrEqual(receiver.Length, 4, "主片とは別に固定ストッパの接触面がある");
        Assert.AreEqual(6.90f, outer, .00002f);
        Assert.AreEqual(receiver.Min(point => Mathf.Abs(point.x)), outer, .00002f,
            "主片の外面を固定ストッパの内面へ接触させる");
    }

    static float Travel(Vector3[] rest, Vector3[] current, HashSet<int> selected) =>
        selected.Sum(index => Vector3.Distance(rest[index], current[index]));

    static void AssertMarkFollows(Vector3[] rest, Vector3[] current, int lane, float distance)
    {
        Assert.AreEqual(rest.Length, current.Length); int moved = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            bool target = Mathf.Sign(rest[i].x) == (lane < 2 ? -1 : 1) && Mathf.Abs(rest[i].z - LaneZ(lane)) < .02f;
            Assert.AreEqual(target ? distance : 0, Mathf.Abs(current[i].x - rest[i].x), .00002f);
            Assert.AreEqual(rest[i].y, current[i].y); Assert.AreEqual(rest[i].z, current[i].z);
            if (target) moved++;
        }
        Assert.AreEqual(4, moved, "対象主片の刻線だけを動かす");
    }
}
