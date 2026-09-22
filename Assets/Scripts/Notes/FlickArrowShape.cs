using UnityEngine;

// C案：大きな先頭と小さな後続のデュアルシェブロン。本体と床で上向き原形を共有。
public static class FlickArrowShape
{
    public static readonly Vector2[] Points = {
        new Vector2(0, .38f), new Vector2(.35f, .035f), new Vector2(.25f, -.065f),
        new Vector2(0, .185f), new Vector2(-.25f, -.065f), new Vector2(-.35f, .035f),
        new Vector2(0, -.045f), new Vector2(.255f, -.30f), new Vector2(.20f, -.355f),
        new Vector2(0, -.16f), new Vector2(-.20f, -.355f), new Vector2(-.255f, -.30f)
    };
    public static readonly int[] Triangles = {
        0, 1, 2, 0, 2, 3, 0, 3, 5, 3, 4, 5,
        6, 7, 8, 6, 8, 9, 6, 9, 11, 9, 10, 11
    };
    public static readonly Vector2[] OutlinePoints = BuildOutline();
    public const int TipIndex = 0;
    // 縁取りの太さ(矢印ローカル単位)。2026-09-22: 白い矢印が明るい本体色に溶けて見にくいとの指摘で 0.018 → 0.045。
    public const float OutlineWidth = .045f;
    static readonly Color TrailColor = new Color(.85f, .81f, .97f, 1);

    public static Color VertexColor(int index) { return index < 6 ? Color.white : TrailColor; }

    // 全体の拡縮では二段の中心がずれるため、各辺を同じ幅だけ外へ押し出す。
    static Vector2[] BuildOutline()
    {
        var outline = new Vector2[Points.Length];
        for (int i = 0; i < Points.Length; i++)
        {
            int start = i < 6 ? 0 : 6, local = i - start;
            Vector2 incoming = (Points[i] - Points[start + (local + 5) % 6]).normalized;
            Vector2 outgoing = (Points[start + (local + 1) % 6] - Points[i]).normalized;
            Vector2 n0 = new Vector2(-incoming.y, incoming.x);
            Vector2 n1 = new Vector2(-outgoing.y, outgoing.x);
            outline[i] = Points[i] + (n0 + n1) * (OutlineWidth / (1 + Vector2.Dot(n0, n1)));
        }
        return outline;
    }

    public static Mesh CreateMesh(bool outline = false)
    {
        var points = outline ? OutlinePoints : Points;
        var vertices = new Vector3[points.Length];
        var colors = new Color[Points.Length];
        for (int i = 0; i < vertices.Length; i++) { vertices[i] = points[i]; colors[i] = outline ? Color.white : VertexColor(i); }
        var mesh = new Mesh { name = outline ? "FlickChevronOutline" : "FlickDualChevron", hideFlags = HideFlags.DontSave };
        mesh.vertices = vertices; mesh.colors = colors; mesh.triangles = Triangles;
        mesh.RecalculateBounds();
        return mesh;
    }
}
