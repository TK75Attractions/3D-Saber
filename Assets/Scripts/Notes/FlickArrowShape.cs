using UnityEngine;

// 本体と床で同じ、軸のある太い矢印を使う。上向きを原形とする。
public static class FlickArrowShape
{
    public static readonly Vector2[] Points = {
        new Vector2(-.16f, -.42f), new Vector2(.16f, -.42f),
        new Vector2(.16f, .02f), new Vector2(.44f, .02f),
        new Vector2(0, .46f), new Vector2(-.44f, .02f), new Vector2(-.16f, .02f)
    };
    public static readonly int[] Triangles = { 0, 1, 2, 0, 2, 6, 5, 3, 4 };

    public static Mesh CreateMesh()
    {
        var vertices = new Vector3[Points.Length];
        var colors = new Color[Points.Length];
        for (int i = 0; i < vertices.Length; i++) { vertices[i] = Points[i]; colors[i] = Color.white; }
        var mesh = new Mesh { name = "FlickArrow", hideFlags = HideFlags.DontSave };
        mesh.vertices = vertices; mesh.colors = colors; mesh.triangles = Triangles;
        mesh.RecalculateBounds();
        return mesh;
    }
}
