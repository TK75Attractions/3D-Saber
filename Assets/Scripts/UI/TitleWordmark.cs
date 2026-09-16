using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// タイトル専用の字形。既存フォントを変形せず、9文字を面取りした線のメッシュで組む。
// 座標はベースラインから上向き。文字幅・線幅・角の切り方を全行で共有する。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class TitleWordmark : MaskableGraphic
{
    const float StrokeWidth = 13f;
    const float CapHeight = 92f;
    const float Tracking = 22f;
    [SerializeField] string word = "BEAT";

    sealed class Glyph
    {
        public readonly float width;
        public readonly Vector2[][] strokes;
        public Glyph(float width, params Vector2[][] strokes) { this.width = width; this.strokes = strokes; }
    }

    static Vector2[] Path(params float[] coordinates)
    {
        var result = new Vector2[coordinates.Length / 2];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector2(coordinates[i * 2], coordinates[i * 2 + 1]);
        return result;
    }

    static readonly Dictionary<char, Glyph> Glyphs = new Dictionary<char, Glyph>
    {
        ['B'] = new Glyph(96,
            Path(8, 0, 8, 92),
            Path(8, 92, 72, 92, 88, 76, 88, 60, 74, 46, 8, 46),
            Path(74, 46, 90, 30, 90, 16, 74, 0, 8, 0)),
        ['E'] = new Glyph(96,
            Path(94, 92, 24, 92, 8, 76, 8, 16, 24, 0, 94, 0),
            Path(8, 46, 78, 46)),
        ['A'] = new Glyph(100,
            Path(8, 0, 8, 68, 32, 92, 68, 92, 92, 68, 92, 0),
            Path(8, 36, 92, 36)),
        ['T'] = new Glyph(104,
            Path(0, 92, 104, 92),
            Path(52, 92, 52, 0)),
        ['R'] = new Glyph(100,
            Path(8, 0, 8, 92, 74, 92, 92, 74, 92, 62, 76, 46, 8, 46),
            Path(51, 43, 94, 0)),
        ['C'] = new Glyph(96,
            Path(90, 82, 80, 92, 24, 92, 8, 76, 8, 16, 24, 0, 80, 0, 90, 10)),
        ['S'] = new Glyph(100,
            Path(92, 82, 82, 92, 24, 92, 8, 76, 8, 60, 22, 46, 78, 46, 92, 32, 92, 16, 76, 0, 18, 0, 8, 10)),
        ['L'] = new Glyph(90,
            Path(8, 92, 8, 16, 24, 0, 90, 0)),
        ['H'] = new Glyph(100,
            Path(8, 0, 8, 92),
            Path(92, 0, 92, 92),
            Path(8, 46, 92, 46))
    };

    public string Word => word;

    public void Configure(string titleWord, Color accent)
    {
        word = titleWord;
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
            if (!Glyphs.TryGetValue(letter, out var glyph)) return;
            width += glyph.width + Tracking;
        }
        Rect rect = GetPixelAdjustedRect();
        float scale = Mathf.Min(rect.width / (width + StrokeWidth + 8f), rect.height / (CapHeight + StrokeWidth + 8f));
        Vector2 origin = rect.center - new Vector2(width, CapHeight) * scale * .5f;

        // わずかな側面で厚みを出す。強いぼかしや太い疑似斜体は使わない。
        DrawWord(vh, origin + new Vector2(2f, -3f) * scale, scale, true);
        DrawWord(vh, origin, scale, false);
    }

    void DrawWord(VertexHelper vh, Vector2 origin, float scale, bool depth)
    {
        foreach (char letter in word)
        {
            Glyph glyph = Glyphs[letter];
            foreach (Vector2[] path in glyph.strokes) DrawStroke(vh, path, origin, scale, depth);
            origin.x += (glyph.width + Tracking) * scale;
        }
    }

    void DrawStroke(VertexHelper vh, Vector2[] path, Vector2 origin, float scale, bool depth)
    {
        int first = vh.currentVertCount;
        for (int i = 0; i < path.Length; i++)
        {
            Vector2 before = i > 0 ? (path[i] - path[i - 1]).normalized : (path[1] - path[0]).normalized;
            Vector2 after = i + 1 < path.Length ? (path[i + 1] - path[i]).normalized : before;
            Vector2 n0 = new Vector2(-before.y, before.x);
            Vector2 n1 = new Vector2(-after.y, after.x);
            Vector2 miter = (n0 + n1).normalized;
            Vector2 edge = miter * (StrokeWidth * .5f / Mathf.Max(.35f, Vector2.Dot(miter, n0)));
            // 直線の端も半線幅だけ伸ばし、H/Tの天地と折れ曲がる文字の天地を揃える。
            Vector2 center = path[i];
            if (i == 0) center -= after * (StrokeWidth * .5f);
            if (i == path.Length - 1) center += before * (StrokeWidth * .5f);
            AddVertex(vh, center + edge, origin, scale, depth);
            AddVertex(vh, center - edge, origin, scale, depth);
            if (i == 0) continue;
            int at = first + i * 2;
            vh.AddTriangle(at - 2, at, at - 1);
            vh.AddTriangle(at - 1, at, at + 1);
        }
    }

    void AddVertex(VertexHelper vh, Vector2 point, Vector2 origin, float scale, bool depth)
    {
        Color tint = depth ? new Color(color.r * .19f, color.g * .19f, color.b * .19f, color.a)
            : Color.Lerp(new Color(color.r * .82f, color.g * .82f, color.b * .82f, color.a),
                Color.Lerp(color, Color.white, .16f), Mathf.Clamp01(point.y / CapHeight));
        tint.a = color.a;
        vh.AddVert(origin + point * scale, tint, Vector2.zero);
    }
}
