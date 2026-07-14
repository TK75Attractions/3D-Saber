using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// 曲選択画面の見た目をランタイムで強化する(シーンは編集しない)。
// SongSelectController.Start が曲リスト/初期選択を組み立てるので、1フレ遅らせてから装飾する。
// 構成:
//   - 英字テキスト(ヘッダー/曲名/難易度/ボタン)はロゴと同じ Chakra Petch で世界観を統一
//   - 曲名は行ごとに ASCII 判定し、日本語等を含む場合は legacy Text のまま(グリフ欠け防止)
//   - 行のホバー/選択アニメは SongRowFX(バー伸長 + 文字間隔パンチ + スケールパンチ)
//   - 日本語の操作ヒント等は legacy Text のまま整える
public class SongSelectSkin : MonoBehaviour
{
    static readonly Color RowNormal = new Color(0.085f, 0.095f, 0.20f, 0.85f);
    static readonly Color RowSelected = new Color(0.10f, 0.24f, 0.33f, 0.95f);
    static readonly Color TextNormal = new Color(0.62f, 0.66f, 0.80f);
    static readonly Color[] DifficultyColors =
        { UISkinPalette.Cyan, UISkinPalette.Yellow, UISkinPalette.Magenta };

    class DifficultyCardView
    {
        public UISkinKit.NeonButtonParts parts;
        public RectTransform root;
        public TextMeshProUGUI tier;
        public TextMeshProUGUI level;
        public Image[] meter;
        public Image selectionGlow;
        public Vector2 basePosition;
        public bool selected;
    }

    SongSelectController ctl;
    readonly List<SongRowFX> rowFX = new List<SongRowFX>();
    readonly List<DifficultyCardView> difficultyCards = new List<DifficultyCardView>();
    TextMeshProUGUI difficultyDisplayTMP;
    TextMeshProUGUI difficultyLevelTMP;
    TextMeshProUGUI startDifficultyHint;
    Image[] selectedLevelMeter;
    Image startIdleGlow;
    float age;

    IEnumerator Start()
    {
        // SongSelectController.Start が走り終わるのを待つ
        yield return null;

        ctl = Object.FindFirstObjectByType<SongSelectController>();
        if (ctl == null) yield break;

        var canvas = ctl.GetComponent<Canvas>();
        if (canvas == null) canvas = ctl.GetComponentInParent<Canvas>();
        if (canvas == null) yield break;

        CyberBackdrop.Ensure(canvas);

        // 選択ハイライトはカード側(SongRowFX)で示すので矢印プレフィックスを外し、
        // ラベルを素の曲名に整えてから行を装飾する。
        ctl.selectedPrefix = "";
        ctl.normalPrefix = "";
        ctl.selectedLabelColor = Color.white;
        ctl.normalLabelColor = TextNormal;
        if (ctl.SelectedIndex >= 0) ctl.Select(ctl.SelectedIndex);

        StyleHeader(canvas);
        StyleSongPanel(canvas);
        StyleSongRows();
        StyleJacket();
        StyleDifficulty(canvas);
        StyleStartButton();
        StyleHintFooter(canvas);
        AddCalibrationButton(canvas);

        ctl.OnSelectionChanged += HandleSelectionChanged;
        ctl.OnDifficultyChanged += HandleDifficultyChanged;
        // 曲が変わると難易度レベル数値も変わるので、選択変更でも難易度表示を更新する
        ctl.OnSelectionChanged += _ => HandleDifficultyChanged(ctl.SelectedDifficultyIndex);
        // ここでは Select を呼び直さない(プレビューが再起動してしまうため)。状態だけ直接反映する。
        HandleSelectionChanged(ctl.SelectedIndex);
        HandleDifficultyChanged(ctl.SelectedDifficultyIndex);

        // セーバーポインタ: 実機セーバーの位置に光るカーソルを出し、
        // 既存のボタン(曲行/難易度/START等)に0.45秒かざすとクリック扱いになる。
        // UDP入力があるときだけ現れるので、マウス/キーボード操作とは併存する。
        SaberUIPointer.Build();
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
        // START ボタンの待機グロー(ゆっくり明滅)
        if (startIdleGlow != null)
        {
            Color c = startIdleGlow.color;
            c.a = 0.10f + 0.07f * (0.5f + 0.5f * Mathf.Sin(age * 2.0f));
            startIdleGlow.color = c;
        }

        // 選択中の難易度カードだけ、弱い呼吸光を残す。
        for (int i = 0; i < difficultyCards.Count; i++)
        {
            var card = difficultyCards[i];
            if (card == null || card.selectionGlow == null) continue;
            Color c = card.selectionGlow.color;
            c.a = card.selected
                ? 0.11f + 0.055f * (0.5f + 0.5f * Mathf.Sin(age * 2.15f))
                : 0f;
            card.selectionGlow.color = c;
        }
    }

