using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

// ロゴと切れるキューブに視線を集める。説明文やモードカードは置かない。
public class TitleSceneSkin : MonoBehaviour
{
    public Vector3 startNoteWorldPos = new Vector3(0f, -1.82f, 0f);

    private TitleMenuController titleCtl;
    private RectTransform titleContainer;
    private TitlePresentationMotion presentationMotion;
    private CanvasGroup startTargetGroup;
    private TitleStartNote startNote;
    private Image flashImage;
    private bool transitioning;
    private AudioClip slashChimeLow, slashChimeHigh;

    void Start()
    {
        titleCtl = Object.FindFirstObjectByType<TitleMenuController>();
        if (titleCtl == null) return;
        var canvas = titleCtl.GetComponent<Canvas>();
        if (canvas == null) canvas = titleCtl.GetComponentInParent<Canvas>();
        if (canvas == null) return;
        TitleConceptSelection.BeginTitle();

        var cam = Camera.main;
        if (cam != null)
        {
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 20f;
        }

        var original = FindTextByContent(canvas, "3D SABER");
        if (original != null) original.gameObject.SetActive(false);
        HideLegacyButtons(canvas);
        if (TitleConceptSelection.Current == 0)
        {
            SaberTitleBackdrop.Ensure(canvas);
        }
        else
        {
            var presentation = new GameObject("TitleConcept_" + TitleConceptSelection.Current, typeof(RectTransform));
            presentation.transform.SetParent(canvas.transform, false);
            var rt = presentation.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            presentation.transform.SetAsFirstSibling();
            TitleConceptSelection.Build(presentation.transform);
        }
        titleContainer = TitleConceptA.BuildLogo(canvas.transform);
        BuildStartTarget(canvas, cam != null);
        BuildExitButton(canvas);

        if (cam != null)
        {
            BuildTitleSaber();
            startNote = TitleStartNote.Build(startNoteWorldPos, UISkinPalette.LogoRed);
            startNote.OnSlashed += HandleSlashed;
        }
        BuildFlashOverlay(canvas);
        presentationMotion = canvas.gameObject.AddComponent<TitlePresentationMotion>();
        presentationMotion.Configure(canvas, titleContainer, startTargetGroup, startNote);
    }

    void Update()
    {
        if (!transitioning && TitleConceptSelection.ReadSelectionKeys())
        {
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            return;
        }
        if (!transitioning && Keyboard.current != null &&
            (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.spaceKey.wasPressedThisFrame))
            HandlePlayPressed();
    }

    // 他画面でも利用する既存のグラデーションAPIは変更しない。
    public static void ApplyThreeStopGradient(TextMeshProUGUI t, Color tint, Color brand, Color dark)
    {
        t.enableVertexGradient = true;
        Color top = Color.Lerp(tint, brand, .45f);
        Color bottom = Color.Lerp(brand, dark, .55f);
        t.colorGradient = new VertexGradient(top, top, bottom, bottom);
        t.outlineWidth = .08f;
        t.outlineColor = new Color(dark.r, dark.g, dark.b, .55f);
    }

    public static void ApplyLogoGradient(TextMeshProUGUI t, Color accent)
    {
        t.enableVertexGradient = true;
        Color top = Color.Lerp(accent, Color.white, .66f);
        Color bottom = Color.Lerp(accent, Color.black, .16f);
        t.colorGradient = new VertexGradient(top, top, bottom, bottom);
        t.outlineWidth = .17f;
        t.outlineColor = new Color(accent.r, accent.g, accent.b, .90f);
    }

    static void HideLegacyButtons(Canvas canvas)
    {
        foreach (var button in canvas.GetComponentsInChildren<Button>())
        {
            var label = button.GetComponentInChildren<Text>();
            if (label != null && (label.text == "QUIT" || label.text == "START"))
                button.gameObject.SetActive(false);
        }
    }

    void BuildStartTarget(Canvas canvas, bool hasNote)
    {
        var go = new GameObject("StartTarget", typeof(RectTransform), typeof(Image), typeof(Button), typeof(CanvasGroup));
        go.transform.SetParent(canvas.transform, false);
        startTargetGroup = go.GetComponent<CanvasGroup>();
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(260f, 240f);
        rt.anchoredPosition = new Vector2(0f, -174f);
        var fill = go.GetComponent<Image>();
        fill.color = Color.clear;
        var button = go.GetComponent<Button>();
        button.targetGraphic = fill;
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(HandlePlayPressed);

        var glowGo = new GameObject("TargetGlow", typeof(RectTransform), typeof(Image));
        glowGo.transform.SetParent(go.transform, false);
        glowGo.GetComponent<RectTransform>().sizeDelta = new Vector2(360f, 280f);
        var glow = glowGo.GetComponent<Image>();
        glow.sprite = UISkinKit.SoftGlow();
        glow.color = new Color(.08f, .65f, 1f, 0f);
        glow.raycastTarget = false;

        // 閉じたボタン枠ではなく、切る対象を示す四隅の切り欠き。
        Color edge = new Color(.22f, .75f, 1f, .54f);
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        {
            Vector2 corner = new Vector2(x * 111f, y * 100f);
            MakeLine(go.transform, "TargetEdge", corner - new Vector2(x * 27f, 0), corner, 2f, edge);
            MakeLine(go.transform, "TargetEdge", corner, corner - new Vector2(0, y * 27f), 2f, edge);
        }
        MakeLine(go.transform, "SlashCue", new Vector2(-135f, -100f), new Vector2(-103f, -68f), 3f,
            new Color(.2f, 1f, .62f, .72f));
        MakeLine(go.transform, "SlashCue", new Vector2(103f, 68f), new Vector2(135f, 100f), 3f,
            new Color(.2f, 1f, .62f, .72f));

        if (!hasNote)
        {
            var fallback = UISkinKit.MakeTMP(go.transform, "StartFallback", "START", 27f,
                edge, TextAlignmentOptions.Center, Vector2.zero, new Vector2(210f, 70f), FontStyles.Normal,
                3f, UISkinKit.FontAsset("Oxanium-Bold"));
            fallback.raycastTarget = false;
        }
        var hover = go.AddComponent<UIHoverEffect>();
        hover.hoverScale = 1.04f;
        hover.pressScale = .98f;
        hover.glow = glow;
        hover.glowHoverAlpha = .22f;
    }

