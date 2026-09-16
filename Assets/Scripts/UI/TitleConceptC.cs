using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// 候補C「刃と残光」。字形と軌跡を同じ細い刃先で揃え、操作対象の周囲に余白を残す。
public static class TitleConceptC
{
    static readonly Color Red = new Color(1f, .11f, .24f);
    static readonly Color Blue = new Color(.06f, .70f, 1f);
    static readonly Color Green = new Color(.13f, 1f, .54f);

    public static void Build(Transform parent)
    {
        var background = Image(parent, "C_DarkSpace", Vector2.zero, new Vector2(1920, 1080),
            new Color(.004f, .007f, .015f));
        background.rectTransform.anchorMin = Vector2.zero;
        background.rectTransform.anchorMax = Vector2.one;
        background.rectTransform.sizeDelta = Vector2.zero;
        Glow(parent, "C_RedAtmosphere", new Vector2(-820, 35), new Vector2(1240, 1550), WithAlpha(Red, .105f));
        Glow(parent, "C_BlueAtmosphere", new Vector2(835, -70), new Vector2(1330, 1610), WithAlpha(Blue, .12f));
        Glow(parent, "C_FloorLight", new Vector2(0, -365), new Vector2(640, 140), new Color(.03f, .50f, .7f, .095f));

        Trail(parent, "C_LeftBlade", Red, new Vector2(-1090, -440), new Vector2(-555, -455),
            new Vector2(-930, 390), new Vector2(-380, 545), 13f, .18f);
        Trail(parent, "C_RightBlade", Blue, new Vector2(1110, -525), new Vector2(620, -555),
            new Vector2(960, 180), new Vector2(560, 495), 15f, .63f);

        // 床は全面の格子にせず、刃が通った跡を二本だけ置く。
        Line(parent, "C_FloorRed", new Vector2(-865, -535), new Vector2(-172, -312), 1.1f, WithAlpha(Red, .20f));
        Line(parent, "C_FloorBlue", new Vector2(865, -535), new Vector2(172, -312), 1.1f, WithAlpha(Blue, .24f));
        Line(parent, "C_FloorEcho", new Vector2(-390, -480), new Vector2(390, -480), 1f, WithAlpha(Blue, .08f));

        Word(parent, "BEAT", new Vector2(-145, 355), Red);
        Word(parent, "TRACE", new Vector2(0, 237), Blue);
        Word(parent, "SLASH", new Vector2(145, 119), Green);

        // 三段の右送りを示す短い刃先。ロゴの字面や切るノーツに線を重ねない。
        Line(parent, "C_RedBladeTip", new Vector2(165, 356), new Vector2(300, 378), 2f, WithAlpha(Red, .54f));
        Line(parent, "C_BlueBladeTip", new Vector2(365, 238), new Vector2(470, 255), 2f, WithAlpha(Blue, .45f));
        Line(parent, "C_GreenBladeTip", new Vector2(-305, 102), new Vector2(-215, 118), 2f, WithAlpha(Green, .37f));
    }

    static void Word(Transform parent, string text, Vector2 position, Color color)
    {
        var go = new GameObject("C_Word_" + text, typeof(RectTransform), typeof(TitleConceptCWordmark));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchoredPosition = position;
        rect.sizeDelta = new Vector2(810, 93);
        go.GetComponent<TitleConceptCWordmark>().Configure(text, color);
    }

    static void Trail(Transform parent, string name, Color color, Vector2 a, Vector2 b, Vector2 c, Vector2 d,
        float seconds, float phase)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TitleConceptCTrail));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(1920, 1080);
        go.GetComponent<TitleConceptCTrail>().Configure(color, a, b, c, d, seconds, phase);
    }

    static Image Image(Transform parent, string name, Vector2 position, Vector2 size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.rectTransform.anchoredPosition = position;
        image.rectTransform.sizeDelta = size;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    static void Glow(Transform parent, string name, Vector2 position, Vector2 size, Color color)
    {
        Image(parent, name, position, size, color).sprite = UISkinKit.SoftGlow();
    }

    static void Line(Transform parent, string name, Vector2 from, Vector2 to, float width, Color color)
    {
        Vector2 delta = to - from;
        var image = Image(parent, name, (from + to) * .5f, new Vector2(delta.magnitude, width), color);
        image.rectTransform.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
    }

    static Color WithAlpha(Color color, float alpha) { color.a = alpha; return color; }
}

