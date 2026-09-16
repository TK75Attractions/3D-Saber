using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// 候補A。太い金属帯を切り抜いた字形と、左右の機構だけで構成する。
// メニュー操作は親が持ち、この階層の描画要素は入力を受け取らない。
public static class TitleConceptA
{
    static readonly Color Red = new Color(1f, .09f, .22f);
    static readonly Color Blue = new Color(.04f, .67f, 1f);
    static readonly Color Green = new Color(.07f, .97f, .49f);

    public static void Build(Transform parent)
    {
        Image baseImage = Image(parent, "A_Space", Vector2.zero, new Vector2(2400f, 1600f),
            new Color(.003f, .006f, .014f, 1f));
        var rt = baseImage.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        Glow(parent, "A_RedReflection", new Vector2(-900f, 110f), new Vector2(1450f, 1560f), new Color(Red.r, Red.g, Red.b, .10f));
        Glow(parent, "A_BlueReflection", new Vector2(900f, 110f), new Vector2(1450f, 1560f), new Color(Blue.r, Blue.g, Blue.b, .14f));
        Glow(parent, "A_FloorReflection", new Vector2(0f, -340f), new Vector2(800f, 410f), new Color(.04f, .35f, .62f, .12f));

        for (int side = -1; side <= 1; side += 2)
        {
            Color accent = side < 0 ? Red : Blue;
            Vector2 a = new Vector2(side * 878f, 430f);
            Vector2 b = new Vector2(side * 748f, 254f);
            Vector2 c = new Vector2(side * 748f, -242f);
            Vector2 d = new Vector2(side * 918f, -512f);
            Color housing = new Color(accent.r * .025f, accent.g * .025f, accent.b * .025f, 1f);
            Line(parent, "A_UpperHousing", a, b, 28f, housing);
            Line(parent, "A_VerticalHousing", b, c, 28f, housing);
            Line(parent, "A_LowerHousing", c, d, 28f, housing);
            Color edge = new Color(accent.r, accent.g, accent.b, .42f);
            Line(parent, "A_UpperRail", a, b, 2.4f, edge);
            Line(parent, "A_VerticalRail", b, c, 2.4f, edge);
            Line(parent, "A_LowerRail", c, d, 2.4f, edge);
            Line(parent, "A_Header", new Vector2(side * 948f, 450f), new Vector2(side * 798f, 450f), 2f,
                new Color(accent.r, accent.g, accent.b, .18f));

            // 接合部だけに短い刻みを置き、意味のない装飾文字は足さない。
            for (int mark = 0; mark < 3; mark++)
            {
                float y = 169f - mark * 15f;
                Line(parent, "A_Joint", new Vector2(side * 710f, y), new Vector2(side * 686f, y), 3f,
                    new Color(accent.r, accent.g, accent.b, mark == 1 ? .43f : .16f));
            }
            Line(parent, "A_FarFloor", new Vector2(side * 205f, -89f), new Vector2(side * 670f, -89f), 1.2f,
                new Color(.07f, .40f, .58f, .22f));
        }

        // 床は薄く保ち、中央のキューブとその四隅を主役にする。
        Vector2 vanishingPoint = new Vector2(0f, -89f);
        Color grid = new Color(.06f, .30f, .47f, .14f);
        for (int ray = -4; ray <= 4; ray++)
            Line(parent, "A_FloorRay", vanishingPoint, new Vector2(ray * 260f, -590f), 1.1f, grid);
        for (int row = 1; row <= 6; row++)
        {
            float depth = row * row / 36f;
            float halfWidth = Mathf.Lerp(80f, 1050f, depth);
            float y = Mathf.Lerp(-89f, -600f, depth);
            Line(parent, "A_FloorStep", new Vector2(-halfWidth, y), new Vector2(halfWidth, y), 1.1f, grid);
        }

        Word(parent, "BEAT", new Vector2(0f, 371f), Red);
        Word(parent, "TRACE", new Vector2(0f, 231f), Blue);
        Word(parent, "SLASH", new Vector2(0f, 91f), Green);
    }

