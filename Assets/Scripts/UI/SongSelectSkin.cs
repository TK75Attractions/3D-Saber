using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// 曲選択画面(デザインハンドオフ 12a "SONG WHEEL" 準拠、1920×1080 基準)。
// シーンは編集せず、ランタイムで組み直す。
//   ・左  : SongWheelView(中央固定セレクタ+回転リスト。シーンの ScrollView は非表示)
//   ・右  : ジャケット(cover.png) + 曲名 + 難易度リボン + START + ドウェル説明
//   ・上  : ヘッダー(SONG SELECT クロームグラデ) + ◀TITLE ボタン
//   ・下  : 操作ヒント + 判定調整ボタン
// 曲送りは既存の「ナビノーツを斬る」方式(SongSelectSlashNav)を維持する(ユーザー指定)。
// MASTER 選択中は「高難易度注意！！」の警告を表示する(ユーザー指定)。
public class SongSelectSkin : MonoBehaviour
{
    // 難易度色(ハンドオフ 12a: EASY 緑 / NORMAL 青 / MASTER 赤。GameHUDSkin.DifficultyColor と同系)
    static readonly Color[] DifficultyColors =
        { UISkinPalette.LogoGreen, UISkinPalette.LogoBlue, UISkinPalette.LogoRed };

    static readonly Color RowLine = new Color(0.118f, 0.133f, 0.275f);     // #1E2246
    static readonly Color DisabledColor = new Color(0.227f, 0.251f, 0.40f); // #3A4066
    static readonly Color BgTop = new Color(0.031f, 0.039f, 0.102f);       // #080A1A
    static readonly Color BgMid = new Color(0.059f, 0.051f, 0.188f);       // #0F0D30
    static readonly Color BgBottom = new Color(0.102f, 0.051f, 0.239f);    // #1A0D3D
    static readonly Color GlowBlue = new Color(56f / 255f, 184f / 255f, 1f); // #38B8FF

    SongSelectController ctl;
    readonly List<DifficultyTileItem> difficultyItems = new List<DifficultyTileItem>();
    readonly Dictionary<int, Sprite> coverCache = new Dictionary<int, Sprite>();

    SongWheelView wheel;
    TextMeshProUGUI panelTitle;
    Image jacketGlow;
    GameObject jacketLockedOverlay;
    TextMeshProUGUI startDifficultyHint;
    UISkinKit.NeonButtonParts startParts;
    Image startIdleGlow;
    GameObject masterWarning;
    Text masterWarningText;
    Image dwellFillRing;
    float age;
    static Sprite backdropGradient;

    IEnumerator Start()
    {
        // SongSelectController.Start が走り終わるのを待つ
        yield return null;

        ctl = Object.FindFirstObjectByType<SongSelectController>();
        if (ctl == null) yield break;

        var canvas = ctl.GetComponent<Canvas>();
        if (canvas == null) canvas = ctl.GetComponentInParent<Canvas>();
        if (canvas == null) yield break;

        // 3Dナビノーツ(切って曲送り)を UI より手前に描くため、
        // タイトル画面と同じく Canvas をカメラ前方の平面へ移す(レイアウトは不変)。
        var worldCam = Camera.main;
        if (worldCam != null)
        {
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = worldCam;
            canvas.planeDistance = 20f;
        }
        EnsureScaler(canvas);

        ctl.selectedPrefix = "";
        ctl.normalPrefix = "";
        if (ctl.SelectedIndex >= 0) ctl.Select(ctl.SelectedIndex);

        HideSceneRelics(canvas);
        BuildBackdrop(canvas);
        BuildHeader(canvas);
        wheel = SongWheelView.Build(ctl, canvas.transform, CoverSprite, DifficultyColor);
        var wheelFade = wheel.gameObject.AddComponent<UIFadeSlideIn>();
        wheelFade.delay = 0.20f;
        wheelFade.duration = 0.5f;
        wheelFade.fromOffset = new Vector2(-40f, 0f);
        BuildRightPanel(canvas);
        BuildFooter(canvas);
        AddCalibrationButton(canvas);

        ctl.OnSelectionChanged += HandleSelectionChanged;
        ctl.OnDifficultyChanged += HandleDifficultyChanged;
        // 曲が変わると難易度レベル数値も変わるので、選択変更でも難易度表示を更新する
        ctl.OnSelectionChanged += _ => HandleDifficultyChanged(ctl.SelectedDifficultyIndex);
        HandleSelectionChanged(ctl.SelectedIndex);
        HandleDifficultyChanged(ctl.SelectedDifficultyIndex);

        // セーバーポインタ(かざして選択)。UDP入力があるときだけ現れる。
        SaberUIPointer.Build();

        // 切って曲送り: ↑/↓ のナビノーツ(ユーザー指定で ▲▼ ボタンの代わりに維持)
        SongSelectSlashNav.Build(ctl);
    }