    // ---- ヘッダー ----

    void StyleHeader(Canvas canvas)
    {
        var header = TitleSceneSkin.FindTextByContent(canvas, "Select Song");
        if (header != null) header.gameObject.SetActive(false);

        var logoFont = UISkinKit.LogoFontAsset();
        FontStyles style = logoFont != null ? FontStyles.Normal : FontStyles.Bold;

        // 小さなパンくず + 大見出し(左上)
        var crumb = UISkinKit.MakeTMP(canvas.transform, "HeaderCrumb", "// PICK YOUR TRACK", 16f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Left,
            new Vector2(-450f, 505f), new Vector2(700f, 30f), FontStyles.Normal, 6f, logoFont);

        var title = UISkinKit.MakeTMP(canvas.transform, "HeaderTitle", "SELECT SONG", 52f,
            UISkinPalette.OffWhite, TextAlignmentOptions.Left,
            new Vector2(-450f, 458f), new Vector2(700f, 70f), style, 6f, logoFont);
        title.enableVertexGradient = true;
        Color top = Color.Lerp(UISkinPalette.Cyan, Color.white, 0.75f);
        title.colorGradient = new VertexGradient(top, top, UISkinPalette.OffWhite, UISkinPalette.OffWhite);

        // 見出し下のアクセントバー
        var bar = new GameObject("HeaderBar", typeof(RectTransform), typeof(Image));
        bar.transform.SetParent(canvas.transform, false);
        var brt = bar.GetComponent<RectTransform>();
        brt.pivot = new Vector2(0f, 0.5f);
        brt.sizeDelta = new Vector2(210f, 3f);
        brt.anchoredPosition = new Vector2(-800f, 420f);
        var bimg = bar.GetComponent<Image>();
        bimg.color = UISkinPalette.Cyan;
        bimg.raycastTarget = false;

        var fade = title.gameObject.AddComponent<UIFadeSlideIn>();
        fade.delay = 0.05f;
        fade.duration = 0.45f;
        fade.fromOffset = new Vector2(-40f, 0f);
        var fade2 = crumb.gameObject.AddComponent<UIFadeSlideIn>();
        fade2.delay = 0.15f;
        fade2.duration = 0.45f;
        fade2.fromOffset = new Vector2(-30f, 0f);
    }

    // ---- 曲リスト ----

