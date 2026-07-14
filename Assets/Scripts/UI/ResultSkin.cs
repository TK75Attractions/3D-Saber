using UnityEngine;
using UnityEngine.UI;
using TMPro;

// リザルト画面(デザインハンドオフ 6a "CHAKRA UNIFIED" 準拠、1920×1080 基準)。
// シーン既存のスタッツ Text は非表示にし、GameSession の値からレイアウトを組み直す(シーンは編集しない)。
//   ・中央: RANK ワードマーク + 六角形ランクバッジ(クライマックス、Slam+衝撃波リング) + ACCURACY
//   ・左  : SCORE(シアングラデ大数字) + MAX COMBO + HI-SCORE(+NEW RECORD!)
//   ・右  : 判定内訳 5 行(罫線付き)
//   ・下  : 判定分布バー + BACK + スキップ案内
// 数字・判定ラベル・ランク文字は Chakra Petch Bold Italic、見出し系は Oxanium。
// 背景(グラデ・中央グロー・パース床グリッド)もコードで生成し、画像アセットは使わない。
public class ResultSkin : MonoBehaviour
{
    // 出現タイミング(秒)。デザインハンドオフのタイムラインをそのまま定数化。
    const float DelayTitle = 0.08f;
    const float DelayScoreBlock = 0.24f;
    const float DelayAccuracy = 0.46f;
    const float DelayRowsStart = 0.66f;
    const float DelayRowsStep = 0.13f;
    const float DelayDistribution = 1.30f;
    const float DelayMaxCombo = 1.36f;
    const float DelayRankWordmark = 1.66f;
    const float DelayRankBadge = 1.82f;
    const float DurRankBadge = 0.65f;
    const float DelayRing = 1.98f;
    const float DurRing = 0.70f;
    const float DelayBackButton = 2.30f;
    const float DelaySkipHint = 2.50f;

    // スライド距離(px)。rvU=下から46 / rvL=左から90 / rvR=右から90。
    static readonly Vector2 FromBelow = new Vector2(0f, -46f);
    static readonly Vector2 FromLeft = new Vector2(-90f, 0f);
    static readonly Vector2 FromRight = new Vector2(90f, 0f);

    // デザイントークン(ハンドオフの HEX 値)
    static readonly Color BgTop = new Color(0.031f, 0.039f, 0.102f);      // #080A1A
    static readonly Color BgMid = new Color(0.059f, 0.051f, 0.188f);      // #0F0D30
    static readonly Color BgBottom = new Color(0.102f, 0.051f, 0.239f);   // #1A0D3D
    static readonly Color RowLine = new Color(0.118f, 0.133f, 0.275f);    // #1E2246
    static readonly Color BadgeInner = new Color(0.043f, 0.055f, 0.141f); // #0B0E24
    static readonly Color DeepCyan = new Color(0.094f, 0.60f, 0.702f);    // #1899B3
    static readonly Color GlowBlue = new Color(56f / 255f, 184f / 255f, 1f); // #38B8FF

    private ResultReveal reveal;
    private static Sprite backdropGradient; // 縦3停止グラデ(共有キャッシュ)

    void Start()
    {
        var ctl = Object.FindFirstObjectByType<ResultController>();
        if (ctl == null) return;

        var canvas = ctl.GetComponent<Canvas>();
        if (canvas == null) canvas = ctl.GetComponentInParent<Canvas>();
        if (canvas == null) return;

        EnsureScaler(canvas);
        reveal = ResultReveal.Ensure(canvas);
        HideLegacyStats(ctl);

        BuildBackdrop(canvas);

        var root = new GameObject("ResultStats", typeof(RectTransform));
        root.transform.SetParent(canvas.transform, false);
        var rootRT = root.GetComponent<RectTransform>();
        rootRT.anchorMin = rootRT.anchorMax = new Vector2(0.5f, 0.5f);
        rootRT.sizeDelta = Vector2.zero;

        float accuracy = PlayRankHelper.Accuracy(
            GameSession.FinalPerfect, GameSession.FinalGreat, GameSession.FinalGood,
            GameSession.FinalBad, GameSession.FinalMiss);
        PlayRank rank = PlayRankHelper.FromAccuracy(accuracy);
        Color rankColor = RankAccentColor(PlayRankHelper.Label(rank));

        BuildTitleRow(root.transform);
        BuildCenterColumn(root.transform, rank, rankColor, accuracy);
        BuildScoreBlock(root.transform);
        BuildJudgeList(root.transform);
        BuildDistributionBar(root.transform);
        StyleBackButton(canvas);
        BuildSkipHint(root.transform);
    }

