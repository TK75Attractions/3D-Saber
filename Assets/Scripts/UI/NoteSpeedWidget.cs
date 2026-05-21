using UnityEngine;
using UnityEngine.UI;

// キャリブレーション画面に置く「ノーツ速度（approachTime）」調整ウィジェット。
// approachTime（HitTime まで何秒かけて流れてくるか）を 0.5〜4.0 秒で調整。
// GameSession.NoteApproachTime に保存され、GamePlayManager がプレイ時に NoteSpawner に適用。
// キャリブレーション中は GamePlayManager.UpdateCalibration が毎フレ反映するので即時。
public class NoteSpeedWidget : MonoBehaviour
{
    public Text valueText;
    public float currentValue;

    public static NoteSpeedWidget Ensure(Canvas canvas)
    {
        if (canvas == null) return null;
        var existing = canvas.GetComponentInChildren<NoteSpeedWidget>(true);
        if (existing != null) return existing;

        var go = new GameObject("NoteSpeedWidget",
            typeof(RectTransform), typeof(Image));
        go.transform.SetParent(canvas.transform, false);
        var w = go.AddComponent<NoteSpeedWidget>();
        w.Build();
        return w;
    }

    void Build()
    {
        var rt = GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(-60f, -200f);
        rt.sizeDelta = new Vector2(540f, 220f);

        var bg = GetComponent<Image>();
        bg.color = new Color(0.08f, 0.06f, 0.18f, 0.75f);
        var ol = gameObject.AddComponent<Outline>();
        ol.effectColor = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.55f);
        ol.effectDistance = new Vector2(2f, -2f);

        var header = MakeLabel("NOTE SPEED (approach time)", new Vector2(0f, 75f), 20, FontStyle.Bold);
        header.color = UISkinPalette.Cyan;

        valueText = MakeLabel("2.00 s", new Vector2(0f, 25f), 44, FontStyle.Bold);
        valueText.color = UISkinPalette.OffWhite;

        MakeButton("- 0.5", new Vector2(-150f, -55f), () => Change(-0.5f));
        MakeButton("- 0.1", new Vector2(-50f, -55f), () => Change(-0.1f));
        MakeButton("+ 0.1", new Vector2(50f, -55f), () => Change(+0.1f));
        MakeButton("+ 0.5", new Vector2(150f, -55f), () => Change(+0.5f));

        currentValue = GameSession.NoteApproachTime;
        UpdateDisplay();
    }

    Text MakeLabel(string text, Vector2 pos, int size, FontStyle style)
    {
        var go = new GameObject("Label_" + text, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(500f, 50f);
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
        var go = new GameObject("Btn_" + label,
            typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(96f, 58f);
        rt.anchoredPosition = pos;
        var img = go.GetComponent<Image>();
        img.color = new Color(UISkinPalette.Cyan.r * 0.18f, UISkinPalette.Cyan.g * 0.18f, UISkinPalette.Cyan.b * 0.28f, 0.85f);
        var ol = go.AddComponent<Outline>();
        ol.effectColor = UISkinPalette.Cyan;
        ol.effectDistance = new Vector2(2f, -2f);

        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(onClick);

        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
        labelGo.transform.SetParent(go.transform, false);
        var lrt = labelGo.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = Vector2.zero;
        lrt.offsetMax = Vector2.zero;
        var t = labelGo.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 24;
        t.fontStyle = FontStyle.Bold;
        t.color = UISkinPalette.Cyan;
        t.alignment = TextAnchor.MiddleCenter;
        t.text = label;
        t.raycastTarget = false;

        return btn;
    }

    public void Change(float delta)
    {
        float next = Mathf.Clamp(currentValue + delta, GameSession.NoteApproachTimeMin, GameSession.NoteApproachTimeMax);
        // 0.1 刻みで丸める
        next = Mathf.Round(next * 10f) / 10f;
        if (Mathf.Approximately(next, currentValue)) return;
        currentValue = next;
        GameSession.NoteApproachTime = currentValue;
        UpdateDisplay();
    }

    public void SetValue(float v)
    {
        currentValue = Mathf.Clamp(v, GameSession.NoteApproachTimeMin, GameSession.NoteApproachTimeMax);
        GameSession.NoteApproachTime = currentValue;
        UpdateDisplay();
    }

    void UpdateDisplay()
    {
        if (valueText == null) return;
        valueText.text = currentValue.ToString("0.0") + " s";
        valueText.color = Mathf.Approximately(currentValue, GameSession.NoteApproachTimeDefault)
            ? UISkinPalette.OffWhite : UISkinPalette.Cyan;
    }
}
