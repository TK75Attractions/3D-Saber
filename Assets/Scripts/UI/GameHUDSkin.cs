using UnityEngine;
using UnityEngine.UI;
using TMPro;

// プレイ画面の HUD を「Neon Focus」テーマで実行時に作り直す。
// 方針:プレイエリア(画面中央)から情報を退避し、フォントをメニューと同じ Chakra Petch に統一する。
//   ・スコア   … 左上(ラベル+数値)
//   ・コンボ   … 右上(数値+ラベル、増加時パンチ、コンボ数で色が育つ)
//   ・判定演出 … 中央のやや下(ノーツの軌道に被らない位置で浮かび上がって消える)
//   ・曲名/難易度 … 上部中央(曲名は日本語対応のため legacy Text)
//   ・進行バー … 画面最上端の細いライン
// 旧 ScoreHUD(legacy Text ベース)は Ensure 時に無効化する。シーンは書き換えない(非破壊)。
public class GameHUDSkin : MonoBehaviour
{
    [Header("Tier popup")]
    public float tierFlashDuration = 0.6f;
    public float tierRiseOffsetY = 70f;
    public float tierPunchScale = 1.3f;

    [Header("Combo punch")]
    public float comboPunchScale = 1.25f;
    public float comboPunchDuration = 0.18f;

    // 判定 tier ごとの色(ScoreHUD と同じ配色を踏襲)
    public static Color TierColor(JudgmentTier t)
    {
        switch (t)
        {
            case JudgmentTier.Perfect: return new Color(0.27f, 1f, 0.97f);
            case JudgmentTier.Great: return new Color(1f, 0.92f, 0.35f);
            case JudgmentTier.Good: return new Color(1f, 0.60f, 0.25f);
            case JudgmentTier.Bad: return new Color(1f, 0.30f, 0.55f);
            default: return new Color(0.55f, 0.60f, 0.75f);
        }
    }

    // コンボ数に応じて育つ色(ScoreHUD.ColorByComboTier と同じ階調)
    public static Color ComboColor(int combo)
    {
        if (combo >= 100) return new Color(1f, 0.30f, 0.85f);   // マゼンタ
        if (combo >= 60) return new Color(1f, 0.55f, 0.25f);    // オレンジ
        if (combo >= 30) return new Color(1f, 0.92f, 0.35f);    // 黄
        if (combo >= 10) return new Color(0.27f, 1f, 0.97f);    // シアン
        return new Color(0.91f, 0.93f, 1f);                     // オフホワイト
    }

    // 難易度の色(SongSelect 系と揃えたロゴ 3 色)
    public static Color DifficultyColor(string difficulty)
    {
        string d = string.IsNullOrEmpty(difficulty) ? "" : difficulty.ToLowerInvariant();
        if (d == "easy") return UISkinPalette.LogoGreen;
        if (d == "hard") return UISkinPalette.LogoRed;
        return UISkinPalette.LogoBlue; // normal / 不明
    }

    // 曲進行 0..1。duration が無効なら 0。テストから直接叩く純関数。
    public static float Progress01(double songTime, double duration)
    {
        if (duration <= 0.0) return 0f;
        return Mathf.Clamp01((float)(songTime / duration));
    }

    // 判定の時間ずれ表示(タイミング学習フィードバック)。負=早い(EARLY)、正=遅い(LATE)。
    // Perfect(調整不要)と Miss/ロング(誤差の概念が無い)では出さない。テストから直接叩く純関数。
    public static string FormatTimingHint(JudgmentTier tier, bool errorValid, double errorMs)
    {
        if (!errorValid) return "";
        if (tier == JudgmentTier.Perfect || tier == JudgmentTier.Miss) return "";
        string word = errorMs < 0 ? "EARLY" : "LATE";
        return $"{word} {Mathf.Abs((float)errorMs):F0}ms";
    }

