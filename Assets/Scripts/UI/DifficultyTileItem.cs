using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 曲選択画面の難易度タイル(デザインハンドオフ 12a 準拠)。
// 「難易度名 + LEVEL 06」の矩形タイル3枚。選択中は難易度色の枠+6px浮き上がり+色14%塗り+グロー。
// 譜面なし(LEVEL --)はタイル全体を半透明にし、数値を無効色にする。
// HorizontalLayoutGroup 配下でも浮き上がりが効くよう、可視要素は内側の Content に載せて動かす。
public class DifficultyTileItem : MonoBehaviour
{
    public const float TileHeight = 92f;
    const float SelectDuration = 0.18f;

    static readonly Color LineColor = new Color(0.118f, 0.133f, 0.275f);       // #1E2246
    static readonly Color FillColor = new Color(20f / 255f, 24f / 255f, 56f / 255f, 0.55f);
    static readonly Color NameGray = new Color(0.55f, 0.60f, 0.75f);           // #8C99BF
    static readonly Color DisabledLevel = new Color(0.227f, 0.251f, 0.40f);    // #3A4066

    RectTransform content;
    CanvasGroup group;
    Image fill;
    Image border;
    Image glow;
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

        glow = MakeImage("Glow", UISkinKit.SoftGlow(), Color.clear);
        var grt = glow.rectTransform;
        grt.anchorMin = Vector2.zero;
        grt.anchorMax = Vector2.one;
        grt.sizeDelta = new Vector2(70f, 60f);

        fill = MakeImage("Fill", UISkinKit.RoundedRect(), FillColor);
        fill.type = Image.Type.Sliced;
        StretchFull(fill.rectTransform);
        fill.raycastTarget = true; // ボタン/ドウェルの当たり判定

        border = MakeImage("Border", UISkinKit.RoundedFrame(), LineColor);
        border.type = Image.Type.Sliced;
        StretchFull(border.rectTransform);

        nameText = UISkinKit.MakeTMP(content, "Name", displayName, 24f,
            NameGray, TextAlignmentOptions.Center,
            new Vector2(0f, 20f), new Vector2(172f, 30f), FontStyles.Normal, 4f,
            UISkinKit.FontAsset("Oxanium-Bold"));
        nameText.raycastTarget = false;

        levelText = UISkinKit.MakeTMP(content, "Level", "", 30f,
            UISkinPalette.OffWhite, TextAlignmentOptions.Center,
            new Vector2(0f, -18f), new Vector2(172f, 38f), FontStyles.Normal, 0f,
            UISkinKit.LogoFontAsset());
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
        if (levelText != null) levelText.text = SongSelectSkin.FormatDifficultyLevel(level);
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

    // 選択度 t(0..1)に応じて枠色・塗り・浮き上がり・グローを適用する
    void Apply(float t)
    {
        if (content != null) content.anchoredPosition = new Vector2(0f, 6f * t);
        if (border != null) border.color = Color.Lerp(LineColor, accent, t);
        if (fill != null)
        {
            fill.color = Color.Lerp(FillColor, new Color(accent.r, accent.g, accent.b, 0.14f), t);
        }
        if (glow != null) glow.color = new Color(accent.r, accent.g, accent.b, 0.27f * t);
        if (nameText != null) nameText.color = Color.Lerp(NameGray, accent, t);
        if (levelText != null)
        {
            levelText.color = hasChart ? UISkinPalette.OffWhite : DisabledLevel;
        }
        if (group != null) group.alpha = hasChart ? 1f : 0.5f;
    }

    Image MakeImage(string name, Sprite sprite, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(content, false);
        var img = go.GetComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
