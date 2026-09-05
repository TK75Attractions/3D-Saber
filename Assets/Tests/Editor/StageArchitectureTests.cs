using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class StageArchitectureTests
{
    private FloorRenderer stage;
    [SetUp] public void Create() { stage = FloorRenderer.Ensure(); }
    [TearDown] public void Cleanup() { if (stage != null) Object.DestroyImmediate(stage.gameObject); }

    [Test]
    public void Architecture_HasLayeredFloorAndSideWalls()
    {
        foreach (string name in new[] { "FloorPanels", "RaisedSideDecks", "FloorRecesses", "FloorEdgeInlays",
            "WallPanels", "WallRecesses", "WallStructuralRibs", "WallLightInlays" })
            Assert.IsNotNull(stage.transform.Find(name), name);
        Assert.Greater(stage.transform.Find("RaisedSideDecks").GetComponent<MeshFilter>().sharedMesh.bounds.max.y,
            stage.transform.Find("FloorPanels").GetComponent<MeshFilter>().sharedMesh.bounds.max.y + .1f);
    }

    [Test]
    public void Walls_LeaveCentralNoteCorridorClear()
    {
        foreach (var filter in stage.GetComponentsInChildren<MeshFilter>())
        {
            if (!filter.name.StartsWith("Wall")) continue;
            foreach (Vector3 vertex in filter.sharedMesh.vertices)
                Assert.GreaterOrEqual(Mathf.Abs(vertex.x), FloorRenderer.ClearCorridorHalfWidth, filter.name);
        }
    }

    [Test]
    public void Geometry_IsFiniteAndUsesBoundedDrawGroups()
    {
        var filters = stage.GetComponentsInChildren<MeshFilter>();
        Assert.LessOrEqual(filters.Length, 28, "小部品を材質・用途別に結合する");
        foreach (var filter in filters)
        {
            var mesh = filter.sharedMesh;
            Assert.Greater(mesh.vertexCount, 0);
            Assert.AreEqual(mesh.vertexCount, mesh.normals.Length);
            foreach (Vector3 v in mesh.vertices)
                Assert.IsFalse(float.IsNaN(v.sqrMagnitude) || float.IsInfinity(v.sqrMagnitude), filter.name);
            Assert.IsTrue(mesh.triangles.All(i => i >= 0 && i < mesh.vertexCount));
        }
        var renderers = stage.GetComponentsInChildren<MeshRenderer>();
        Assert.LessOrEqual(renderers.Select(r => r.sharedMaterial).Distinct().Count(), 9);
        Assert.IsEmpty(stage.GetComponentsInChildren<Collider>());
    }

    [Test]
    public void Build_IsIdempotentAndEnvironmentLightIsSubdued()
    {
        int count = stage.transform.childCount;
        stage.Build();
        Assert.AreEqual(count, stage.transform.childCount);
        foreach (var renderer in stage.GetComponentsInChildren<MeshRenderer>())
        {
            var material = renderer.sharedMaterial;
            if (material.HasProperty("_EmissionColor"))
                Assert.Less(material.GetColor("_EmissionColor").maxColorComponent, .8f);
        }
    }

    [Test]
    public void Materials_UsePackagedBackgroundShaderWithoutCompilerErrors()
    {
        var shader = Resources.Load<Shader>("Stage/ObsidianMetal");
        Assert.IsNotNull(shader, "ビルドにも含まれる Resources 内の背景用シェーダー");
        Assert.IsFalse(ShaderUtil.ShaderHasError(shader));
        foreach (var renderer in stage.GetComponentsInChildren<MeshRenderer>())
            Assert.AreSame(shader, renderer.sharedMaterial.shader);
    }

    [Test]
    public void Destroy_ReleasesOwnedMeshesAndMaterials()
    {
        var mesh = stage.transform.Find("WallPanels").GetComponent<MeshFilter>().sharedMesh;
        var material = stage.transform.Find("WallPanels").GetComponent<MeshRenderer>().sharedMaterial;
        Object.DestroyImmediate(stage.gameObject);
        Assert.IsTrue(mesh == null);
        Assert.IsTrue(material == null);
    }
}
