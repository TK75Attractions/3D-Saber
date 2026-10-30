using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

// ロゴと切れるキューブに視線を集める。説明文やモードカードは置かず、1回に遊べる曲数だけ左下の隅に小さく出す(2キーで切り替え)。
public class TitleSceneSkin : MonoBehaviour
{
    public Vector3 startNoteWorldPos = new Vector3(0f, -1.82f, 0f);

    private TitleMenuController titleCtl;
    private VisitorModeBadge visitorMode;
    private RectTransform titleContainer;
    private TitlePresentationMotion presentationMotion;
    private CanvasGroup startTargetGroup;
    private TitleStartNote startNote;
    private GameObject receptionLock;
    // 開始の的の位置と倍率。2026-10-31「タイトルノーツでかくして」で開始ノーツを 1.6 倍(TitleStartNote.Size)にしたので、
    // 枠・光・クリックの的も 1.5 倍にした(中の寸法と TitleTargetProximity.Outline は前のまま)。
    // 大きいノーツはカメラより下にあるので、映りの中心が y=-190 あたりへ下がる(16:9 で、浮き沈み込みで約 -300〜-70)。枠もそこへ下げた。
    static readonly Vector2 StartTargetPosition = new Vector2(0f, -190f);
    public const float StartTargetScale = 1.5f;
    private TitleTargetProximity startProximity;
    private readonly SaberInputBridge[] titleBlades = new SaberInputBridge[2];
    private Image flashImage;
    private bool transitioning;
    private AudioClip slashChimeLow, slashChimeHigh;
    // 開始ノーツを切った後の問いかけ。「はい」なら Game シーンの練習へ、「いいえ」なら選曲へ。
    private Canvas canvasRef;
    private TitleTutorialPrompt prompt;
    public const string TutorialSceneName = "Game";

    void Start()
    {
        titleCtl = Object.FindFirstObjectByType<TitleMenuController>();
        if (titleCtl == null) return;
        var canvas = titleCtl.GetComponent<Canvas>();
        if (canvas == null) canvas = titleCtl.GetComponentInParent<Canvas>();
        if (canvas == null) return;
        canvasRef = canvas;
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
        RectTransform scenery;
        if (TitleConceptSelection.Current == 0)
        {
            scenery = SaberTitleBackdrop.Ensure(canvas).GetComponent<RectTransform>();
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
            scenery = rt;
        }
        titleContainer = TitleConceptA.BuildLogo(canvas.transform);
        BuildStartTarget(canvas, cam != null);
        BuildReceptionLock(canvas);
        BuildExitButton(canvas);
        visitorMode = VisitorModeBadge.Build(canvas.transform);

        if (cam != null)
        {
            BuildTitleSaber(SaberHand.Right, 1, true);
            BuildTitleSaber(SaberHand.Left, 2, false);
            startNote = TitleStartNote.Build(startNoteWorldPos, UISkinPalette.LogoRed);
            startNote.OnSlashed += HandleSlashed;
        }
        BuildFlashOverlay(canvas);
        presentationMotion = canvas.gameObject.AddComponent<TitlePresentationMotion>();
        presentationMotion.Configure(canvas, titleContainer, startTargetGroup, startNote, scenery, flashImage);
        // 受付の鍵(TitleReception)。運営モードではタイトルに入るたびに閉じ、開始ノーツの代わりに鍵の印を出す。
        TitleReception.EnterTitle(OperatorMode.IsOn);
        ApplyReception();
    }

    // 開始ノーツと的が出ているか(受付が開いているか)。テストと運営の確認用。
    public bool StartNoteShown => startNote != null ? startNote.gameObject.activeSelf : startTargetGroup != null && startTargetGroup.gameObject.activeSelf;
    // 鍵の印が出ているか(受付が閉じているか)。
    public bool ReceptionLockShown => receptionLock != null && receptionLock.activeSelf;

    // スタッフのマウスの中ボタン(テストからも呼ぶ)。鍵を開け閉めし、運営の知らせと音で伝える。開始ノーツを切ったあとは受けない。
    public void ToggleReception(string how)
    {
        if (transitioning || prompt != null || ScreenTransition.IsBusy) return;
        TitleReception.Toggle(how);
        ApplyReception();
        OperatorNotice.Show(TitleReception.Locked ? TitleReception.ClosedNotice : TitleReception.OpenNotice, 3f);
        MenuSfx.Difficulty(TitleReception.Locked ? 0 : 2);
    }

