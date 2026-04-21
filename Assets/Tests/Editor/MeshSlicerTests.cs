using NUnit.Framework;
using UnityEngine;

public class MeshSlicerTests
{
    private Mesh MakeCubeMesh()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Mesh m = go.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(go);
        return m;
    }

    [Test]
    public void Slice_MissesMesh_ReturnsFalse()
    {
        Mesh cube = MakeCubeMesh();
        // 平面がメッシュから遠く離れている
        Plane p = new Plane(Vector3.up, new Vector3(0, 100f, 0));
        bool ok = MeshSlicer.Slice(cube, p, out _, out _);
        Assert.IsFalse(ok);
    }

    [Test]
    public void Slice_HorizontalCut_ProducesTwoMeshes()
    {
        Mesh cube = MakeCubeMesh();
        Plane p = new Plane(Vector3.up, Vector3.zero);
        bool ok = MeshSlicer.Slice(cube, p, out Mesh above, out Mesh below);
        Assert.IsTrue(ok);
        Assert.IsNotNull(above);
        Assert.IsNotNull(below);
        Assert.Greater(above.vertexCount, 0);
        Assert.Greater(below.vertexCount, 0);
    }

    [Test]
    public void Slice_VerticalCut_ProducesTwoMeshes()
    {
        Mesh cube = MakeCubeMesh();
        Plane p = new Plane(Vector3.right, Vector3.zero);
        bool ok = MeshSlicer.Slice(cube, p, out Mesh above, out Mesh below);
        Assert.IsTrue(ok);
        Assert.Greater(above.vertexCount, 0);
        Assert.Greater(below.vertexCount, 0);
    }

    [Test]
    public void Slice_Halves_HaveTrianglesOnBothSides()
    {
        Mesh cube = MakeCubeMesh();
        Plane p = new Plane(Vector3.up, Vector3.zero);
        MeshSlicer.Slice(cube, p, out Mesh above, out Mesh below);
        Assert.Greater(above.triangles.Length, 0);
        Assert.Greater(below.triangles.Length, 0);
    }

    [Test]
    public void Slice_CutCapsHalves_AddsTrianglesBeyondSourceCount()
    {
        Mesh cube = MakeCubeMesh();
        int srcTriCount = cube.triangles.Length;
        Plane p = new Plane(Vector3.up, Vector3.zero);
        MeshSlicer.Slice(cube, p, out Mesh above, out Mesh below);
        int totalAfter = above.triangles.Length + below.triangles.Length;
        // キャップが追加されているので、元の三角数より多いはず
        Assert.Greater(totalAfter, srcTriCount);
    }
}
