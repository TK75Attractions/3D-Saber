using UnityEngine;
using UnityEngine.UI;

// Game シーンがキャリブレーションモードのとき表示する UI オーバーレイ。
// - 上部にヘッダー (CALIBRATION MODE)
// - 右側に JudgmentOffsetWidget（既存）を大きめに配置
// - 右上に「BACK」ボタン
//
// 既存の HUD（スコア・コンボ・tier 表示）はそのまま使えるので、ここでは追加 UI のみ被せる。
public class CalibrationOverlay : MonoBehaviour
{
    public static CalibrationOverlay Ensure()
    {
        var existing = Object.FindFirstObjectByType<CalibrationOverlay>();
        if (existing != null) return existing;

        // 専用 Canvas を作る（既存 HUD と衝突しないように）
        var canvasGo = new GameObject("CalibrationOverlay",
            typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        var overlay = canvasGo.AddComponent<CalibrationOverlay>();
        overlay.Build(canvas);
        return overlay;
    }

    void Build(Canvas canvas)
    {
        // EventSystem 確認（無ければ作る）
        if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var ev = new GameObject("EventSystem",
                typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
        }

        BuildHeader(canvas);
        BuildBackButton(canvas);
        BuildInstructions(canvas);

        // 既存の JudgmentOffsetWidget を使って、右側の中央あたりに大きく配置
        var widget = JudgmentOffsetWidget.Ensure(canvas);
        if (widget != null)
        {
            var rt = widget.GetComponent<RectTransform>();
            // 右側中央寄りに再アンカー
            rt.anchorMin = new Vector2(1f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = new Vector2(-60f, -50f);
            rt.sizeDelta = new Vector2(540f, 280f);
        }
    }

    void BuildHeader(Canvas canvas)
    {
        var go = new GameObject("CalibHeader", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(canvas.transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -40f);
        rt.sizeDelta = new Vector2(900f, 70f);
        var t = go.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 44;
        t.fontStyle = FontStyle.Bold;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = UISkinPalette.Cyan;
        t.text = "// CALIBRATION";
        t.raycastTarget = false;
    }

    void BuildInstructions(Canvas canvas)
    {
        var go = new GameObject("CalibInstructions", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(canvas.transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0f);
        rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, 40f);
        rt.sizeDelta = new Vector2(900f, 70f);
        var t = go.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 22;
        t.fontStyle = FontStyle.Normal;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = UISkinPalette.SubtleGray;
        t.text = "BPM 120 のクリック音に合わせてノーツを切る → OFFSET 調整で PERFECT を狙う";
        t.raycastTarget = false;
    }

    void BuildBackButton(Canvas canvas)
    {
        var go = new GameObject("CalibBackBtn",
            typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(canvas.transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(40f, -40f);
        rt.sizeDelta = new Vector2(220f, 80f);
        var img = go.GetComponent<Image>();
        img.color = new Color(UISkinPalette.Magenta.r * 0.18f, UISkinPalette.Magenta.g * 0.18f, UISkinPalette.Magenta.b * 0.25f, 0.9f);
        var ol = go.AddComponent<Outline>();
        ol.effectColor = UISkinPalette.Magenta;
        ol.effectDistance = new Vector2(2f, -2f);

        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(OnBackClicked);

        var label = new GameObject("Label", typeof(RectTransform), typeof(Text));
        label.transform.SetParent(go.transform, false);
        var lrt = label.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = Vector2.zero;
        lrt.offsetMax = Vector2.zero;
        var t = label.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 30;
        t.fontStyle = FontStyle.Bold;
        t.color = UISkinPalette.Magenta;
        t.alignment = TextAnchor.MiddleCenter;
        t.text = "< BACK";
        t.raycastTarget = false;
    }

    public void OnBackClicked()
    {
        GamePlayManager.ExitCalibration("SongSelect");
    }
}
