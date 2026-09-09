using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 曲選択の共通金属パネル。難易度名と数値、選択色の細線を同じ基準で配置する。
// 譜面なし(LEVEL --)はタイル全体を半透明にし、数値を無効色にする。
// 選択しても位置を動かさず、文字と当たり判定の位置を固定する。
public class DifficultyTileItem : MonoBehaviour
{
    public const float TileHeight = 92f;
    const float SelectDuration = 0.18f;

    static readonly Color LineColor = SongSelectVisuals.Edge;
    static readonly Color FillColor = SongSelectVisuals.Surface;
    static readonly Color NameGray = SongSelectVisuals.Muted;
    static readonly Color DisabledLevel = SongSelectVisuals.Disabled;

    RectTransform content;
    CanvasGroup group;
    SongSelectPanelGraphic fill;
    SongSelectPanelGraphic selectionLine;
    TextMeshProUGUI nameText;
    TextMeshProUGUI levelText;
    Color accent;
    bool selected;
    bool hasChart = true;
    float amount;   // 0=非選択 → 1=選択(0.18秒で補間)
    float target;

    public bool Selected => selected;

    public void Build(Button button, Color accentColor, string displayName, int level)
    {
        accent = accentColor;

        // シーンに置かれた旧ラベルは残したまま無効化する(非破壊)
        foreach (Transform child in transform) child.gameObject.SetActive(false);

        group = GetComponent<CanvasGroup>();
        if (group == null) group = gameObject.AddComponent<CanvasGroup>();

        var layout = GetComponent<LayoutElement>();
        if (layout == null) layout = gameObject.AddComponent<LayoutElement>();
        layout.preferredWidth = 177f;
        layout.preferredHeight = TileHeight;
        layout.flexibleWidth = 1f;
        layout.flexibleHeight = 0f;

        var dwell = GetComponent<SaberDwellTarget>();
        if (dwell == null) dwell = gameObject.AddComponent<SaberDwellTarget>();
        dwell.dwellSeconds = 1f;
        dwell.progressColor = accent;

        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(transform, false);
        content = contentGo.GetComponent<RectTransform>();
        content.anchorMin = Vector2.zero;
        content.anchorMax = Vector2.one;
        content.offsetMin = content.offsetMax = Vector2.zero;

        fill = SongSelectVisuals.Panel(content,"Fill",Vector2.zero,Vector2.zero,FillColor,LineColor,8f);
        StretchFull(fill.rectTransform);
        fill.raycastTarget = true; // ボタン/ドウェルの当たり判定
        selectionLine = SongSelectVisuals.Panel(content,"SelectionLine",new Vector2(0,-42),new Vector2(145,2),Color.clear,Color.clear,0);

        nameText = UISkinKit.MakeTMP(content, "Name", displayName, 21f,
            NameGray, TextAlignmentOptions.Center,
            new Vector2(0f, 23f), new Vector2(172f, 28f), FontStyles.Normal, 1f,
            UISkinKit.FontAsset("Oxanium-Bold"));
        nameText.raycastTarget = false;

        levelText = UISkinKit.MakeTMP(content, "Level", "", 31f,
            SongSelectVisuals.Text, TextAlignmentOptions.Center,
            new Vector2(0f, -15f), new Vector2(172f, 38f), FontStyles.Normal, 0f,
            UISkinKit.FontAsset("Oxanium-ExtraBold"));
        levelText.raycastTarget = false;

        var rootImage = GetComponent<Image>();
        if (rootImage != null) rootImage.enabled = false;
        button.targetGraphic = fill;
        button.transition = Selectable.Transition.None;

        SetLevel(level);
        Apply(0f);
    }

    public void SetLevel(int level)
    {
        hasChart = level > 0;
        if (levelText != null) levelText.text = SongSelectSkin.FormatDifficultyCardLevel(level);
        Apply(amount);
    }

    public void SetSelected(bool value, bool immediate = false)
    {
        selected = value;
        target = value ? 1f : 0f;
        if (immediate)
        {
            amount = target;
            Apply(amount);
        }
    }

    void Update()
    {
        if (Mathf.Approximately(amount, target)) return;
        amount = Mathf.MoveTowards(amount, target, Time.unscaledDeltaTime / SelectDuration);
        Apply(amount);
    }

    // 選択度に応じて枠色・塗り・下線を適用する。
    void Apply(float t)
    {
        if (content != null) content.anchoredPosition = Vector2.zero;
        if (fill != null)
        {
            fill.SetColors(Color.Lerp(FillColor,Color.Lerp(FillColor,accent,.16f),t),Color.Lerp(LineColor,accent,t));
        }
        if (selectionLine != null) selectionLine.color = new Color(accent.r,accent.g,accent.b,t);
        if (nameText != null) nameText.color = Color.Lerp(NameGray, accent, t);
        if (levelText != null)
        {
            levelText.color = hasChart ? SongSelectVisuals.Text : DisabledLevel;
        }
        if (group != null) group.alpha = hasChart ? 1f : 0.5f;
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
