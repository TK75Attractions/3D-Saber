using UnityEngine;
using UnityEngine.UI;

// Dotの8曲線を元のサンプル点と正投影カメラで描く。文字画像を加工しない。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class HardIntroLinesGraphic : MaskableGraphic
{
    Vector2[][] paths;
    float elapsed;
    bool reduced;
    static readonly Color Cyan = new Color(.58f, .84f, .90f, .88f);
    static readonly Color Violet = new Color(.73f, .66f, .89f, .88f);

    public void SetTime(float time, bool reduceMotion)
    {
        elapsed = time; reduced = reduceMotion;
        if (paths == null) BuildPaths();
        SetVerticesDirty();
    }
    static Vector2 Project(float x, float y, float z)
    {
        const float angle = .14562827348709106f, scale = 1280f / 11.8f;
        return new Vector2(x * scale, ((y + 2.1f) * Mathf.Cos(angle) + (z - 15f) * Mathf.Sin(angle)) * scale);
    }
    void BuildPaths()
    {
        paths = new Vector2[8][];
        for (int j = 0; j < 2; j++)
        {
            paths[j] = new Vector2[201];
            for (int i = 0; i <= 200; i++)
            {
                float t = Mathf.Lerp(j == 0 ? 0 : 3.15f, j == 0 ? 2.8f : 5.9f, i / 200f);
                paths[j][i] = Project(5.18f * Mathf.Cos(t), 2.66f * Mathf.Sin(t), -.48f + .13f * Mathf.Sin(2 * t));
            }
        }
        for (int j = 0; j < 3; j++)
        {
            paths[2 + j] = new Vector2[100]; paths[5 + j] = new Vector2[100];
            for (int i = 0; i < 100; i++)
            {
                float t = i / 99f;
                paths[2 + j][i] = Project(-6 + t * 4.6f, 1.7f + Mathf.Sin(t * 2.8f + j * .2f) * .9f + j * .12f, -.66f);
                paths[5 + j][i] = Project(1.8f + t * 4.2f, -2.3f + Mathf.Sin(t * 2.4f) * .70f - j * .08f, -.65f);
            }
        }
    }
    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (paths == null) return;
        for (int j = 0; j < paths.Length; j++)
        {
            float opacity = HardIntroTimeline.Smooth(0, .22f, HardIntroTimeline.LineProgress(elapsed, j));
            if (opacity <= 0) continue;
            Vector2 offset = HardIntroTimeline.LineOffset(elapsed, j, reduced);
            Color tint = j == 0 || j == 3 ? Cyan : j == 1 || j == 6 ? Violet : Color.Lerp(Cyan, Violet, .45f);
            tint.a *= opacity;
            float width = (j < 2 ? .016f : j < 5 ? .014f : .012f) * (1280f / 11.8f);
            var points = paths[j];
            int first = vh.currentVertCount;
            Color edge = tint; edge.a = 0;
            for (int i = 0; i < points.Length; i++)
            {
                Vector2 tangent = points[Mathf.Min(i + 1, points.Length - 1)] - points[Mathf.Max(i - 1, 0)];
                Vector2 normal = new Vector2(-tangent.y, tangent.x).normalized;
                Vector2 center = points[i] + offset;
                // 細線の両縁をサブピクセル幅で透明へ落とし、720pでも階段状にしない。
                float half = width * .5f;
                vh.AddVert(center - normal * (half + .65f), edge, Vector2.zero);
                vh.AddVert(center - normal * half, tint, Vector2.zero);
                vh.AddVert(center + normal * half, tint, Vector2.zero);
                vh.AddVert(center + normal * (half + .65f), edge, Vector2.zero);
                if (i == 0) continue;
                int previous = first + (i - 1) * 4, current = first + i * 4;
                for (int band = 0; band < 3; band++)
                {
                    vh.AddTriangle(previous + band, current + band, current + band + 1);
                    vh.AddTriangle(previous + band, current + band + 1, previous + band + 1);
                }
            }
        }
    }
}
