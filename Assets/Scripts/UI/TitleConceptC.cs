using UnityEngine;
using UnityEngine.UI;

// 背景C「刃と残光」。操作対象と共通ロゴの周囲に余白を残す。
public static class TitleConceptC
{
    static readonly Color Red = new Color(1f, .11f, .24f);
    static readonly Color Blue = new Color(.06f, .70f, 1f);
    public static void Build(Transform parent) => BuildBackground(parent);

    public static void BuildBackground(Transform parent)
    {
        var root = new GameObject("C_Presentation", typeof(RectTransform), typeof(TitleConceptCPresentation));
        root.transform.SetParent(parent, false);
        Stretch(root.GetComponent<RectTransform>());
        parent = root.transform;
        var background = Image(parent, "C_DarkSpace", Vector2.zero, new Vector2(1920, 1080),
            new Color(.004f, .007f, .015f));
        background.rectTransform.anchorMin = Vector2.zero;
        background.rectTransform.anchorMax = Vector2.one;
        background.rectTransform.sizeDelta = Vector2.zero;
        var atmosphere = Group(parent, "C_Atmosphere");
        Glow(atmosphere.transform, "C_RedAtmosphere", new Vector2(-820, 35), new Vector2(1240, 1550), WithAlpha(Red, .105f));
        Glow(atmosphere.transform, "C_BlueAtmosphere", new Vector2(835, -70), new Vector2(1330, 1610), WithAlpha(Blue, .12f));
        Glow(atmosphere.transform, "C_FloorLight", new Vector2(0, -365), new Vector2(640, 140), new Color(.03f, .50f, .7f, .095f));

        var left = Trail(parent, "C_LeftBlade", Red, new Vector2(-1090, -440), new Vector2(-555, -455),
            new Vector2(-930, 390), new Vector2(-380, 545), 13f, .18f);
        var right = Trail(parent, "C_RightBlade", Blue, new Vector2(1110, -525), new Vector2(620, -555),
            new Vector2(960, 180), new Vector2(560, 495), 15f, .63f);

        // 床は全面の格子にせず、刃が通った跡を二本だけ置く。
        var floor = Group(parent, "C_Floor");
        Line(floor.transform, "C_FloorRed", new Vector2(-865, -535), new Vector2(-172, -312), 1.1f, WithAlpha(Red, .20f));
        Line(floor.transform, "C_FloorBlue", new Vector2(865, -535), new Vector2(172, -312), 1.1f, WithAlpha(Blue, .24f));
        Line(floor.transform, "C_FloorEcho", new Vector2(-390, -480), new Vector2(390, -480), 1f, WithAlpha(Blue, .08f));
        root.GetComponent<TitleConceptCPresentation>().Configure(atmosphere, floor, left, right);
    }

    static CanvasGroup Group(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasGroup));
        go.transform.SetParent(parent, false);
        Stretch(go.GetComponent<RectTransform>());
        var group = go.GetComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;
        return group;
    }

    static TitleConceptCTrail Trail(Transform parent, string name, Color color, Vector2 a, Vector2 b, Vector2 c, Vector2 d,
        float seconds, float phase)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TitleConceptCTrail));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(1920, 1080);
        var trail = go.GetComponent<TitleConceptCTrail>();
        trail.Configure(color, a, b, c, d, seconds, phase);
        return trail;
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

    static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }
}

// 親の一つの時計で管理し、任意の時刻へ進めても同じ画面を再現する。
public sealed class TitleConceptCPresentation : MonoBehaviour, ITitlePresentationLayer
{
    CanvasGroup atmosphere;
    CanvasGroup floor;
    TitleConceptCTrail left;
    TitleConceptCTrail right;

    public void Configure(CanvasGroup glow, CanvasGroup ground, TitleConceptCTrail leftBlade, TitleConceptCTrail rightBlade)
    {
        atmosphere = glow; floor = ground; left = leftBlade; right = rightBlade;
        SetPresentationTime(0f, 0f);
    }

    public void SetPresentationTime(float age, float departure)
    {
        age = Mathf.Max(0f, age);
        departure = Mathf.Clamp01(departure);
        float entrance = Mathf.SmoothStep(0f, 1f, age / 1.25f);
        if (atmosphere != null)
            atmosphere.alpha = entrance * (1f - departure * .55f) * (.96f + .04f * Mathf.Sin(age * .37f));
        if (floor != null)
            floor.alpha = Mathf.SmoothStep(0f, 1f, (age - .25f) / 1f) * (1f - departure * .7f);
        if (left != null) left.SetPresentationTime(age, departure);
        if (right != null) right.SetPresentationTime(Mathf.Max(0f, age - .12f), departure);
    }
}

// 固定された薄い刃と、その上を一方向に進む短い残光。点滅やタイトル自体の移動は行わない。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class TitleConceptCTrail : MaskableGraphic
{
    Vector2 a, b, c, d;
    float period = 13f;
    float initialPhase;
    float age;
    float departure;

    public void Configure(Color tint, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float seconds, float phase)
    {
        color = tint;
        a = p0; b = p1; c = p2; d = p3;
        period = seconds;
        initialPhase = phase;
        raycastTarget = false;
        SetVerticesDirty();
    }

    public void SetPresentationTime(float presentationAge, float exitProgress)
    {
        age = Mathf.Max(0f, presentationAge);
        departure = Mathf.Clamp01(exitProgress);
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
        float reveal = Mathf.SmoothStep(0f, 1f, age / 1.45f);
        float fade = 1f - departure;
        Strip(vh, 0, reveal, 23f, .035f * fade, false);
        Strip(vh, 0, reveal, 4.5f, .28f * fade, false);
        Strip(vh, 0, reveal, 1.5f, .82f * fade, false);
        // 登場中は描画先端に刃の光、待機中は短い残光、開始時は先端へ抜ける。
        float idleHead = Mathf.Repeat(initialPhase + age / period, 1f);
        float head = age < 1.45f ? reveal : Mathf.Lerp(idleHead, 1f, Mathf.SmoothStep(0f, 1f, departure));
        float visible = Mathf.SmoothStep(0, 1, Mathf.Min(head / .10f, (1f - head) / .10f));
        float settled = age < 1.45f ? 1f : Mathf.SmoothStep(0f, 1f, (age - 1.45f) / .6f);
        float strength = (1f + .65f * Mathf.Sin(departure * Mathf.PI)) * visible * fade * settled;
        Strip(vh, Mathf.Max(0, head - .20f), head, 13f, .14f * strength, true);
        Strip(vh, Mathf.Max(0, head - .16f), head, 3f, .95f * strength, true);
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
