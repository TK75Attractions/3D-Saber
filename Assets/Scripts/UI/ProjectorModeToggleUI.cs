using System.Collections;
using UnityEngine;
using UnityEngine.UI;

// 曲選択画面の左下(JUDGMENT SETUP の右隣)にプロジェクターモードの ON/OFF ボタンを置く。
// SongSelectSkin は編集せず、スキンの構築完了(CalibrationButton の出現)を待ってから同じ Canvas に相乗りする。
public class ProjectorModeToggleUI : MonoBehaviour
{
    public const string ButtonName = "ProjectorModeButton";

    UISkinKit.NeonButtonParts parts;

    public static string LabelFor(bool on)
    {
        return on ? "PROJECTOR: ON" : "PROJECTOR: OFF";
    }

    IEnumerator Start()
    {
        Transform canvasTf = null;
        Transform calib = null;
        for (int i = 0; i < 300 && calib == null; i++)
        {
            var ctl = Object.FindFirstObjectByType<SongSelectController>();
            Canvas canvas = null;
            if (ctl != null)
            {
                canvas = ctl.GetComponent<Canvas>();
                if (canvas == null) canvas = ctl.GetComponentInParent<Canvas>();
            }
            if (canvas != null)
            {
                canvasTf = canvas.transform;
                calib = canvasTf.Find("CalibrationButton");
            }
            if (calib == null) yield return null;
        }
        if (canvasTf == null || canvasTf.Find(ButtonName) != null) yield break;

        parts = UISkinKit.MakeNeonButton(canvasTf, ButtonName, LabelFor(DisplaySettings.ProjectorMode),
            Vector2.zero, new Vector2(330f, 76f), UISkinPalette.Yellow, ProjectorModeHotkey.Toggle, 21f);
        var rt = parts.button.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = Vector2.zero;
        rt.anchoredPosition = new Vector2(380f, 30f); // JUDGMENT SETUP(30,30 / 幅330)の右隣
        DisplaySettings.OnProjectorModeChanged += HandleChanged;
    }

    void OnDestroy()
    {
        DisplaySettings.OnProjectorModeChanged -= HandleChanged;
    }

    void HandleChanged(bool on)
    {
        if (parts.label != null) parts.label.text = LabelFor(on);
    }
}
