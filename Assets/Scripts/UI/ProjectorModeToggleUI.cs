using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 選曲の設定操作も、共通のノーツとラベル付きパネルに揃える。
public class ProjectorModeToggleUI : MonoBehaviour
{
    public const string ButtonName = "ProjectorModeButton";
    TextMeshProUGUI label;
    public static string LabelFor(bool on) => on ? "PROJECTOR: ON" : "PROJECTOR: OFF";

    IEnumerator Start()
    {
        Transform canvasTf = null;
        for (int i = 0; i < 300; i++)
        {
            var ctl = Object.FindFirstObjectByType<SongSelectController>();
            var canvas = ctl != null ? ctl.GetComponentInParent<Canvas>() : null;
            if (canvas != null && canvas.transform.Find("CalibrationButton") != null)
            { canvasTf = canvas.transform; break; }
            yield return null;
        }
        if (canvasTf == null || canvasTf.Find(ButtonName) != null) yield break;
        var rt = SongSelectVisuals.Rect(canvasTf,ButtonName,new Vector2(-430,-491),new Vector2(316,68));
        var button = rt.gameObject.AddComponent<Button>();
        button.onClick.AddListener(ProjectorModeHotkey.Toggle);
        SongSelectVisuals.StyleAction(button,LabelFor(DisplaySettings.ProjectorMode),false);
        MenuNoteAction.Attach(button,new Vector2(-122,0),42,new Color(.94f,.77f,.40f));
        label = rt.Find("ActionLabel").GetComponent<TextMeshProUGUI>();
        DisplaySettings.OnProjectorModeChanged += HandleChanged;
    }

    void OnDestroy() { DisplaySettings.OnProjectorModeChanged -= HandleChanged; }
    void HandleChanged(bool on) { if (label != null) label.text = LabelFor(on); }
}
