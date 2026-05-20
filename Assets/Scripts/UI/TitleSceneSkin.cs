using UnityEngine;
using UnityEngine.UI;

// タイトル画面の見た目をランタイムで強化する。
// 既存シーンを編集せず、Canvas を見つけて子要素を上書き/追加する。
public class TitleSceneSkin : MonoBehaviour
{
    public float titlePulseHz = 0.8f;
    public float titlePulseAmplitude = 0.08f;
    public string tagline = "// CUT . THE . RHYTHM";

    private Text[] titleHalves;
    private RectTransform titleContainer;
    private float age;

    void Start()
    {
        var titleCtl = Object.FindFirstObjectByType<TitleMenuController>();
        if (titleCtl == null) return;

        var canvas = titleCtl.GetComponent<Canvas>();
        if (canvas == null) canvas = titleCtl.GetComponentInParent<Canvas>();
        if (canvas == null) return;

        CyberBackdrop.Ensure(canvas);
        StyleTitle(canvas);
        StyleButtons(canvas);
        AddTagline(canvas);
    }

    void Update()
    {
        if (titleContainer == null) return;
        age += Time.deltaTime;
        float pulse = 1f + titlePulseAmplitude * Mathf.Sin(age * titlePulseHz * 2f * Mathf.PI);
        titleContainer.localScale = new Vector3(pulse, pulse, 1f);
    }

    void StyleTitle(Canvas canvas)
    {
        Text original = FindTextByContent(canvas, "3D SABER");
        if (original == null) return;

        // 元タイトルは非表示
        original.gameObject.SetActive(false);
        var orgRT = original.GetComponent<RectTransform>();
        Vector2 anchored = orgRT.anchoredPosition;

        // コンテナ
        var container = new GameObject("TitleSplit", typeof(RectTransform));
        container.transform.SetParent(canvas.transform, false);
        titleContainer = container.GetComponent<RectTransform>();
        titleContainer.sizeDelta = new Vector2(1100, 240);
        titleContainer.anchoredPosition = anchored;

        // 二色スプリット: "3D" シアン / "SABER" マゼンタ
        Text t1 = BuildTitleHalf(container.transform, "3D", UISkinPalette.Cyan, TextAnchor.MiddleRight, new Vector2(-30, 0));
        Text t2 = BuildTitleHalf(container.transform, "SABER", UISkinPalette.Magenta, TextAnchor.MiddleLeft, new Vector2(30, 0));
        titleHalves = new[] { t1, t2 };

        // 下のネオン下線（シアン→マゼンタのグラデっぽく見えるよう、薄い2本を重ねる）
        BuildUnderline(container.transform);
    }

    Text BuildTitleHalf(Transform parent, string text, Color color, TextAnchor anchor, Vector2 offset)
    {
        // ハロー（後ろの大きめ・低α）
        var halo = MakeText(parent, "Halo_" + text, text, color, anchor, offset, 152, 0.25f, new Vector2(560, 240));
        halo.name = "Halo_" + text;

        // 本体
        var main = MakeText(parent, "Main_" + text, text, color, anchor, offset, 132, 1f, new Vector2(540, 220));
        // 内側に黒っぽい縁取りで存在感
        var outline = main.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.65f);
        outline.effectDistance = new Vector2(2f, -2f);

        return main;
    }

    static Text MakeText(Transform parent, string name, string text, Color color, TextAnchor anchor, Vector2 offset, int fontSize, float alpha, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = size;
        rt.anchoredPosition = offset;
        var t = go.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = fontSize;
        t.fontStyle = FontStyle.Bold;
        t.alignment = anchor;
        t.text = text;
        Color c = color; c.a = alpha;
        t.color = c;
        t.raycastTarget = false;
        return t;
    }

    void BuildUnderline(Transform parent)
    {
        // 横一直線のネオン下線（シアン→マゼンタの2本を半分ずつ重ねる）
        var left = new GameObject("UnderlineL", typeof(RectTransform), typeof(Image));
        left.transform.SetParent(parent, false);
        var lrt = left.GetComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0.5f, 0.5f);
        lrt.anchorMax = new Vector2(0.5f, 0.5f);
        lrt.pivot = new Vector2(1f, 0.5f);
        lrt.sizeDelta = new Vector2(280, 3);
        lrt.anchoredPosition = new Vector2(0, -90);
        left.GetComponent<Image>().color = UISkinPalette.Cyan;

        var right = new GameObject("UnderlineR", typeof(RectTransform), typeof(Image));
        right.transform.SetParent(parent, false);
        var rrt = right.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 0.5f);
        rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0f, 0.5f);
        rrt.sizeDelta = new Vector2(280, 3);
        rrt.anchoredPosition = new Vector2(0, -90);
        right.GetComponent<Image>().color = UISkinPalette.Magenta;
    }

    void StyleButtons(Canvas canvas)
    {
        foreach (var btn in canvas.GetComponentsInChildren<Button>())
        {
            string label = GetButtonLabelText(btn);
            if (label == "START") StyleButton(btn, UISkinPalette.Cyan);
            else if (label == "QUIT") StyleButton(btn, UISkinPalette.Magenta);
        }
    }

    static string GetButtonLabelText(Button btn)
    {
        var t = btn.GetComponentInChildren<Text>();
        return t != null ? t.text : "";
    }

    static void StyleButton(Button btn, Color accent)
    {
        // 背景：暗めの半透明、アクセント色の薄塗り
        var img = btn.GetComponent<Image>();
        if (img != null)
        {
            img.color = new Color(accent.r * 0.10f, accent.g * 0.10f, accent.b * 0.15f, 0.85f);
        }
        // 枠線（Outline で代用）
        var outline = btn.gameObject.GetComponent<Outline>();
        if (outline == null) outline = btn.gameObject.AddComponent<Outline>();
        outline.effectColor = accent;
        outline.effectDistance = new Vector2(2.5f, -2.5f);

        // ラベル色をアクセント色に
        var label = btn.GetComponentInChildren<Text>();
        if (label != null) label.color = accent;
    }

    void AddTagline(Canvas canvas)
    {
        var tag = new GameObject("Tagline", typeof(RectTransform), typeof(Text));
        tag.transform.SetParent(canvas.transform, false);
        var rt = tag.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(800, 50);
        rt.anchoredPosition = new Vector2(0, 80);
        var t = tag.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 22;
        t.alignment = TextAnchor.MiddleCenter;
        t.text = tagline;
        t.color = UISkinPalette.SubtleGray;
        t.fontStyle = FontStyle.Normal;
        t.raycastTarget = false;
    }

    public static Text FindTextByContent(Canvas canvas, string content)
    {
        foreach (var t in canvas.GetComponentsInChildren<Text>(true))
        {
            if (t.text == content) return t;
        }
        return null;
    }
}