    // EARLY は青系(急ぎすぎ=クールダウン)、LATE はオレンジ系(遅れ=加速)の直感マッピング
    public static Color TimingHintColor(double errorMs)
    {
        return errorMs < 0 ? new Color(0.45f, 0.75f, 1f) : new Color(1f, 0.62f, 0.30f);
    }

    public bool IsBuilt { get; private set; }

    private ScoreManager score;
    private SongPlayer songPlayer;
    private NoteSpawner noteSpawner;

    private TextMeshProUGUI scoreValue;
    private TextMeshProUGUI comboValue;
    private TextMeshProUGUI comboLabel;
    private TextMeshProUGUI tierText;
    private TextMeshProUGUI timingHintText;
    private TextMeshProUGUI flickWarningText;
    private TextMeshProUGUI difficultyText;
    private Text songTitleText;
    private RectTransform progressFill;

    private RectTransform tierRT;
    private Vector2 tierBasePos;
    private float tierFlashAge = 999f;
    private bool currentWasFlickFail;

    private RectTransform comboRT;
    private int lastCombo;
    private float comboPunchAge = 999f;

    // ランク表示(左上・スコアの下)。バッジはリザルト画面と同じ六角形のコード描画(画像アセット不使用)。
    private ResultHexBadgeGraphic rankHexOuter;
    private Image rankHexGlow;
    private TextMeshProUGUI rankBadgeText; // 六角バッジ中央のランク文字
    private RectTransform rankFill;
    private Image rankFillImg;
    private TextMeshProUGUI rankNextLabel;
    private PlayRank currentRank = PlayRank.SPlus;
    private bool rankVisualInit;

    private Font jpFont;
    private bool fontResolved;

    // HUD を(無ければ)生成して返す。冪等。
    public static GameHUDSkin Ensure()
    {
        var existing = Object.FindFirstObjectByType<GameHUDSkin>();
        if (existing != null) return existing;
        var go = new GameObject("GameHUDSkin");
        var skin = go.AddComponent<GameHUDSkin>();
        skin.Build();
        return skin;
    }

    void OnDestroy()
    {
        if (score != null) score.OnJudgmentEx -= OnJudgmentEx;
    }

    // ---------------------------------------------------------------
    // 構築
    // ---------------------------------------------------------------
    public void Build()
    {
        if (IsBuilt) return;
        IsBuilt = true;

        score = Object.FindFirstObjectByType<ScoreManager>();
        songPlayer = Object.FindFirstObjectByType<SongPlayer>();
        noteSpawner = Object.FindFirstObjectByType<NoteSpawner>();
        if (score != null) score.OnJudgmentEx += OnJudgmentEx;

        DisableLegacyHud();

        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        var font = UISkinKit.LogoFontAsset();
        // 数字(スコア/コンボ)は Rajdhani。無ければ Chakra Petch のまま。
        var numberFont = UISkinKit.FontAsset("Rajdhani-Bold");
        if (numberFont == null) numberFont = font;

        // --- スコア(左上) ---
        var scoreLabel = UISkinKit.MakeTMP(transform, "ScoreLabel", "SCORE", 22f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(300f, 30f), FontStyles.Normal, 6f, font);
        AnchorTopLeft(scoreLabel.rectTransform, new Vector2(36f, -26f));

        scoreValue = UISkinKit.MakeTMP(transform, "ScoreValue", "0", 52f,
            UISkinPalette.OffWhite, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(480f, 64f), FontStyles.Normal, 2f, numberFont);
        AnchorTopLeft(scoreValue.rectTransform, new Vector2(36f, -52f));

        // --- ランク(左上・スコアの下) ---
        BuildRankWidget(font);

        // --- コンボ(右上) ---
        comboValue = UISkinKit.MakeTMP(transform, "ComboValue", "", 84f,
            UISkinPalette.OffWhite, TextAlignmentOptions.TopRight,
            Vector2.zero, new Vector2(400f, 96f), FontStyles.Normal, 1f, numberFont);
        AnchorTopRight(comboValue.rectTransform, new Vector2(-36f, -26f));
        comboRT = comboValue.rectTransform;

        comboLabel = UISkinKit.MakeTMP(transform, "ComboLabel", "", 22f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.TopRight,
            Vector2.zero, new Vector2(300f, 30f), FontStyles.Normal, 6f, font);
        AnchorTopRight(comboLabel.rectTransform, new Vector2(-38f, -112f));

        // --- 判定演出(中央やや下:ノーツ軌道の外) ---
        tierText = UISkinKit.MakeTMP(transform, "TierText", "", 64f,
            Color.white, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(700f, 84f), FontStyles.Normal, 4f, font);
        AnchorCenter(tierText.rectTransform, new Vector2(0f, -330f));
        tierRT = tierText.rectTransform;
        tierBasePos = tierRT.anchoredPosition;

        timingHintText = UISkinKit.MakeTMP(transform, "TimingHint", "", 28f,
            Color.white, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(700f, 36f), FontStyles.Normal, 4f, font);
        AnchorCenter(timingHintText.rectTransform, new Vector2(0f, -386f));

        flickWarningText = UISkinKit.MakeTMP(transform, "FlickWarning", "", 26f,
            new Color(1f, 0.70f, 0.20f), TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(700f, 34f), FontStyles.Normal, 3f, font);
        AnchorCenter(flickWarningText.rectTransform, new Vector2(0f, -428f));

        // --- 曲名+難易度(上部中央) ---
        BuildSongHeader(font);

        // --- 進行バー(画面最上端) ---
        BuildProgressBar();

        RefreshStaticTexts();
    }

