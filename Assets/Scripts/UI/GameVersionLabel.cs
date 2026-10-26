using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ゲームの版(仕様面の点検 14、2026-10-21)。台A・台B が同じ版かを、どの画面でも右下の小さく薄い「v1.0」で確かめる。
// 更新するたびに上げる: 軽微な直しは 1.0.1 → 1.0.2、中くらいの変更は 1.1 → 1.2(下の桁は 0 に戻して省く)。
public static class GameVersion
{
    public const string Number = "1.24";
    public static string Label => "v" + Number;
}

// 版の表示。起動時に一度だけ作り、シーンをまたいで残す(シーンは編集しない)。幕や運営の知らせより下、HUD より上。
public sealed class GameVersionLabel : MonoBehaviour
{
    public const float FontSize = 18;
    public const float Alpha = .32f;
    public static readonly Vector2 Margin = new Vector2(16, 6);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Initialize()
    {
        if (Object.FindFirstObjectByType<GameVersionLabel>() != null) return;
        var label = Create();
        Object.DontDestroyOnLoad(label.gameObject);
    }

    public static GameVersionLabel Create()
    {
        var root = new GameObject("GameVersionLabel", typeof(Canvas), typeof(CanvasScaler));
        var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 840;
        var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        var view = root.AddComponent<GameVersionLabel>();
        var text = UISkinKit.MakeTMP(root.transform, "Version", GameVersion.Label, FontSize, new Color(1, 1, 1, Alpha),
            TextAlignmentOptions.BottomRight, Vector2.zero, new Vector2(160, 26), FontStyles.Normal, 0,
            UISkinKit.FontAsset("Oxanium-Bold"));
        var rt = text.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1, 0);
        rt.anchoredPosition = new Vector2(-Margin.x, Margin.y);
        text.raycastTarget = false;
        view.Text = text;
        return view;
    }

    public TextMeshProUGUI Text { get; private set; }
}
