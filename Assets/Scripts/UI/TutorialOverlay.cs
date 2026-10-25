using TMPro;
using UnityEngine;
using UnityEngine.UI;

// チュートリアルの覆い。上部に 1 行の文、本編と同じ位置の判定文字、画面下の帯にスキップの的。
// ノーツの近くには文字を出さない(本編の方針)。描画の更新は TutorialController.Tick からだけ。
public sealed class TutorialOverlay : MonoBehaviour
{
    static readonly Color Ink = new Color(.91f, .97f, 1f), Muted = new Color(.65f, .76f, .82f),
        Cyan = new Color(.43f, .93f, .87f), PanelColor = new Color(.025f, .043f, .065f, .97f);
    // 本編の GameHUDSkin と同じ: 画面中央から下へ 330px、64pt、0.6 秒で 70px 上がりながら消え、出た瞬間 1.3 倍。
    public const float JudgeY = -330f, JudgeFontSize = 64f, JudgeFlashSeconds = .6f, JudgeRise = 70f, JudgePunch = 1.3f;

    public RectTransform Controls { get; private set; }
    public Button SkipButton { get; private set; }
    public string TitleText => title != null ? title.text : "";
    public string JudgeText => judge != null && judge.gameObject.activeSelf ? judge.text : "";
    public bool HandsVisible => handsCard != null && handsCard.activeSelf;
    public string TimerText => timer != null ? timer.text : "";
    // 範囲を合わせる段で、剣が届いた範囲を囲む枠(剣の平面を画面へ写したもの)。
    public bool RangeCoverVisible => rangeBox != null && rangeBox.gameObject.activeSelf;
    public RectTransform RangeCoverRect => rangeBox != null ? rangeBox.rectTransform : null;
    public const float RangeCoverMinimumSize = 40f;

    public TutorialGuideModel Guide { get; private set; }

    TutorialController ctl;
    ScoreManager score;
    TextMeshProUGUI title, sub, hint, judge, judgeSub, timer;
    GameObject hintPanel, handsCard;
    SongSelectDiscGraphic rangeBox;
    int shownRangeSeconds = -1;
    RectTransform dots;
    Image[] dotImages = System.Array.Empty<Image>();
    Vector2 judgeBasePos;
    float judgeShownAt = -100f;
    string shownTitle, shownSub, shownHint;
    int shownStep = -1;
    (TutorialStep step, int successes, int directions, bool done) shownProgressState;