// 固定された薄い刃と、その上を一方向に進む短い残光。点滅やタイトル自体の移動は行わない。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class TitleConceptCTrail : MaskableGraphic
{
    Vector2 a, b, c, d;
    float period = 13f;
    float initialPhase;
    float age;

    public void Configure(Color tint, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float seconds, float phase)
    {
        color = tint;
        a = p0; b = p1; c = p2; d = p3;
        period = seconds;
        initialPhase = phase;
        raycastTarget = false;
        SetVerticesDirty();
    }

    void Update()
    {
        age += Time.unscaledDeltaTime;
        SetVerticesDirty();
    }

    Vector2 Point(float t)
    {
        float u = 1f - t;
        return u * u * u * a + 3f * u * u * t * b + 3f * u * t * t * c + t * t * t * d;
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Strip(vh, 0, 1, 23f, .035f, false);
        Strip(vh, 0, 1, 4.5f, .28f, false);
        Strip(vh, 0, 1, 1.25f, .82f, false);
        float head = Mathf.Repeat(initialPhase + age / period, 1f);
        float visible = Mathf.SmoothStep(0, 1, Mathf.Min(head / .10f, (1f - head) / .10f));
        Strip(vh, Mathf.Max(0, head - .20f), head, 8f, .10f * visible, true);
        Strip(vh, Mathf.Max(0, head - .16f), head, 2.5f, .75f * visible, true);
    }

    void Strip(VertexHelper vh, float from, float to, float width, float alpha, bool taper)
    {
        if (to - from < .0001f) return;
        const int samples = 72;
        int first = vh.currentVertCount;
        for (int i = 0; i <= samples; i++)
        {
            float progress = i / (float)samples;
            float t = Mathf.Lerp(from, to, progress);
            Vector2 p = Point(t);
            Vector2 tangent = Point(Mathf.Min(1f, t + .001f)) - Point(Mathf.Max(0, t - .001f));
            Vector2 edge = new Vector2(-tangent.y, tangent.x).normalized * width * .5f;
            float weight = taper ? Mathf.Sin(progress * Mathf.PI) * progress : Mathf.Sin(t * Mathf.PI);
            Color tint = color;
            tint.a *= alpha * weight;
            vh.AddVert(p + edge, tint, Vector2.zero);
            vh.AddVert(p - edge, tint, Vector2.zero);
            if (i == 0) continue;
            int at = first + i * 2;
            vh.AddTriangle(at - 2, at, at - 1);
            vh.AddTriangle(at - 1, at, at + 1);
        }
    }
}

// 横に長い直立字形を専用の線メッシュで構成。すべての文字に同じ線幅と浅い面取りを使う。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class TitleConceptCWordmark : MaskableGraphic
{
    const float Stroke = 7.5f;
    const float Tracking = 22f;
    const float GlyphWidth = 90f;
    const float Cap = 70f;
    string word;

    static Vector2[] Path(params float[] values)
    {
        var result = new Vector2[values.Length / 2];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector2(values[i * 2], values[i * 2 + 1]);
        return result;
    }

    static readonly Dictionary<char, Vector2[][]> Letters = new Dictionary<char, Vector2[][]>
    {
        ['B'] = new[] { Path(0, 0, 0, 70), Path(0, 70, 75, 70, 88, 57, 88, 48, 75, 35, 0, 35), Path(75, 35, 88, 22, 88, 13, 75, 0, 0, 0) },
        ['E'] = new[] { Path(90, 70, 13, 70, 0, 57, 0, 13, 13, 0, 90, 0), Path(0, 35, 70, 35) },
        ['A'] = new[] { Path(0, 0, 0, 54, 16, 70, 74, 70, 90, 54, 90, 0), Path(0, 27, 90, 27) },
        ['T'] = new[] { Path(0, 70, 90, 70), Path(45, 70, 45, 0) },
        ['R'] = new[] { Path(0, 0, 0, 70, 75, 70, 88, 57, 88, 48, 75, 35, 0, 35), Path(51, 35, 90, 0) },
        ['C'] = new[] { Path(90, 60, 80, 70, 13, 70, 0, 57, 0, 13, 13, 0, 80, 0, 90, 10) },
        ['S'] = new[] { Path(90, 70, 13, 70, 0, 57, 0, 47, 13, 35, 77, 35, 90, 23, 90, 13, 77, 0, 0, 0) },
        ['L'] = new[] { Path(0, 70, 0, 13, 13, 0, 90, 0) },
        ['H'] = new[] { Path(0, 0, 0, 70), Path(90, 0, 90, 70), Path(0, 35, 90, 35) }
    };

    public void Configure(string text, Color tint)
    {
        word = text;
        color = tint;
        raycastTarget = false;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (string.IsNullOrEmpty(word)) return;
        float width = word.Length * (GlyphWidth + Tracking) - Tracking;
        Rect bounds = GetPixelAdjustedRect();
        float scale = Mathf.Min(bounds.height / (Cap + Stroke + 6f), bounds.width / (width + Stroke + 6f));
        Vector2 origin = bounds.center - new Vector2(width, Cap) * scale * .5f;
        foreach (char letter in word)
        {
            if (!Letters.TryGetValue(letter, out Vector2[][] paths)) continue;
            foreach (Vector2[] path in paths) DrawPath(vh, path, origin, scale);
            origin.x += (GlyphWidth + Tracking) * scale;
        }
    }

    void DrawPath(VertexHelper vh, Vector2[] path, Vector2 origin, float scale)
    {
        int first = vh.currentVertCount;
        for (int i = 0; i < path.Length; i++)
        {
            Vector2 before = i > 0 ? (path[i] - path[i - 1]).normalized : (path[1] - path[0]).normalized;
            Vector2 after = i + 1 < path.Length ? (path[i + 1] - path[i]).normalized : before;
            Vector2 normalBefore = new Vector2(-before.y, before.x);
            Vector2 normalAfter = new Vector2(-after.y, after.x);
            Vector2 miter = (normalBefore + normalAfter).normalized;
            Vector2 edge = miter * (Stroke * .5f / Mathf.Max(.4f, Vector2.Dot(miter, normalBefore)));
            Vector2 point = path[i];
            if (i == 0) point -= after * Stroke * .5f;
            if (i == path.Length - 1) point += before * Stroke * .5f;
            // 平面の色を主体にし、上側の刃先だけ僅かに明るくする。
            Color tint = Color.Lerp(color * .82f, Color.Lerp(color, Color.white, .12f), point.y / Cap);
            tint.a = color.a;
            vh.AddVert(origin + (point + edge) * scale, tint, Vector2.zero);
            vh.AddVert(origin + (point - edge) * scale, tint, Vector2.zero);
            if (i == 0) continue;
            int at = first + i * 2;
            vh.AddTriangle(at - 2, at, at - 1);
            vh.AddTriangle(at - 1, at, at + 1);
        }
    }
}