    // ---- 純関数(テストから直接叩く) ----

    // 精度の百分率表記
    public static string FormatAccuracy(float accuracy01)
    {
        return (Mathf.Clamp01(accuracy01) * 100f).ToString("F1") + "%";
    }

    // 曲ID("ElDorado" 等)を表示タイトル("EL DORADO")へ。大文字の前に区切りを入れて全大文字化。
    public static string SongIdToDisplayTitle(string songId)
    {
        if (string.IsNullOrEmpty(songId)) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < songId.Length; i++)
        {
            char ch = songId[i];
            if (ch == '_' || ch == '-') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(ch) && !char.IsUpper(songId[i - 1]) && songId[i - 1] != ' ')
            {
                sb.Append(' ');
            }
            sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    // 判定分布バーの各セグメント幅。合計 0 のときは全て 0。
    public static float[] DistributionWidths(int[] counts, float totalWidth)
    {
        var widths = new float[counts != null ? counts.Length : 0];
        if (counts == null) return widths;
        int total = 0;
        foreach (int c in counts) total += Mathf.Max(0, c);
        if (total <= 0) return widths;
        for (int i = 0; i < counts.Length; i++)
        {
            widths[i] = totalWidth * Mathf.Max(0, counts[i]) / total;
        }
        return widths;
    }

    // ランク色マップ(バッジ枠・グロー・リング用。ハンドオフ仕様)
    public static Color RankAccentColor(string rankLabel)
    {
        switch (rankLabel)
        {
            case "S+": return UISkinPalette.Cyan;      // #45FFF7
            case "S": return UISkinPalette.LogoBlue;   // #38B8FF
            case "A": return UISkinPalette.LogoRed;    // #FF3854
            case "B": return UISkinPalette.LogoGreen;  // #59FF8C
            default: return UISkinPalette.NoteFlick;   // C: #BF66FF
        }
    }

    // ---- セットアップ ----

