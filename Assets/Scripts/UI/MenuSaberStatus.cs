using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 接続の有無ではなく、最近カメラから剣の位置を受信したかを示す。プレイ中は作らない。
public sealed class MenuSaberStatus : MonoBehaviour
{
    public const double FreshSeconds = 1;
    TextMeshProUGUI label;
    string previous;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Initialize()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }
    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Title" && scene.name != "SongSelect" && scene.name != "Result") return;
        if (Object.FindFirstObjectByType<MenuSaberStatus>() != null) return;
        var root = new GameObject("MenuSaberStatus", typeof(Canvas), typeof(CanvasScaler));
        SceneManager.MoveGameObjectToScene(root, scene);
        var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 850;
        var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        var view = root.AddComponent<MenuSaberStatus>();
        view.label = UISkinKit.MakeTMP(root.transform, "PositionStatus", "", 24, Color.white,
            TextAlignmentOptions.MidlineLeft, Vector2.zero, new Vector2(430, 32), FontStyles.Normal, 0,
            UISkinKit.JapaneseFallbackFontAsset());
        view.label.rectTransform.anchorMin = view.label.rectTransform.anchorMax = new Vector2(0, 0);
        view.label.rectTransform.pivot = new Vector2(0, 0);
        view.label.rectTransform.anchoredPosition = new Vector2(24, 4);
        view.label.raycastTarget = false;
    }
    public static string Status(double now, double red, double blue)
    {
        if (red < 0 && blue < 0) return "";
        return "位置受信  <color=#FF7387>赤 " + (red >= 0 && now - red < FreshSeconds ? "●" : "○") +
            "</color>  <color=#72B5FF>青 " + (blue >= 0 && now - blue < FreshSeconds ? "●" : "○") + "</color>";
    }
    void Update()
    {
        var input = InputPoint.Instance;
        string next = input == null ? "" : Status(Time.realtimeSinceStartupAsDouble, input.LastReceivedTime, input.LastReceivedTime2);
        if (next == previous) return;
        previous = next; if (label != null) label.text = next;
    }
}
