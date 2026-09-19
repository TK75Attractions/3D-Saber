using UnityEngine;
using UnityEngine.UI;

// 照準・残り時間・発射時の衝撃波。画像を増やさず、一枚のUIメッシュへ描く。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class SongSelectAimGraphic : MaskableGraphic
{
    float progress, shotAge = 10;
    bool locked;
    public bool ImpactOnly { get; set; }
    public void Show(float fill, bool waiting, float age = 10)
    { progress = fill; locked = waiting; shotAge = age; SetVerticesDirty(); }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Color cyan = locked ? new Color(.48f, .59f, .63f, .8f) : new Color(.55f, .94f, 1f, .95f);
        if (!ImpactOnly)
        {
            Arc(vh, 27, 2, 1, new Color(.02f, .06f, .09f, .92f));
            Arc(vh, 27, 1, 1, new Color(cyan.r, cyan.g, cyan.b, .3f));
            Arc(vh, 27, 3, progress, Color.Lerp(cyan, new Color(1f, .81f, .4f), progress));
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * .5f;
                Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Line(vh, d * 12, d * 21, 4, new Color(.02f, .04f, .06f, .95f));
                Line(vh, d * 13, d * 20, 1.6f, cyan);
            }
            Line(vh, new Vector2(-2, 0), new Vector2(2, 0), 3, cyan);
        }
        if (shotAge < .34f)
        {
            float t = Mathf.Clamp01(shotAge / .34f), alpha = (1 - t) * .9f;
            Arc(vh, 8 + t * 64, 4 * (1 - t) + 1, 1, new Color(.65f, .96f, 1f, alpha));
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4;
                Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Line(vh, d * (8 + t * 38), d * (21 + t * 50), 2, new Color(1, .86f, .5f, alpha));
            }
        }
    }

    static void Arc(VertexHelper vh, float radius, float width, float amount, Color tint)
    {
        int segments = Mathf.CeilToInt(72 * amount);
        for (int i = 0; i < segments; i++)
        {
            float a = Mathf.PI / 2 - i / 72f * Mathf.PI * 2;
            float b = Mathf.PI / 2 - Mathf.Min((i + 1) / 72f, amount) * Mathf.PI * 2;
            Vector2 x = new Vector2(Mathf.Cos(a), Mathf.Sin(a)), y = new Vector2(Mathf.Cos(b), Mathf.Sin(b));
            SongSelectPanelGraphic.Quad(vh, x * (radius - width / 2), x * (radius + width / 2),
                y * (radius + width / 2), y * (radius - width / 2), tint);
        }
    }
    static void Line(VertexHelper vh, Vector2 a, Vector2 b, float width, Color tint)
    {
        Vector2 d = (b - a).normalized, n = new Vector2(-d.y, d.x) * width / 2;
        SongSelectPanelGraphic.Quad(vh, a - n, a + n, b + n, b - n, tint);
    }
}