    // 1920×1080 基準の絶対座標を成立させる(16:9 は FreezeAspectRate 側で固定済み)
    static void EnsureScaler(Canvas canvas)
    {
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler == null) scaler = canvas.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
    }

    // 旧スタッツ(ラベル+数値が同居した Text)と旧曲名は非表示にして作り直す。非破壊(SetActive のみ)。
    void HideLegacyStats(ResultController ctl)
    {
        if (ctl.titleText != null) ctl.titleText.gameObject.SetActive(false);
        if (ctl.scoreText != null) ctl.scoreText.gameObject.SetActive(false);
        if (ctl.comboText != null) ctl.comboText.gameObject.SetActive(false);
        if (ctl.perfectText != null) ctl.perfectText.gameObject.SetActive(false);
        if (ctl.greatText != null) ctl.greatText.gameObject.SetActive(false);
        if (ctl.goodText != null) ctl.goodText.gameObject.SetActive(false);
        if (ctl.badText != null) ctl.badText.gameObject.SetActive(false);
        if (ctl.missText != null) ctl.missText.gameObject.SetActive(false);
    }

    // ---- 背景(グラデ + 中央グロー + パース床グリッド) ----

    void BuildBackdrop(Canvas canvas)
    {
        var root = new GameObject("ResultBackdrop", typeof(RectTransform));
        root.transform.SetParent(canvas.transform, false);
        root.transform.SetAsFirstSibling();
        var rt = root.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        // 縦グラデ #080A1A → 62% #0F0D30 → #1A0D3D
        var bg = new GameObject("Gradient", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(root.transform, false);
        var bgRT = bg.GetComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = bgRT.offsetMax = Vector2.zero;
        var bgImg = bg.GetComponent<Image>();
        bgImg.sprite = BackdropGradientSprite();
        bgImg.raycastTarget = false;

        // 中央グロー: 1100×1100、中心 (50%, 46%)
        AddGlow(root.transform, "CenterGlow", new Vector2(0f, 43f), new Vector2(1100f, 1100f),
            new Color(GlowBlue.r, GlowBlue.g, GlowBlue.b, 0.18f));

        // 床グリッド: 画面下部 40%
        var grid = new GameObject("FloorGrid", typeof(RectTransform), typeof(ResultFloorGridGraphic));
        grid.transform.SetParent(root.transform, false);
        var gridRT = grid.GetComponent<RectTransform>();
        gridRT.anchorMin = new Vector2(0.5f, 0f);
        gridRT.anchorMax = new Vector2(0.5f, 0f);
        gridRT.pivot = new Vector2(0.5f, 0f);
        gridRT.sizeDelta = new Vector2(1920f, 432f);
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
            float fromTop = 1f - y / (float)(H - 1); // 0=上端, 1=下端
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

    // ---- タイトル行(top:40 中央) ----

    void BuildTitleRow(Transform parent)
    {
        var row = new GameObject("TitleRow", typeof(RectTransform));
        row.transform.SetParent(parent, false);
        var rt = row.GetComponent<RectTransform>();
        rt.anchoredPosition = new Vector2(0f, 473f);
        var layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 28f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var fitter = row.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        string songId = GameSession.SelectedSongId;
        string title = GameSession.SelectedSongTitle;
        string en = !string.IsNullOrEmpty(songId) ? SongIdToDisplayTitle(songId)
                  : (!string.IsNullOrEmpty(title) && UISkinKit.IsAsciiOnly(title)
                      ? title.ToUpperInvariant() : "RESULT");
        UISkinKit.MakeTMP(row.transform, "TitleEn", en, 46f,
            UISkinPalette.OffWhite, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(10f, 56f), FontStyles.Normal, 6f,
            UISkinKit.FontAsset("Oxanium-ExtraBold"));

        // 和名(日本語タイトルがあるときだけ。TMP は ASCII フォントのため legacy Text)
        if (!string.IsNullOrEmpty(title) && !UISkinKit.IsAsciiOnly(title) && title != songId)
        {
            var jp = new GameObject("TitleJp", typeof(RectTransform), typeof(Text));
            jp.transform.SetParent(row.transform, false);
            var t = jp.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = 24;
            t.fontStyle = FontStyle.Bold;
            t.alignment = TextAnchor.MiddleCenter;
            t.text = title;
            t.color = UISkinPalette.SubtleGray;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
        }
        Reveal(row, DelayTitle, 0.5f, FromBelow);
    }

    // ---- 中央カラム(RANK ワードマーク + 六角バッジ + ACCURACY) ----

    void BuildCenterColumn(Transform parent, PlayRank rank, Color rankColor, float accuracy)
    {
        var chakra = ChakraFont();

        // RANK ワードマーク(クロームグラデ + 青グロー)
        var wordmarkRoot = new GameObject("RankWordmark", typeof(RectTransform));
        wordmarkRoot.transform.SetParent(parent, false);
        wordmarkRoot.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, 351f);
        AddGlow(wordmarkRoot.transform, "Glow", Vector2.zero, new Vector2(460f, 170f),
            new Color(GlowBlue.r, GlowBlue.g, GlowBlue.b, 0.30f));
        var wm = UISkinKit.MakeTMP(wordmarkRoot.transform, "Text", "RANK", 86f,
            Color.white, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(520f, 100f), FontStyles.Normal, 4f, chakra);
        wm.enableVertexGradient = true;
        wm.colorGradient = new VertexGradient(Color.white, Color.white, GlowBlue, GlowBlue);
        Reveal(wordmarkRoot, DelayRankWordmark, 0.5f, FromBelow);

        // 衝撃波リング(バッジの外周 inset:-36 相当。Slam と独立に拡大+フェード)
        var ring = new GameObject("ShockRing", typeof(RectTransform), typeof(Image));
        ring.transform.SetParent(parent, false);
        var ringRT = ring.GetComponent<RectTransform>();
        ringRT.anchoredPosition = new Vector2(0f, 74f);
        ringRT.sizeDelta = new Vector2(472f, 512f);
        var ringImg = ring.GetComponent<Image>();
        ringImg.sprite = UISkinKit.CircleRing();
        ringImg.color = rankColor;
        ringImg.raycastTarget = false;
        if (reveal != null) reveal.Add(ring, DelayRing, DurRing, ResultReveal.Kind.Ring, Vector2.zero);

        // 六角形バッジ(デーン)
        var badgeRoot = new GameObject("RankBadge", typeof(RectTransform));
        badgeRoot.transform.SetParent(parent, false);
        var badgeRT = badgeRoot.GetComponent<RectTransform>();
        badgeRT.anchoredPosition = new Vector2(0f, 74f);
        badgeRT.sizeDelta = new Vector2(400f, 440f);

        AddGlow(badgeRoot.transform, "BadgeGlow", Vector2.zero, new Vector2(760f, 800f),
            new Color(rankColor.r, rankColor.g, rankColor.b, 0.50f));

        var outer = new GameObject("HexOuter", typeof(RectTransform), typeof(ResultHexBadgeGraphic));
        outer.transform.SetParent(badgeRoot.transform, false);
        outer.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 440f);
        var outerHex = outer.GetComponent<ResultHexBadgeGraphic>();
        outerHex.topColor = rankColor;
        outerHex.raycastTarget = false;

        var inner = new GameObject("HexInner", typeof(RectTransform), typeof(ResultHexBadgeGraphic));
        inner.transform.SetParent(badgeRoot.transform, false);
        inner.GetComponent<RectTransform>().sizeDelta = new Vector2(378f, 416f);
        var innerHex = inner.GetComponent<ResultHexBadgeGraphic>();
        innerHex.flatFill = true;
        innerHex.topColor = BadgeInner;
        innerHex.raycastTarget = false;

        // ランク文字(白 + ランク色の二重グロー)
        AddGlow(badgeRoot.transform, "LetterGlowWide", Vector2.zero, new Vector2(680f, 680f),
            new Color(rankColor.r, rankColor.g, rankColor.b, 0.30f));
        AddGlow(badgeRoot.transform, "LetterGlow", Vector2.zero, new Vector2(360f, 360f),
            new Color(rankColor.r, rankColor.g, rankColor.b, 0.75f));
        UISkinKit.MakeTMP(badgeRoot.transform, "RankLetter", PlayRankHelper.Label(rank), 250f,
            Color.white, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(400f, 300f), FontStyles.Normal, 0f, chakra);

        if (reveal != null) reveal.Add(badgeRoot, DelayRankBadge, DurRankBadge, ResultReveal.Kind.Slam, Vector2.zero);

        // ACCURACY(ラベル + シアン大数値)
        var accRow = new GameObject("AccuracyRow", typeof(RectTransform));
        accRow.transform.SetParent(parent, false);
        accRow.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, -185f);
        var accLayout = accRow.AddComponent<HorizontalLayoutGroup>();
        accLayout.spacing = 14f;
        accLayout.childAlignment = TextAnchor.MiddleCenter;
        accLayout.childForceExpandWidth = false;
        accLayout.childForceExpandHeight = false;
        var accFitter = accRow.AddComponent<ContentSizeFitter>();
        accFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        accFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        UISkinKit.MakeTMP(accRow.transform, "Label", "ACCURACY", 25f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(10f, 32f), FontStyles.Normal, 4f,
            UISkinKit.FontAsset("Oxanium-Bold"));
        UISkinKit.MakeTMP(accRow.transform, "Value", FormatAccuracy(accuracy), 50f,
            UISkinPalette.Cyan, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(10f, 58f), FontStyles.Normal, 0f, chakra);
        Reveal(accRow, DelayAccuracy, 0.5f, FromBelow);
    }

    // ---- SCORE ブロック(左カラム) ----

    void BuildScoreBlock(Transform parent)
    {
        var chakra = ChakraFont();
        var oxBold = UISkinKit.FontAsset("Oxanium-Bold");

        var block = new GameObject("ScoreBlock", typeof(RectTransform));
        block.transform.SetParent(parent, false);
        block.GetComponent<RectTransform>().sizeDelta = Vector2.zero;

        // SCORE ラベル(シアン + グロー)。列間が広すぎて見切れたため中央へ寄せている(元: left 120px)。
        AddGlow(block.transform, "LabelGlow", new Vector2(-675f, 185f), new Vector2(220f, 70f),
            new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.30f));
        UISkinKit.MakeTMP(block.transform, "ScoreLabel", "SCORE", 25f,
            UISkinPalette.Cyan, TextAlignmentOptions.MidlineLeft,
            new Vector2(-540f, 185f), new Vector2(430f, 32f), FontStyles.Normal, 6f, oxBold);

        // スコア数字(白→シアン→深シアンの縦グラデ近似 + グロー)
        AddGlow(block.transform, "ScoreGlow", new Vector2(-595f, 110f), new Vector2(520f, 170f),
            new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.25f));
        var scoreValue = UISkinKit.MakeTMP(block.transform, "ScoreValue", GameSession.FinalScore.ToString("N0"), 104f,
            Color.white, TextAlignmentOptions.MidlineLeft,
            new Vector2(-540f, 110f), new Vector2(430f, 122f), FontStyles.Normal, 1f, chakra);
        scoreValue.enableVertexGradient = true;
        scoreValue.colorGradient = new VertexGradient(Color.white, Color.white, DeepCyan, DeepCyan);

        // MAX COMBO(数値だけ 1.36s に遅れて出る)
        UISkinKit.MakeTMP(block.transform, "MaxComboLabel", "MAX COMBO", 23f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.MidlineLeft,
            new Vector2(-660f, -3f), new Vector2(190f, 30f), FontStyles.Normal, 2f, oxBold);
        var comboValue = UISkinKit.MakeTMP(block.transform, "MaxComboValue", GameSession.FinalMaxCombo.ToString("N0"), 54f,
            UISkinPalette.OffWhite, TextAlignmentOptions.MidlineLeft,
            new Vector2(-416f, -1f), new Vector2(300f, 62f), FontStyles.Normal, 0f, chakra);
        Reveal(comboValue.gameObject, DelayMaxCombo, 0.5f, FromLeft);

        // HI-SCORE(記録前の従来ベスト。今回それを超えたら NEW RECORD!)
        BuildHiScoreRow(block.transform, chakra, oxBold);

        Reveal(block, DelayScoreBlock, 0.5f, FromLeft);
    }

    void BuildHiScoreRow(Transform parent, TMP_FontAsset chakra, TMP_FontAsset oxBold)
    {
        string songId = GameSession.SelectedSongId;
        if (string.IsNullOrEmpty(songId)) return; // シーン直起動などは行ごと省略

        var table = HighScoreStore.Load(songId, GameSession.SelectedDifficulty);
        int prevBest = table.entries.Count > 0 ? table.entries[0].score : 0;
        bool newRecord = GameSession.FinalScore > prevBest && GameSession.FinalScore > 0;
        int shownScore = prevBest > 0 ? prevBest : GameSession.FinalScore;

        // 今回の結果を記録(prevBest を読んだ後に保存)
        float accuracy = PlayRankHelper.Accuracy(
            GameSession.FinalPerfect, GameSession.FinalGreat, GameSession.FinalGood,
            GameSession.FinalBad, GameSession.FinalMiss);
        HighScoreStore.Record(songId, GameSession.SelectedDifficulty, new HighScoreEntry
        {
            score = GameSession.FinalScore,
            rank = PlayRankHelper.Label(PlayRankHelper.FromAccuracy(accuracy)),
            accuracy = accuracy,
            date = System.DateTime.Now.ToString("yyyy/MM/dd"),
        }, out _);

        var row = new GameObject("HiScoreRow", typeof(RectTransform));
        row.transform.SetParent(parent, false);
        var rt = row.GetComponent<RectTransform>();
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = new Vector2(-755f, -66f);
        var layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 14f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var fitter = row.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        UISkinKit.MakeTMP(row.transform, "Label", "HI-SCORE", 23f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.MidlineLeft,
            Vector2.zero, new Vector2(10f, 30f), FontStyles.Normal, 2f, oxBold);
        UISkinKit.MakeTMP(row.transform, "Value", shownScore.ToString("N0"), 38f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.MidlineLeft,
            Vector2.zero, new Vector2(10f, 46f), FontStyles.Normal, 0f, chakra);
        if (newRecord)
        {
            UISkinKit.MakeTMP(row.transform, "NewRecord", "NEW RECORD!", 28f,
                UISkinPalette.Magenta, TextAlignmentOptions.MidlineLeft,
                Vector2.zero, new Vector2(10f, 36f), FontStyles.Normal, 0f, chakra);
        }
    }

    // ---- 判定内訳(右カラム 5 行) ----

    void BuildJudgeList(Transform parent)
    {
        var chakra = ChakraFont();
        string[] labels = { "PERFECT", "GREAT", "GOOD", "BAD", "MISS" };
        int[] counts =
        {
            GameSession.FinalPerfect, GameSession.FinalGreat, GameSession.FinalGood,
            GameSession.FinalBad, GameSession.FinalMiss,
        };
        Color[] colors =
        {
            UISkinPalette.Cyan, UISkinPalette.Yellow, UISkinPalette.Orange,
            UISkinPalette.Magenta, UISkinPalette.SubtleGray,
        };

        for (int i = 0; i < labels.Length; i++)
        {
            var row = new GameObject("Judge" + labels[i], typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var rt = row.GetComponent<RectTransform>();
            rt.anchoredPosition = new Vector2(560f, 186f - 77f * i);
            rt.sizeDelta = new Vector2(400f, 58f);

            UISkinKit.MakeTMP(row.transform, "Label", labels[i], 40f,
                colors[i], TextAlignmentOptions.MidlineLeft,
                new Vector2(-60f, 0f), new Vector2(280f, 56f), FontStyles.Normal, 2f, chakra);
            UISkinKit.MakeTMP(row.transform, "Value", counts[i].ToString("N0"), 42f,
                i == labels.Length - 1 ? UISkinPalette.SubtleGray : UISkinPalette.OffWhite,
                TextAlignmentOptions.MidlineRight,
                new Vector2(60f, 0f), new Vector2(280f, 56f), FontStyles.Normal, 0f, chakra);

            if (i < labels.Length - 1)
            {
                var line = new GameObject("Border", typeof(RectTransform), typeof(Image));
                line.transform.SetParent(row.transform, false);
                var lrt = line.GetComponent<RectTransform>();
                lrt.anchoredPosition = new Vector2(0f, -28f);
                lrt.sizeDelta = new Vector2(400f, 1f);
                var img = line.GetComponent<Image>();
                img.color = RowLine;
                img.raycastTarget = false;
            }
            Reveal(row, DelayRowsStart + DelayRowsStep * i, 0.45f, FromRight);
        }
    }

    // ---- 判定分布バー(下部) ----

    void BuildDistributionBar(Transform parent)
    {
        int[] counts =
        {
            GameSession.FinalPerfect, GameSession.FinalGreat, GameSession.FinalGood,
            GameSession.FinalBad, GameSession.FinalMiss,
        };
        Color[] colors =
        {
            UISkinPalette.Cyan, UISkinPalette.Yellow, UISkinPalette.Orange,
            UISkinPalette.Magenta, UISkinPalette.SubtleGray,
        };
        int total = 0;
        foreach (int c in counts) total += Mathf.Max(0, c);

        var block = new GameObject("Distribution", typeof(RectTransform));
        block.transform.SetParent(parent, false);
        block.GetComponent<RectTransform>().sizeDelta = Vector2.zero;

        const float barWidth = 1520f;
        const float barY = -345f;
        float[] widths = DistributionWidths(counts, barWidth);
        float cursor = -barWidth * 0.5f;
        for (int i = 0; i < widths.Length; i++)
        {
            if (widths[i] <= 0.01f) { continue; }
            if (i == 0)
            {
                // PERFECT セグメントだけシアンのグロー(box-shadow 相当)
                AddGlow(block.transform, "PerfectGlow",
                    new Vector2(cursor + widths[i] * 0.5f, barY), new Vector2(widths[i] + 60f, 70f),
                    new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.30f));
            }
            var seg = new GameObject("Seg" + i, typeof(RectTransform), typeof(Image));
            seg.transform.SetParent(block.transform, false);
            var rt = seg.GetComponent<RectTransform>();
            rt.anchoredPosition = new Vector2(cursor + widths[i] * 0.5f, barY);
            rt.sizeDelta = new Vector2(widths[i], 18f);
            var img = seg.GetComponent<Image>();
            img.color = colors[i];
            img.raycastTarget = false;
            cursor += widths[i];
        }

        var oxBold = UISkinKit.FontAsset("Oxanium-Bold");
        UISkinKit.MakeTMP(block.transform, "NotesLabel", total.ToString("N0") + " NOTES", 21f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.MidlineLeft,
            new Vector2(-560f, -377f), new Vector2(400f, 26f), FontStyles.Normal, 1f, oxBold);
        float perfectRate = total > 0 ? counts[0] * 100f / total : 0f;
        UISkinKit.MakeTMP(block.transform, "PerfectRateLabel",
            "PERFECT RATE " + perfectRate.ToString("F1") + "%", 21f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.MidlineRight,
            new Vector2(560f, -377f), new Vector2(400f, 26f), FontStyles.Normal, 1f, oxBold);

        Reveal(block, DelayDistribution, 0.5f, FromBelow);
    }

    // ---- BACK ボタン / スキップ案内 ----

    void StyleBackButton(Canvas canvas)
    {
        foreach (var btn in canvas.GetComponentsInChildren<Button>())
        {
            var label = btn.GetComponentInChildren<Text>();
            if (label != null && label.text == "BACK")
            {
                var rt = btn.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(0f, -468f);
                rt.sizeDelta = new Vector2(280f, 58f);
                var parts = UISkinKit.RestyleButton(btn, UISkinPalette.Cyan, 27f, "◀ BACK");
                if (parts.label != null) parts.label.characterSpacing = 4f;
                Reveal(btn.gameObject, DelayBackButton, 0.45f, FromBelow);
            }
        }
    }

    void BuildSkipHint(Transform parent)
    {
        var hint = UISkinKit.MakeTMP(parent, "SkipHint", "CLICK / ANY KEY TO SKIP", 18f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.MidlineRight,
            new Vector2(560f, -473f), new Vector2(400f, 24f), FontStyles.Normal, 2f,
            UISkinKit.FontAsset("Oxanium-Bold"));
        Reveal(hint.gameObject, DelaySkipHint, 0.45f, Vector2.zero); // フェードのみ
    }

    // ---- 小物 ----

    // 数字・判定・ランク用の Chakra Petch Bold Italic(ロゴと共用)。無い環境は Oxanium で代替。
    static TMP_FontAsset ChakraFont()
    {
        var chakra = UISkinKit.LogoFontAsset();
        if (chakra != null) return chakra;
        return UISkinKit.FontAsset("Oxanium-ExtraBold");
    }

    static Image AddGlow(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        var img = go.GetComponent<Image>();
        img.sprite = UISkinKit.SoftGlow();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    void Reveal(GameObject go, float delay, float duration, Vector2 from)
    {
        if (reveal != null) reveal.Add(go, delay, duration, ResultReveal.Kind.Slide, from);
    }
}
