using UnityEngine;
using UnityEngine.UI;

// リザルト画面の見た目を強化。
public class ResultSkin : MonoBehaviour
{
    void Start()
    {
        var ctl = Object.FindFirstObjectByType<ResultController>();
        if (ctl == null) return;

        var canvas = ctl.GetComponent<Canvas>();
        if (canvas == null) canvas = ctl.GetComponentInParent<Canvas>();
        if (canvas == null) return;

        CyberBackdrop.Ensure(canvas);
        StyleTexts(ctl);
        StyleBackButton(canvas);
        AddResultHeader(canvas);
    }

    void StyleTexts(ResultController ctl)
    {
        // タイトル（曲名）
        if (ctl.titleText != null)
        {
            ctl.titleText.color = UISkinPalette.OffWhite;
            ctl.titleText.fontStyle = FontStyle.Bold;
        }
        // スコア（最重要、シアン強発光）
        if (ctl.scoreText != null)
        {
            ctl.scoreText.color = UISkinPalette.Cyan;
            var ol = EnsureOutline(ctl.scoreText.gameObject);
            ol.effectColor = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.55f);
            ol.effectDistance = new Vector2(3f, -3f);
        }
        if (ctl.comboText != null)
        {
            ctl.comboText.color = UISkinPalette.OffWhite;
        }
        // tier 別カラー
        if (ctl.perfectText != null) ctl.perfectText.color = UISkinPalette.Cyan;
        if (ctl.greatText != null) ctl.greatText.color = UISkinPalette.Yellow;
        if (ctl.goodText != null) ctl.goodText.color = UISkinPalette.Orange;
        if (ctl.badText != null) ctl.badText.color = UISkinPalette.Magenta;
        if (ctl.missText != null) ctl.missText.color = UISkinPalette.SubtleGray;
    }

    void StyleBackButton(Canvas canvas)
    {
        foreach (var btn in canvas.GetComponentsInChildren<Button>())
        {
            var label = btn.GetComponentInChildren<Text>();
            if (label != null && label.text == "BACK")
            {
                SongSelectSkin.ApplyNeon(btn, UISkinPalette.Magenta, 0.20f);
            }
        }
    }

    void AddResultHeader(Canvas canvas)
    {
        var header = new GameObject("ResultHeader", typeof(RectTransform), typeof(Text));
        header.transform.SetParent(canvas.transform, false);
        var rt = header.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(800, 60);
        rt.anchoredPosition = new Vector2(0, 480);
        var t = header.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 28;
        t.fontStyle = FontStyle.Bold;
        t.alignment = TextAnchor.MiddleCenter;
        t.text = "// RESULT";
        t.color = UISkinPalette.SubtleGray;
        t.raycastTarget = false;
    }

    static Outline EnsureOutline(GameObject go)
    {
        var ol = go.GetComponent<Outline>();
        if (ol == null) ol = go.AddComponent<Outline>();
        return ol;
    }
}