    // 旧 ScoreHUD と、その参照する legacy Text 群を無効化する(非破壊:停止すれば元のまま)。
    private void DisableLegacyHud()
    {
        var legacy = Object.FindFirstObjectByType<ScoreHUD>();
        if (legacy == null) return;
        if (legacy.scoreText != null) legacy.scoreText.gameObject.SetActive(false);
        if (legacy.comboText != null) legacy.comboText.gameObject.SetActive(false);
        if (legacy.tierText != null) legacy.tierText.gameObject.SetActive(false);
        if (legacy.flickWarningText != null) legacy.flickWarningText.gameObject.SetActive(false);
        legacy.enabled = false;
    }

    private void BuildSongHeader(TMP_FontAsset font)
    {
        string title = GameSession.SelectedSongTitle;
        if (string.IsNullOrEmpty(title)) title = GameSession.SelectedSongId;
        string difficulty = GameSession.SelectedDifficulty;

        // 曲名:日本語を含み得るので legacy Text + OS フォント
        var titleGo = new GameObject("SongTitle", typeof(RectTransform), typeof(Text));
        titleGo.transform.SetParent(transform, false);
        songTitleText = titleGo.GetComponent<Text>();
        songTitleText.font = JpFont();
        songTitleText.text = title ?? "";
        songTitleText.fontSize = 26;
        songTitleText.fontStyle = FontStyle.Bold;
        songTitleText.alignment = TextAnchor.UpperCenter;
        songTitleText.color = new Color(0.85f, 0.89f, 1f, 0.9f);
        songTitleText.horizontalOverflow = HorizontalWrapMode.Overflow;
        songTitleText.verticalOverflow = VerticalWrapMode.Overflow;
        songTitleText.raycastTarget = false;
        var trt = titleGo.GetComponent<RectTransform>();
        trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f);
        trt.pivot = new Vector2(0.5f, 1f);
        trt.anchoredPosition = new Vector2(0f, -22f);
        trt.sizeDelta = new Vector2(900f, 34f);