    static void Word(Transform parent, string value, Vector2 position, Color accent)
    {
        var go = new GameObject("A_Word_" + value, typeof(RectTransform), typeof(TitleConceptAWordmark));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = position;
        rt.sizeDelta = new Vector2(900f, 126f);
        go.GetComponent<TitleConceptAWordmark>().Configure(value, accent);
    }

    static Image Image(Transform parent, string name, Vector2 position, Vector2 size, Color tint)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.rectTransform.anchoredPosition = position;
        image.rectTransform.sizeDelta = size;
        image.color = tint;
        image.raycastTarget = false;
        return image;
    }

    static void Glow(Transform parent, string name, Vector2 position, Vector2 size, Color tint)
    {
        Image(parent, name, position, size, tint).sprite = UISkinKit.SoftGlow();
    }

    static void Line(Transform parent, string name, Vector2 from, Vector2 to, float width, Color tint)
    {
        Image image = Image(parent, name, (from + to) * .5f, new Vector2((to - from).magnitude, width), tint);
        Vector2 delta = to - from;
        image.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
    }
}

// 文字ごとの骨格と共通の切削規則から、解像度に依存しないメッシュを作る。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class TitleConceptAWordmark : MaskableGraphic
{
    const float Stroke = 23f;
    const float Height = 96f;
    const float Tracking = 16f;
    string word = "BEAT";

    sealed class Glyph
    {
        public readonly float width;
        public readonly Vector2[][] paths;
        public Glyph(float width, params Vector2[][] paths) { this.width = width; this.paths = paths; }
    }

    static Vector2[] Path(params float[] values)
    {
        var path = new Vector2[values.Length / 2];
        for (int i = 0; i < path.Length; i++) path[i] = new Vector2(values[i * 2], values[i * 2 + 1]);
        return path;
    }

    static readonly Dictionary<char, Glyph> Glyphs = new Dictionary<char, Glyph>
    {
        ['B'] = new Glyph(126f, Path(14, 0, 14, 96),
            Path(14, 96, 91, 96, 110, 79, 110, 64, 93, 49, 14, 49),
            Path(93, 49, 113, 32, 113, 17, 95, 0, 14, 0)),
        ['E'] = new Glyph(124f, Path(110, 96, 32, 96, 14, 78, 14, 18, 32, 0, 110, 0),
            Path(14, 48, 94, 48)),
        ['A'] = new Glyph(132f, Path(14, 0, 14, 75, 35, 96, 97, 96, 118, 75, 118, 0),
            Path(14, 35, 118, 35)),
        ['T'] = new Glyph(136f, Path(0, 96, 136, 96), Path(68, 96, 68, 0)),
        ['R'] = new Glyph(132f, Path(14, 0, 14, 96, 94, 96, 114, 78, 114, 66, 96, 49, 14, 49),
            Path(60, 45, 117, 0)),
        ['C'] = new Glyph(128f, Path(110, 83, 97, 96, 34, 96, 14, 76, 14, 20, 34, 0, 97, 0, 110, 13)),
        ['S'] = new Glyph(128f, Path(110, 82, 96, 96, 34, 96, 14, 76, 14, 64, 30, 48, 96, 48, 112, 32, 112, 18, 94, 0, 28, 0, 14, 14)),
        ['L'] = new Glyph(116f, Path(14, 96, 14, 20, 34, 0, 110, 0)),
        ['H'] = new Glyph(132f, Path(14, 0, 14, 96), Path(118, 0, 118, 96), Path(14, 48, 118, 48))
    };

    public void Configure(string value, Color accent)
    {
        word = value;
        color = accent;
        raycastTarget = false;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (string.IsNullOrEmpty(word)) return;
        float width = -Tracking;
        foreach (char letter in word)
        {
            if (!Glyphs.TryGetValue(letter, out Glyph glyph)) return;
            width += glyph.width + Tracking;
        }
        Rect rect = GetPixelAdjustedRect();
        float scale = Mathf.Min(rect.width / (width + Stroke + 8f), rect.height / (Height + Stroke + 6f));
        Vector2 origin = rect.center - new Vector2(width, Height) * (.5f * scale);
        DrawWord(vh, origin + new Vector2(2f, -2f) * scale, scale, true);
        DrawWord(vh, origin, scale, false);
    }

    void DrawWord(VertexHelper vh, Vector2 origin, float scale, bool depth)
    {
        foreach (char letter in word)
        {
            Glyph glyph = Glyphs[letter];
            foreach (Vector2[] path in glyph.paths)
            {
                var left = new Vector2[path.Length];
                var right = new Vector2[path.Length];
                for (int i = 0; i < path.Length; i++)
                {
                    Vector2 before = i > 0 ? (path[i] - path[i - 1]).normalized : (path[1] - path[0]).normalized;
                    Vector2 after = i + 1 < path.Length ? (path[i + 1] - path[i]).normalized : before;
                    Vector2 normalBefore = new Vector2(-before.y, before.x);
                    Vector2 normalAfter = new Vector2(-after.y, after.x);
                    Vector2 miter = (normalBefore + normalAfter).normalized;
                    Vector2 edge = miter * (Stroke * .5f / Mathf.Max(.4f, Vector2.Dot(miter, normalBefore)));
                    // 直線終端も半線幅だけ延長し、T/Hと折れ線の文字の天地を揃える。
                    Vector2 center = path[i];
                    if (i == 0) center -= after * (Stroke * .5f);
                    if (i == path.Length - 1) center += before * (Stroke * .5f);
                    left[i] = center + edge;
                    right[i] = center - edge;
                }
                for (int i = 1; i < path.Length; i++)
                {
                    var quad = new List<Vector2> { left[i - 1], left[i], right[i], right[i - 1] };
                    // 全文字で同じ幅・傾斜の切断。A/Rの横棒や脚にはかからない高さ。
                    Fill(vh, Clip(quad, 61.5f, false), origin, scale, depth);
                    Fill(vh, Clip(quad, 68.5f, true), origin, scale, depth);
                }
            }
            origin.x += (glyph.width + Tracking) * scale;
        }
    }

    static List<Vector2> Clip(List<Vector2> polygon, float intercept, bool above)
    {
        var result = new List<Vector2>(6);
        if (polygon.Count == 0) return result;
        Vector2 previous = polygon[polygon.Count - 1];
        float previousDistance = previous.y - previous.x * .08f - intercept;
        bool previousInside = above ? previousDistance >= 0f : previousDistance <= 0f;
        foreach (Vector2 current in polygon)
        {
            float distance = current.y - current.x * .08f - intercept;
            bool inside = above ? distance >= 0f : distance <= 0f;
            if (inside != previousInside)
            {
                float ratio = previousDistance / (previousDistance - distance);
                result.Add(Vector2.LerpUnclamped(previous, current, ratio));
            }
            if (inside) result.Add(current);
            previous = current;
            previousDistance = distance;
            previousInside = inside;
        }
        return result;
    }

    void Fill(VertexHelper vh, List<Vector2> polygon, Vector2 origin, float scale, bool depth)
    {
        if (polygon.Count < 3) return;
        int start = vh.currentVertCount;
        foreach (Vector2 point in polygon)
        {
            Color tint = depth ? new Color(color.r * .18f, color.g * .18f, color.b * .18f, color.a)
                : Color.Lerp(new Color(color.r * .75f, color.g * .75f, color.b * .75f, color.a),
                    Color.Lerp(color, Color.white, .06f), Mathf.Clamp01(point.y / Height));
            tint.a = color.a;
            vh.AddVert(origin + point * scale, tint, Vector2.zero);
        }
        for (int i = 2; i < polygon.Count; i++) vh.AddTriangle(start, start + i - 1, start + i);
    }
}
