using System.Collections;
using UnityEngine;
using UnityEngine.UI;

// 曲選択画面の見た目をランタイムで強化する。
// SongSelectController.Start が曲リスト/初期選択を組み立てるので、
// 1 フレ遅らせてから装飾する（順序非決定を避ける）。
public class SongSelectSkin : MonoBehaviour
{
    IEnumerator Start()
    {
        // SongSelectController.Start が走り終わるのを待つ
        yield return null;

        var ctl = Object.FindFirstObjectByType<SongSelectController>();
        if (ctl == null) yield break;

        var canvas = ctl.GetComponent<Canvas>();
        if (canvas == null) canvas = ctl.GetComponentInParent<Canvas>();
        if (canvas == null) yield break;

        CyberBackdrop.Ensure(canvas);
        StyleHeader(canvas);
        StyleDifficultyButtons(ctl);
        StyleStartButton(ctl);
        StyleSongPanel(canvas);
        StyleSelectedColor(ctl);
        // 左下に判定オフセット調整ウィジェット、その右隣に「その場で試せる」練習ゾーン
        JudgmentOffsetWidget.Ensure(canvas);
        JudgmentTestWidget.Ensure(canvas);
    }

    void StyleHeader(Canvas canvas)
    {
        var header = TitleSceneSkin.FindTextByContent(canvas, "Select Song");
        if (header == null) return;
        header.color = UISkinPalette.OffWhite;
        header.fontStyle = FontStyle.Bold;
        // ヘッダー下に発光ライン
        var line = new GameObject("HeaderLine", typeof(RectTransform), typeof(Image));
        line.transform.SetParent(canvas.transform, false);
        var rt = line.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(360, 3);
        var hrt = header.GetComponent<RectTransform>();
        rt.anchoredPosition = new Vector2(hrt.anchoredPosition.x, hrt.anchoredPosition.y - 50);
        var img = line.GetComponent<Image>();
        img.color = UISkinPalette.Cyan;
        img.raycastTarget = false;
    }

    void StyleDifficultyButtons(SongSelectController ctl)
    {
        // [Easy, Normal, Hard] にシアン/イエロー/マゼンタ
        Color[] colors = { UISkinPalette.Cyan, UISkinPalette.Yellow, UISkinPalette.Magenta };
        if (ctl.difficultyButtons == null) return;
        for (int i = 0; i < ctl.difficultyButtons.Length && i < colors.Length; i++)
        {
            var btn = ctl.difficultyButtons[i];
            if (btn == null) continue;
            ApplyNeon(btn, colors[i], fillAlpha: 0.20f);
        }
        // 難易度表示テキストもアクセント
        if (ctl.difficultyDisplay != null)
        {
            ctl.difficultyDisplay.color = UISkinPalette.Cyan;
        }
    }

    void StyleStartButton(SongSelectController ctl)
    {
        if (ctl.startButton == null) return;
        ApplyNeon(ctl.startButton, UISkinPalette.Cyan, fillAlpha: 0.25f);
        var label = ctl.startButton.GetComponentInChildren<Text>();
        if (label != null) label.color = UISkinPalette.Cyan;
    }

    void StyleSongPanel(Canvas canvas)
    {
        // 大きめのスクロールパネル背景を見つけて、もっと半透明＆紫がかった色に
        foreach (var img in canvas.GetComponentsInChildren<Image>(true))
        {
            // 元の暗いパネル色を狙い撃ち
            if (img.gameObject.name == "ScrollView")
            {
                img.color = new Color(0.08f, 0.06f, 0.18f, 0.65f);
                // 縁取り
                var ol = img.gameObject.GetComponent<Outline>();
                if (ol == null) ol = img.gameObject.AddComponent<Outline>();
                ol.effectColor = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.5f);
                ol.effectDistance = new Vector2(2f, -2f);
            }
        }
    }

    void StyleSelectedColor(SongSelectController ctl)
    {
        // 選択行のテキスト色を、より目立つネオン色に上書き
        ctl.selectedLabelColor = UISkinPalette.Cyan;
        ctl.normalLabelColor = UISkinPalette.SubtleGray;
        // 既に Populate+Select(0) 済みなので、現選択を再選択して色反映
        ctl.Select(0);
    }

    public static void ApplyNeon(Button btn, Color accent, float fillAlpha)
    {
        var img = btn.GetComponent<Image>();
        if (img != null)
        {
            img.color = new Color(accent.r, accent.g, accent.b, fillAlpha);
        }
        var ol = btn.gameObject.GetComponent<Outline>();
        if (ol == null) ol = btn.gameObject.AddComponent<Outline>();
        ol.effectColor = accent;
        ol.effectDistance = new Vector2(2f, -2f);
        var label = btn.GetComponentInChildren<Text>();
        if (label != null) label.color = accent;
    }
}
