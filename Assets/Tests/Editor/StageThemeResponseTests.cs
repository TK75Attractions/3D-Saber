using NUnit.Framework;
using UnityEngine;

public class StageThemeResponseTests
{
    GameObject root;
    [SetUp] public void Setup() { root = new GameObject("ThemeResponseTest"); }
    [TearDown] public void Cleanup() { if (root != null) Object.DestroyImmediate(root); }
    StageThemeResponse Create(StageTheme theme) => StageThemeResponse.Create(root.transform, theme, -2.5f);

    [Test]
    public void UnsupportedThemesOrInvalidFloorDoNotCreateOrReplaceAnything()
    {
        for (int i = 0; i < StageThemeCatalog.Count; i++)
            if (i != (int)StageTheme.MoonlitGarden && i != (int)StageTheme.AmberFoundry)
                Assert.IsNull(Create((StageTheme)i));
        Assert.IsNull(StageThemeResponse.Create(root.transform, StageTheme.MoonlitGarden, float.NaN));
        Assert.AreEqual(0, root.transform.childCount);
    }

    [TestCase(StageTheme.MoonlitGarden, 0)] [TestCase(StageTheme.MoonlitGarden, 1)]
    [TestCase(StageTheme.MoonlitGarden, 2)] [TestCase(StageTheme.MoonlitGarden, 3)]
    [TestCase(StageTheme.AmberFoundry, 0)] [TestCase(StageTheme.AmberFoundry, 1)]
    [TestCase(StageTheme.AmberFoundry, 2)] [TestCase(StageTheme.AmberFoundry, 3)]
    public void EachLaneOwnsOnlyItsResponseAndStaysOutsideTheCorridor(StageTheme theme, int lane)
    {
        var response = Create(theme);
        Assert.IsNotNull(response); Assert.IsTrue(response.ReplacesSideResponse);
        response.Tick(10); response.OnPerfect(lane); response.Tick(10.25);
        Assert.AreEqual(1, response.ActiveResponseCount);
        Assert.AreEqual(1 << lane, response.ActiveLaneMask);
        foreach (var filter in response.GetComponentsInChildren<MeshFilter>())
            foreach (var point in filter.sharedMesh.vertices)
            {
                Assert.Greater(Mathf.Abs(point.x), 6f);
                Assert.IsFalse(float.IsNaN(point.x) || float.IsInfinity(point.x));
                if (theme == StageTheme.MoonlitGarden)
                {
                    Assert.AreEqual(lane < 2 ? -1 : 1, Mathf.Sign(point.x));
                    Assert.Less(point.y, -2.2f, "葉と波紋をノーツ高さへ飛ばさない");
                }
            }
    }

    [TestCase(StageTheme.MoonlitGarden, StageThemeResponse.GardenLifetime)]
    [TestCase(StageTheme.AmberFoundry, StageThemeResponse.FoundryLifetime)]
    public void SongClockFreezesAndThenExpiresAtItsOwnLifetime(StageTheme theme, float lifetime)
    {
        var response = Create(theme); response.Tick(10); response.OnPerfect(0); response.Tick(10.2);
        var details = response.transform.Find("Details").GetComponent<MeshFilter>().sharedMesh;
        Vector3[] before = details.vertices;
        response.Tick(10.2); response.Tick(double.NaN); response.Tick(double.PositiveInfinity);
        CollectionAssert.AreEqual(before, details.vertices);
        Assert.AreEqual(10.2, response.LastTickSeconds);
        response.Tick(10 + lifetime - .001); Assert.AreEqual(1, response.ActiveResponseCount);
        response.Tick(10 + lifetime + .001); Assert.AreEqual(0, response.ActiveResponseCount);
        Assert.AreEqual(0, response.ActiveLaneMask);
        if (theme == StageTheme.MoonlitGarden) Assert.AreEqual(0, details.vertexCount);
    }

    [TestCase(StageTheme.MoonlitGarden)] [TestCase(StageTheme.AmberFoundry)]
    public void RewindClearAndDisableDoNotCarryOldSuccessIntoAnotherPlay(StageTheme theme)
    {
        var response = Create(theme); response.Tick(10); response.OnPerfect(1); response.OnPerfect(3);
        response.Tick(10.2); Assert.AreEqual(10, response.ActiveLaneMask);
        response.Tick(2); Assert.AreEqual(0, response.ActiveLaneMask);
        response.OnPerfect(0); response.Clear(); response.Tick(200);
        Assert.AreEqual(0, response.ActiveResponseCount);
        response.OnPerfect(2); response.Tick(200.1); response.enabled = false;
        Assert.AreEqual(0, response.ActiveResponseCount);
        foreach (var renderer in response.GetComponentsInChildren<MeshRenderer>()) Assert.IsFalse(renderer.enabled);
        response.OnPerfect(0); response.enabled = true; response.Tick(500);
        Assert.AreEqual(0, response.ActiveResponseCount);
    }

