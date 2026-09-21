using UnityEngine;
using UnityEngine.UI;

// 左右の先端位置から毎フレーム枠を組み直す。線幅や角は拡大せず、間隔だけを広げる。
public sealed class ClearAchievementGraphic : MaskableGraphic
{
    bool perfect, low;
    float span, elapsed;
    static readonly Vector2[] Stars = { new Vector2(-676, 143), new Vector2(692, -139),
        new Vector2(353, 117), new Vector2(-273, -125), new Vector2(-778, -4), new Vector2(780, 5) };

    public void SetFrame(bool allPerfect, float halfWidth, float age, bool reduced)
    {
        perfect = allPerfect;
        span = halfWidth;
        elapsed = age;
        low = reduced;
        SetVerticesDirty();
    }

    Color Accent => perfect ? new Color(.55f, .55f, 1) : new Color(1, .74f, .19f);

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        if (span <= 0) return;
        float openness = Mathf.InverseLerp(115, 904, span);
        Color accent = Accent;
        Halo(mesh, span + 55, 420, Alpha(accent, low ? .045f : .075f));
        Quad(mesh, new Vector2(-span, 85), new Vector2(0, 30), new Vector2(0, -30),
            new Vector2(-span, -90), Alpha(accent, 0), Alpha(accent, .19f));
        Quad(mesh, new Vector2(span, -90), new Vector2(0, -30), new Vector2(0, 30),
            new Vector2(span, 85), Alpha(accent, 0), Alpha(accent, .19f));
        Line(mesh, new Vector2(-span, 0), new Vector2(span, 0), low ? 10 : 38, Alpha(accent, .035f));
        Line(mesh, new Vector2(-span, 0), new Vector2(span, 0), 2, Alpha(accent, .5f));

        if (!low)
        {
            Orbit(mesh, span * .93f, 191, -10 + elapsed * 1.2f, .24f);
            Orbit(mesh, span * .84f, 158, 10 - elapsed * .8f, .12f);
        }

        // 左右対称の切り欠き枠。中央の細線が伸び、先端と山形の飾りは外へ平行移動する。
        for (int side = -1; side <= 1; side += 2)
        {
            float shoulder = Mathf.Max(0, span - 119);
            Border(mesh, side, span, shoulder, 137, 2.5f, .54f);
            Border(mesh, side, span - 22, Mathf.Max(0, shoulder - 15), 117, 1, .35f);
            float outer = Mathf.Max(0, span - 55);
            float rail = span * .78f;
            float step = Mathf.Min(177, span * .24f);
            for (int vertical = -1; vertical <= 1; vertical += 2)
            {
                Line(mesh, new Vector2(0, vertical * 158), new Vector2(side * step, vertical * 158), 3.5f, Rim(0, 1));
                Line(mesh, new Vector2(side * step, vertical * 158), new Vector2(side * (step + 22), vertical * 145), 3.5f, Rim(.3f, 1));
                Line(mesh, new Vector2(side * (step + 22), vertical * 145), new Vector2(side * rail, vertical * 145),
                    3.5f, Rim(.35f, 1), Rim(1, 1));
                Line(mesh, new Vector2(side * (step + 22), vertical * 156), new Vector2(side * (rail - 20), vertical * 156), 1, Rim(.5f, .42f));
                Line(mesh, new Vector2(side * (outer - 64), vertical * 108), new Vector2(side * (span - 38), 0), 8, Rim(.8f, .9f));
            }
            for (int i = 0; i < 4; i++)
            {
                float x = span - 71 + i * 29;
                Color color = Rim(.75f, (.70f - i * .14f) * openness);
                Vector2 top = new Vector2(side * (x - 54), 82);
                Vector2 point = new Vector2(side * (x + 15), 0);
                Vector2 bottom = new Vector2(side * (x - 54), -82);
                Line(mesh, top, point, 10, color);
                Line(mesh, point, bottom, 10, color);
            }
        }