        // 難易度:ASCII なので Chakra Petch。色で難度が分かる。
        if (!string.IsNullOrEmpty(difficulty))
        {
            difficultyText = UISkinKit.MakeTMP(transform, "Difficulty", difficulty.ToUpperInvariant(), 20f,
                DifficultyColor(difficulty), TextAlignmentOptions.Top,
                Vector2.zero, new Vector2(300f, 26f), FontStyles.Normal, 8f, font);
            var drt = difficultyText.rectTransform;
            drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 1f);
            drt.pivot = new Vector2(0.5f, 1f);
            drt.anchoredPosition = new Vector2(0f, -56f);
        }
    }

    // 左上・スコアの下に「現在ランクのバッジ + 次ランクへの進捗バー」を作る。
    // バッジはリザルト画面と同じ六角形(ResultHexBadgeGraphic)+白のランク文字。
    private void BuildRankWidget(TMP_FontAsset labelFont)
    {
        var rankLabel = UISkinKit.MakeTMP(transform, "RankLabel", "RANK", 22f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(300f, 30f), FontStyles.Normal, 6f, labelFont);
        AnchorTopLeft(rankLabel.rectTransform, new Vector2(36f, -124f));

        var badgeGo = new GameObject("RankBadge", typeof(RectTransform));
        badgeGo.transform.SetParent(transform, false);
        var badgeRT = badgeGo.GetComponent<RectTransform>();
        AnchorTopLeft(badgeRT, new Vector2(36f, -150f));
        badgeRT.sizeDelta = new Vector2(64f, 70f); // リザルトの 400×440 と同比率

        var glowGo = new GameObject("Glow", typeof(RectTransform), typeof(Image));
        glowGo.transform.SetParent(badgeGo.transform, false);
        glowGo.GetComponent<RectTransform>().sizeDelta = new Vector2(112f, 118f);
        rankHexGlow = glowGo.GetComponent<Image>();
        rankHexGlow.sprite = UISkinKit.SoftGlow();
        rankHexGlow.raycastTarget = false;

        var outerGo = new GameObject("HexOuter", typeof(RectTransform), typeof(ResultHexBadgeGraphic));
        outerGo.transform.SetParent(badgeGo.transform, false);
        outerGo.GetComponent<RectTransform>().sizeDelta = new Vector2(64f, 70f);
        rankHexOuter = outerGo.GetComponent<ResultHexBadgeGraphic>();
        rankHexOuter.raycastTarget = false;

        var innerGo = new GameObject("HexInner", typeof(RectTransform), typeof(ResultHexBadgeGraphic));
        innerGo.transform.SetParent(badgeGo.transform, false);
        innerGo.GetComponent<RectTransform>().sizeDelta = new Vector2(58f, 64f);
        var innerHex = innerGo.GetComponent<ResultHexBadgeGraphic>();
        innerHex.flatFill = true;
        innerHex.topColor = new Color(0.043f, 0.055f, 0.141f); // #0B0E24(リザルトと同じ)
        innerHex.raycastTarget = false;

        var chakra = UISkinKit.LogoFontAsset();
        if (chakra == null) chakra = UISkinKit.FontAsset("Oxanium-ExtraBold");
        rankBadgeText = UISkinKit.MakeTMP(badgeGo.transform, "RankLetter", "", 34f,
            Color.white, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(64f, 48f), FontStyles.Normal, 0f, chakra);

        var barBg = new GameObject("RankProgressBg", typeof(RectTransform), typeof(Image));
        barBg.transform.SetParent(transform, false);
        var barRT = barBg.GetComponent<RectTransform>();
        AnchorTopLeft(barRT, new Vector2(114f, -172f));
        barRT.sizeDelta = new Vector2(150f, 6f);
        var barImg = barBg.GetComponent<Image>();
        barImg.color = new Color(1f, 1f, 1f, 0.10f);
        barImg.raycastTarget = false;

        var fillGo = new GameObject("RankProgressFill", typeof(RectTransform), typeof(Image));
        fillGo.transform.SetParent(barBg.transform, false);
        rankFill = fillGo.GetComponent<RectTransform>();
        rankFill.anchorMin = new Vector2(0f, 0f);
        rankFill.anchorMax = new Vector2(0f, 1f); // x は UpdateRank で伸ばす
        rankFill.offsetMin = rankFill.offsetMax = Vector2.zero;
        rankFill.pivot = new Vector2(0f, 0.5f);
        rankFillImg = fillGo.GetComponent<Image>();
        rankFillImg.raycastTarget = false;

        rankNextLabel = UISkinKit.MakeTMP(transform, "RankNextLabel", "", 16f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(200f, 22f), FontStyles.Normal, 2f, labelFont);
        AnchorTopLeft(rankNextLabel.rectTransform, new Vector2(114f, -184f));
    }

    private void BuildProgressBar()
    {
        var bg = new GameObject("ProgressBg", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(transform, false);
        var brt = bg.GetComponent<RectTransform>();
        brt.anchorMin = new Vector2(0f, 1f);
        brt.anchorMax = new Vector2(1f, 1f);
        brt.pivot = new Vector2(0.5f, 1f);
        brt.anchoredPosition = Vector2.zero;
        brt.sizeDelta = new Vector2(0f, 5f);
        var bgImg = bg.GetComponent<Image>();
        bgImg.color = new Color(1f, 1f, 1f, 0.08f);
        bgImg.raycastTarget = false;

        var fill = new GameObject("ProgressFill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(bg.transform, false);
        progressFill = fill.GetComponent<RectTransform>();
        progressFill.anchorMin = new Vector2(0f, 0f);
        progressFill.anchorMax = new Vector2(0f, 1f); // x は Update で伸ばす
        progressFill.offsetMin = Vector2.zero;
        progressFill.offsetMax = Vector2.zero;
        progressFill.pivot = new Vector2(0f, 0.5f);
        var fillImg = fill.GetComponent<Image>();
        fillImg.color = UISkinPalette.Cyan;
        fillImg.raycastTarget = false;
    }

    private void RefreshStaticTexts()
    {
        if (scoreValue != null && score != null) scoreValue.text = score.Score.ToString("N0");
    }

    // ---------------------------------------------------------------
    // 更新
    // ---------------------------------------------------------------
    void Update()
    {
        if (!IsBuilt) return;
        if (score != null)
        {
            if (scoreValue != null) scoreValue.text = score.Score.ToString("N0");
            UpdateCombo();
        }
        UpdateTierAnimation();
        UpdateProgress();
        UpdateRank();
    }

    // 現在の判定カウントからランクを計算して左上の表示を更新する。
    // 曲中は「曲全体に対する合計割合」(分母=総ノーツ数固定)なので 0 から始まり
    // ヒットするほど単調に上がっていく。曲終了時は従来の精度と同じ値に収束する。
    // 総ノーツ数が取れない環境(テスト等)では従来のその時点精度にフォールバック。
    // バッジの色と文字の更新はランクが変わった時だけ。
    private void UpdateRank()
    {
        if (score == null) return;
        int totalNotes = noteSpawner != null ? noteSpawner.TotalNoteCount : 0;
        float acc = totalNotes > 0
            ? PlayRankHelper.TotalAccuracy(
                score.PerfectCount, score.GreatCount, score.GoodCount, score.BadCount, totalNotes)
            : PlayRankHelper.Accuracy(
                score.PerfectCount, score.GreatCount, score.GoodCount, score.BadCount, score.MissCount);
        PlayRank rank = PlayRankHelper.FromAccuracy(acc);
        // 色はリザルト画面の六角バッジと同じマップに統一する
        Color c = ResultSkin.RankAccentColor(PlayRankHelper.Label(rank));

        if (!rankVisualInit || rank != currentRank)
        {
            rankVisualInit = true;
            currentRank = rank;
            if (rankHexOuter != null)
            {
                rankHexOuter.topColor = c;
                rankHexOuter.SetVerticesDirty();
            }
            if (rankHexGlow != null)
            {
                rankHexGlow.color = new Color(c.r, c.g, c.b, 0.35f);
            }
            if (rankBadgeText != null)
            {
                rankBadgeText.text = PlayRankHelper.Label(rank);
            }
        }

        if (rankFill != null)
        {
            rankFill.anchorMax = new Vector2(PlayRankHelper.ProgressToNext(acc), 1f);
            rankFillImg.color = new Color(c.r, c.g, c.b, 0.9f);
        }
        if (rankNextLabel != null)
        {
            rankNextLabel.text = PlayRankHelper.TryNextRank(rank, out PlayRank next)
                ? "NEXT  " + PlayRankHelper.Label(next)
                : "MAX";
        }
    }

    private void OnJudgmentEx(JudgmentTier tier, int awarded, bool wasWrongFlick)
    {
        if (tierText != null)
        {
            tierText.text = JudgmentTierHelper.Label(tier);
            tierText.color = TierColor(tier);
        }
        if (timingHintText != null)
        {
            bool valid = score != null && score.LastErrorValid;
            double errorMs = score != null ? score.LastErrorMs : 0.0;
            timingHintText.text = FormatTimingHint(tier, valid, errorMs);
            timingHintText.color = TimingHintColor(errorMs);
        }
        currentWasFlickFail = wasWrongFlick;
        if (flickWarningText != null)
        {
            flickWarningText.text = wasWrongFlick ? "! FLICK" : "";
        }
        tierFlashAge = 0f;
    }

    private void UpdateTierAnimation()
    {
        if (tierText == null || tierRT == null) return;
        tierFlashAge += Time.deltaTime;
        float t = Mathf.Clamp01(tierFlashAge / tierFlashDuration);
        float ease = 1f - (1f - t) * (1f - t);
        tierRT.anchoredPosition = tierBasePos + new Vector2(0f, tierRiseOffsetY * ease);
        float punch = 1f + (tierPunchScale - 1f) * Mathf.Max(0f, 1f - tierFlashAge / 0.18f);
        tierRT.localScale = Vector3.one * punch;
        float alpha = Mathf.Clamp01(1f - t);
        Color c = tierText.color; c.a = alpha; tierText.color = c;
        if (timingHintText != null)
        {
            Color h = timingHintText.color;
            h.a = alpha;
            timingHintText.color = h;
        }
        if (flickWarningText != null)
        {
            Color w = flickWarningText.color;
            w.a = currentWasFlickFail ? alpha : 0f;
            flickWarningText.color = w;
        }
    }

    private void UpdateCombo()
    {
        if (comboValue == null) return;
        int combo = score.Combo;

        if (combo > lastCombo) comboPunchAge = 0f;
        lastCombo = combo;

        if (combo <= 0)
        {
            comboValue.text = "";
            if (comboLabel != null) comboLabel.text = "";
            return;
        }

        comboValue.text = combo.ToString();
        Color c = ComboColor(combo);
        comboValue.color = c;
        if (comboLabel != null)
        {
            comboLabel.text = "COMBO";
            comboLabel.color = new Color(c.r, c.g, c.b, 0.75f);
        }

        comboPunchAge += Time.deltaTime;
        float p = Mathf.Clamp01(comboPunchAge / comboPunchDuration);
        float scale = Mathf.Lerp(comboPunchScale, 1f, p * p * (3f - 2f * p));
        if (comboRT != null) comboRT.localScale = Vector3.one * scale;
    }

    private void UpdateProgress()
    {
        if (progressFill == null || songPlayer == null) return;
        float p = Progress01(songPlayer.SongTime, songPlayer.Duration);
        progressFill.anchorMax = new Vector2(p, 1f);
    }

    // ---------------------------------------------------------------
    // フォント
    // ---------------------------------------------------------------
    private Font JpFont()
    {
        if (fontResolved) return jpFont;
        fontResolved = true;
        jpFont = Font.CreateDynamicFontFromOSFont(
            new[] { "Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic",
                    "Hiragino Sans", "Hiragino Kaku Gothic ProN", "Noto Sans CJK JP", "Arial" }, 26);
        if (jpFont == null) jpFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return jpFont;
    }

    private static void AnchorTopLeft(RectTransform rt, Vector2 pos)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = pos;
    }

    private static void AnchorTopRight(RectTransform rt, Vector2 pos)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = pos;
    }

    private static void AnchorCenter(RectTransform rt, Vector2 pos)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
    }
}