    void StyleSongPanel(Canvas canvas)
    {
        foreach (var img in canvas.GetComponentsInChildren<Image>(true))
        {
            if (img.gameObject.name != "ScrollView") continue;
            img.sprite = UISkinKit.RoundedRect();
            img.type = Image.Type.Sliced;
            img.color = new Color(0.05f, 0.05f, 0.14f, 0.72f);
            var oldOutline = img.GetComponent<Outline>();
            if (oldOutline != null) UISkinKit.SafeDestroy(oldOutline);

            var frame = new GameObject("PanelFrame", typeof(RectTransform), typeof(Image));
            frame.transform.SetParent(img.transform, false);
            var frt = frame.GetComponent<RectTransform>();
            frt.anchorMin = Vector2.zero;
            frt.anchorMax = Vector2.one;
            frt.sizeDelta = Vector2.zero;
            var fimg = frame.GetComponent<Image>();
            fimg.sprite = UISkinKit.RoundedFrame();
            fimg.type = Image.Type.Sliced;
            fimg.color = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.30f);
            fimg.raycastTarget = false;
            break;
        }
    }

    void StyleSongRows()
    {
        rowFX.Clear();
        if (ctl.scrollContent == null) return;
        var logoFont = UISkinKit.LogoFontAsset();

        foreach (Transform child in ctl.scrollContent)
        {
            var btn = child.GetComponent<Button>();
            if (btn == null) continue;

            var img = child.GetComponent<Image>();
            if (img != null)
            {
                img.sprite = UISkinKit.RoundedRect();
                img.type = Image.Type.Sliced;
                img.color = RowNormal;
            }

            // 選択中カードの左端アクセントバー(高さ/不透明度は SongRowFX が動かす)
            var bar = new GameObject("SelectedBar", typeof(RectTransform), typeof(Image));
            bar.transform.SetParent(child, false);
            var brt = bar.GetComponent<RectTransform>();
            brt.anchorMin = new Vector2(0f, 0.5f);
            brt.anchorMax = new Vector2(0f, 0.5f);
            brt.pivot = new Vector2(0f, 0.5f);
            brt.anchoredPosition = new Vector2(9f, 0f);
            brt.sizeDelta = new Vector2(5f, 0f);
            var bImg = bar.GetComponent<Image>();
            bImg.sprite = UISkinKit.RoundedRect();
            bImg.type = Image.Type.Sliced;
            bImg.color = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0f);
            bImg.raycastTarget = false;

            // 曲名ラベル: ASCII ならロゴフォントの TMP に差し替え、日本語等は legacy のまま
            var legacy = child.GetComponentInChildren<Text>();
            TextMeshProUGUI tmp = null;
            string songName = legacy != null ? legacy.text.Trim() : "";
            if (legacy != null && logoFont != null && UISkinKit.IsAsciiOnly(songName))
            {
                legacy.gameObject.SetActive(false);
                tmp = UISkinKit.MakeTMP(child, "SongLabelTMP", songName, 27f,
                    TextNormal, TextAlignmentOptions.Left,
                    Vector2.zero, Vector2.zero, FontStyles.Normal, 2f, logoFont);
                var lrt = tmp.rectTransform;
                lrt.anchorMin = Vector2.zero;
                lrt.anchorMax = Vector2.one;
                lrt.offsetMin = new Vector2(30f, 0f);
                lrt.offsetMax = new Vector2(-12f, 0f);
            }
            else if (legacy != null)
            {
                legacy.alignment = TextAnchor.MiddleLeft;
                legacy.fontSize = 26;
                legacy.fontStyle = FontStyle.Bold;
                var lrt = legacy.rectTransform;
                lrt.offsetMin = new Vector2(30f, 0f);
                lrt.offsetMax = new Vector2(-12f, 0f);
            }

            btn.transition = Selectable.Transition.None;
            var fx = child.gameObject.AddComponent<SongRowFX>();
            fx.fill = img;
            fx.accentBar = bImg;
            fx.accentBarRT = brt;
            fx.labelTMP = tmp;
            fx.labelLegacy = tmp == null ? legacy : null;
            fx.fillNormal = RowNormal;
            fx.fillSelected = RowSelected;
            fx.textNormal = TextNormal;
            fx.textSelected = Color.white;
            fx.accent = UISkinPalette.Cyan;
            rowFX.Add(fx);
        }
    }

    void HandleSelectionChanged(int idx)
    {
        for (int i = 0; i < rowFX.Count; i++)
        {
            if (rowFX[i] != null) rowFX[i].SetSelected(i == idx);
        }
    }

    // ---- ジャケット ----

    void StyleJacket()
    {
        if (ctl.jacketImage == null) return;
        var jrt = ctl.jacketImage.rectTransform;
        var parent = jrt.parent;
        int siblingIndex = jrt.GetSiblingIndex();
        Vector2 pos = jrt.anchoredPosition;
        Vector2 size = jrt.sizeDelta;

        // 枠コンテナ(元のレイヤ順を保つ)
        var frame = new GameObject("JacketFrame", typeof(RectTransform), typeof(CanvasGroup));
        frame.transform.SetParent(parent, false);
        frame.transform.SetSiblingIndex(siblingIndex);
        var frt = frame.GetComponent<RectTransform>();
        frt.anchoredPosition = pos;
        frt.sizeDelta = size + new Vector2(26f, 26f);

        // 背面グロー
        var glow = new GameObject("Glow", typeof(RectTransform), typeof(Image));
        glow.transform.SetParent(frame.transform, false);
        var grt = glow.GetComponent<RectTransform>();
        grt.anchorMin = Vector2.zero;
        grt.anchorMax = Vector2.one;
        grt.sizeDelta = new Vector2(150f, 150f);
        var gimg = glow.GetComponent<Image>();
        gimg.sprite = UISkinKit.SoftGlow();
        gimg.color = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.18f);
        gimg.raycastTarget = false;

        // 角丸パネル + マスク(ジャケット画像を角丸に切り抜く)
        var panel = new GameObject("MaskPanel", typeof(RectTransform), typeof(Image), typeof(Mask));
        panel.transform.SetParent(frame.transform, false);
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.sizeDelta = new Vector2(-18f, -18f);
        var pimg = panel.GetComponent<Image>();
        pimg.sprite = UISkinKit.RoundedRect();
        pimg.type = Image.Type.Sliced;
        pimg.color = new Color(0.06f, 0.06f, 0.15f, 1f);
        panel.GetComponent<Mask>().showMaskGraphic = true;

        // ジャケット本体をマスクの中へ
        jrt.SetParent(panel.transform, false);
        jrt.anchorMin = Vector2.zero;
        jrt.anchorMax = Vector2.one;
        jrt.offsetMin = Vector2.zero;
        jrt.offsetMax = Vector2.zero;

        // 枠線
        var line = new GameObject("FrameLine", typeof(RectTransform), typeof(Image));
        line.transform.SetParent(frame.transform, false);
        var lrt = line.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.sizeDelta = new Vector2(-18f, -18f);
        var limg = line.GetComponent<Image>();
        limg.sprite = UISkinKit.RoundedFrame();
        limg.type = Image.Type.Sliced;
        limg.color = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.55f);
        limg.raycastTarget = false;

        var fade = frame.AddComponent<UIFadeSlideIn>();
        fade.delay = 0.20f;
        fade.duration = 0.5f;
        fade.fromOffset = new Vector2(36f, 0f);
    }

    // ---- 難易度 ----

    void StyleDifficulty(Canvas canvas)
    {
        var logoFont = UISkinKit.LogoFontAsset();
        var oldLabel = TitleSceneSkin.FindTextByContent(canvas, "Difficulty");
        if (oldLabel != null) oldLabel.gameObject.SetActive(false);
        if (ctl.difficultyDisplay != null) ctl.difficultyDisplay.gameObject.SetActive(false);

        // 難易度名・レベル・3譜面を1つの情報パネルとしてまとめる。
        var deck = new GameObject("DifficultyDeck", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
        deck.transform.SetParent(canvas.transform, false);
        var deckRT = deck.GetComponent<RectTransform>();
        deckRT.sizeDelta = new Vector2(540f, 220f);
        deckRT.anchoredPosition = new Vector2(550f, -115f);
        var deckFill = deck.GetComponent<Image>();
        deckFill.sprite = UISkinKit.RoundedRect();
        deckFill.type = Image.Type.Sliced;
        deckFill.color = new Color(0.010f, 0.016f, 0.045f, 0.82f);
        deckFill.raycastTarget = false;

        var frameGo = new GameObject("Frame", typeof(RectTransform), typeof(Image));
        frameGo.transform.SetParent(deck.transform, false);
        var frameRT = frameGo.GetComponent<RectTransform>();
        frameRT.anchorMin = Vector2.zero;
        frameRT.anchorMax = Vector2.one;
        frameRT.sizeDelta = Vector2.zero;
        var frame = frameGo.GetComponent<Image>();
        frame.sprite = UISkinKit.RoundedFrame();
        frame.type = Image.Type.Sliced;
        frame.color = new Color(0.52f, 0.67f, 0.88f, 0.30f);
        frame.raycastTarget = false;

        MakeDeckAccent(deck.transform, "EasyAccent", new Vector2(-180f, 109f), UISkinPalette.Cyan);
        MakeDeckAccent(deck.transform, "NormalAccent", new Vector2(0f, 109f), UISkinPalette.Yellow);
        MakeDeckAccent(deck.transform, "HardAccent", new Vector2(180f, 109f), UISkinPalette.Magenta);

        var uiFont = UISkinKit.FontAsset("Rajdhani-SemiBold");
        UISkinKit.MakeTMP(deck.transform, "DifficultyHeading", "CHART DIFFICULTY", 14f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Left,
            new Vector2(-118f, 88f), new Vector2(280f, 24f), FontStyles.Bold, 5f, uiFont);
        UISkinKit.MakeTMP(deck.transform, "DifficultyInputHint", "A / D", 11f,
            new Color(0.52f, 0.61f, 0.78f, 0.62f), TextAlignmentOptions.Right,
            new Vector2(205f, 88f), new Vector2(80f, 22f), FontStyles.Bold, 3f, uiFont);

        FontStyles summaryStyle = logoFont != null ? FontStyles.Normal : FontStyles.Bold;
        difficultyDisplayTMP = UISkinKit.MakeTMP(deck.transform, "SelectedDifficultyName", "", 29f,
            UISkinPalette.Cyan, TextAlignmentOptions.Left,
            new Vector2(-102f, 57f), new Vector2(250f, 40f), summaryStyle, 4f, logoFont);
        difficultyLevelTMP = UISkinKit.MakeTMP(deck.transform, "SelectedDifficultyLevel", "", 18f,
            UISkinPalette.OffWhite, TextAlignmentOptions.Right,
            new Vector2(153f, 57f), new Vector2(190f, 34f), FontStyles.Bold, 3f, uiFont);
        selectedLevelMeter = BuildLevelMeter(deck.transform, "SelectedLevelMeter",
            new Vector2(0f, 28f), 28f, 6f);

        difficultyCards.Clear();
        if (ctl.difficultyButtons == null) return;
        ctl.suppressDefaultDifficultyTint = true;
        for (int i = 0; i < ctl.difficultyButtons.Length; i++)
        {
            var btn = ctl.difficultyButtons[i];
            if (btn == null) { difficultyCards.Add(null); continue; }

            Color accent = DifficultyColors[Mathf.Min(i, DifficultyColors.Length - 1)];
            string name = ctl.difficultyNames != null && i < ctl.difficultyNames.Length
                ? ctl.difficultyNames[i].ToUpperInvariant()
                : $"CHART {i + 1}";

            var rt = btn.GetComponent<RectTransform>();
            rt.SetParent(deck.transform, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(156f, 102f);
            Vector2 basePosition = new Vector2(-170f + i * 170f, -38f);
            rt.anchoredPosition = basePosition;

            var parts = UISkinKit.RestyleButton(btn, accent, 18f, name);
            parts.hover.hoverScale = 1.025f;
            parts.hover.pressScale = 0.985f;
            parts.hover.glowHoverAlpha = 0.30f;
            var labelRT = parts.label.rectTransform;
            labelRT.anchorMin = new Vector2(0f, 0.48f);
            labelRT.anchorMax = new Vector2(1f, 0.96f);
            labelRT.offsetMin = Vector2.zero;
            labelRT.offsetMax = Vector2.zero;
            parts.label.fontSize = 18f;
            parts.label.characterSpacing = 3f;

            var selectionGlowGo = new GameObject("SelectionGlow", typeof(RectTransform), typeof(Image));
            selectionGlowGo.transform.SetParent(btn.transform, false);
            selectionGlowGo.transform.SetAsFirstSibling();
            var glowRT = selectionGlowGo.GetComponent<RectTransform>();
            glowRT.anchorMin = Vector2.zero;
            glowRT.anchorMax = Vector2.one;
            glowRT.sizeDelta = new Vector2(52f, 52f);
            var selectionGlow = selectionGlowGo.GetComponent<Image>();
            selectionGlow.sprite = UISkinKit.SoftGlow();
            selectionGlow.color = new Color(accent.r, accent.g, accent.b, 0f);
            selectionGlow.raycastTarget = false;

            var tier = UISkinKit.MakeTMP(btn.transform, "Tier", $"0{i + 1}", 10f,
                new Color(0.58f, 0.65f, 0.78f, 0.55f), TextAlignmentOptions.Left,
                new Vector2(-56f, 38f), new Vector2(42f, 18f), FontStyles.Bold, 2f, uiFont);
            var level = UISkinKit.MakeTMP(btn.transform, "Level", "", 14f,
                accent, TextAlignmentOptions.Center,
                new Vector2(0f, -13f), new Vector2(130f, 24f), FontStyles.Bold, 2f, uiFont);
            var meter = BuildLevelMeter(btn.transform, "LevelMeter",
                new Vector2(0f, -38f), 8.3f, 2.8f);

            difficultyCards.Add(new DifficultyCardView
            {
                parts = parts,
                root = rt,
                tier = tier,
                level = level,
                meter = meter,
                selectionGlow = selectionGlow,
                basePosition = basePosition
            });
        }

        var fade = deck.AddComponent<UIFadeSlideIn>();
        fade.delay = 0.20f;
        fade.duration = 0.48f;
        fade.fromOffset = new Vector2(32f, 0f);
    }

    void HandleDifficultyChanged(int idx)
    {
        if (ctl == null || ctl.difficultyNames == null || ctl.difficultyNames.Length == 0) return;
        idx = Mathf.Clamp(idx, 0, ctl.difficultyNames.Length - 1);

        for (int i = 0; i < difficultyCards.Count; i++)
        {
            var card = difficultyCards[i];
            if (card == null || card.parts.button == null) continue;
            Color accent = DifficultyColors[Mathf.Min(i, DifficultyColors.Length - 1)];
            bool sel = i == idx;
            int level = ctl.DifficultyLevelAt(i);

            card.selected = sel;
            card.root.anchoredPosition = card.basePosition + new Vector2(0f, sel ? 5f : 0f);
            if (card.parts.fill != null)
            {
                card.parts.fill.color = sel
                    ? new Color(accent.r * 0.20f, accent.g * 0.20f, accent.b * 0.24f, 0.96f)
                    : UISkinKit.DarkFill(accent);
            }
            if (card.parts.frame != null)
            {
                Color fc = accent;
                fc.a = sel ? 0.96f : 0.24f;
                card.parts.frame.color = fc;
            }
            if (card.parts.label != null)
            {
                card.parts.label.color = sel ? Color.Lerp(accent, Color.white, 0.64f)
                                             : new Color(accent.r, accent.g, accent.b, 0.62f);
            }
            if (card.tier != null)
                card.tier.color = sel ? new Color(accent.r, accent.g, accent.b, 0.90f)
                                      : new Color(0.58f, 0.65f, 0.78f, 0.42f);
            if (card.level != null)
            {
                card.level.text = FormatDifficultyCardLevel(level);
                card.level.color = sel ? Color.Lerp(accent, Color.white, 0.28f)
                                       : new Color(accent.r, accent.g, accent.b, 0.58f);
            }
            UpdateLevelMeter(card.meter, level, accent, sel ? 0.92f : 0.46f);
        }

        string baseName = ctl.difficultyNames[idx].ToUpperInvariant();
        int selectedLevel = ctl.CurrentDifficultyLevel();
        Color selectedAccent = DifficultyColors[Mathf.Min(idx, DifficultyColors.Length - 1)];
        if (difficultyDisplayTMP != null)
        {
            difficultyDisplayTMP.text = baseName;
            difficultyDisplayTMP.color = selectedAccent;
        }
        if (difficultyLevelTMP != null)
        {
            difficultyLevelTMP.text = FormatDifficultyLevel(selectedLevel);
            difficultyLevelTMP.color = Color.Lerp(selectedAccent, Color.white, 0.48f);
        }
        UpdateLevelMeter(selectedLevelMeter, selectedLevel, selectedAccent, 0.90f);
        if (startDifficultyHint != null)
            startDifficultyHint.text = $"{baseName}  //  {FormatDifficultyLevel(selectedLevel)}";
    }

    static void MakeDeckAccent(Transform parent, string name, Vector2 position, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(180f, 3f);
        rt.anchoredPosition = position;
        var image = go.GetComponent<Image>();
        image.color = new Color(color.r, color.g, color.b, 0.86f);
        image.raycastTarget = false;
    }

    static Image[] BuildLevelMeter(Transform parent, string name, Vector2 position,
        float segmentWidth, float gap)
    {
        const int count = 10;
        float totalWidth = count * segmentWidth + (count - 1) * gap;
        var root = new GameObject(name, typeof(RectTransform));
        root.transform.SetParent(parent, false);
        var rootRT = root.GetComponent<RectTransform>();
        rootRT.sizeDelta = new Vector2(totalWidth, 6f);
        rootRT.anchoredPosition = position;

        var segments = new Image[count];
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject($"Segment_{i + 1:00}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(root.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(segmentWidth, 4f);
            rt.anchoredPosition = new Vector2((i - (count - 1) * 0.5f) * (segmentWidth + gap), 0f);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.36f, 0.45f, 0.62f, 0.14f);
            image.raycastTarget = false;
            segments[i] = image;
        }
        return segments;
    }

    static void UpdateLevelMeter(Image[] meter, int level, Color accent, float activeAlpha)
    {
        if (meter == null) return;
        int active = MeterSegmentsForLevel(level);
        for (int i = 0; i < meter.Length; i++)
        {
            if (meter[i] == null) continue;
            meter[i].color = i < active
                ? new Color(accent.r, accent.g, accent.b, activeAlpha)
                : new Color(0.36f, 0.45f, 0.62f, 0.12f);
        }
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

    // ---- START ----

    void StyleStartButton()
    {
        if (ctl.startButton == null) return;
        var rt = ctl.startButton.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(500f, 84f);
        rt.anchoredPosition = new Vector2(550f, -300f);
        var parts = UISkinKit.RestyleButton(ctl.startButton, UISkinPalette.Cyan, 31f, "START");
        if (parts.fill != null)
        {
            // START は他より一段明るい塗りで主役感を出す
            parts.fill.color = new Color(UISkinPalette.Cyan.r * 0.17f, UISkinPalette.Cyan.g * 0.17f,
                UISkinPalette.Cyan.b * 0.23f, 0.94f);
        }
        if (parts.label != null)
        {
            parts.label.characterSpacing = 7f;
            parts.label.rectTransform.anchoredPosition = new Vector2(0f, 10f);
        }

        startDifficultyHint = UISkinKit.MakeTMP(ctl.startButton.transform, "DifficultyHint", "", 13f,
            new Color(0.73f, 0.92f, 1f, 0.72f), TextAlignmentOptions.Center,
            new Vector2(0f, -23f), new Vector2(420f, 22f), FontStyles.Bold, 4f,
            UISkinKit.FontAsset("Rajdhani-SemiBold"));
        startDifficultyHint.raycastTarget = false;

        // 待機中の明滅グロー(ホバーグローとは別レイヤ)
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

    // ---- フッター(操作ヒント) ----

    // シーンに焼き込まれた「↑↓: 曲 …」の操作説明は説明感が強いので非表示にする(シーンは編集しない)。
    void StyleHintFooter(Canvas canvas)
    {
        foreach (var t in canvas.GetComponentsInChildren<Text>(true))
        {
            if (t.text.Contains("↑↓")) { t.gameObject.SetActive(false); break; }
        }
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

    public static void EnterCalibration()
    {
        GameSession.IsCalibrationMode = true;
        UnityEngine.SceneManagement.SceneManager.LoadScene("Game");
    }

    public static void ApplyNeon(Button btn, Color accent, float fillAlpha)
    {
        // 旧 API 互換(ResultSkin などから利用)。新スタイルへ委譲する。
        UISkinKit.RestyleButton(btn, accent);
    }
}
