using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 円形バッジが選択時に横へ伸び、難易度名を見せるリボンUI。
public class DifficultyRibbonItem : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public const float CollapsedWidth = 92f;
    public const float ExpandedWidth = 252f;
    public const float ItemHeight = 92f;
    public const float SelectionDuration = 0.34f;

    private RectTransform root;
    private LayoutElement layout;
    private Image pillFill;
    private Image pillHighlight;
    private Image badgeGlow;
    private Image badgeDisk;
    private Image badgeRing;
    private RectTransform badgeRoot;
    private TextMeshProUGUI levelText;
    private TextMeshProUGUI nameText;
    private CanvasGroup nameGroup;
    private Color accent;
    private bool selected;
    private bool hovered;
    private float hoverAmount;
    private float selectionProgress;
    private float animationFrom;
    private float animationAge = SelectionDuration;

    public bool Selected => selected;
    public float SelectionProgress => selectionProgress;
    public float PreferredWidth => layout != null ? layout.preferredWidth : 0f;

    public void Build(Button button, Color accentColor, string displayName, int level)
    {
        root = GetComponent<RectTransform>();
        accent = accentColor;

        // シーンに置かれた旧ラベルは残したまま無効化し、新素材を個別レイヤーで組み直す。
        foreach (Transform child in transform) child.gameObject.SetActive(false);

        var rootImage = GetComponent<Image>();
        if (rootImage == null) rootImage = gameObject.AddComponent<Image>();
        rootImage.sprite = UISkinKit.RoundedRect();
        rootImage.type = Image.Type.Sliced;
        rootImage.color = Color.clear;
        rootImage.raycastTarget = true;
        button.targetGraphic = rootImage;
        button.transition = Selectable.Transition.None;

        layout = GetComponent<LayoutElement>();
        if (layout == null) layout = gameObject.AddComponent<LayoutElement>();
        layout.minWidth = CollapsedWidth;
        layout.preferredWidth = CollapsedWidth;
        layout.minHeight = ItemHeight;
        layout.preferredHeight = ItemHeight;
        layout.flexibleWidth = 0f;
        layout.flexibleHeight = 0f;

        var dwell = GetComponent<SaberDwellTarget>();
        if (dwell == null) dwell = gameObject.AddComponent<SaberDwellTarget>();
        dwell.dwellSeconds = 1f;
        dwell.progressColor = accent;

        var glow = MakeImage("BadgeGlow", UISkinKit.SoftGlow(),
            new Color(accent.r, accent.g, accent.b, 0f));
        badgeGlow = glow;
        SetLeftCentered(glow.rectTransform, new Vector2(47f, 0f), new Vector2(116f, 116f));

        pillFill = MakeImage("PillFill", UISkinKit.RoundedRect(), Color.clear);
        pillFill.type = Image.Type.Sliced;
        Stretch(pillFill.rectTransform, new Vector2(2f, 3f), new Vector2(-2f, -3f));

        pillHighlight = MakeImage("PillHighlight", UISkinKit.RoundedRect(), Color.clear);
        pillHighlight.type = Image.Type.Sliced;
        var highlightRT = pillHighlight.rectTransform;
        highlightRT.anchorMin = new Vector2(0f, 0.12f);
        highlightRT.anchorMax = new Vector2(0.56f, 0.88f);
        highlightRT.offsetMin = new Vector2(8f, 0f);
        highlightRT.offsetMax = Vector2.zero;

        var badgeGo = new GameObject("Badge", typeof(RectTransform));
        badgeGo.transform.SetParent(transform, false);
        badgeRoot = badgeGo.GetComponent<RectTransform>();
        SetLeftCentered(badgeRoot, new Vector2(47f, 0f), new Vector2(86f, 86f));

        badgeDisk = MakeImage("Disk", UISkinKit.Circle(), new Color(0.045f, 0.055f, 0.15f, 0.96f), badgeRoot);
        Stretch(badgeDisk.rectTransform, Vector2.zero, Vector2.zero);

        badgeRing = MakeImage("Ring", UISkinKit.CircleRing(), accent, badgeRoot);
        Stretch(badgeRing.rectTransform, Vector2.zero, Vector2.zero);

        var starGo = new GameObject("Star", typeof(RectTransform), typeof(DifficultyStarGraphic));
        starGo.transform.SetParent(badgeRoot, false);
        var starRT = starGo.GetComponent<RectTransform>();
        starRT.sizeDelta = new Vector2(55f, 55f);
        var star = starGo.GetComponent<DifficultyStarGraphic>();
        star.color = new Color(0.035f, 0.045f, 0.13f, 0.94f);
        star.raycastTarget = false;

        var font = UISkinKit.FontAsset("Oxanium-ExtraBold");
        levelText = UISkinKit.MakeTMP(badgeRoot, "Level", level.ToString(), 24f,
            Color.white, TextAlignmentOptions.Center, Vector2.zero, new Vector2(64f, 40f),
            FontStyles.Bold, 0f, font);
        levelText.raycastTarget = false;

        nameText = UISkinKit.MakeTMP(transform, "Name", displayName, 30f,
            new Color(0.055f, 0.035f, 0.14f, 1f), TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(142f, 64f), FontStyles.Bold, 0.5f, font);
        var nameRT = nameText.rectTransform;
        nameRT.anchorMin = new Vector2(0f, 0.5f);
        nameRT.anchorMax = new Vector2(0f, 0.5f);
        nameRT.pivot = new Vector2(0f, 0.5f);
        nameRT.anchoredPosition = new Vector2(99f, 0f);
        nameGroup = nameText.gameObject.AddComponent<CanvasGroup>();
        nameGroup.alpha = 0f;
        nameGroup.blocksRaycasts = false;
        nameGroup.interactable = false;

        SetLevel(level);
        ApplyVisuals();
    }

    public void SetLevel(int level)
    {
        if (levelText != null) levelText.text = Mathf.Clamp(level, 0, 99).ToString();
    }

    public void SetSelected(bool value, bool immediate = false)
    {
        if (selected == value && !immediate) return;
        selected = value;
        animationFrom = selectionProgress;
        animationAge = 0f;
        if (immediate)
        {
            selectionProgress = selected ? 1f : 0f;
            animationAge = SelectionDuration;
            ApplyVisuals();
        }
    }

    void Update()
    {
        hoverAmount = Mathf.MoveTowards(hoverAmount, hovered ? 1f : 0f,
            Time.unscaledDeltaTime * 7f);

        if (animationAge < SelectionDuration)
        {
            animationAge = Mathf.Min(SelectionDuration, animationAge + Time.unscaledDeltaTime);
            float t = animationAge / SelectionDuration;
            float eased = selected ? EaseOutBack(t) : Smooth01(t);
            selectionProgress = Mathf.LerpUnclamped(animationFrom, selected ? 1f : 0f, eased);
            if (animationAge >= SelectionDuration) selectionProgress = selected ? 1f : 0f;
        }
        ApplyVisuals();
    }

    void ApplyVisuals()
    {
        float visual = Mathf.Clamp01(selectionProgress);
        float widthT = Mathf.Clamp(selectionProgress, 0f, 1.08f);
        bool widthChanged = false;
        if (layout != null)
        {
            float preferredWidth = Mathf.LerpUnclamped(CollapsedWidth, ExpandedWidth, widthT);
            widthChanged = !Mathf.Approximately(layout.preferredWidth, preferredWidth);
            layout.preferredWidth = preferredWidth;
        }

        if (pillFill != null)
            pillFill.color = new Color(accent.r, accent.g, accent.b, 0.92f * visual);
        if (pillHighlight != null)
        {
            Color light = Color.Lerp(accent, Color.white, 0.42f);
            pillHighlight.color = new Color(light.r, light.g, light.b, 0.16f * visual);
        }
        if (badgeDisk != null)
        {
            Color diskColor = Color.Lerp(accent * 0.68f,
                Color.Lerp(accent, Color.white, 0.16f), visual);
            diskColor.a = 0.96f;
            badgeDisk.color = diskColor;
        }
        if (badgeRing != null)
            badgeRing.color = new Color(accent.r, accent.g, accent.b,
                Mathf.Clamp01(0.76f + visual * 0.24f + hoverAmount * 0.10f));
        if (badgeGlow != null)
            badgeGlow.color = new Color(accent.r, accent.g, accent.b,
                0.08f + visual * 0.14f + hoverAmount * 0.12f);
        if (badgeRoot != null)
        {
            float scale = 1f + visual * 0.025f + hoverAmount * 0.035f;
            badgeRoot.localScale = Vector3.one * scale;
        }
        if (nameGroup != null)
            nameGroup.alpha = Smooth01(Mathf.InverseLerp(0.18f, 0.72f, visual));

        if (widthChanged && root != null && root.parent is RectTransform parent)
            LayoutRebuilder.MarkLayoutForRebuild(parent);
    }

    public void OnPointerEnter(PointerEventData eventData) { hovered = true; }
    public void OnPointerExit(PointerEventData eventData) { hovered = false; }

    public static float EaseOutBack(float t)
    {
        t = Mathf.Clamp01(t);
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float x = t - 1f;
        return 1f + c3 * x * x * x + c1 * x * x;
    }

    static float Smooth01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    Image MakeImage(string objectName, Sprite sprite, Color color, Transform parent = null)
    {
        var go = new GameObject(objectName, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent != null ? parent : transform, false);
        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    static void Stretch(RectTransform rt, Vector2 offsetMin, Vector2 offsetMax)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
    }

    static void SetLeftCentered(RectTransform rt, Vector2 position, Vector2 size)
    {
        rt.anchorMin = new Vector2(0f, 0.5f);
        rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
    }
}
