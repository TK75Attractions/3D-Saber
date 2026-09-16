using UnityEngine;
using UnityEngine.UI;

// 候補B。門の面と重なりで奥行きを作り、二段のロゴを空間の入口に置く。
// ゲーム開始の操作は既存の開始ノーツに任せ、装飾は入力を受け取らない。
public static class TitleConceptB
{
    public static void Build(Transform parent)
    {
        var background = new GameObject("GateSpace", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(parent, false);
        Stretch(background.GetComponent<RectTransform>());
        var baseImage = background.GetComponent<Image>();
        baseImage.color = new Color(.004f, .009f, .018f, 1f);
        baseImage.raycastTarget = false;

        Glow(parent, "LeftGateAtmosphere", new Vector2(-770f, -20f), new Vector2(1100f, 1350f),
            new Color(.72f, .035f, .13f, .13f));
        Glow(parent, "RightGateAtmosphere", new Vector2(770f, -20f), new Vector2(1100f, 1350f),
            new Color(.015f, .37f, .72f, .19f));
        Glow(parent, "GateFloor", new Vector2(0f, -370f), new Vector2(720f, 170f),
            new Color(.05f, .45f, .57f, .12f));

        var gate = new GameObject("ArchitecturalGates", typeof(RectTransform), typeof(TitleConceptBGateGraphic));
        gate.transform.SetParent(parent, false);
        Stretch(gate.GetComponent<RectTransform>());
        gate.GetComponent<TitleConceptBGateGraphic>().raycastTarget = false;

        // BEATとTRACEを一行に組み、動作を表すSLASHを大きく独立させる。
        // 同じ字形で空間との関係だけを比較できるよう、既存の専用メッシュを再利用する。
        Word(parent, "BEAT", new Vector2(-302f, 345f), new Vector2(500f, 105f),
            new Color(1f, .13f, .25f));
        Word(parent, "TRACE", new Vector2(265f, 345f), new Vector2(615f, 105f),
            new Color(.12f, .72f, 1f));
        Word(parent, "SLASH", new Vector2(0f, 192f), new Vector2(1080f, 176f),
            new Color(.18f, 1f, .56f));
    }

    static void Word(Transform parent, string word, Vector2 position, Vector2 size, Color accent)
    {
        var go = new GameObject("GateLogo_" + word, typeof(RectTransform), typeof(TitleWordmark));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
        go.GetComponent<TitleWordmark>().Configure(word, accent);
    }

    static void Glow(Transform parent, string name, Vector2 position, Vector2 size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
        var image = go.GetComponent<Image>();
        image.sprite = UISkinKit.SoftGlow();
        image.color = color;
        image.raycastTarget = false;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}

[RequireComponent(typeof(CanvasRenderer))]
public sealed class TitleConceptBGateGraphic : MaskableGraphic
{
    static readonly Color Red = new Color(1f, .09f, .23f);
    static readonly Color Blue = new Color(.06f, .63f, 1f);

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        // 格子線の代わりに、床の一枚の面と二本の反射を使う。
        Quad(vh, new Vector2(-162f, -314f), new Vector2(162f, -314f),
            new Vector2(510f, -570f), new Vector2(-510f, -570f),
            new Color(.017f, .045f, .057f, 1f), new Color(.006f, .014f, .026f, 1f));
        Segment(vh, new Vector2(-162f, -314f), new Vector2(-510f, -570f), 2f,
            new Color(Red.r, Red.g, Red.b, .23f));
        Segment(vh, new Vector2(162f, -314f), new Vector2(510f, -570f), 2f,
            new Color(Blue.r, Blue.g, Blue.b, .3f));

        for (int side = -1; side <= 1; side += 2)
        {
            Color accent = side < 0 ? Red : Blue;
            // 奥から描く。中央の開始ノーツと四隅の目印を囲む余白は空ける。
            Gate(vh, side, accent, .3f, 13f, new[] {
                new Vector2(385f, -385f), new Vector2(322f, -95f),
                new Vector2(225f, -20f), new Vector2(168f, -20f) });
            Gate(vh, side, accent, .52f, 25f, new[] {
                new Vector2(640f, -580f), new Vector2(515f, -5f),
                new Vector2(390f, 72f), new Vector2(185f, 72f) });
            Gate(vh, side, accent, .9f, 44f, new[] {
                new Vector2(1050f, -580f), new Vector2(890f, 130f),
                new Vector2(635f, 470f), new Vector2(380f, 470f) });

            // 無意味な文字や全面の目盛りを置かず、門の接合部だけを示す。
            for (int index = 0; index < 3; index++)
            {
                float y = 18f - index * 13f;
                Segment(vh, new Vector2(side * 853f, y), new Vector2(side * 880f, y),
                    3f, new Color(accent.r, accent.g, accent.b, index == 1 ? .62f : .22f));
            }
        }
    }

    static void Gate(VertexHelper vh, int side, Color accent, float strength, float width, Vector2[] path)
    {
        for (int i = 0; i < path.Length; i++) path[i].x *= side;
        Color body = Color.Lerp(new Color(.014f, .019f, .031f), accent, .047f * strength);
        body.a = 1f;
        Color bevel = Color.Lerp(new Color(.019f, .029f, .047f), accent, .065f * strength);
        bevel.a = 1f;

        // 面幅を持つフレーム、斜めの側面、発光する内側の縁を分ける。
        Vector2 sideOffset = new Vector2(side * width * .6f, -width * .25f);
        Stroke(vh, path, width * 1.3f, bevel, sideOffset);
        Stroke(vh, path, width, body, Vector2.zero);
        var edge = new Color(accent.r, accent.g, accent.b, strength);
        Vector2 lightOffset = new Vector2(-side * width * .38f, 0f);
        Stroke(vh, path, 10f * strength, new Color(accent.r, accent.g, accent.b, .075f * strength), lightOffset);
        // 最奥の線も720pで一画素を保ち、距離による弱さは主に明度で表す。
        Stroke(vh, path, Mathf.Max(1.5f, 2.7f * strength), edge, lightOffset);
    }

    static void Stroke(VertexHelper vh, Vector2[] points, float width, Color tint, Vector2 offset)
    {
        int first = vh.currentVertCount;
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 before = i > 0 ? (points[i] - points[i - 1]).normalized
                : (points[1] - points[0]).normalized;
            Vector2 after = i + 1 < points.Length ? (points[i + 1] - points[i]).normalized : before;
            Vector2 n0 = new Vector2(-before.y, before.x);
            Vector2 n1 = new Vector2(-after.y, after.x);
            Vector2 miter = (n0 + n1).normalized;
            Vector2 edge = miter * (width * .5f / Mathf.Max(.35f, Vector2.Dot(miter, n0)));
            vh.AddVert(points[i] + edge + offset, tint, Vector2.zero);
            vh.AddVert(points[i] - edge + offset, tint, Vector2.zero);
            if (i == 0) continue;
            int at = first + i * 2;
            vh.AddTriangle(at - 2, at, at - 1);
            vh.AddTriangle(at - 1, at, at + 1);
        }
    }

    static void Segment(VertexHelper vh, Vector2 from, Vector2 to, float width, Color color)
    {
        Vector2 direction = (to - from).normalized;
        Vector2 edge = new Vector2(-direction.y, direction.x) * width * .5f;
        Quad(vh, from + edge, to + edge, to - edge, from - edge, color, color);
    }

    static void Quad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color top, Color bottom)
    {
        int index = vh.currentVertCount;
        vh.AddVert(a, top, Vector2.zero);
        vh.AddVert(b, top, Vector2.zero);
        vh.AddVert(c, bottom, Vector2.zero);
        vh.AddVert(d, bottom, Vector2.zero);
        vh.AddTriangle(index, index + 1, index + 2);
        vh.AddTriangle(index, index + 2, index + 3);
    }
}