    [Test]
    public void InnerAndOuterGardenLanesHaveSeparatePoolLocations()
    {
        var response = Create(StageTheme.MoonlitGarden); response.Tick(1);
        response.OnPerfect(0); response.Tick(1.25);
        var leaves = response.transform.Find("Surface").GetComponent<MeshFilter>().sharedMesh;
        Bounds near = leaves.bounds;
        response.Clear(); response.Tick(1); response.OnPerfect(1); response.Tick(1.25);
        Bounds far = leaves.bounds;
        Assert.Less(near.max.z, far.min.z);
        Assert.Less(near.max.x, 0); Assert.Less(far.max.x, 0);
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void FoundryMovesOnlyTheSelectedGaugeAndReturnsToItsExactRestPose(int lane)
    {
        var response = Create(StageTheme.AmberFoundry); response.Tick(2);
        var details = response.transform.Find("Details").GetComponent<MeshFilter>().sharedMesh;
        Vector3[] rest = details.vertices;
        response.OnPerfect(lane); response.Tick(2.15);
        Vector3[] moved = details.vertices;
        Assert.AreEqual(rest.Length, moved.Length);
        int changes = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == moved[i]) continue;
            changes++;
            Assert.AreEqual(lane < 2 ? -1 : 1, Mathf.Sign(moved[i].x));
            Assert.That(moved[i].z, Is.InRange(lane == 0 || lane == 3 ? 5f : 14f,
                lane == 0 || lane == 3 ? 6f : 15.2f));
        }
        Assert.Greater(changes, 0, "選択列の針と弁が動く");
        response.Tick(3); CollectionAssert.AreEqual(rest, details.vertices);
    }

    [Test]
    public void AResponseBeforeTheFirstClockTickDoesNotExpireAgainstAbsoluteSongTime()
    {
        var response = Create(StageTheme.MoonlitGarden);
        response.OnPerfect(2); response.Tick(100);
        Assert.AreEqual(4, response.ActiveLaneMask);
        response.Tick(double.MaxValue);
        Assert.AreEqual(0, response.ActiveResponseCount);
        Assert.AreEqual(0, response.transform.Find("Details").GetComponent<MeshFilter>().sharedMesh.vertexCount);
    }

    [Test]
    public void RapidGardenPerfectsKeepTheFirstLeafLandingAndDoNotExtendItsLifetime()
    {
        var response = Create(StageTheme.MoonlitGarden); response.Tick(10); response.OnPerfect(0);
        response.Tick(10);
        var details = response.transform.Find("Details").GetComponent<MeshFilter>().sharedMesh;
        int leafOnlyVertices = details.vertexCount;
        for (int hit = 1; hit <= 9; hit++)
        {
            response.Tick(10 + hit * .125);
            response.OnPerfect(0);
            response.Tick(10 + hit * .125);
            Assert.AreEqual(1, response.ActiveResponseCount);
            if (hit == 2)
                Assert.Greater(details.vertexCount, leafOnlyVertices, "125ms連打中にも着水後の波紋を描く");
        }
        response.OnPerfect(1); // 別列は先行する葉の寿命から独立する。
        Assert.AreEqual(3, response.ActiveLaneMask);
        response.Tick(11.201);
        Assert.AreEqual(2, response.ActiveLaneMask, "同列の連打で最初の1.2秒を延長しない");
        response.Tick(12.326);
        Assert.AreEqual(0, response.ActiveResponseCount);
        Assert.AreEqual(0, details.vertexCount);
    }

    [TestCase(StageTheme.MoonlitGarden)] [TestCase(StageTheme.AmberFoundry)]
    public void DisablingThePreviewParentClearsResponsesAndInactiveDestructionReleasesResources(StageTheme theme)
    {
        var response = Create(theme); response.Tick(1); response.OnPerfect(0); response.Tick(1.25);
        var filters = response.GetComponentsInChildren<MeshFilter>();
        var renderers = response.GetComponentsInChildren<MeshRenderer>();
        var meshes = new[] { filters[0].sharedMesh, filters[1].sharedMesh };
        var materials = new[] { renderers[0].sharedMaterial, renderers[1].sharedMaterial };
        root.SetActive(false);
        Assert.AreEqual(0, response.ActiveResponseCount, "編集プレビューの親を隠した時点で確定反応を消す");
        Assert.AreEqual(0, response.ActiveLaneMask);
        foreach (var renderer in renderers) Assert.IsFalse(renderer.enabled);
        Object.DestroyImmediate(response.gameObject);
        foreach (var mesh in meshes) Assert.IsTrue(mesh == null, "非表示で破棄しても所有メッシュを解放する");
        foreach (var material in materials) Assert.IsTrue(material == null, "非表示で破棄しても所有材質を解放する");
    }

    [TestCase(StageTheme.MoonlitGarden)] [TestCase(StageTheme.AmberFoundry)]
    public void DenseSuccessesReuseFourSlotsAndExactlyTwoOwnedMeshesAndMaterials(StageTheme theme)
    {
        var response = Create(theme); response.Tick(1);
        var filters = response.GetComponentsInChildren<MeshFilter>();
        var renderers = response.GetComponentsInChildren<MeshRenderer>();
        Assert.AreEqual(2, filters.Length); Assert.AreEqual(2, renderers.Length);
        var meshes = new[] { filters[0].sharedMesh, filters[1].sharedMesh };
        var materials = new[] { renderers[0].sharedMaterial, renderers[1].sharedMaterial };
        for (int hit = 0; hit < 200; hit++)
        {
            response.OnPerfect(hit % 4); response.OnPerfect(-1); response.OnPerfect(4);
            response.Tick(1 + hit * .002);
            Assert.LessOrEqual(response.ActiveResponseCount, 4);
            for (int i = 0; i < 2; i++)
            {
                Assert.AreSame(meshes[i], filters[i].sharedMesh);
                Assert.AreSame(materials[i], renderers[i].sharedMaterial);
                Assert.LessOrEqual(meshes[i].vertexCount, StageThemeResponse.VertexBudget);
            }
        }
        Assert.AreEqual(15, response.ActiveLaneMask);
        Assert.AreEqual(2, response.transform.childCount);
        Object.DestroyImmediate(response.gameObject);
        foreach (var mesh in meshes) Assert.IsTrue(mesh == null, "所有メッシュを解放する");
        foreach (var material in materials) Assert.IsTrue(material == null, "所有材質を解放する");
    }
}
