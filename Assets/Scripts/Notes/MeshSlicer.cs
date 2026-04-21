using System.Collections.Generic;
using UnityEngine;

// 平面でメッシュを2分割する汎用スライサー。
// 凸メッシュ（Unity の Cube 等）向け。切断面は重心からファン三角形で閉じる。
public static class MeshSlicer
{
    private struct V
    {
        public Vector3 pos;
        public Vector3 normal;
        public Vector2 uv;
    }

    // planeLocal はメッシュのローカル座標系での平面。
    // 返り値 true の場合 above / below 両方にメッシュが入る。
    public static bool Slice(Mesh source, Plane planeLocal, out Mesh above, out Mesh below)
    {
        above = null;
        below = null;
        if (source == null) return false;

        var srcVerts = source.vertices;
        var srcTris = source.triangles;
        var srcNormals = source.normals;
        var srcUVs = source.uv;
        bool hasNormals = srcNormals != null && srcNormals.Length == srcVerts.Length;
        bool hasUVs = srcUVs != null && srcUVs.Length == srcVerts.Length;

        var aboveB = new Builder();
        var belowB = new Builder();
        var cutEdges = new List<(Vector3 a, Vector3 b)>();

        V Get(int i) => new V
        {
            pos = srcVerts[i],
            normal = hasNormals ? srcNormals[i] : Vector3.zero,
            uv = hasUVs ? srcUVs[i] : Vector2.zero
        };

        for (int t = 0; t < srcTris.Length; t += 3)
        {
            V[] tri = { Get(srcTris[t]), Get(srcTris[t + 1]), Get(srcTris[t + 2]) };
            bool[] sides = {
                planeLocal.GetSide(tri[0].pos),
                planeLocal.GetSide(tri[1].pos),
                planeLocal.GetSide(tri[2].pos)
            };
            int aboveCount = (sides[0] ? 1 : 0) + (sides[1] ? 1 : 0) + (sides[2] ? 1 : 0);

            if (aboveCount == 3)
            {
                aboveB.AddTri(tri[0], tri[1], tri[2]);
            }
            else if (aboveCount == 0)
            {
                belowB.AddTri(tri[0], tri[1], tri[2]);
            }
            else if (aboveCount == 1)
            {
                int top = sides[0] ? 0 : sides[1] ? 1 : 2;
                int next = (top + 1) % 3;
                int prev = (top + 2) % 3;
                V e1 = Interp(tri[top], tri[next], planeLocal);
                V e2 = Interp(tri[prev], tri[top], planeLocal);
                aboveB.AddTri(tri[top], e1, e2);
                belowB.AddTri(e1, tri[next], tri[prev]);
                belowB.AddTri(e1, tri[prev], e2);
                cutEdges.Add((e2.pos, e1.pos));
            }
            else // 2
            {
                int bot = !sides[0] ? 0 : !sides[1] ? 1 : 2;
                int next = (bot + 1) % 3;
                int prev = (bot + 2) % 3;
                V e1 = Interp(tri[bot], tri[next], planeLocal);
                V e2 = Interp(tri[prev], tri[bot], planeLocal);
                aboveB.AddTri(e1, tri[next], tri[prev]);
                aboveB.AddTri(e1, tri[prev], e2);
                belowB.AddTri(tri[bot], e1, e2);
                cutEdges.Add((e1.pos, e2.pos));
            }
        }

        if (aboveB.Count == 0 || belowB.Count == 0) return false;

        CapCut(cutEdges, planeLocal, aboveB, aboveSide: true);
        CapCut(cutEdges, planeLocal, belowB, aboveSide: false);

        above = aboveB.Build();
        below = belowB.Build();
        return true;
    }

    private static V Interp(V a, V b, Plane p)
    {
        float da = p.GetDistanceToPoint(a.pos);
        float db = p.GetDistanceToPoint(b.pos);
        float denom = da - db;
        float t = Mathf.Approximately(denom, 0f) ? 0.5f : da / denom;
        return new V
        {
            pos = Vector3.Lerp(a.pos, b.pos, t),
            normal = Vector3.Lerp(a.normal, b.normal, t).normalized,
            uv = Vector2.Lerp(a.uv, b.uv, t)
        };
    }

    private static void CapCut(List<(Vector3 a, Vector3 b)> edges, Plane p, Builder b, bool aboveSide)
    {
        if (edges.Count < 3) return;

        Vector3 centroid = Vector3.zero;
        int n = 0;
        foreach (var (ea, eb) in edges) { centroid += ea + eb; n += 2; }
        centroid /= n;

        Vector3 capNormal = aboveSide ? -p.normal : p.normal;

        foreach (var (ea, eb) in edges)
        {
            Vector3 v1 = ea - centroid;
            Vector3 v2 = eb - centroid;
            Vector3 triNormal = Vector3.Cross(v1, v2);
            V vc = new V { pos = centroid, normal = capNormal };
            V va = new V { pos = ea, normal = capNormal };
            V vb = new V { pos = eb, normal = capNormal };
            if (Vector3.Dot(triNormal, capNormal) < 0f)
            {
                b.AddTri(vc, vb, va);
            }
            else
            {
                b.AddTri(vc, va, vb);
            }
        }
    }

    private class Builder
    {
        public readonly List<Vector3> verts = new List<Vector3>();
        public readonly List<Vector3> normals = new List<Vector3>();
        public readonly List<Vector2> uvs = new List<Vector2>();
        public readonly List<int> triangles = new List<int>();

        public int Count => triangles.Count;

        public void AddTri(V a, V b, V c)
        {
            int i0 = verts.Count;
            verts.Add(a.pos); normals.Add(a.normal); uvs.Add(a.uv);
            verts.Add(b.pos); normals.Add(b.normal); uvs.Add(b.uv);
            verts.Add(c.pos); normals.Add(c.normal); uvs.Add(c.uv);
            triangles.Add(i0);
            triangles.Add(i0 + 1);
            triangles.Add(i0 + 2);
        }

        public Mesh Build()
        {
            var m = new Mesh();
            m.indexFormat = verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(verts);
            m.SetTriangles(triangles, 0);
            if (normals.Count == verts.Count) m.SetNormals(normals);
            else m.RecalculateNormals();
            if (uvs.Count == verts.Count) m.SetUVs(0, uvs);
            m.RecalculateBounds();
            return m;
        }
    }
}