    void OnDestroy()
    {
        if (ctl != null)
        {
            ctl.OnSelectionChanged -= HandleSelectionChanged;
            ctl.OnDifficultyChanged -= HandleDifficultyChanged;
        }
    }

    void Update()
    {
        age += Time.unscaledDeltaTime;
        // START ボタンの待機グロー(1.6s 周期の脈動)
        if (startIdleGlow != null)
        {
            Color c = startIdleGlow.color;
            c.a = startIdleGlow.enabled ? 0.10f + 0.08f * (0.5f + 0.5f * Mathf.Sin(age * Mathf.PI * 2f / 1.6f)) : 0f;
            startIdleGlow.color = c;
        }
        // MASTER 警告の脈動
        if (masterWarningText != null && masterWarning != null && masterWarning.activeSelf)
        {
            Color w = masterWarningText.color;
            w.a = 0.72f + 0.28f * (0.5f + 0.5f * Mathf.Sin(age * Mathf.PI * 2f / 0.9f));
            masterWarningText.color = w;
        }
        // ドウェル説明のデモリング(1.3s 周期で満ちる)
        if (dwellFillRing != null)
        {
            dwellFillRing.fillAmount = Mathf.Clamp01(age % 1.3f / 1.0f);
        }
    }

    // ---- 純関数・共有ユーティリティ(テストから直接叩く) ----

    public static Color DifficultyColor(int index)
    {
        return DifficultyColors[Mathf.Clamp(index, 0, DifficultyColors.Length - 1)];
    }

    public static string DifficultyDisplayName(int index, string sourceName)
    {
        if (index == 2) return "MASTER";
        if (string.IsNullOrEmpty(sourceName)) return $"CHART {index + 1}";
        return sourceName.ToUpperInvariant();
    }

    public static int MeterSegmentsForLevel(int level)
    {
        return Mathf.Clamp(level, 0, 10);
    }

    public static string FormatDifficultyLevel(int level)
    {
        return level > 0 ? $"LEVEL {Mathf.Clamp(level, 0, 99):00}" : "LEVEL --";
    }

    public static string FormatDifficultyCardLevel(int level)
    {
        return level > 0 ? $"LV {Mathf.Clamp(level, 0, 99):00}" : "LV --";
    }

    public static void EnterCalibration()
    {
        GameSession.IsCalibrationMode = true;
        UnityEngine.SceneManagement.SceneManager.LoadScene("Game");
    }

    public static void ApplyNeon(Button btn, Color accent, float fillAlpha)
    {
        // 旧 API 互換。新スタイルへ委譲する。
        UISkinKit.RestyleButton(btn, accent);
    }

    // ---- セットアップ ----

