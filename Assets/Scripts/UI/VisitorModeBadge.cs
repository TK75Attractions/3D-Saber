using TMPro;
using UnityEngine;

// タイトル左下の小さな曲数の札(スタッフが確かめる用。来場者に見えてもよい言葉)。入力と時計は TitleSceneSkin から渡す。
// タイトルはロゴと開始ノーツに視線を集めるので、右下の閉じるボタンと左右対称の隅に1行だけ置く。
public sealed class VisitorModeBadge : MonoBehaviour
{
    SongSelectPanelGraphic panel;
    TextMeshProUGUI label;
    float pulseLeft;
    Color accent;
    static readonly Color Fill = new Color(.015f, .03f, .06f, .82f);
    public const float PulseSeconds = .35f;
    // 1920×1080 基準。左下から(閉じるボタン 88×88・下 32 と中心の高さをそろえる)。
    public static readonly Vector2 Size = new Vector2(360, 72);
    public static readonly Vector2 Margin = new Vector2(36, 40);

    public static string ModeText(int songs) =>
        songs == 1 ? "1回で1曲 あそべる" : "1回で2曲 あそべる";
    public static string NoticeText(int songs) =>
        songs == 1 ? "1曲モード: 1人1曲まで" : "2曲モード: 1人2曲まで";

    public static VisitorModeBadge Build(Transform parent)
    {
        var panel = SongSelectVisuals.Panel(parent, "VisitorMode", Vector2.zero, Size, Fill, UISkinPalette.Cyan, 10);
        var rt = panel.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
        rt.anchoredPosition = Margin;
        var view = panel.gameObject.AddComponent<VisitorModeBadge>();
        view.panel = panel;
        view.label = UISkinKit.MakeTMP(panel.transform, "VisitorModeText", "", 30,
            UISkinPalette.OffWhite, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(Size.x - 28, Size.y - 16), FontStyles.Normal, 0,
            UISkinKit.JapaneseFallbackFontAsset());
        view.label.raycastTarget = false;
        view.Refresh(false);
        return view;
    }

    public void Refresh(bool animate)
    {
        label.text = ModeText(VisitorPlays.SongsPerVisitor);
        accent = VisitorPlays.SongsPerVisitor == 2 ? UISkinPalette.Cyan : UISkinPalette.SubtleGray;
        pulseLeft = animate ? PulseSeconds : 0;
        panel.rectTransform.localScale = Vector3.one;
        panel.SetColors(Fill, accent);
    }

    public void Tick(float dt, bool visible)
    {
        if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
        if (pulseLeft <= 0) return;
        pulseLeft = Mathf.Max(0, pulseLeft - dt);
        float pulse = Mathf.Sin(Mathf.PI * pulseLeft / PulseSeconds);
        float scale = DisplaySettings.ReducedEffects ? 1 : 1 + .04f * pulse;
        panel.rectTransform.localScale = Vector3.one * scale;
        panel.SetColors(Color.Lerp(Fill, accent, .16f * pulse), Color.Lerp(accent, Color.white, pulse));
    }
}
