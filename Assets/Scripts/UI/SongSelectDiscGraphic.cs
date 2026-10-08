using UnityEngine;
using UnityEngine.UI;

// 選曲専用の図形。円形ジャケットも同じUI描画経路で切り抜く。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class SongSelectDiscGraphic : MaskableGraphic
{
    public enum Shape { Panel, Tab, Disc, Cover, Ring, Halo, Clock, Arrow }
    public Shape Form;
    public Color Edge = new Color(.14f, .25f, .35f);
    public Color EdgeRight;
    public Color Bottom;
    public float Width = 3, Radius = 24, Progress = 1;
    public Sprite Artwork;
    public override Texture mainTexture => Form == Shape.Cover && Artwork != null ? Artwork.texture : s_WhiteTexture;
    public void Refresh() { SetVerticesDirty(); SetMaterialDirty(); }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear(); Rect r = rectTransform.rect; float radius = Mathf.Min(r.width, r.height) * .5f;
        if (Form == Shape.Cover) { Circle(vh, radius, color, true); return; }
        if (Form == Shape.Ring) { Ring(vh, radius - Width * .5f, Width, Progress, color); return; }
        if (Form == Shape.Disc)
        {
            Circle(vh, radius, new Color(.014f, .017f, .022f), false);
            for (float at = radius * .85f; at < radius - 1; at += 3)
                Ring(vh, at, .7f, 1, new Color(.045f, .05f, .06f));
            return;
        }
        if (Form == Shape.Halo)
        {
            for (int i = 0; i < 18; i++)
            {
                float at = radius - Width + Width * (i + .5f) / 18;
                var c = color; c.a *= Mathf.Exp(-i * i / 44f);
                Ring(vh, at, Width / 18 + .3f, 1, c);
            }
            return;
        }
        if (Form == Shape.Clock)
        {
            Circle(vh, radius, color, false);
            Line(vh, Vector2.zero, new Vector2(0, radius * .65f), 6, Edge);
            Line(vh, Vector2.zero, new Vector2(radius * .48f, 0), 6, Edge);
            return;
        }
        if (Form == Shape.Arrow)
        {
            Triangle(vh, new Vector2(r.xMin, 0), new Vector2(r.xMax, r.yMax), new Vector2(r.xMax, r.yMin), color);
            return;
        }
        int steps = Form == Shape.Tab ? 1 : 10;
        int count = steps * 4;
        for (int i = 0; i < count; i++)
        {
            var a = Point(r, i, steps, 0); var b = Point(r, (i + 1) % count, steps, 0);
            var c = Point(r, (i + 1) % count, steps, Width); var d = Point(r, i, steps, Width);
            Quad(vh, a, b, c, d, Border(r, a), Border(r, b), Border(r, c), Border(r, d));
            int index = vh.currentVertCount;
            vh.AddVert(r.center, Fill(r, r.center), Vector2.zero);
            vh.AddVert(d, Fill(r, d), Vector2.zero); vh.AddVert(c, Fill(r, c), Vector2.zero);
            vh.AddTriangle(index, index + 1, index + 2);
        }
    }
    Color Fill(Rect r, Vector2 p) => Color.Lerp(Bottom.a > 0 ? Bottom : color, color, Mathf.InverseLerp(r.yMin, r.yMax, p.y));
    Color Border(Rect r, Vector2 p) => Color.Lerp(Edge, EdgeRight.a > 0 ? EdgeRight : Edge, Mathf.InverseLerp(r.xMin, r.xMax, p.x));
    Vector2 Point(Rect rect, int index, int steps, float inset)
    {
        var r = new Rect(rect.x + inset, rect.y + inset, rect.width - inset * 2, rect.height - inset * 2);
        if (Form == Shape.Tab)
        {
            var p = new[] { new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin) }[index];
            p.x += p.y * .2867454f; return p;
        }
        float radius = Mathf.Clamp(Radius - inset, 0, Mathf.Min(r.width, r.height) * .5f);
        int corner = index / steps; float angle = (corner * 90 + (index % steps) * 90f / (steps - 1)) * Mathf.Deg2Rad;
        Vector2 center = new Vector2(corner == 0 || corner == 3 ? r.xMax - radius : r.xMin + radius,
            corner < 2 ? r.yMax - radius : r.yMin + radius);
        return center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
    }
    // 円形への中央トリミング。縦長・横長のジャケットを押し潰さず、Spriteの部分領域も守る。
    public static Rect CoverUvRect(Sprite sprite)
    {
        if (sprite == null || sprite.texture == null) return new Rect(0, 0, 1, 1);
        Rect r = sprite.rect;
        float side = Mathf.Min(r.width, r.height);
        return new Rect((r.center.x - side * .5f) / sprite.texture.width,
            (r.center.y - side * .5f) / sprite.texture.height,
            side / sprite.texture.width, side / sprite.texture.height);
    }
    void Circle(VertexHelper vh, float radius, Color c, bool texture)
    {
        Rect uv = texture ? CoverUvRect(Artwork) : new Rect(0, 0, 1, 1);
        for (int i = 0; i < 128; i++)
        {
            float a = i * Mathf.PI * 2 / 128, b = (i + 1) * Mathf.PI * 2 / 128;
            Vector2 p = new Vector2(Mathf.Cos(a), Mathf.Sin(a)), q = new Vector2(Mathf.Cos(b), Mathf.Sin(b));
            int start = vh.currentVertCount;
            vh.AddVert(Vector2.zero, c, uv.center);
            vh.AddVert(p * radius, c, uv.center + Vector2.Scale(p, uv.size) * .5f);
            vh.AddVert(q * radius, c, uv.center + Vector2.Scale(q, uv.size) * .5f);
            vh.AddTriangle(start, start + 1, start + 2);
        }
    }
    public static void Ring(VertexHelper vh, float radius, float width, float progress, Color c)
    {
        int count = Mathf.CeilToInt(128 * Mathf.Clamp01(progress));
        for (int i = 0; i < count; i++)
        {
            float a = Mathf.PI / 2 - i * Mathf.PI * 2 / 128, b = Mathf.PI / 2 - Mathf.Min((i + 1) / 128f, progress) * Mathf.PI * 2;
            Vector2 p = new Vector2(Mathf.Cos(a), Mathf.Sin(a)), q = new Vector2(Mathf.Cos(b), Mathf.Sin(b));
            SongSelectPanelGraphic.Quad(vh, p * (radius - width / 2), p * (radius + width / 2), q * (radius + width / 2), q * (radius - width / 2), c);
        }
    }
    static void Line(VertexHelper vh, Vector2 a, Vector2 b, float width, Color c)
    {
        var direction = (b - a).normalized; Vector2 n = new Vector2(-direction.y, direction.x) * width / 2;
        SongSelectPanelGraphic.Quad(vh, a - n, a + n, b + n, b - n, c);
    }
    static void Triangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color color)
    {
        int start = vh.currentVertCount; vh.AddVert(a, color, Vector2.zero); vh.AddVert(b, color, Vector2.zero); vh.AddVert(c, color, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
    }
    static void Quad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color ca, Color cb, Color cc, Color cd)
    {
        int start = vh.currentVertCount;
        vh.AddVert(a, ca, Vector2.zero); vh.AddVert(b, cb, Vector2.zero); vh.AddVert(c, cc, Vector2.zero); vh.AddVert(d, cd, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
    }
}
