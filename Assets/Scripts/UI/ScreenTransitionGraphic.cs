using UnityEngine;
using UnityEngine.UI;

// 赤と青の斜めの光を境界に、両側から幕を閉じる。完全暗転では次のシーンを一切見せない。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class ScreenTransitionGraphic : MaskableGraphic
{
    float progress;
    ScreenTransition.Style style;
    public float Progress => progress;

    public void SetProgress(float value, ScreenTransition.Style nextStyle)
    {
        progress = Mathf.Clamp01(value);
        style = nextStyle;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (progress <= 0f) return;
        Rect r = rectTransform.rect;
        Color dark = new Color(.003f, .007f, .014f, 1f);
        if (progress >= 1f)
        {
            Quad(vh, new Vector2(r.xMin, r.yMin), new Vector2(r.xMin, r.yMax),
                new Vector2(r.xMax, r.yMax), new Vector2(r.xMax, r.yMin), dark);
            return;
        }

        // 幕が届く前にも薄く背景を落とし、強い全画面フラッシュを避ける。
        Quad(vh, new Vector2(r.xMin, r.yMin), new Vector2(r.xMin, r.yMax),
            new Vector2(r.xMax, r.yMax), new Vector2(r.xMax, r.yMin), new Color(dark.r, dark.g, dark.b, progress * .48f));
        float ease = Mathf.SmoothStep(0f, 1f, progress);
        float slant = r.height * (style == ScreenTransition.Style.Back ? -.16f : .16f);
        float reach = r.width * .5f + Mathf.Abs(slant) + 4f;
        float left = r.xMin - Mathf.Abs(slant) + reach * ease;
        float right = r.xMax + Mathf.Abs(slant) - reach * ease;
        Vector2 lb = new Vector2(left - slant, r.yMin), lt = new Vector2(left + slant, r.yMax);
        Vector2 rb = new Vector2(right - slant, r.yMin), rt = new Vector2(right + slant, r.yMax);
        Quad(vh, new Vector2(r.xMin - r.height, r.yMin), new Vector2(r.xMin - r.height, r.yMax), lt, lb, dark);
        Quad(vh, rb, rt, new Vector2(r.xMax + r.height, r.yMax), new Vector2(r.xMax + r.height, r.yMin), dark);
        float light = Mathf.Sin(progress * Mathf.PI);
        Color red = new Color(1f, .13f, .27f, light);
        Color blue = new Color(.14f, .73f, 1f, light);
        if (style == ScreenTransition.Style.Result) red = new Color(1f, .72f, .28f, light);
        if (style == ScreenTransition.Style.Calibration) red = new Color(.20f, 1f, .68f, light);
        Edge(vh, lb, lt, red);
        Edge(vh, rb, rt, blue);
    }

    static void Edge(VertexHelper vh, Vector2 bottom, Vector2 top, Color tint)
    {
        Color glow = tint; glow.a *= .12f;
        Band(vh, bottom, top, 26f, glow);
        glow.a = tint.a * .22f;
        Band(vh, bottom, top, 9f, glow);
        Band(vh, bottom, top, 2.5f, tint);
    }

    static void Band(VertexHelper vh, Vector2 bottom, Vector2 top, float width, Color tint)
    {
        Vector2 offset = Vector2.right * width * .5f;
        Quad(vh, bottom - offset, top - offset, top + offset, bottom + offset, tint);
    }

    static void Quad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color tint)
    {
        int first = vh.currentVertCount;
        vh.AddVert(a, tint, Vector2.zero); vh.AddVert(b, tint, Vector2.zero);
        vh.AddVert(c, tint, Vector2.zero); vh.AddVert(d, tint, Vector2.zero);
        vh.AddTriangle(first, first + 1, first + 2); vh.AddTriangle(first, first + 2, first + 3);
    }
}