    public static TutorialOverlay Ensure()
    {
        var existing = UnityEngine.Object.FindFirstObjectByType<TutorialOverlay>();
        if (existing != null) return existing;
        var go = new GameObject("TutorialOverlay", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 100;
        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        var overlay = go.AddComponent<TutorialOverlay>();
        overlay.Build();
        return overlay;
    }

    public void Bind(TutorialController controller, ScoreManager scoring)
    {
        ctl = controller;
        if (score != null) { score.OnJudgmentEx -= OnJudgment; score.OnLongCut -= OnJudgment; }
        score = scoring;
        // ロングは1回切るごとに判定1つなので、途中の回も判定の文字を出す(本編の HUD と同じ)。
        if (score != null) { score.OnJudgmentEx += OnJudgment; score.OnLongCut += OnJudgment; }
    }

    RectTransform Rect(Transform p, string name, float x, float y, float w, float h) =>
        SongSelectVisuals.Rect(p, name, new Vector2(x, y), new Vector2(w, h));

    TextMeshProUGUI Label(Transform p, string name, string text, float size, float x, float y, float w, float h, Color color,
        bool center = true, bool strong = false)
    {
        var t = SongSelectVisuals.Label(p, name, text, size, new Vector2(x, y), new Vector2(w, h), color,
            center ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft, strong);
        t.textWrappingMode = TextWrappingModes.Normal; t.overflowMode = TextOverflowModes.Ellipsis; t.raycastTarget = false;
        return t;
    }

    // 判定調整と同じ作りの照準の的(1 秒の滞留)。
    Button Action(Transform p, string name, string text, float x, float y, float w, float h, UnityEngine.Events.UnityAction click)
    {
        var rt = Rect(p, name, x, y, w, h);
        var b = rt.gameObject.AddComponent<Button>();
        var face = SongSelectVisuals.Panel(rt, "ActionSurface", Vector2.zero, Vector2.zero, PanelColor, Cyan, 8);
        SongSelectVisuals.Stretch(face.rectTransform); face.raycastTarget = true;
        b.targetGraphic = face; b.transition = Selectable.Transition.None;
        b.navigation = new Navigation { mode = Navigation.Mode.None };
        var label = Label(rt, "ActionLabel", text, 30, 0, 0, w - 24, h - 8, Ink);
        label.enableAutoSizing = true; label.fontSizeMin = 24; label.fontSizeMax = 30; label.textWrappingMode = TextWrappingModes.NoWrap;
        rt.gameObject.AddComponent<SongSelectActionStyle>().Initialize(b, face, label, false);
        b.onClick.AddListener(click);
        var dwell = rt.gameObject.AddComponent<SaberDwellTarget>();
        dwell.dwellSeconds = 1.5f; dwell.progressColor = Cyan;
        return b;
    }

    void Build()
    {
        if (UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem), typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
        // 範囲を合わせる段の枠。文・お手本・的より奥に置き、薄い塗りと水色の縁だけにする。
        rangeBox = SongSelectSkin.Graphic(transform, "RangeCover", Vector2.zero, Vector2.one * RangeCoverMinimumSize,
            SongSelectDiscGraphic.Shape.Panel, new Color(Cyan.r, Cyan.g, Cyan.b, .08f));
        rangeBox.Edge = Cyan; rangeBox.Width = 4; rangeBox.Radius = 10;
        rangeBox.gameObject.SetActive(false);
        title = Label(transform, "Title", "", 56, 0, 350, 1700, 90, Ink, true, true);
        title.enableAutoSizing = true; title.fontSizeMin = 36; title.fontSizeMax = 56; title.textWrappingMode = TextWrappingModes.NoWrap;
        sub = Label(transform, "Sub", "", 30, 0, 282, 1600, 48, Muted);
        sub.gameObject.SetActive(false);
        hintPanel = SongSelectVisuals.Panel(transform, "HintPanel", new Vector2(0, 296), new Vector2(900, 56),
            new Color(.08f, .3f, .3f, .8f), Cyan, 8).gameObject;
        hint = Label(hintPanel.transform, "Hint", "", 26, 0, 0, 860, 48, Cyan);
        hintPanel.SetActive(false);
        var chip = Label(transform, "Chip", "れんしゅう", 24, 790, 480, 260, 40, Cyan);
        chip.font = UISkinKit.JapaneseFallbackFontAsset(); chip.fontStyle = FontStyles.Bold; chip.characterSpacing = 8f;
        timer = Label(transform, "Timer", "", 30, 0, 282, 600, 48, Ink);
        dots = Rect(transform, "Steps", -820, 446, 400, 20);
        judge = Label(transform, "Judge", "", JudgeFontSize, 0, JudgeY, 1200, 90, Ink);
        judge.font = UISkinKit.LogoFontAsset(); judge.fontStyle = FontStyles.Bold | FontStyles.Italic; judge.textWrappingMode = TextWrappingModes.NoWrap;
        judgeBasePos = judge.rectTransform.anchoredPosition;
        judge.gameObject.SetActive(false);
        judgeSub = Label(transform, "JudgeSub", "", 26, 0, JudgeY - 58, 800, 40, Ink);
        judgeSub.font = UISkinKit.LogoFontAsset(); judgeSub.characterSpacing = 4f;
        judgeSub.gameObject.SetActive(false);
        // 持ち方の絵: 左に青の刃、右に赤の刃。
        handsCard = SongSelectVisuals.Panel(transform, "HandsCard", new Vector2(0, -40), new Vector2(1060, 340), PanelColor,
            new Color(.22f, .39f, .45f), 12).gameObject;
        Blade(handsCard.transform, "LeftBlade", -180, UISkinPalette.LogoBlue, 16f, "左手");
        Blade(handsCard.transform, "RightBlade", 180, UISkinPalette.LogoRed, -16f, "右手");
        Label(handsCard.transform, "HandsText", "来た箱を、同じ色の剣で切る", 28, 0, -148, 980, 44, Ink);
        handsCard.SetActive(false);
        var guideRect = Rect(transform, "TutorialGuide", 760, 20, 360, 400);
        Guide = guideRect.gameObject.AddComponent<TutorialGuideModel>();
        Guide.raycastTarget = false;
        Label(transform, "GuideCaption", "お手本", 28, 760, 232, 300, 44, Ink, true, true);
        Label(transform, "GuideHands", "青は左手　赤は右手", 22, 760, -204, 360, 40, Ink);
        // 画面下の帯(本編の判定調整と同じ下 18%)。見えない枠だけ置き、照準の受付範囲にする。
        Controls = Rect(transform, "Controls", 0, -414, 1840, 236);
        SkipButton = Action(Controls, "Skip", "練習をやめて 曲をえらぶ", 575, -28, 650, 108, () => { if (ctl != null) ctl.Skip(); });
    }

    void Blade(Transform parent, string name, float x, Color color, float tilt, string caption)
    {
        var rt = Rect(parent, name, x, 40, 26, 230);
        var image = rt.gameObject.AddComponent<Image>();
        image.sprite = UISkinKit.SoftGlow(); image.color = color; image.raycastTarget = false;
        rt.localRotation = Quaternion.Euler(0, 0, tilt);
        Label(parent, name + "Caption", caption, 30, x, -105, 220, 44, color, true, true);
    }

    public void ShowStep(TutorialStep step, int index, int count)
    {
        ShowBanner(step.Title, step.Sub);
        if (handsCard != null) handsCard.SetActive(step.Id == "hands");
        EnsureDots(count); RefreshDots(index); shownStep = index;
        // 範囲だけの回(段が1つ)では、進み具合の点を出さない。
        if (dots != null) dots.gameObject.SetActive(count > 1);
        // 範囲を合わせる間は画面の下まで振るので、やめるの的に剣が止まって押されないよう隠す(約10秒)。
        if (SkipButton != null) SkipButton.gameObject.SetActive(step.Kind != TutorialStepKind.Range);
    }

    public void ShowBanner(string titleText, string subText)
    {
        shownTitle = titleText ?? ""; shownSub = subText ?? "";
        if (title != null) title.text = shownTitle;
        if (sub != null) sub.text = shownSub;
    }

    public void ShowFinished()
    {
        if (handsCard != null) handsCard.SetActive(false);
        if (hintPanel != null) hintPanel.SetActive(false);
        if (SkipButton != null) SkipButton.gameObject.SetActive(false);
    }

    void EnsureDots(int count)
    {
        if (dotImages.Length == count) return;
        foreach (var d in dotImages) if (d != null) Destroy(d.gameObject);
        dotImages = new Image[count];
        for (int i = 0; i < count; i++)
        {
            var rt = Rect(dots, "Dot" + i, i * 26f, 0, 16, 16);
            var image = rt.gameObject.AddComponent<Image>();
            image.sprite = UISkinKit.SoftGlow(); image.raycastTarget = false;
            dotImages[i] = image;
        }
    }

    void RefreshDots(int current)
    {
        for (int i = 0; i < dotImages.Length; i++)
            if (dotImages[i] != null) dotImages[i].color = i < current ? Cyan : i == current ? Ink : new Color(1f, 1f, 1f, .25f);
    }

    public void Tick(float frameSeconds)
    {
        if (ctl == null) return;
        bool ranging = ctl.Step != null && ctl.Step.Kind == TutorialStepKind.Range && !ctl.StepDone;
        if (Guide != null)
        {
            if (ranging) { float pose = TutorialGuideMotion.RangeSweep(ctl.DemonstrationTime); Guide.ShowPose(pose, pose); }
            else Guide.Show(ctl.DemonstrationNotes, ctl.DemonstrationTime);
        }
        if (timer != null)
        {
            if (ctl.Step != null && ctl.Step.Kind == TutorialStepKind.Range)
            {
                // 残り秒が変わったときだけ文字を作り直す。
                int seconds = ranging ? (int)System.Math.Ceiling(ctl.RangeSecondsLeft - 1e-6) : 0;
                if (seconds != shownRangeSeconds || shownProgressState.step != ctl.Step || shownProgressState.done != ctl.StepDone)
                {
                    shownRangeSeconds = seconds;
                    shownProgressState = (ctl.Step, 0, 0, ctl.StepDone);
                    timer.text = TutorialProgram.RangeTimerText(seconds);
                }
            }
            else
            {
                var state = (ctl.Step, ctl.Successes, ctl.RemainingDirections, ctl.StepDone);
                if (state != shownProgressState)
                {
                    shownProgressState = state;
                    timer.text = TutorialProgram.RemainingText(state.Item1, state.Item2, state.Item3, state.Item4);
                }
            }
        }
        ShowRangeCover(ranging);
        if (ctl.Step != null && shownStep != ctl.StepIndex) { shownStep = ctl.StepIndex; RefreshDots(shownStep); }
        if (shownTitle != ctl.BannerTitle || shownSub != ctl.BannerSub) ShowBanner(ctl.BannerTitle, ctl.BannerSub);
        if (shownHint != ctl.Hint)
        {
            shownHint = ctl.Hint;
            if (hintPanel != null) hintPanel.SetActive(false);
            if (hint != null) hint.text = shownHint ?? "";
        }
        string nextTitle = !ctl.StepDone && !string.IsNullOrEmpty(ctl.Hint) ? ctl.Hint : shownTitle;
        if (title != null && title.text != nextTitle) title.text = nextTitle;
        if (judge != null && judge.gameObject.activeSelf)
        {
            float t = (Time.unscaledTime - judgeShownAt) / JudgeFlashSeconds;
            if (t >= 1f) { judge.gameObject.SetActive(false); judgeSub.gameObject.SetActive(false); }
            else
            {
                judge.rectTransform.localScale = Vector3.one * Mathf.Lerp(JudgePunch, 1f, Mathf.Clamp01(t * 4f));
                judge.rectTransform.anchoredPosition = judgeBasePos + Vector2.up * (JudgeRise * t);
                float alpha = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.5f, 1f, t));
                var c = judge.color; c.a = alpha; judge.color = c;
                var cs = judgeSub.color; cs.a = alpha; judgeSub.color = cs;
            }
        }
    }

    // 剣の平面の矩形を、剣を映すカメラで画面へ写す。カメラの左右・上下の反転にかかわらず、写した2隅の外側を囲む。
    void ShowRangeCover(bool ranging)
    {
        if (rangeBox == null) return;
        Vector2 center = default, size = default;
        bool show = ranging && ctl.HasRangeCover && ProjectRange(ctl.RangeCamera, ctl.RangeCover, ctl.RangeCoverZ, out center, out size);
        if (show)
        {
            rangeBox.rectTransform.anchoredPosition = center;
            rangeBox.rectTransform.sizeDelta = size;
        }
        if (rangeBox.gameObject.activeSelf != show) rangeBox.gameObject.SetActive(show);
    }

    bool ProjectRange(Camera camera, Rect world, float z, out Vector2 center, out Vector2 size)
    {
        center = size = default;
        if (camera == null) return false;
        var canvas = (RectTransform)transform;
        Vector3 a = camera.WorldToScreenPoint(new Vector3(world.xMin, world.yMin, z));
        Vector3 b = camera.WorldToScreenPoint(new Vector3(world.xMax, world.yMax, z));
        if (a.z <= 0 || b.z <= 0 ||
            !RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, a, null, out Vector2 p) ||
            !RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, b, null, out Vector2 q)) return false;
        center = (p + q) * .5f;
        size = new Vector2(Mathf.Max(RangeCoverMinimumSize, Mathf.Abs(q.x - p.x)), Mathf.Max(RangeCoverMinimumSize, Mathf.Abs(q.y - p.y)));
        return true;
    }

    void OnJudgment(JudgmentTier tier, int awarded, bool wrongFlick)
    {
        if (judge == null) return;
        judge.text = JudgmentTierHelper.Label(tier);
        var color = GameHUDSkin.TierColor(tier); color.a = 1f; judge.color = color;
        judgeSub.text = "";
        judgeSub.gameObject.SetActive(false);
        judge.rectTransform.anchoredPosition = judgeBasePos;
        judge.gameObject.SetActive(true);
        judgeShownAt = Time.unscaledTime;
    }

    static string FormatTime(double seconds)
    {
        int minutes = (int)(seconds / 60);
        return minutes + ":" + (seconds - minutes * 60).ToString("00.0");
    }

    void OnDestroy()
    {
        if (score != null) { score.OnJudgmentEx -= OnJudgment; score.OnLongCut -= OnJudgment; }
    }
}
