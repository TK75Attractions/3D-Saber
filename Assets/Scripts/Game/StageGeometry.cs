using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 材質別に頂点を蓄積し、面取りを含む構造を一つのメッシュへまとめる。
internal sealed partial class StageGeometry
{
    private readonly List<Vector3> vertices = new List<Vector3>();
    private readonly List<Vector3> normals = new List<Vector3>();
    private readonly List<int> triangles = new List<int>();
    private readonly List<Vector4> motionAnchors = new List<Vector4>();
    public int VertexCount => vertices.Count;
    private void Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        int start = vertices.Count;
        Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
        vertices.Add(a); vertices.Add(b); vertices.Add(c);
        normals.Add(normal); normals.Add(normal); normals.Add(normal);
        triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
    }
    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Triangle(a, b, c); Triangle(a, c, d);
    }
    public void Box(Vector3 center, Vector3 size, Quaternion rotation = default)
    {
        if (rotation.Equals(default(Quaternion))) rotation = Quaternion.identity;
        Vector3 h = size * .5f;
        var p = new Vector3[8];
        for (int i = 0; i < 8; i++)
            p[i] = center + rotation * new Vector3((i & 1) == 0 ? -h.x : h.x,
                (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z);
        Quad(p[0], p[2], p[3], p[1]); Quad(p[4], p[5], p[7], p[6]);
        Quad(p[0], p[4], p[6], p[2]); Quad(p[1], p[3], p[7], p[5]);
        Quad(p[0], p[1], p[5], p[4]); Quad(p[2], p[6], p[7], p[3]);
    }
    public void Beam(Vector3 a, Vector3 b, float width, float depth)
    {
        Box((a + b) * .5f, new Vector3(width, Vector3.Distance(a, b), depth),
            Quaternion.FromToRotation(Vector3.up, (b - a).normalized));
    }
    // 正面は -Z。四隅を切った八角形＋傾斜した細い縁で実際の厚みを付ける。
    public void Panel(Vector3 center, Vector3 size, Quaternion rotation, float corner)
    {
        int firstVertex = vertices.Count;
        float x = size.x * .5f, y = size.y * .5f, z = size.z * .5f;
        float cut = Mathf.Min(corner, Mathf.Min(x, y) * .45f);
        float bevel = Mathf.Min(.035f, Mathf.Min(size.z * .3f, cut * .3f));
        var ring = new[] {
            new Vector2(-x + cut, -y), new Vector2(x - cut, -y),
            new Vector2(x, -y + cut), new Vector2(x, y - cut),
            new Vector2(x - cut, y), new Vector2(-x + cut, y),
            new Vector2(-x, y - cut), new Vector2(-x, -y + cut)
        };
        var front = new Vector3[8]; var rim = new Vector3[8]; var back = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            Vector2 p = ring[i];
            front[i] = center + rotation * new Vector3(p.x - Mathf.Sign(p.x) * bevel, p.y - Mathf.Sign(p.y) * bevel, -z);
            rim[i] = center + rotation * new Vector3(p.x, p.y, -z + bevel);
            back[i] = center + rotation * new Vector3(p.x, p.y, z);
        }
        Vector3 frontCenter = center + rotation * new Vector3(0f, 0f, -z);
        Vector3 backCenter = center + rotation * new Vector3(0f, 0f, z);
        for (int i = 0; i < 8; i++)
        {
            int next = (i + 1) % 8;
            Triangle(frontCenter, front[next], front[i]); Triangle(backCenter, back[i], back[next]);
            Quad(rim[i], front[i], front[next], rim[next]); Quad(rim[i], rim[next], back[next], back[i]);
        }
        TagMotionSince(firstVertex, center);
    }
    // 一枚の板の全頂点に共通の支点を保存する。結合メッシュのまま剛体として動かせる。
    public void TagMotionSince(int firstVertex, Vector3 center)
    {
        while (motionAnchors.Count < vertices.Count) motionAnchors.Add(Vector4.zero);
        for (int i = firstVertex; i < vertices.Count; i++) motionAnchors[i] = new Vector4(center.x, center.y, center.z, 1);
    }
    public Mesh CreateMesh(string name)
    {
        var mesh = new Mesh { name = "Stage/" + name };
        if (vertices.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetTriangles(triangles, 0);
        if (motionAnchors.Count > 0)
        {
            while (motionAnchors.Count < vertices.Count) motionAnchors.Add(Vector4.zero);
            mesh.SetUVs(1, motionAnchors);
        }
        mesh.RecalculateBounds();
        return mesh;
    }
}
