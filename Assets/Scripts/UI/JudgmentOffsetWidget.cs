using UnityEngine;
using UnityEngine.UI;

// SongSelect 画面の左下に置く、判定オフセット調整ウィジェット。
// 「-10 / -1 / +1 / +10 ms」のボタンで GameSession.JudgmentOffsetMs を上下させる。
// 値は PlayerPrefs に永続化、GamePlayManager がプレイ開始時に読み出す。
public class JudgmentOffsetWidget : MonoBehaviour
{
    public Text valueText;
    public int currentMs;

    public static JudgmentOffsetWidget Ensure(Canvas canvas)
    {
        if (canvas == null) return null;
        var existing = canvas.GetComponentInChildren<JudgmentOffsetWidget>(true);
        if (existing != null) return existing;

        var go = new GameObject("JudgmentOffsetWidget", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(canvas.transform, false);
        var w = go.AddComponent<JudgmentOffsetWidget>();
        w.Build();
        return w;
    }

    void Build()
    {
        var rt = GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(30f, 30f);
        rt.sizeDelta = new Vector2(440f, 210f);

        // 背景パネル
        var bg = GetComponent<Image>();
        bg.color = new Color(0.08f, 0.06f, 0.18f, 0.75f);
        bg.raycastTarget = true;
        var ol = gameObject.AddComponent<Outline>();
        ol.effectColor = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.55f);
        ol.effectDistance = new Vector2(2f, -2f);

        // ヘッダー
        var header = MakeLabel("JUDGE OFFSET", new Vector2(0f, 75f), 22, FontStyle.Bold);
        header.color = UISkinPalette.Cyan;

        // 現在値
        valueText = MakeLabel("+0 ms", new Vector2(0f, 25f), 44, FontStyle.Bold);
        valueText.color = UISkinPalette.OffWhite;

        // ボタン群（-10 / -1 / +1 / +10）
        MakeButton("-10", new Vector2(-150f, -55f), () => Change(-10));
        MakeButton("-1",  new Vector2(-50f,  -55f), () => Change(-1));
        MakeButton("+1",  new Vector2( 50f,  -55f), () => Change(+1));
        MakeButton("+10", new Vector2(150f,  -55f), () => Change(+10));

        currentMs = GameSession.JudgmentOffsetMs;
        UpdateDisplay();
    }

    Text MakeLabel(string text, Vector2 pos, int size, FontStyle style)
    {
        var go = new GameObject("Label_" + text, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(420f, 60f);
        rt.anchoredPosition = pos;
        var t = go.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = TextAnchor.MiddleCenter;
        t.text = text;
        t.color = UISkinPalette.OffWhite;
        t.raycastTarget = false;
        return t;
    }

    Button MakeButton(string label, Vector2 pos, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(86f, 58f);
        rt.anchoredPosition = pos;
        var img = go.GetComponent<Image>();
        img.color = new Color(UISkinPalette.Cyan.r * 0.18f, UISkinPalette.Cyan.g * 0.18f, UISkinPalette.Cyan.b * 0.28f, 0.85f);
        var ol = go.AddComponent<Outline>();
        ol.effectColor = UISkinPalette.Cyan;
        ol.effectDistance = new Vector2(2f, -2f);

        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(onClick);

        // ラベル子
        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
        labelGo.transform.SetParent(go.transform, false);
        var lrt = labelGo.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = Vector2.zero;
        lrt.offsetMax = Vector2.zero;
        var t = labelGo.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 26;
        t.fontStyle = FontStyle.Bold;
        t.color = UISkinPalette.Cyan;
        t.alignment = TextAnchor.MiddleCenter;
        t.text = label;
        t.raycastTarget = false;

        return btn;
    }

    public void Change(int delta)
    {
        int next = Mathf.Clamp(currentMs + delta, GameSession.JudgmentOffsetMinMs, GameSession.JudgmentOffsetMaxMs);
        if (next == currentMs) return;
        currentMs = next;
        GameSession.JudgmentOffsetMs = currentMs;
        UpdateDisplay();
    }

    public void SetValue(int ms)
    {
        currentMs = Mathf.Clamp(ms, GameSession.JudgmentOffsetMinMs, GameSession.JudgmentOffsetMaxMs);
        GameSession.JudgmentOffsetMs = currentMs;
        UpdateDisplay();
    }

    void UpdateDisplay()
    {
        if (valueText == null) return;
        string sign = currentMs > 0 ? "+" : (currentMs < 0 ? "" : "+");
        valueText.text = $"{sign}{currentMs} ms";
        valueText.color = currentMs == 0 ? UISkinPalette.OffWhite : UISkinPalette.Cyan;
    }
}