    static void EnsureScaler(Canvas canvas)
    {
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler == null) scaler = canvas.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
    }

    // シーン側の旧UI(スクロールリスト・ヘッダー・ヒント等)は非表示にする(非破壊)。
    void HideSceneRelics(Canvas canvas)
    {
        foreach (var img in canvas.GetComponentsInChildren<Image>(true))
        {
            if (img.gameObject.name == "ScrollView") { img.gameObject.SetActive(false); break; }
        }
        var header = TitleSceneSkin.FindTextByContent(canvas, "Select Song");
        if (header != null) header.gameObject.SetActive(false);
        var oldLabel = TitleSceneSkin.FindTextByContent(canvas, "Difficulty");
        if (oldLabel != null) oldLabel.gameObject.SetActive(false);
        if (ctl.difficultyDisplay != null) ctl.difficultyDisplay.gameObject.SetActive(false);
        foreach (var t in canvas.GetComponentsInChildren<Text>(true))
        {
            if (t.text.Contains("↑↓")) { t.gameObject.SetActive(false); break; }
        }
    }

    // ---- 背景(グラデ + 右寄りグロー + 床グリッド) ----

    void BuildBackdrop(Canvas canvas)
    {
        var root = new GameObject("SelectBackdrop", typeof(RectTransform));
        root.transform.SetParent(canvas.transform, false);
        root.transform.SetAsFirstSibling();
        var rt = root.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        var bg = new GameObject("Gradient", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(root.transform, false);
        var bgRT = bg.GetComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = bgRT.offsetMax = Vector2.zero;
        var bgImg = bg.GetComponent<Image>();
        bgImg.sprite = BackdropGradientSprite();
        bgImg.raycastTarget = false;

        // 右寄り (72%, 42%) の青グロー
        var glow = new GameObject("Glow", typeof(RectTransform), typeof(Image));
        glow.transform.SetParent(root.transform, false);
        var grt = glow.GetComponent<RectTransform>();
        grt.sizeDelta = new Vector2(1100f, 1000f);
        grt.anchoredPosition = new Vector2(422f, 86f);
        var gimg = glow.GetComponent<Image>();
        gimg.sprite = UISkinKit.SoftGlow();
        gimg.color = new Color(GlowBlue.r, GlowBlue.g, GlowBlue.b, 0.15f);
        gimg.raycastTarget = false;

        // 床グリッド(下部 34%)
        var grid = new GameObject("FloorGrid", typeof(RectTransform), typeof(ResultFloorGridGraphic));
        grid.transform.SetParent(root.transform, false);
        var gridRT = grid.GetComponent<RectTransform>();
        gridRT.anchorMin = new Vector2(0.5f, 0f);
        gridRT.anchorMax = new Vector2(0.5f, 0f);
        gridRT.pivot = new Vector2(0.5f, 0f);
        gridRT.sizeDelta = new Vector2(1920f, 367f);
        gridRT.anchoredPosition = Vector2.zero;
        grid.GetComponent<ResultFloorGridGraphic>().raycastTarget = false;
    }

    static Sprite BackdropGradientSprite()
    {
        if (backdropGradient != null) return backdropGradient;
        const int H = 128;
        var tex = new Texture2D(1, H, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < H; y++)
        {
            float fromTop = 1f - y / (float)(H - 1);
            Color c = fromTop < 0.62f
                ? Color.Lerp(BgTop, BgMid, fromTop / 0.62f)
                : Color.Lerp(BgMid, BgBottom, (fromTop - 0.62f) / 0.38f);
            tex.SetPixel(0, y, c);
        }
        tex.Apply();
        backdropGradient = Sprite.Create(tex, new Rect(0, 0, 1, H), new Vector2(0.5f, 0.5f), 100f);
        backdropGradient.hideFlags = HideFlags.HideAndDontSave;
        return backdropGradient;
    }

    // ---- ヘッダー ----

    void BuildHeader(Canvas canvas)
    {
        var oxBold = UISkinKit.FontAsset("Oxanium-Bold");
        var chakra = UISkinKit.LogoFontAsset();

        var crumb = UISkinKit.MakeTMP(canvas.transform, "HeaderCrumb", "// PICK YOUR TRACK", 19f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.MidlineLeft,
            new Vector2(-510f, 488f), new Vector2(700f, 26f), FontStyles.Normal, 8f, oxBold);

        // SONG SELECT(クロームグラデ + 青グロー)
        var glow = new GameObject("HeaderGlow", typeof(RectTransform), typeof(Image));
        glow.transform.SetParent(canvas.transform, false);
        var hgrt = glow.GetComponent<RectTransform>();
        hgrt.sizeDelta = new Vector2(560f, 130f);
        hgrt.anchoredPosition = new Vector2(-580f, 436f);
        var hgimg = glow.GetComponent<Image>();
        hgimg.sprite = UISkinKit.SoftGlow();
        hgimg.color = new Color(GlowBlue.r, GlowBlue.g, GlowBlue.b, 0.25f);
        hgimg.raycastTarget = false;

        var title = UISkinKit.MakeTMP(canvas.transform, "HeaderTitle", "SONG SELECT", 60f,
            Color.white, TextAlignmentOptions.MidlineLeft,
            new Vector2(-580f, 436f), new Vector2(560f, 76f), FontStyles.Normal, 2f, chakra);
        title.enableVertexGradient = true;
        title.colorGradient = new VertexGradient(Color.white, Color.white, GlowBlue, GlowBlue);

        var jp = new GameObject("HeaderJp", typeof(RectTransform), typeof(Text));
        jp.transform.SetParent(canvas.transform, false);
        var jpText = jp.GetComponent<Text>();
        jpText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        jpText.fontSize = 22;
        jpText.fontStyle = FontStyle.Bold;
        jpText.alignment = TextAnchor.MiddleLeft;
        jpText.text = "楽曲選択";
        jpText.color = UISkinPalette.SubtleGray;
        jpText.raycastTarget = false;
        jpText.horizontalOverflow = HorizontalWrapMode.Overflow;
        var jprt = jp.GetComponent<RectTransform>();
        jprt.sizeDelta = new Vector2(220f, 30f);
        jprt.anchoredPosition = new Vector2(-330f, 424f);

        // ◀ TITLE(タイトル画面へ戻る)
        var back = new GameObject("BackToTitle", typeof(RectTransform), typeof(Image), typeof(Button));
        back.transform.SetParent(canvas.transform, false);
        var brt = back.GetComponent<RectTransform>();
        brt.sizeDelta = new Vector2(190f, 54f);
        brt.anchoredPosition = new Vector2(760f, 449f);
        var bimg = back.GetComponent<Image>();
        bimg.sprite = UISkinKit.RoundedFrame();
        bimg.type = Image.Type.Sliced;
        bimg.color = RowLine;
        var backLabel = UISkinKit.MakeTMP(back.transform, "Label", "◀ TITLE", 20f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(190f, 54f), FontStyles.Normal, 5f, oxBold);
        backLabel.raycastTarget = false;
        back.GetComponent<Button>().targetGraphic = bimg;
        back.GetComponent<Button>().onClick.AddListener(
            () => UnityEngine.SceneManagement.SceneManager.LoadScene("Title"));

        var fade = title.gameObject.AddComponent<UIFadeSlideIn>();
        fade.delay = 0.08f;
        fade.duration = 0.5f;
        fade.fromOffset = new Vector2(0f, -30f);
        var fade2 = crumb.gameObject.AddComponent<UIFadeSlideIn>();
        fade2.delay = 0.08f;
        fade2.duration = 0.5f;
        fade2.fromOffset = new Vector2(0f, -20f);
    }

    // ---- 右パネル(ジャケット + 曲名 + リボン + 警告 + START + ドウェル説明) ----

    void BuildRightPanel(Canvas canvas)
    {
        BuildJacket(canvas);

        panelTitle = UISkinKit.MakeTMP(canvas.transform, "PanelSongTitle", "", 32f,
            UISkinPalette.OffWhite, TextAlignmentOptions.Center,
            new Vector2(580f, -22f), new Vector2(620f, 44f), FontStyles.Normal, 2f,
            UISkinKit.FontAsset("Oxanium-ExtraBold"));

        BuildDifficultyRibbons(canvas);
        BuildMasterWarning(canvas);
        BuildStartButton(canvas);
        BuildDwellDemo(canvas);
    }

    void BuildJacket(Canvas canvas)
    {
        if (ctl.jacketImage == null) return;
        var jrt = ctl.jacketImage.rectTransform;

        var frame = new GameObject("JacketFrame", typeof(RectTransform), typeof(CanvasGroup));
        frame.transform.SetParent(canvas.transform, false);
        var frt = frame.GetComponent<RectTransform>();
        frt.anchoredPosition = new Vector2(580f, 184f);
        frt.sizeDelta = new Vector2(386f, 386f);

        // 背面グロー(選択曲の状態で色が変わる)
        var glowGo = new GameObject("Glow", typeof(RectTransform), typeof(Image));
        glowGo.transform.SetParent(frame.transform, false);
        var grt = glowGo.GetComponent<RectTransform>();
        grt.anchorMin = Vector2.zero;
        grt.anchorMax = Vector2.one;
        grt.sizeDelta = new Vector2(170f, 170f);
        jacketGlow = glowGo.GetComponent<Image>();
        jacketGlow.sprite = UISkinKit.SoftGlow();
        jacketGlow.color = new Color(GlowBlue.r, GlowBlue.g, GlowBlue.b, 0.30f);
        jacketGlow.raycastTarget = false;

        var panel = new GameObject("MaskPanel", typeof(RectTransform), typeof(Image), typeof(Mask));
        panel.transform.SetParent(frame.transform, false);
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.sizeDelta = new Vector2(-26f, -26f);
        var pimg = panel.GetComponent<Image>();
        pimg.sprite = UISkinKit.RoundedRect();
        pimg.type = Image.Type.Sliced;
        pimg.color = new Color(0.06f, 0.06f, 0.15f, 1f);
        panel.GetComponent<Mask>().showMaskGraphic = true;

        jrt.SetParent(panel.transform, false);
        jrt.anchorMin = Vector2.zero;
        jrt.anchorMax = Vector2.one;
        jrt.offsetMin = Vector2.zero;
        jrt.offsetMax = Vector2.zero;

        var line = new GameObject("FrameLine", typeof(RectTransform), typeof(Image));
        line.transform.SetParent(frame.transform, false);
        var lrt = line.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.sizeDelta = new Vector2(-26f, -26f);
        var limg = line.GetComponent<Image>();
        limg.sprite = UISkinKit.RoundedFrame();
        limg.type = Image.Type.Sliced;
        limg.color = RowLine;
        limg.raycastTarget = false;

        // ロック曲の暗幕 + LOCKED
        jacketLockedOverlay = new GameObject("LockedOverlay", typeof(RectTransform), typeof(Image));
        jacketLockedOverlay.transform.SetParent(panel.transform, false);
        var ort = jacketLockedOverlay.GetComponent<RectTransform>();
        ort.anchorMin = Vector2.zero;
        ort.anchorMax = Vector2.one;
        ort.offsetMin = ort.offsetMax = Vector2.zero;
        var oimg = jacketLockedOverlay.GetComponent<Image>();
        oimg.color = new Color(8f / 255f, 10f / 255f, 26f / 255f, 0.72f);
        oimg.raycastTarget = false;
        var lockedLabel = UISkinKit.MakeTMP(jacketLockedOverlay.transform, "Label", "LOCKED", 24f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(300f, 40f), FontStyles.Normal, 6f,
            UISkinKit.FontAsset("Oxanium-Bold"));
        lockedLabel.raycastTarget = false;
        jacketLockedOverlay.SetActive(false);

        var fade = frame.AddComponent<UIFadeSlideIn>();
        fade.delay = 0.20f;
        fade.duration = 0.5f;
        fade.fromOffset = new Vector2(0f, -46f);
    }

    // 難易度タイル3枚(デザイン準拠: 名前 + LEVEL、選択中は色枠+浮き上がり)
    void BuildDifficultyRibbons(Canvas canvas)
    {
        var rowGo = new GameObject("DifficultyRibbonRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(CanvasGroup));
        rowGo.transform.SetParent(canvas.transform, false);
        var rowRT = rowGo.GetComponent<RectTransform>();
        rowRT.sizeDelta = new Vector2(560f, DifficultyTileItem.TileHeight);
        rowRT.anchoredPosition = new Vector2(580f, -114f);
        var row = rowGo.GetComponent<HorizontalLayoutGroup>();
        row.spacing = 14f;
        row.childAlignment = TextAnchor.MiddleCenter;
        row.childControlWidth = true;
        row.childControlHeight = true;
        row.childForceExpandWidth = true;
        row.childForceExpandHeight = false;

        difficultyItems.Clear();
        if (ctl.difficultyButtons != null)
        {
            ctl.suppressDefaultDifficultyTint = true;
            for (int i = 0; i < ctl.difficultyButtons.Length; i++)
            {
                var btn = ctl.difficultyButtons[i];
                if (btn == null) { difficultyItems.Add(null); continue; }

                Color accent = DifficultyColor(i);
                string sourceName = ctl.difficultyNames != null && i < ctl.difficultyNames.Length
                    ? ctl.difficultyNames[i] : null;
                string displayName = DifficultyDisplayName(i, sourceName);
                int displayLevel = ctl.DifficultyDisplayLevelAt(i);

                var rt = btn.GetComponent<RectTransform>();
                rt.SetParent(rowGo.transform, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);

                var item = btn.GetComponent<DifficultyTileItem>();
                if (item == null) item = btn.gameObject.AddComponent<DifficultyTileItem>();
                item.Build(btn, accent, displayName, displayLevel);
                item.SetSelected(i == ctl.SelectedDifficultyIndex, immediate: true);
                difficultyItems.Add(item);
            }
        }

        var fade = rowGo.AddComponent<UIFadeSlideIn>();
        fade.delay = 0.24f;
        fade.duration = 0.48f;
        fade.fromOffset = new Vector2(0f, -32f);
    }

    // MASTER 選択時の「高難易度注意！！」(ユーザー指定)。日本語のため legacy Text。
    void BuildMasterWarning(Canvas canvas)
    {
        masterWarning = new GameObject("MasterWarning", typeof(RectTransform));
        masterWarning.transform.SetParent(canvas.transform, false);
        masterWarning.GetComponent<RectTransform>().anchoredPosition = new Vector2(580f, -178f);

        var glow = new GameObject("Glow", typeof(RectTransform), typeof(Image));
        glow.transform.SetParent(masterWarning.transform, false);
        glow.GetComponent<RectTransform>().sizeDelta = new Vector2(420f, 80f);
        var gimg = glow.GetComponent<Image>();
        gimg.sprite = UISkinKit.SoftGlow();
        gimg.color = new Color(UISkinPalette.LogoRed.r, UISkinPalette.LogoRed.g, UISkinPalette.LogoRed.b, 0.25f);
        gimg.raycastTarget = false;

        var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        textGo.transform.SetParent(masterWarning.transform, false);
        masterWarningText = textGo.GetComponent<Text>();
        masterWarningText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        masterWarningText.fontSize = 26;
        masterWarningText.fontStyle = FontStyle.Bold;
        masterWarningText.alignment = TextAnchor.MiddleCenter;
        masterWarningText.text = "⚠ 高難易度注意！！";
        masterWarningText.color = UISkinPalette.LogoRed;
        masterWarningText.raycastTarget = false;
        masterWarningText.horizontalOverflow = HorizontalWrapMode.Overflow;
        textGo.GetComponent<RectTransform>().sizeDelta = new Vector2(420f, 40f);

        masterWarning.SetActive(false);
    }

    void BuildStartButton(Canvas canvas)
    {
        if (ctl.startButton == null) return;
        var rt = ctl.startButton.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(400f, 92f);
        rt.anchoredPosition = new Vector2(520f, -256f);

        startParts = UISkinKit.RestyleButton(ctl.startButton, UISkinPalette.Cyan, 30f, "START ▶");
        if (startParts.label != null)
        {
            startParts.label.characterSpacing = 6f;
            startParts.label.rectTransform.anchoredPosition = new Vector2(0f, 13f);
        }

        startDifficultyHint = UISkinKit.MakeTMP(ctl.startButton.transform, "DifficultyHint", "", 16f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Center,
            new Vector2(0f, -24f), new Vector2(380f, 24f), FontStyles.Normal, 3f,
            UISkinKit.FontAsset("Oxanium-Bold"));
        startDifficultyHint.raycastTarget = false;

        // 待機中の脈動グロー
        var idle = new GameObject("IdlePulse", typeof(RectTransform), typeof(Image));
        idle.transform.SetParent(ctl.startButton.transform, false);
        idle.transform.SetAsFirstSibling();
        var irt = idle.GetComponent<RectTransform>();
        irt.anchorMin = Vector2.zero;
        irt.anchorMax = Vector2.one;
        irt.sizeDelta = new Vector2(90f, 90f);
        startIdleGlow = idle.GetComponent<Image>();
        startIdleGlow.sprite = UISkinKit.SoftGlow();
        startIdleGlow.color = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.12f);
        startIdleGlow.raycastTarget = false;

        var fade = ctl.startButton.gameObject.AddComponent<UIFadeSlideIn>();
        fade.delay = 0.30f;
        fade.duration = 0.45f;
        fade.fromOffset = new Vector2(0f, -24f);
    }

    // セーバー「かざして選択」の説明デモ(リングが1秒で満ちる)
    void BuildDwellDemo(Canvas canvas)
    {
        var root = new GameObject("DwellDemo", typeof(RectTransform));
        root.transform.SetParent(canvas.transform, false);
        root.GetComponent<RectTransform>().anchoredPosition = new Vector2(806f, -252f);

        var ringBg = new GameObject("RingBg", typeof(RectTransform), typeof(Image));
        ringBg.transform.SetParent(root.transform, false);
        ringBg.GetComponent<RectTransform>().sizeDelta = new Vector2(56f, 56f);
        var rbImg = ringBg.GetComponent<Image>();
        rbImg.sprite = UISkinKit.CircleRing();
        rbImg.color = RowLine;
        rbImg.raycastTarget = false;

        var ringFill = new GameObject("RingFill", typeof(RectTransform), typeof(Image));
        ringFill.transform.SetParent(root.transform, false);
        ringFill.GetComponent<RectTransform>().sizeDelta = new Vector2(56f, 56f);
        dwellFillRing = ringFill.GetComponent<Image>();
        dwellFillRing.sprite = UISkinKit.CircleRing();
        dwellFillRing.color = UISkinPalette.Cyan;
        dwellFillRing.type = Image.Type.Filled;
        dwellFillRing.fillMethod = Image.FillMethod.Radial360;
        dwellFillRing.fillOrigin = (int)Image.Origin360.Top;
        dwellFillRing.fillClockwise = true;
        dwellFillRing.raycastTarget = false;

        var dot = new GameObject("Pointer", typeof(RectTransform), typeof(Image));
        dot.transform.SetParent(root.transform, false);
        dot.GetComponent<RectTransform>().sizeDelta = new Vector2(22f, 22f);
        var dImg = dot.GetComponent<Image>();
        dImg.sprite = UISkinKit.SoftGlow();
        dImg.color = GlowBlue;
        dImg.raycastTarget = false;

        var hint = UISkinKit.MakeTMP(root.transform, "Hint", "HOLD 1.0s", 13f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Center,
            new Vector2(0f, -44f), new Vector2(140f, 20f), FontStyles.Normal, 2f,
            UISkinKit.FontAsset("Oxanium-Bold"));
        hint.raycastTarget = false;
    }

    // ---- フッター ----

    void BuildFooter(Canvas canvas)
    {
        var footer = new GameObject("FooterHint", typeof(RectTransform), typeof(Text));
        footer.transform.SetParent(canvas.transform, false);
        var t = footer.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 18;
        t.fontStyle = FontStyle.Bold;
        t.alignment = TextAnchor.MiddleCenter;
        t.text = "↑↓ / ナビノーツを斬って曲送り　　←→ 難易度　　ENTER スタート　　セーバー: かざして選択";
        t.color = UISkinPalette.SubtleGray;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        var rt = footer.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(1400f, 28f);
        rt.anchoredPosition = new Vector2(0f, -494f);

        var fade = footer.AddComponent<UIFadeSlideIn>();
        fade.delay = 0.70f;
        fade.duration = 0.45f;
        fade.fromOffset = new Vector2(0f, -16f);
    }

    // ---- 判定調整ボタン ----

    void AddCalibrationButton(Canvas canvas)
    {
        if (canvas.transform.Find("CalibrationButton") != null) return;

        var parts = UISkinKit.MakeNeonButton(canvas.transform, "CalibrationButton", "JUDGMENT SETUP",
            Vector2.zero, new Vector2(330f, 76f), UISkinPalette.Cyan, EnterCalibration, 21f);
        var rt = parts.button.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(30f, 30f);
    }

    // ---- 状態更新 ----

    void HandleSelectionChanged(int idx)
    {
        if (wheel != null) wheel.SetSelected(idx);

        bool locked = ctl.IsLocked(idx);
        if (panelTitle != null) panelTitle.text = ResultSkin.SongIdToDisplayTitle(ctl.SongIdAt(idx));
        if (jacketGlow != null)
        {
            jacketGlow.color = locked
                ? new Color(20f / 255f, 24f / 255f, 56f / 255f, 0.6f)
                : new Color(GlowBlue.r, GlowBlue.g, GlowBlue.b, 0.30f);
        }
        if (jacketLockedOverlay != null) jacketLockedOverlay.SetActive(locked);
    }

    void HandleDifficultyChanged(int idx)
    {
        if (ctl == null || ctl.difficultyNames == null || ctl.difficultyNames.Length == 0) return;
        idx = Mathf.Clamp(idx, 0, ctl.difficultyNames.Length - 1);

        string baseName = DifficultyDisplayName(idx, ctl.difficultyNames[idx]);
        int level = ctl.CurrentDifficultyDisplayLevel();
        for (int i = 0; i < difficultyItems.Count; i++)
        {
            var item = difficultyItems[i];
            if (item == null) continue;
            item.SetLevel(ctl.DifficultyDisplayLevelAt(i));
            item.SetSelected(i == idx);
        }

        if (wheel != null) wheel.RefreshLevels(idx);
        RefreshStartState(baseName, level);

        // MASTER 選択中で遊べる譜面があるときだけ警告(ユーザー指定)
        if (masterWarning != null)
        {
            masterWarning.SetActive(idx == 2 && level > 0);
        }
    }

    void RefreshStartState(string baseName, int level)
    {
        bool locked = ctl.SelectedSongLocked;
        bool canStart = ctl.startButton != null && ctl.startButton.interactable && level > 0;
        Color accent = canStart ? UISkinPalette.Cyan : DisabledColor;

        if (startParts.frame != null)
        {
            Color fc = accent;
            fc.a = canStart ? 0.95f : 0.65f;
            startParts.frame.color = fc;
        }
        if (startParts.label != null) startParts.label.color = accent;
        if (startIdleGlow != null) startIdleGlow.enabled = canStart;
        if (startDifficultyHint != null)
        {
            startDifficultyHint.text = locked
                ? "LOCKED  //  COMING SOON"
                : $"{baseName}  //  {FormatDifficultyLevel(level)}";
        }
    }

    // ---- カバーアート(曲ごとの cover.png、共有キャッシュ) ----

    Sprite CoverSprite(int index)
    {
        if (coverCache.TryGetValue(index, out Sprite cached)) return cached;
        Sprite sprite = null;
        string songId = ctl != null ? ctl.SongIdAt(index) : "";
        if (!string.IsNullOrEmpty(songId))
        {
            string path = Path.Combine(Application.streamingAssetsPath, "Songs", songId, "cover.png");
            if (File.Exists(path))
            {
                try
                {
                    byte[] data = File.ReadAllBytes(path);
                    var tex = new Texture2D(2, 2);
                    if (tex.LoadImage(data))
                    {
                        sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                            new Vector2(0.5f, 0.5f), 100f);
                    }
                }
                catch (System.Exception)
                {
                    sprite = null; // 読めないカバーはプレースホルダーに任せる
                }
            }
        }
        coverCache[index] = sprite;
        return sprite;
    }
}