    // 鍵の間は開始ノーツと的を隠し(切れない・押せない)、同じ場所に鍵の印を出す。開けたら入れ替える。
    void ApplyReception()
    {
        if (transitioning) return;
        bool open = !TitleReception.Locked;
        if (startNote != null) startNote.gameObject.SetActive(open);
        if (startTargetGroup != null) startTargetGroup.gameObject.SetActive(open);
        if (receptionLock != null) receptionLock.SetActive(!open);
    }

    // 問いかけ(と問いかけへ向かう演出)の間は、Esc を2秒以内に2回でタイトルを読み直す(来場者の画面で共通。仕様面の点検 2)。
    readonly EscapeTwiceGate escapeGate = new EscapeTwiceGate();

    // 開始ノーツを切る前の、最初の状態か(問いかけも、問いかけ・選曲へ向かう演出も無い)。
    public bool AtStart => prompt == null && !transitioning;

    // タイトルを読み直して最初の状態へ戻す(問いかけの間の Esc 2回と、右クリック3回の RightClickHome)。
    // 最初の状態のまま・幕の間は何もしない(false)。
    public bool ReturnToStart()
    {
        if (AtStart || ScreenTransition.IsBusy) return false;
        if (!ScreenTransition.Load(SceneManager.GetActiveScene().name, ScreenTransition.Style.Back)) return false;
        if (prompt != null) prompt.Cancel();
        MenuSfx.ToTitle();
        return true;
    }

