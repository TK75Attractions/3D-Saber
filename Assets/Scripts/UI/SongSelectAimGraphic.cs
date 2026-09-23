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

    // 照準の半径(1080p基準px)。投影で細い線が消えないよう、暗い縁取りの上に太い明色を重ねる。
    public const float ReticleRadius = 40f;
    public const float RingWidth = 7f;

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Color cyan = locked ? new Color(.48f, .59f, .63f, .85f) : new Color(.55f, .94f, 1f, 1f);
        Color dark = new Color(.01f, .03f, .05f, .94f);
        if (!ImpactOnly)
        {
            float r = ReticleRadius;
            Arc(vh, r, RingWidth + 4f, 1, dark);                                     // 帯全体の暗い縁取り
            Arc(vh, r, 2.6f, 1, new Color(cyan.r, cyan.g, cyan.b, .38f));           // 薄い全周
            Arc(vh, r, RingWidth, progress, Color.Lerp(cyan, new Color(1f, .84f, .42f), progress)); // 蓄積
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * .5f;
                Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Line(vh, d * 16, d * 32, 8.5f, dark);
                Line(vh, d * 17.5f, d * 30.5f, 4f, cyan);
            }
            // 中心点: 縁取り付きの塗り円(Arc の半径=幅/2 で円盤になる)。
            Arc(vh, 3.6f, 7.2f, 1, dark);
            Arc(vh, 2.4f, 4.8f, 1, Color.Lerp(cyan, Color.white, .5f));
        }
        if (shotAge < .34f)
        {
            float t = Mathf.Clamp01(shotAge / .34f), alpha = (1 - t) * .9f;
            float ringWidth = 4 * (1 - t) + 1.5f;
            Arc(vh, 8 + t * 64, ringWidth + 3f, 1, new Color(dark.r, dark.g, dark.b, alpha * .7f));
            Arc(vh, 8 + t * 64, ringWidth, 1, new Color(.65f, .96f, 1f, alpha));
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4;
                Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Line(vh, d * (8 + t * 38), d * (21 + t * 50), 4.6f, new Color(dark.r, dark.g, dark.b, alpha * .7f));
                Line(vh, d * (8 + t * 38), d * (21 + t * 50), 2.4f, new Color(1, .86f, .5f, alpha));
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
