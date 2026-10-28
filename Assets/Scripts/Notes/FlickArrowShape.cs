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

    // Q の隠した向きの左右の両矢印(↔)。大きい方の山形(Points の 0〜5)を縮めて左右へ向け、短い軸でつなぐ。
    // 幅 0.88・高さ 0.49(上向きの矢印の長さ 0.735 と同じくらい)。縁取りは同じ縮みの OutlineWidth で外へ。
    // 軸の太さは 0.06(0.04 では投影で細かった。Codex の点検、10/28)。
    public const float SideHeadScale = .7f, SideHeadOffset = .174f, SideShaftHalfLength = .32f, SideShaftHalfWidth = .06f;

    public static Mesh CreateSideMesh(bool outline = false)
    {
        var source = outline ? OutlinePoints : Points;
        var vertices = new Vector3[16];
        var colors = new Color[16];
        for (int i = 0; i < 6; i++)
        {
            Vector2 p = source[i] * SideHeadScale;
            vertices[i] = new Vector3(p.y + SideHeadOffset, -p.x, 0f);        // 右向き(上向きを右へ回す)
            vertices[6 + i] = new Vector3(-p.y - SideHeadOffset, p.x, 0f);    // 左向き
        }
        float grow = outline ? OutlineWidth * SideHeadScale : 0f;
        float hx = SideShaftHalfLength + grow, hy = SideShaftHalfWidth + grow;
        vertices[12] = new Vector3(-hx, -hy, 0f);
        vertices[13] = new Vector3(hx, -hy, 0f);
        vertices[14] = new Vector3(hx, hy, 0f);
        vertices[15] = new Vector3(-hx, hy, 0f);
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        // 山形は元の三角形(回しても向きは変わらない)、軸は同じ回り方の2枚。
        var triangles = new int[12 * 2 + 6];
        for (int head = 0; head < 2; head++)
            for (int t = 0; t < 12; t++) triangles[head * 12 + t] = head * 6 + Triangles[t];
        int[] shaft = { 12, 15, 14, 12, 14, 13 };
        for (int t = 0; t < shaft.Length; t++) triangles[24 + t] = shaft[t];
        var mesh = new Mesh { name = outline ? "SideArrowOutline" : "SideArrow", hideFlags = HideFlags.DontSave };
        mesh.vertices = vertices; mesh.colors = colors; mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }
}
