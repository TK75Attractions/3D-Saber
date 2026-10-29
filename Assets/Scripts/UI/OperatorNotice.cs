using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 運営キーを押した結果を短く知らせる。受付を覆わず、シーンを出たら消える。
public sealed class OperatorNotice : MonoBehaviour
{
    // 長い知らせ(ランキング同期の「相手の台が見つかりません(…)」は、30px の1行で画面の幅を超えていた)は
    // 2行まで折り返し、それでも入らなければ字を小さくして枠の中に収める(2026-10-30 全体の点検)。
    public const float MessageFontSize = 30f, MessageMinimumFontSize = 18f;
    public static readonly Vector2 MessageSize = new Vector2(1080, 92);
    TextMeshProUGUI label;
    float hideAt;
    public TextMeshProUGUI Label => label;
    public static void Show(string message, float seconds = 2f)
    {
        if (!Application.isPlaying) return;
        var view = Object.FindFirstObjectByType<OperatorNotice>();
        if (view == null)
        {
            var go = new GameObject("OperatorNotice", typeof(Canvas), typeof(CanvasScaler));
            var canvas = go.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 950;
            var scaler = go.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920,1080); scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            view = go.AddComponent<OperatorNotice>();
            var panel = SongSelectVisuals.Panel(go.transform,"Notice",new Vector2(0,440),new Vector2(1120,MessageSize.y+12),new Color(.015f,.03f,.05f,.96f),Color.white,10);
            panel.raycastTarget = false;
            view.label = UISkinKit.MakeTMP(panel.transform,"Message",message,MessageFontSize,Color.white,TextAlignmentOptions.Center,Vector2.zero,MessageSize,FontStyles.Normal,0,UISkinKit.JapaneseFallbackFontAsset());
            view.label.raycastTarget = false;
            view.label.enableWordWrapping = true; view.label.enableAutoSizing = true;
            view.label.fontSizeMin = MessageMinimumFontSize; view.label.fontSizeMax = MessageFontSize;
        }
        view.label.text = message; view.hideAt = Time.unscaledTime + seconds;
    }
    void Update() { if (Time.unscaledTime >= hideAt) Destroy(gameObject); }
}
