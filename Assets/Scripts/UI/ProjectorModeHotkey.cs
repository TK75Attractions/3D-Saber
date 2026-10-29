using UnityEngine;
using UnityEngine.InputSystem;

// F4 でプロジェクターモードを切り替える常駐コンポーネント(UISkinBootstrap が生成、シーンを跨いで生存)。
// 切替後は、その場で反映できるもの(ポストプロセス / フォグ / セーバーの線)を即時更新する。
// ノーツや UI の太さ・色は生成時に決まるので、次にシーンが組み直されたときに反映される。
public class ProjectorModeHotkey : MonoBehaviour
{
    public Key toggleKey = Key.F4;

    public static ProjectorModeHotkey Ensure()
    {
        var existing = Object.FindFirstObjectByType<ProjectorModeHotkey>();
        if (existing != null) return existing;
        var go = new GameObject("ProjectorModeHotkey");
        if (Application.isPlaying) DontDestroyOnLoad(go);
        return go.AddComponent<ProjectorModeHotkey>();
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (!Accepts(ChartMarkCommentPanel.IsOpen, ScreenTransition.IsBusy)) return;
        if (kb == null || !kb[toggleKey].wasPressedThisFrame) return;
        Toggle();
    }

    // F4 を受け付けるか。結果画面の一言の欄で打っている間と、幕の間は受け付けない(F12 と同じ。幕の間に押すと切り替わっても
    // 知らせが幕に隠れてシーンとともに消え、スタッフが押し直して元に戻してしまう。2026-10-30 全体の点検)。
    public static bool Accepts(bool commentOpen, bool transitionBusy) => !commentOpen && !transitionBusy;

    public static void Toggle()
    {
        DisplaySettings.ProjectorMode = !DisplaySettings.ProjectorMode;
        ApplyNow();
        OperatorNotice.Show("プロジェクター表示：" + (DisplaySettings.ProjectorMode ? "ON" : "OFF"));
    }

    // 今のシーンに即時反映できる分を適用する。
    public static void ApplyNow()
    {
        var cam = Camera.main;
        ProjectorMode.Apply(cam);
        // 本編シーン(フォグが有効)ではフォグ密度も更新する
        if (RenderSettings.fog) GameStageSkin.ApplyCameraAndFog(cam);
        foreach (var bridge in Object.FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None))
        {
            bridge.RefreshProjectorStyle();
        }
    }
}
