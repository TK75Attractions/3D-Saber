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
        if(source == null) { above=below=null; return false; }
        above = new Mesh(); below = new Mesh();
        if (SliceInto(source, planeLocal, above, below)) return true;
        UISkinKit.SafeDestroy(above); UISkinKit.SafeDestroy(below); above = below = null; return false;
    }

    // Unityのメインスレッド専用。コールバックを挟まず、一度の切断が終わってから次へ使う。
    static readonly List<Vector3> srcVerts = new List<Vector3>(64), srcNormals = new List<Vector3>(64);
    static readonly List<Vector2> srcUVs = new List<Vector2>(64);
    static readonly List<int> srcTris = new List<int>(128);
    static readonly List<int> submeshTris = new List<int>(128);
    static readonly Builder aboveB = new Builder(), belowB = new Builder();
    static readonly List<(Vector3 a, Vector3 b)> cutEdges = new List<(Vector3, Vector3)>(32);
    static readonly V[] tri = new V[3];
    static readonly bool[] sides = new bool[3];
    public static bool SliceInto(Mesh source, Plane planeLocal, Mesh above, Mesh below)
    {
        if (source == null || above == null || below == null) return false;
        source.GetVertices(srcVerts); srcTris.Clear();
        for(int submesh=0;submesh<source.subMeshCount;submesh++) { source.GetTriangles(submeshTris,submesh); srcTris.AddRange(submeshTris); }
        source.GetNormals(srcNormals); source.GetUVs(0,srcUVs);
        bool hasNormals = srcNormals.Count == srcVerts.Count, hasUVs = srcUVs.Count == srcVerts.Count;
        aboveB.Clear(); belowB.Clear(); cutEdges.Clear();
        for (int t = 0; t < srcTris.Count; t += 3)
        {
            for (int j=0;j<3;j++)
            {
                int index=srcTris[t+j];
                tri[j]=new V { pos=srcVerts[index], normal=hasNormals?srcNormals[index]:Vector3.zero, uv=hasUVs?srcUVs[index]:Vector2.zero };
                sides[j]=planeLocal.GetSide(tri[j].pos);
            }
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

        aboveB.BuildInto(above);
        belowB.BuildInto(below);
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

        public void Clear() { verts.Clear(); normals.Clear(); uvs.Clear(); triangles.Clear(); }
        public void BuildInto(Mesh m)
        {
            m.Clear();
            m.indexFormat = verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(verts);
            m.SetTriangles(triangles, 0);
            if (normals.Count == verts.Count) m.SetNormals(normals);
            else m.RecalculateNormals();
            if (uvs.Count == verts.Count) m.SetUVs(0, uvs);
            m.RecalculateBounds();

        }
    }
}
