using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 運営キーを押した結果を短く知らせる。受付を覆わず、シーンを出たら消える。
public sealed class OperatorNotice : MonoBehaviour
{
    TextMeshProUGUI label;
    float hideAt;
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
            var panel = SongSelectVisuals.Panel(go.transform,"Notice",new Vector2(0,448),new Vector2(1120,88),new Color(.015f,.03f,.05f,.96f),Color.white,10);
            panel.raycastTarget = false;
            view.label = UISkinKit.MakeTMP(panel.transform,"Message",message,30,Color.white,TextAlignmentOptions.Center,Vector2.zero,new Vector2(1080,76),FontStyles.Normal,0,UISkinKit.JapaneseFallbackFontAsset());
            view.label.raycastTarget = false;
        }
        view.label.text = message; view.hideAt = Time.unscaledTime + seconds;
    }
    void Update() { if (Time.unscaledTime >= hideAt) Destroy(gameObject); }
}