    void Update()
    {
        visitorMode?.Tick(Time.unscaledDeltaTime, !transitioning);
        startProximity?.Tick(canvasRef, Camera.main, InputPoint.Instance, titleBlades,
            !transitioning && !ScreenTransition.IsBusy, Time.unscaledDeltaTime);
        if (ScreenTransition.IsBusy) return;
        if (prompt != null && InputManager.EscapeGetDown
            && escapeGate.PressAndNotify(Time.unscaledTimeAsDouble, EscapeTwiceGate.ToTitleNotice))
        {
            ReturnToStart();
            return;
        }
        var mouse = Mouse.current;
        if (!transitioning && prompt == null && mouse != null && mouse.middleButton.wasPressedThisFrame)
        {
            ToggleReception("middle-click");
            return;
        }
        var keys = Keyboard.current;
        if (!transitioning && prompt == null && keys != null
            && !SongSelectController.OperatorChordHeld(keys)
            && (keys.digit2Key.wasPressedThisFrame || keys.numpad2Key.wasPressedThisFrame))
        {
            VisitorPlays.ToggleSongsPerVisitor();
            visitorMode?.Refresh(true);
            OperatorNotice.Show(VisitorModeBadge.NoticeText(VisitorPlays.SongsPerVisitor));
            MenuSfx.Difficulty(VisitorPlays.SongsPerVisitor - 1);
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
        rt.anchoredPosition = StartTargetPosition;
        rt.localScale = Vector3.one * StartTargetScale;
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
        Color edge = new Color(.22f, .75f, 1f, .75f);
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        {
            Vector2 corner = new Vector2(x * 111f, y * 100f);
            MakeLine(go.transform, "TargetEdge", corner - new Vector2(x * 27f, 0), corner, 3f, edge);
            MakeLine(go.transform, "TargetEdge", corner, corner - new Vector2(0, y * 27f), 3f, edge);
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
        startProximity = new TitleTargetProximity(rt, glow);
    }

    // 受付が閉じている間、開始ノーツの場所に出す鍵の印(改善案①の絵: 暗い四角に錠前、下に一言)。押せない(raycast なし)。
    void BuildReceptionLock(Canvas canvas)
    {
        receptionLock = new GameObject("ReceptionLock", typeof(RectTransform));
        receptionLock.transform.SetParent(canvas.transform, false);
        var rt = receptionLock.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(360f, 360f);
        rt.anchoredPosition = StartTargetPosition;

        // 開始ノーツの代わりの、暗い四角(45度回し。対角 240)。
        var plate = new GameObject("LockPlate", typeof(RectTransform), typeof(Image));
        plate.transform.SetParent(rt, false);
        var plateRect = plate.GetComponent<RectTransform>();
        plateRect.sizeDelta = new Vector2(170f, 170f);
        plateRect.localRotation = Quaternion.Euler(0f, 0f, 45f);
        var plateImage = plate.GetComponent<Image>();
        Color plateColor = new Color(.075f, .125f, .204f, .94f);
        plateImage.color = plateColor;
        plateImage.raycastTarget = false;
        Color stroke = new Color(.33f, .48f, .62f, 1f);
        MakeLine(plate.transform, "LockPlateEdge", new Vector2(-85f, -85f), new Vector2(85f, -85f), 4f, stroke);
        MakeLine(plate.transform, "LockPlateEdge", new Vector2(85f, -85f), new Vector2(85f, 85f), 4f, stroke);
        MakeLine(plate.transform, "LockPlateEdge", new Vector2(85f, 85f), new Vector2(-85f, 85f), 4f, stroke);
        MakeLine(plate.transform, "LockPlateEdge", new Vector2(-85f, 85f), new Vector2(-85f, -85f), 4f, stroke);

        // 錠前: 胴・鍵穴・つる(左右の縦線と半円)。
        Color metal = new Color(.55f, .66f, .78f, 1f);
        var body = new GameObject("LockBody", typeof(RectTransform), typeof(Image));
        body.transform.SetParent(rt, false);
        var bodyRect = body.GetComponent<RectTransform>();
        bodyRect.sizeDelta = new Vector2(58f, 44f);
        bodyRect.anchoredPosition = new Vector2(0f, -13f);
        var bodyImage = body.GetComponent<Image>();
        bodyImage.color = metal;
        bodyImage.raycastTarget = false;
        var hole = new GameObject("LockKeyhole", typeof(RectTransform), typeof(Image));
        hole.transform.SetParent(body.transform, false);
        hole.GetComponent<RectTransform>().sizeDelta = new Vector2(8f, 17f);
        var holeImage = hole.GetComponent<Image>();
        holeImage.color = plateColor;
        holeImage.raycastTarget = false;
        MakeLine(rt, "LockShackle", new Vector2(-17f, 9f), new Vector2(-17f, 18f), 6f, metal);
        MakeLine(rt, "LockShackle", new Vector2(17f, 9f), new Vector2(17f, 18f), 6f, metal);
        for (int i = 0; i < 8; i++)
        {
            float a0 = Mathf.PI * i / 8f, a1 = Mathf.PI * (i + 1) / 8f;
            MakeLine(rt, "LockShackle", new Vector2(17f * Mathf.Cos(a0), 18f + 17f * Mathf.Sin(a0)),
                new Vector2(17f * Mathf.Cos(a1), 18f + 17f * Mathf.Sin(a1)), 6f, metal);
        }

        var label = UISkinKit.MakeTMP(rt, "ReceptionLockText", TitleReception.WaitingText, 40f,
            new Color(UISkinPalette.OffWhite.r, UISkinPalette.OffWhite.g, UISkinPalette.OffWhite.b, .9f),
            TextAlignmentOptions.Center, new Vector2(0f, -165f), new Vector2(900f, 60f), FontStyles.Normal, 0f,
            UISkinKit.JapaneseFallbackFontAsset());
        label.raycastTarget = false;
        receptionLock.SetActive(false);
    }

    void BuildExitButton(Canvas canvas)
    {
        var go = new GameObject("QuitMini", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(canvas.transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
        rt.sizeDelta = new Vector2(88f, 88f);
        rt.anchoredPosition = new Vector2(-36f, 32f);
        var fill = go.GetComponent<Image>();
        fill.color = new Color(.035f, .08f, .12f, .65f);
        var button = go.GetComponent<Button>();
        button.targetGraphic = fill;
        go.AddComponent<TitleQuitHold>().Initialize(() => titleCtl.OnQuitButton(),
            () => !transitioning && !ScreenTransition.IsBusy && titleCtl != null);
        Color stroke = new Color(.65f, .82f, .92f, .9f);
        MakeLine(go.transform, "CloseA", new Vector2(-14f, -14f), new Vector2(14f, 14f), 3f, stroke);
        MakeLine(go.transform, "CloseB", new Vector2(-14f, 14f), new Vector2(14f, -14f), 3f, stroke);
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
        if (transitioning || ScreenTransition.IsBusy) return;
        // 受付が閉じている間は Enter・Space・クリックでも始めない(開けるのはマウスの中ボタン)。
        if (TitleReception.Locked) { OperatorNotice.Show(TitleReception.LockedNotice, 3f); return; }
        if (startNote != null) startNote.SlashProgrammatically();
        else if (titleCtl != null) HandleSlashed();
    }

    public bool TryStartPresentation()
    {
        if (presentationMotion == null) return false;
        HandlePlayPressed();
        return true;
    }

    void BuildTitleSaber(SaberHand hand, int stick, bool mouseFallback)
    {
        InputPoint.EnsureInstance();
        var saber = new GameObject(stick == 1 ? "TitleSaber" : "TitleSaberLeft");
        var tracker = saber.AddComponent<SaberTracker>();
        var bridge = saber.AddComponent<SaberInputBridge>();
        bridge.SetBladeColor(SaberHandHelper.HandColor(hand));
        bridge.stickIndex = stick;
        titleBlades[stick - 1] = bridge;
        bridge.useInputPoint = true;
        bridge.fallbackToMouse = mouseFallback;
        bridge.fixedZ = 0f;
        var judge = saber.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.hand = hand;
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
        if (transitioning || ScreenTransition.IsBusy || titleCtl == null || TitleReception.Locked) return;
        // 3D の開始ノーツが無い環境(カメラ無し)は、従来どおり前進演出つきで選曲へ。
        if (startNote == null || presentationMotion == null || canvasRef == null)
        {
            if (!ScreenTransition.Load(titleCtl.songSelectSceneName, ScreenTransition.Style.Forward,
                progress => { if (presentationMotion != null) presentationMotion.SetDeparture(progress); },
                TitlePresentationMotion.DepartureDuration)) return;
            transitioning = true;
            LockStartTarget();
            PlaySlashChime();
            return;
        }
        // 幕の中で「チュートリアルをしますか？」と問いかけ、答えで行き先を分ける。
        transitioning = true;
        LockStartTarget();
        PlaySlashChime();
        prompt = TitleTutorialPrompt.Begin(canvasRef, presentationMotion, HandlePromptAnswer);
    }

    void LockStartTarget()
    {
        if (startTargetGroup == null) return;
        startTargetGroup.interactable = false;
        startTargetGroup.blocksRaycasts = false;
    }

    void HandlePromptAnswer(bool tutorial)
    {
        if (titleCtl == null) return;
        // 「いいえ」でも、スマホの座標を受けているときは振る範囲を合わせる段だけ通ってから選曲へ
        // (2026-10-26 ユーザー「チュートリアルがなくても範囲検査は行うようにして」)。マウスで遊ぶときはそのまま選曲へ。
        bool rangeOnly = !tutorial && TutorialController.PhoneInputActive != null && TutorialController.PhoneInputActive();
        GameSession.TutorialPending = tutorial || rangeOnly;
        GameSession.TutorialRangeOnly = rangeOnly;
        string scene = tutorial || rangeOnly ? TutorialSceneName : titleCtl.songSelectSceneName;
        if (ScreenTransition.Load(scene, ScreenTransition.Style.Forward)) return;
        // 読み込めないときは問いかけを閉じ、タイトルを操作できる状態に戻す。
        GameSession.TutorialPending = false;
        GameSession.TutorialRangeOnly = false;
        if (prompt != null) Destroy(prompt.gameObject);
        prompt = null;
        transitioning = false;
        if (startTargetGroup != null)
        {
            startTargetGroup.interactable = true;
            startTargetGroup.blocksRaycasts = true;
        }
    }

    void PlaySlashChime()
    {
        if (slashChimeLow == null) slashChimeLow = JudgmentSfx.Beep(880f, .18f);
        if (slashChimeHigh == null) slashChimeHigh = JudgmentSfx.Beep(1318.5f, .35f);
        var go = new GameObject("TitleSlashSfx", typeof(AudioSource));
        go.transform.SetParent(transform, false);
        var src = go.GetComponent<AudioSource>();
        src.playOnAwake = false;
        // 作った音は鳴らす前に中身を確かめる(音の出力先が変わると消えるため。ProceduralSfx.Revive)。
        src.PlayOneShot(ProceduralSfx.Revive(ref slashChimeLow), .50f);
        src.PlayOneShot(ProceduralSfx.Revive(ref slashChimeHigh), .35f);
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