        // 文字が開ききると光が一度だけ中央から外側へ走る。
        float sweep = Mathf.Clamp01((elapsed - .42f) / .72f);
        if (!low && sweep > 0 && sweep < 1)
        {
            Color glint = Alpha(new Color(1, 1, .88f), Mathf.Sin(sweep * Mathf.PI) * .85f);
            for (int side = -1; side <= 1; side += 2)
            {
                float x = side * sweep * (span - 40);
                Star(mesh, new Vector2(x, 144), 27, glint);
                Star(mesh, new Vector2(x, -144), 22, glint);
            }
        }
        float sparkleAlpha = Mathf.Clamp01((elapsed - .25f) / .4f);
        for (int i = 0; i < Stars.Length; i++)
        {
            Vector2 p = Stars[i];
            p.x *= span / 904f;
            float pulse = low ? .65f : .8f + .2f * Mathf.Sin(elapsed * 3 + i);
            Star(mesh, p, (i < 2 ? 29 : 18) * pulse, Alpha(new Color(1, 1, .9f), sparkleAlpha));
        }
        int count = low ? 12 : perfect ? 40 : 24;
        for (int i = 0; i < count; i++)
        {
            float x = (-730 + (i * 193 + 91) % 1460) * span / 904f;
            float y = i % 2 == 0 ? 170 + (i * 17) % 98 : -194 - (i * 13) % 68;
            if (!low) y += Mathf.Sign(y) * Mathf.Max(0, elapsed - .3f) * (3 + i % 4);
            float size = 1.5f + i % 3 * .6f;
            Color tint = perfect ? Color.Lerp(new Color(.77f, .98f, 1), new Color(1, .8f, .94f), i % 3 / 2f) : new Color(1, .91f, .65f);
            tint.a = (.3f + i % 4 * .15f) * sparkleAlpha;
            Diamond(mesh, new Vector2(x, y), size, tint);
        }
    }

    void Border(VertexHelper mesh, int side, float tip, float shoulder, float height, float width, float alpha)
    {
        Line(mesh, new Vector2(0, height), new Vector2(side * shoulder, height), width, Rim(0, alpha), Rim(.8f, alpha));
        Line(mesh, new Vector2(side * shoulder, height), new Vector2(side * tip, 0), width, Rim(.8f, alpha));
        Line(mesh, new Vector2(side * tip, 0), new Vector2(side * shoulder, -height), width, Rim(.8f, alpha));
        Line(mesh, new Vector2(side * shoulder, -height), new Vector2(0, -height), width, Rim(.8f, alpha), Rim(0, alpha));
    }

    Color Rim(float edge, float alpha)
    {
        Color bright = perfect ? new Color(.85f, .96f, 1) : new Color(1, .95f, .69f);
        Color dark = perfect ? new Color(.42f, .38f, .85f) : new Color(.67f, .39f, .08f);
        return Alpha(Color.Lerp(bright, dark, edge * edge), alpha);
    }

    void Orbit(VertexHelper mesh, float rx, float ry, float angle, float alpha)
    {
        float radians = angle * Mathf.Deg2Rad;
        Vector2 last = OrbitPoint(0, rx, ry, radians);
        for (int i = 1; i <= 100; i++)
        {
            Vector2 next = OrbitPoint(i * Mathf.PI * 2 / 100, rx, ry, radians);
            Line(mesh, last, next, 1.8f, Rim(Mathf.Abs(next.x) / Mathf.Max(1, rx), alpha));
            last = next;
        }
    }

    static Vector2 OrbitPoint(float t, float rx, float ry, float a)
    {
        float x = Mathf.Cos(t) * rx, y = Mathf.Sin(t) * ry;
        return new Vector2(x * Mathf.Cos(a) - y * Mathf.Sin(a), x * Mathf.Sin(a) + y * Mathf.Cos(a));
    }

    static void Halo(VertexHelper mesh, float rx, float ry, Color tint)
    {
        const int rings = 12;
        for (int ring = 0; ring < rings; ring++)
        {
            float inner = ring / (float)rings, outer = (ring + 1) / (float)rings;
            Color a = Alpha(tint, tint.a * Mathf.Pow(1 - inner, 2));
            Color b = Alpha(tint, tint.a * Mathf.Pow(1 - outer, 2));
            for (int i = 0; i < 64; i++)
            {
                float u = i * Mathf.PI * 2 / 64, v = (i + 1) * Mathf.PI * 2 / 64;
                Vector2 p = new Vector2(Mathf.Cos(u) * rx, Mathf.Sin(u) * ry);
                Vector2 q = new Vector2(Mathf.Cos(v) * rx, Mathf.Sin(v) * ry);
                Quad(mesh, p * inner, p * outer, q * outer, q * inner, a, b);
            }
        }
    }

    static void Star(VertexHelper mesh, Vector2 p, float size, Color tint)
    {
        Quad(mesh, p + Vector2.left * size, p + Vector2.up * 2.4f, p + Vector2.right * size, p + Vector2.down * 2.4f, tint, tint);
        Quad(mesh, p + Vector2.down * size, p + Vector2.left * 2.4f, p + Vector2.up * size, p + Vector2.right * 2.4f, tint, tint);
    }

    static void Diamond(VertexHelper mesh, Vector2 p, float size, Color tint)
    {
        Quad(mesh, p + Vector2.left * size, p + Vector2.up * size, p + Vector2.right * size, p + Vector2.down * size, tint, tint);
    }

    static Color Alpha(Color color, float alpha) { color.a = alpha; return color; }
    static void Line(VertexHelper mesh, Vector2 a, Vector2 b, float width, Color tint) => Line(mesh, a, b, width, tint, tint);
    static void Line(VertexHelper mesh, Vector2 a, Vector2 b, float width, Color start, Color end)
    {
        Vector2 normal = new Vector2(a.y - b.y, b.x - a.x).normalized * (width * .5f);
        Quad(mesh, a - normal, b - normal, b + normal, a + normal, start, end);
    }

    // 四角の両端に別々の色を与え、グラデーションもメッシュ内で完結させる。
    static void Quad(VertexHelper mesh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color start, Color end)
    {
        int at = mesh.currentVertCount;
        mesh.AddVert(a, start, Vector2.zero); mesh.AddVert(b, end, Vector2.zero);
        mesh.AddVert(c, end, Vector2.zero); mesh.AddVert(d, start, Vector2.zero);
        mesh.AddTriangle(at, at + 1, at + 2); mesh.AddTriangle(at, at + 2, at + 3);
    }
}