    void BuildExitButton(Canvas canvas)
    {
        var go = new GameObject("QuitMini", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(canvas.transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
        rt.sizeDelta = new Vector2(48f, 48f);
        rt.anchoredPosition = new Vector2(-30f, 24f);
        var fill = go.GetComponent<Image>();
        fill.color = new Color(.035f, .08f, .12f, .12f);
        var button = go.GetComponent<Button>();
        button.targetGraphic = fill;
        button.onClick.AddListener(() => { if (titleCtl != null) titleCtl.OnQuitButton(); });
        Color stroke = new Color(.35f, .6f, .75f, .48f);
        MakeLine(go.transform, "CloseA", new Vector2(-7f, -7f), new Vector2(7f, 7f), 1.8f, stroke);
        MakeLine(go.transform, "CloseB", new Vector2(-7f, 7f), new Vector2(7f, -7f), 1.8f, stroke);
    }

    static void MakeLine(Transform parent, string name, Vector2 from, Vector2 to, float width, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        Vector2 delta = to - from;
        rt.anchoredPosition = (from + to) * .5f;
        rt.sizeDelta = new Vector2(delta.magnitude, width);
        rt.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        var image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }

    void HandlePlayPressed()
    {
        if (transitioning) return;
        if (startNote != null) startNote.SlashProgrammatically();
        else if (titleCtl != null) titleCtl.OnStartButton();
    }

    void BuildTitleSaber()
    {
        InputPoint.EnsureInstance();
        var saber = new GameObject("TitleSaber");
        var tracker = saber.AddComponent<SaberTracker>();
        var bridge = saber.AddComponent<SaberInputBridge>();
        bridge.useInputPoint = true;
        bridge.fallbackToMouse = true;
        bridge.fixedZ = 0f;
        var judge = saber.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.bladeRadius = .32f;
        judge.noteHitRadiusXY = .60f;
        judge.minCutSpeed = 2.5f;
    }

    void BuildFlashOverlay(Canvas canvas)
    {
        var go = new GameObject("SlashFlash", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(canvas.transform, false);
        go.transform.SetAsLastSibling();
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        flashImage = go.GetComponent<Image>();
        flashImage.color = new Color(1f, 1f, 1f, 0f);
        flashImage.raycastTarget = false;
    }

    void HandleSlashed()
    {
        if (transitioning) return;
        transitioning = true;
        PlaySlashChime();
        StartCoroutine(TransitionAfterSlash());
    }

    IEnumerator TransitionAfterSlash()
    {
        float t = 0f;
        const float total = 1.05f;
        const float spike = .10f;
        while (t < total)
        {
            t += Time.unscaledDeltaTime;
            if (presentationMotion != null) presentationMotion.SetDeparture(t / total);
            if (flashImage != null)
            {
                float a = t < spike ? Mathf.Lerp(0f, .22f, t / spike)
                    : Mathf.Lerp(.22f, 0f, (t - spike) / .22f);
                flashImage.color = new Color(1f, 1f, 1f, a);
            }
            yield return null;
        }
        if (titleCtl != null) titleCtl.OnStartButton();
    }

    void PlaySlashChime()
    {
        if (slashChimeLow == null) slashChimeLow = JudgmentSfx.Beep(880f, .18f);
        if (slashChimeHigh == null) slashChimeHigh = JudgmentSfx.Beep(1318.5f, .35f);
        var go = new GameObject("TitleSlashSfx", typeof(AudioSource));
        go.transform.SetParent(transform, false);
        var src = go.GetComponent<AudioSource>();
        src.playOnAwake = false;
        src.PlayOneShot(slashChimeLow, .50f);
        src.PlayOneShot(slashChimeHigh, .35f);
        Destroy(go, 1.5f);
    }

    void OnDestroy()
    {
        UISkinKit.SafeDestroy(slashChimeLow);
        UISkinKit.SafeDestroy(slashChimeHigh);
        slashChimeLow = slashChimeHigh = null;
    }

    public static Text FindTextByContent(Canvas canvas, string content)
    {
        foreach (var t in canvas.GetComponentsInChildren<Text>(true))
            if (t.text == content) return t;
        return null;
    }
}
