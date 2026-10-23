using UnityEngine;
using UnityEngine.UI;
using TMPro;

// プレイ画面の HUD をデザインハンドオフ(プレイ画面版)準拠で実行時に作り直す。
// 数字は Chakra Petch Bold Italic + 縦グラデ、ラベルは Oxanium(リザルト画面と同じデザイン言語)。
//   ・スコア   … 左上(ラベル+ゼロ埋めグラデ数字+六角ランクバッジ+次ランク進捗バー)
//   ・コンボ   … 右上(数値+COMBO。増加時パンチ、コンボ数で色とサイズが育つ)
//   ・判定演出 … 中央のやや下(ノーツの軌道に被らない位置で浮かび上がって消える)
//   ・曲名/難易度 … 上部中央(英タイトル+和名(legacy Text)+難易度チップ)
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

    // 最大コンボと同じ段階の金色で、連続成功の成長を示す。
    public static Color ComboColor(int combo) => combo >= 300 ? SongSelectSkin.Gold : ComboBonusPresentation.Accent(combo);

    // コンボ数に応じて数字が育つ(90px → 128px、150 コンボで最大)。テストから直接叩く純関数。
    public static float ComboFontSize(int combo)
    {
        return Mathf.Lerp(90f, 128f, Mathf.Clamp01(combo / 150f));
    }

    // 難易度の色(SongSelect 系と揃えたロゴ 3 色)
    public static Color DifficultyColor(string difficulty)
    {
        string d = string.IsNullOrEmpty(difficulty) ? "" : difficulty.ToLowerInvariant();
        if (d == "e") return RushESecretState.Purple;
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

    // 鳴らす範囲(start〜end、音源の頭からの秒)のうち、どこまで進んだか。頭を飛ばす曲・鳴り終わりを持つ曲も 0→1 で進む(仕様面の点検 7)。
    public static float Progress01(double songTime, double start, double end) => Progress01(songTime - start, end - start);

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

    private TextMeshProUGUI scoreValue;
    private TextMeshProUGUI comboValue;
    private TextMeshProUGUI comboLabel;
    private TextMeshProUGUI maxComboValue;
    private int lastMaxCombo;
    private float maxComboPunchAge = 999f;
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
    private Image comboGlow;
    private int lastCombo;
    private float comboPunchAge = 999f;
    private TextMeshProUGUI comboBreakValue;
    private RectTransform comboBreakRT;
    private Vector2 comboBreakOrigin;
    private Vector3 comboBreakScale;
    private VertexGradient comboBreakGradient;
    private float comboBreakAge;
    private bool comboBreakActive, comboBreakReduced;

    public const float ComboBreakSeconds = .28f, ReducedComboBreakSeconds = .2f;

    public static bool ShouldBreakCombo(int previous, int current) => previous >= 10 && current == 0;

    // 通常は下へ落として縮め、灰色へ寄せて消す。控えめでは薄れるだけ。純関数。
    public static void ComboBreakFrame(float age, bool reduced,
        out float y, out float scale, out float gray, out float alpha)
    {
        float u = Mathf.Clamp01(age / (reduced ? ReducedComboBreakSeconds : ComboBreakSeconds));
        y = reduced ? 0f : -60f * u * u;
        scale = reduced ? 1f : Mathf.Lerp(1f, .82f, u);
        gray = reduced ? 0f : Mathf.Clamp01(age / .1f);
        alpha = 1f - u;
    }

    // スコア/コンボ数字の縦グラデ下端色(#1899B3、リザルトと同じ)
    private static readonly Color ScoreGradientBottom = new Color(0.094f, 0.60f, 0.702f);

    // ランク表示(左上・スコアの下)。バッジはリザルト画面と同じ六角形のコード描画(画像アセット不使用)。
    private ResultHexBadgeGraphic rankHexOuter;
    private Image rankHexGlow;
    private TextMeshProUGUI rankBadgeText; // 六角バッジ中央のランク文字
    private RectTransform rankFill;
    private Image rankFillImg;
    private TextMeshProUGUI rankNextLabel;
    private PlayRank currentRank = PlayRank.SPlus;
    private bool rankVisualInit;

    // 派手さカタログ(2026-10-20)の HUD の動き。
    //   波1: コンボの節目(30・50・100・以後50ごと)で数字を 1.7 倍に弾ませて 0.3 秒で戻し、0.6 秒だけ金に光らせる。
    //   波3: スコアは 0.25 秒で転がして変え、ランクが上がったらバッジを判子のように着地させて「ガシャン」。下がるときは静かなまま。
    //   斬2 の金の「+点」は同日ユーザーの指定でやめた(金のときだけ数字が出るのはそろわない)。どのノーツでも点の数字は出さず、
    //   金は音(GoldNoteSfx の「キィン」と「チャリン」)と光で特別にする。
    public const float MilestonePunchScale = 1.7f;
    public const float MilestonePunchSeconds = .3f;
    public const float MilestoneGlowSeconds = .6f;
    public const float ScoreRollSeconds = .25f;
    public const float RankStampSeconds = .36f;
    private float milestonePunchAge = 999f;
    private bool milestoneTinted;
    private int rollFrom, rollTo;
    private float rollAge = 999f;
    private RectTransform rankBadgeRT;
    private float rankStampAge = 999f;
    private AudioSource hudAudio;
    private AudioClip rankStampClip;
    public int RankStampCount { get; private set; }
    public bool MilestonePunching => milestonePunchAge < MilestonePunchSeconds;
    public int ShownScore => shownScore;


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
        if (score != null) { score.OnJudgmentEx -= OnJudgmentEx; score.OnLongCut -= OnJudgmentEx; }
        UISkinKit.SafeDestroy(rankStampClip);
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
        // ロングは1回切るごとに判定1つなので、途中の回も判定の文字を出す。
        if (score != null) { score.OnJudgmentEx += OnJudgmentEx; score.OnLongCut += OnJudgmentEx; }

        DisableLegacyHud();

        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        var font = UISkinKit.LogoFontAsset(); // Chakra Petch Bold Italic(数字・勢いのある文字)
        var oxBold = UISkinKit.FontAsset("Oxanium-Bold"); // ラベル類
        if (oxBold == null) oxBold = font;

        // --- スコア(左上、デザイン: top:40 left:70) ---
        var scoreLabel = UISkinKit.MakeTMP(transform, "ScoreLabel", "SCORE", 28f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(300f, 38f), FontStyles.Normal, 4f, oxBold);
        AnchorTopLeft(scoreLabel.rectTransform, new Vector2(70f, -30f));

        var scoreGlow = AddHudGlow(new Vector2(30f, -46f), new Vector2(420f, 110f),
            new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.18f));
        AnchorTopLeft(scoreGlow.rectTransform, new Vector2(30f, -46f));

        scoreValue = UISkinKit.MakeTMP(transform, "ScoreValue", "000,000", 64f,
            Color.white, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(480f, 76f), FontStyles.Normal, 0f, font);
        AnchorTopLeft(scoreValue.rectTransform, new Vector2(70f, -66f));
        scoreValue.enableVertexGradient = true;
        scoreValue.colorGradient = new VertexGradient(
            Color.white, Color.white, ScoreGradientBottom, ScoreGradientBottom);

        if (Application.isPlaying)
        {
            hudAudio = gameObject.AddComponent<AudioSource>();
            hudAudio.playOnAwake = false; hudAudio.spatialBlend = 0;
            rankStampClip = ProceduralSfx.Clip("RankStamp", FlourishSfx.Cached("RankStamp", FlourishSfx.RankStamp));
        }

        // --- ランク(左上・スコアの下) ---
        BuildRankWidget(oxBold);

        // 最大コンボを現在コンボの上へ置き、ミス後も記録を残す。
        maxComboValue = UISkinKit.MakeTMP(transform, "MaxComboValue", "0", 64f,
            Color.white, TextAlignmentOptions.TopRight,
            Vector2.zero, new Vector2(500f, 96f), FontStyles.Normal, 0f, font);
        AnchorTopRight(maxComboValue.rectTransform, new Vector2(-70f, -28f));
        var maxLabel = UISkinKit.MakeTMP(transform, "MaxComboLabel", "MAX COMBO", 28f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.TopRight,
            Vector2.zero, new Vector2(350f, 38f), FontStyles.Normal, 4f, oxBold);
        AnchorTopRight(maxLabel.rectTransform, new Vector2(-72f, -130f));

        // --- コンボ(最大値の下。色とサイズは UpdateCombo で育つ) ---
        comboGlow = AddHudGlow(Vector2.zero, new Vector2(440f, 180f), Color.clear);
        AnchorTopRight(comboGlow.rectTransform, new Vector2(-20f, -144f));

        comboValue = UISkinKit.MakeTMP(transform, "ComboValue", "", 90f,
            Color.white, TextAlignmentOptions.TopRight,
            Vector2.zero, new Vector2(500f, 140f), FontStyles.Normal, 0f, font);
        AnchorTopRight(comboValue.rectTransform, new Vector2(-70f, -40f));
        comboRT = comboValue.rectTransform;
        // パンチは右上を支点に(CSS transform-origin:100% 20% 相当)
        comboRT.pivot = new Vector2(1f, 0.8f);
        comboRT.anchoredPosition = new Vector2(-70f, -174f - 140f * 0.2f);

        // 消える数字は構築時に1つだけ用意し、新しいコンボの表示から独立させる。
        comboBreakValue = Object.Instantiate(comboValue, transform);
        comboBreakValue.name = "ComboBreakValue";
        comboBreakRT = comboBreakValue.rectTransform;
        comboBreakValue.raycastTarget = false;
        comboBreakValue.gameObject.SetActive(false);

        comboLabel = UISkinKit.MakeTMP(transform, "ComboLabel", "", 28f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.TopRight,
            Vector2.zero, new Vector2(300f, 38f), FontStyles.Normal, 6f, oxBold);
        AnchorTopRight(comboLabel.rectTransform, new Vector2(-72f, -330f));

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
        BuildSongHeader(oxBold);

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
        if (legacy.maxComboText != null) legacy.maxComboText.gameObject.SetActive(false);
        if (legacy.comboText != null) legacy.comboText.gameObject.SetActive(false);
        if (legacy.tierText != null) legacy.tierText.gameObject.SetActive(false);
        if (legacy.flickWarningText != null) legacy.flickWarningText.gameObject.SetActive(false);
        legacy.enabled = false;
    }

    private void BuildSongHeader(TMP_FontAsset labelFont)
    {
        string songId = GameSession.SelectedSongId;
        string title = GameSession.SelectedSongTitle;
        if (string.IsNullOrEmpty(title)) title = songId;
        string difficulty = GameSession.SelectedDifficulty;

        // 英タイトル(Oxanium)。songId 由来、無ければ ASCII タイトルをそのまま。
        string en = !string.IsNullOrEmpty(songId) ? ResultSkin.SongIdToDisplayTitle(songId)
                  : (!string.IsNullOrEmpty(title) && UISkinKit.IsAsciiOnly(title)
                      ? title.ToUpperInvariant() : "");
        if (!string.IsNullOrEmpty(en))
        {
            var enTmp = UISkinKit.MakeTMP(transform, "SongTitleEn", en, 34f,
                UISkinPalette.OffWhite, TextAlignmentOptions.Top,
                Vector2.zero, new Vector2(900f, 42f), FontStyles.Normal, 2f,
                UISkinKit.FontAsset("Oxanium-ExtraBold"));
            var ert = enTmp.rectTransform;
            ert.anchorMin = ert.anchorMax = new Vector2(0.5f, 1f);
            ert.pivot = new Vector2(0.5f, 1f);
            ert.anchoredPosition = new Vector2(0f, -36f);
        }

        // 和名(legacy Text も共通の日本語フォント)。英タイトルが無いときは主タイトル扱い。
        var titleGo = new GameObject("SongTitle", typeof(RectTransform), typeof(Text));
        titleGo.transform.SetParent(transform, false);
        songTitleText = titleGo.GetComponent<Text>();
        songTitleText.font = UISkinKit.JapaneseFont();
        songTitleText.text = title ?? "";
        songTitleText.fontSize = string.IsNullOrEmpty(en) ? 32 : 24;
        songTitleText.fontStyle = FontStyle.Bold;
        songTitleText.alignment = TextAnchor.UpperCenter;
        songTitleText.color = UISkinPalette.SubtleGray;
        songTitleText.horizontalOverflow = HorizontalWrapMode.Overflow;
        songTitleText.verticalOverflow = VerticalWrapMode.Overflow;
        songTitleText.raycastTarget = false;
        var trt = titleGo.GetComponent<RectTransform>();
        trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f);
        trt.pivot = new Vector2(0.5f, 1f);
        trt.anchoredPosition = new Vector2(0f, string.IsNullOrEmpty(en) ? -40f : -80f);
        trt.sizeDelta = new Vector2(900f, 36f);
        // 英タイトルと同じ内容しか無い(ASCII のみ、または「揺籠」のように英タイトル行と同一文字列)なら
        // 二重表示を避けて空にする。TMP 側は日本語フォールバック付きで描けるようになった。
        if (!string.IsNullOrEmpty(en) && !string.IsNullOrEmpty(songTitleText.text)
            && (UISkinKit.IsAsciiOnly(songTitleText.text) || songTitleText.text == en))
        {
            songTitleText.text = "";
        }

        // 難易度チップ(枠+色付きラベル。色は難易度マップで情報を保つ)
        if (!string.IsNullOrEmpty(difficulty))
        {
            Color accent = DifficultyColor(difficulty);
            var chip = new GameObject("Difficulty", typeof(RectTransform), typeof(Image));
            chip.transform.SetParent(transform, false);
            var crt = chip.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.anchoredPosition = new Vector2(0f, -122f);
            var frame = chip.GetComponent<Image>();
            frame.sprite = UISkinKit.RoundedFrame();
            frame.type = Image.Type.Sliced;
            frame.color = accent;
            frame.raycastTarget = false;
            var layout = chip.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(14, 14, 3, 3);
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            var fitter = chip.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            difficultyText = UISkinKit.MakeTMP(chip.transform, "Label", difficulty.ToUpperInvariant(), 24f,
                accent, TextAlignmentOptions.Center,
                Vector2.zero, new Vector2(10f, 34f), FontStyles.Normal, 3f, labelFont);
        }
    }

    // 左上・スコアの下に「六角ランクバッジ + (RANK/進捗バー/NEXT)の右列」を作る(デザイン準拠)。
    // バッジはリザルト画面と同じ六角形(ResultHexBadgeGraphic)+白のランク文字。
    private void BuildRankWidget(TMP_FontAsset labelFont)
    {
        var badgeGo = new GameObject("RankBadge", typeof(RectTransform));
        badgeGo.transform.SetParent(transform, false);
        var badgeRT = badgeGo.GetComponent<RectTransform>();
        AnchorTopLeft(badgeRT, new Vector2(70f, -152f));
        badgeRT.sizeDelta = new Vector2(84f, 92f);
        // 判子の着地は中心を支点に拡縮する(位置は左上のまま)。
        badgeRT.pivot = new Vector2(.5f, .5f);
        badgeRT.anchoredPosition = new Vector2(70f + 42f, -152f - 46f);
        rankBadgeRT = badgeRT;

        var glowGo = new GameObject("Glow", typeof(RectTransform), typeof(Image));
        glowGo.transform.SetParent(badgeGo.transform, false);
        glowGo.GetComponent<RectTransform>().sizeDelta = new Vector2(150f, 158f);
        rankHexGlow = glowGo.GetComponent<Image>();
        rankHexGlow.sprite = UISkinKit.SoftGlow();
        rankHexGlow.raycastTarget = false;

        var outerGo = new GameObject("HexOuter", typeof(RectTransform), typeof(ResultHexBadgeGraphic));
        outerGo.transform.SetParent(badgeGo.transform, false);
        outerGo.GetComponent<RectTransform>().sizeDelta = new Vector2(84f, 92f);
        rankHexOuter = outerGo.GetComponent<ResultHexBadgeGraphic>();
        rankHexOuter.raycastTarget = false;

        var innerGo = new GameObject("HexInner", typeof(RectTransform), typeof(ResultHexBadgeGraphic));
        innerGo.transform.SetParent(badgeGo.transform, false);
        innerGo.GetComponent<RectTransform>().sizeDelta = new Vector2(76f, 84f);
        var innerHex = innerGo.GetComponent<ResultHexBadgeGraphic>();
        innerHex.flatFill = true;
        innerHex.topColor = new Color(0.043f, 0.055f, 0.141f); // #0B0E24(リザルトと同じ)
        innerHex.raycastTarget = false;

        var chakra = UISkinKit.LogoFontAsset();
        if (chakra == null) chakra = UISkinKit.FontAsset("Oxanium-ExtraBold");
        rankBadgeText = UISkinKit.MakeTMP(badgeGo.transform, "RankLetter", "", 44f,
            Color.white, TextAlignmentOptions.Center,
            Vector2.zero, new Vector2(84f, 60f), FontStyles.Normal, 0f, chakra);

        // 右列: RANK / 進捗バー(枠付き) / NEXT
        var rankLabel = UISkinKit.MakeTMP(transform, "RankLabel", "RANK", 24f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(240f, 32f), FontStyles.Normal, 3f, labelFont);
        AnchorTopLeft(rankLabel.rectTransform, new Vector2(170f, -152f));

        var barFrame = new GameObject("RankProgressFrame", typeof(RectTransform), typeof(Image));
        barFrame.transform.SetParent(transform, false);
        var frameRT = barFrame.GetComponent<RectTransform>();
        AnchorTopLeft(frameRT, new Vector2(170f, -190f));
        frameRT.sizeDelta = new Vector2(242f, 12f);
        var frameImg = barFrame.GetComponent<Image>();
        frameImg.color = new Color(0.118f, 0.133f, 0.275f); // #1E2246(枠)
        frameImg.raycastTarget = false;

        var barBg = new GameObject("RankProgressBg", typeof(RectTransform), typeof(Image));
        barBg.transform.SetParent(barFrame.transform, false);
        var barRT = barBg.GetComponent<RectTransform>();
        barRT.anchorMin = Vector2.zero;
        barRT.anchorMax = Vector2.one;
        barRT.offsetMin = new Vector2(1f, 1f);
        barRT.offsetMax = new Vector2(-1f, -1f);
        var barImg = barBg.GetComponent<Image>();
        barImg.color = new Color(0.078f, 0.094f, 0.220f); // #141838
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

        rankNextLabel = UISkinKit.MakeTMP(transform, "RankNextLabel", "", 24f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.TopLeft,
            Vector2.zero, new Vector2(300f, 34f), FontStyles.Normal, 3f, labelFont);
        AnchorTopLeft(rankNextLabel.rectTransform, new Vector2(170f, -214f));
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
        brt.sizeDelta = new Vector2(0f, 12f);
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
        if (scoreValue != null && score != null)
        {
            shownScore = rollFrom = rollTo = score.Score;
            scoreValue.text = score.Score.ToString("000,000");
        }
    }

    // 表示中の値。変わったときだけ文字列・TMP メッシュを作り直す（毎フレームの GC とメッシュ再生成を避ける）。
    int shownScore = int.MinValue, shownCombo = int.MinValue, shownMaxCombo = int.MinValue;

    // ---------------------------------------------------------------
    // 更新
    // ---------------------------------------------------------------
    void Update()
    {
        if (!IsBuilt) return;
        TickComboBreak(Time.deltaTime);
        if (score != null)
        {
            UpdateScoreRoll();
            UpdateCombo();
            UpdateMaxCombo();
        }
        UpdateTierAnimation();
        UpdateProgress();
        UpdateRank();
    }

    // 転がしている途中の表示の点。from から to へ、出だしは速く最後はゆっくり。純関数。
    public static int RolledScore(int from, int to, float age)
    {
        if (age >= ScoreRollSeconds || from == to) return to;
        float p = Mathf.Clamp01(age / ScoreRollSeconds);
        float ease = 1f - (1f - p) * (1f - p) * (1f - p);
        return Mathf.RoundToInt(Mathf.Lerp(from, to, ease));
    }

    private void UpdateScoreRoll()
    {
        if (scoreValue == null) return;
        int target = score.Score;
        if (target != rollTo)
        {
            // 減ったとき(やり直し)は転がさずにそのまま合わせる。
            rollFrom = target < rollTo || shownScore == int.MinValue ? target : shownScore;
            rollTo = target;
            rollAge = 0f;
        }
        rollAge += Time.deltaTime;
        int value = RolledScore(rollFrom, rollTo, rollAge);
        if (value != shownScore)
        {
            shownScore = value;
            scoreValue.text = value.ToString("000,000");
        }
    }

    // 点数(コンボ点込み)からランクを計算して左上の表示を更新する。結果画面と同じく点数で決める。
    // 曲中は「今の最大コンボで終わったときの点」(分母=譜面全体)なので 0 から始まり
    // 切るほど単調に上がっていく。曲終了時は結果画面の点と同じ値になる。
    // バッジの色と文字の更新はランクが変わった時だけ。
    private void UpdateRank()
    {
        if (score == null) return;
        int points = score.ScoreWithCombo;
        float acc = PlayRankHelper.ScoreFraction(points);
        PlayRank rank = PlayRankHelper.FromScore(points);
        // 色はリザルト画面の六角バッジと同じマップに統一する
        Color c = ResultSkin.RankAccentColor(PlayRankHelper.Label(rank));

        if (!rankVisualInit || rank != currentRank)
        {
            // 上がったときだけ判子のように着地させて「ガシャン」。最初の表示と下がるときは静かなまま(波3)。
            if (rankVisualInit && rank > currentRank)
            {
                rankStampAge = 0f;
                RankStampCount++;
                if (hudAudio != null && rankStampClip != null) hudAudio.PlayOneShot(ProceduralSfx.Revive(ref rankStampClip), 1f);
            }
            rankVisualInit = true;
            currentRank = rank;
            if (rankHexOuter != null)
            {
                rankHexOuter.topColor = c;
                rankHexOuter.SetVerticesDirty();
            }
            if (rankHexGlow != null)
            {
                rankHexGlow.color = new Color(c.r, c.g, c.b, 0.40f);
            }
            if (rankBadgeText != null)
            {
                rankBadgeText.text = rank == PlayRank.C ? "" : PlayRankHelper.Label(rank);
            }

            bool hasNext = PlayRankHelper.TryNextRank(rank, out PlayRank next);
            Color nextColor = hasNext ? ResultSkin.RankAccentColor(PlayRankHelper.Label(next)) : c;
            if (rankFillImg != null)
            {
                // 現ランク色 → 次ランク色の横グラデ(デザイン準拠)
                rankFillImg.sprite = RankBarSprite(rank, c, nextColor);
                rankFillImg.color = Color.white;
            }
            if (rankNextLabel != null)
            {
                rankNextLabel.text = hasNext
                    ? "NEXT <color=#" + ColorUtility.ToHtmlStringRGB(nextColor) + "><b>"
                      + PlayRankHelper.Label(next) + "</b></color>"
                    : "MAX";
            }
        }

        if (rankFill != null)
        {
            rankFill.anchorMax = new Vector2(PlayRankHelper.ProgressToNext(acc), 1f);
        }
        if (rankBadgeRT != null)
        {
            rankStampAge += Time.deltaTime;
            rankBadgeRT.localScale = Vector3.one * RankStampScale(rankStampAge, DisplaySettings.ReducedEffects);
        }
    }

    // 判子の大きさ: 2.2 倍から 0.18 秒で着地し、残りで小さく弾んで 1 に戻る。LOW は動かさない。純関数。
    public static float RankStampScale(float age, bool reduced)
    {
        if (reduced || age >= RankStampSeconds || age < 0f) return 1f;
        const float land = .18f;
        if (age < land) { float q = age / land; return Mathf.Lerp(2.2f, 1f, q * q); }
        float b = (age - land) / (RankStampSeconds - land);
        return 1f + .12f * Mathf.Sin(b * Mathf.PI) * (1f - b);
    }

    // ランク進捗バー用の横グラデスプライト(ランクごとに共有キャッシュ)
    private static readonly System.Collections.Generic.Dictionary<PlayRank, Sprite> rankBarSprites =
        new System.Collections.Generic.Dictionary<PlayRank, Sprite>();

    private static Sprite RankBarSprite(PlayRank rank, Color from, Color to)
    {
        if (rankBarSprites.TryGetValue(rank, out Sprite cached) && cached != null) return cached;
        var tex = new Texture2D(2, 1, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        tex.SetPixel(0, 0, from);
        tex.SetPixel(1, 0, to);
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, 2, 1), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        rankBarSprites[rank] = sprite;
        return sprite;
    }

    private void OnJudgmentEx(JudgmentTier tier, int awarded, bool wasWrongFlick)
    {
        if (score != null && ShouldBreakCombo(lastCombo, score.Combo))
        {
            StartComboBreak();
            lastCombo = 0;
            comboPunchAge = milestonePunchAge = 999f;
            milestoneTinted = false;
        }
        if (tierText != null)
        {
            tierText.text = JudgmentTierHelper.Label(tier);
            tierText.color = TierColor(tier);
        }
        if (timingHintText != null)
        {
            bool valid = score != null && score.LastErrorValid && !wasWrongFlick;
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
        tierRT.anchoredPosition = tierBasePos + new Vector2(0f, DisplaySettings.ReducedEffects ? 0 : tierRiseOffsetY * ease);
        float punch = 1f + (DisplaySettings.ReducedEffects ? 0 : tierPunchScale - 1f) * Mathf.Max(0f, 1f - tierFlashAge / 0.18f);
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
        if (ShouldBreakCombo(lastCombo, combo)) StartComboBreak();

        if (combo > lastCombo)
        {
            comboPunchAge = 0f;
            // 節目を飛び越えたコマでも出す(同時押しで 29→31。音と輪は判定ごとに出ていた。2026-10-23 点検)。
            if (GameplayFeedbackPresenter.CrossedMilestone(lastCombo, combo)) milestonePunchAge = 0f;
        }
        lastCombo = combo;

        if (combo <= 0)
        {
            comboPunchAge = milestonePunchAge = 999f;
            milestoneTinted = false;
            comboValue.text = "";
            shownCombo = int.MinValue;
            if (comboLabel != null) comboLabel.text = "";
            if (comboGlow != null) comboGlow.color = Color.clear;
            return;
        }

        // コンボ数に応じて色(既存の階調)とサイズが育つ。上端は白、下端がコンボ色の縦グラデ。
        Color c = ComboColor(combo);
        if (combo != shownCombo)
        {
            shownCombo = combo;
            comboValue.text = combo.ToString();
            comboValue.fontSize = ComboFontSize(combo);
            comboValue.enableVertexGradient = true;
            comboValue.colorGradient = ComboBonusPresentation.Gradient(combo);
            MeasureComboDigits();
        }
        comboValue.color = Color.white;
        // 節目の 0.6 秒だけ数字の上側と後ろの光を金にする(波1)。下側は最大コンボと同じ今のコンボの色のまま。
        milestonePunchAge += Time.deltaTime;
        bool tint = milestonePunchAge < MilestoneGlowSeconds;
        if (tint)
        {
            var palette = ComboBonusPresentation.Gradient(combo);
            comboValue.colorGradient = new VertexGradient(SongSelectSkin.Gold, SongSelectSkin.Gold, palette.bottomLeft, palette.bottomRight);
        }
        else if (milestoneTinted) comboValue.colorGradient = ComboBonusPresentation.Gradient(combo);
        milestoneTinted = tint;
        if (comboGlow != null) comboGlow.color = tint
            ? new Color(SongSelectSkin.Gold.r, SongSelectSkin.Gold.g, SongSelectSkin.Gold.b, .45f * DisplaySettings.AccentScale)
            : new Color(c.r, c.g, c.b, 0.20f * DisplaySettings.AccentScale);
        if (comboLabel != null)
        {
            comboLabel.text = "COMBO";
            comboLabel.color = UISkinPalette.SubtleGray;
        }

        comboPunchAge += Time.deltaTime;
        float p = Mathf.Clamp01(comboPunchAge / comboPunchDuration);
        float scale = Mathf.Lerp(DisplaySettings.ReducedEffects ? 1f : comboPunchScale, 1f, p * p * (3f - 2f * p));
        if (milestonePunchAge < MilestonePunchSeconds && !DisplaySettings.ReducedEffects)
        {
            float m = Mathf.Clamp01(milestonePunchAge / MilestonePunchSeconds);
            scale = Mathf.Lerp(MilestonePunchScale, 1f, 1f - (1f - m) * (1f - m));
        }
        if (comboRT != null) comboRT.localScale = Vector3.one * scale;
        // 節目の拡大(最大 1.7 倍)の間も、数字の下端に合わせて「COMBO」を下げる(拡大は右上寄りの支点まわり)。
        PlaceComboLabel();
    }

    void StartComboBreak()
    {
        if (comboBreakValue == null || comboRT == null) return;
        comboBreakValue.text = comboValue.text;
        comboBreakValue.fontSize = comboValue.fontSize;
        comboBreakValue.enableVertexGradient = true;
        comboBreakGradient = comboValue.colorGradient;
        comboBreakOrigin = comboRT.anchoredPosition;
        comboBreakScale = comboRT.localScale;
        comboBreakReduced = DisplaySettings.ReducedEffects;
        comboBreakAge = 0f;
        comboBreakActive = true;
        comboBreakValue.gameObject.SetActive(true);
        TickComboBreak(0f);
    }

    internal void TickComboBreak(float dt)
    {
        if (!comboBreakActive || comboBreakValue == null) return;
        comboBreakAge += Mathf.Max(0f, dt);
        ComboBreakFrame(comboBreakAge, comboBreakReduced, out float y, out float scale, out float gray, out float alpha);
        comboBreakRT.anchoredPosition = comboBreakOrigin + Vector2.up * y;
        comboBreakRT.localScale = comboBreakScale * scale;
        Color miss = TierColor(JudgmentTier.Miss);
        comboBreakValue.colorGradient = new VertexGradient(
            Color.Lerp(comboBreakGradient.topLeft, miss, gray),
            Color.Lerp(comboBreakGradient.topRight, miss, gray),
            Color.Lerp(comboBreakGradient.bottomLeft, miss, gray),
            Color.Lerp(comboBreakGradient.bottomRight, miss, gray));
        comboBreakValue.color = new Color(1f, 1f, 1f, alpha);
        if (alpha <= 0f) HideComboBreak();
    }

    void HideComboBreak()
    {
        comboBreakActive = false;
        if (comboBreakValue == null) return;
        comboBreakValue.text = "";
        comboBreakValue.gameObject.SetActive(false);
    }

    void OnDisable() { HideComboBreak(); }

    // 「COMBO」の文字の上端(右上の基準からの y)。数字は 90→128pt に育つので、描いた数字の下端より ComboLabelGap 下へ置く。
    // 既定の位置(ComboLabelRestY)より上へは上げない(2026-10-22、3けたで数字が文字にかかっていた)。純関数。
    public const float ComboLabelRestY = -330f, ComboLabelGap = 6f;
    public static float ComboLabelTop(float digitsBottom) =>
        float.IsNaN(digitsBottom) || float.IsInfinity(digitsBottom) ? ComboLabelRestY : Mathf.Min(ComboLabelRestY, digitsBottom - ComboLabelGap);

    // 描いた数字の下端(数字の支点からのローカルの y、拡大前)。文字が描けない(フォントが無い)ときは NaN で、今までどおりの位置。
    float comboDigitsMinY = float.NaN;
    void MeasureComboDigits()
    {
        if (comboValue == null) return;
        comboValue.ForceMeshUpdate();
        var bounds = comboValue.textBounds;
        comboDigitsMinY = bounds.size.y > 0 ? bounds.min.y : float.NaN;
    }

    void PlaceComboLabel()
    {
        if (comboLabel == null || comboRT == null) return;
        comboLabel.rectTransform.anchoredPosition = new Vector2(-72f, ComboLabelTop(ComboDigitsBottom));
    }

    public float ComboLabelY => comboLabel != null ? comboLabel.rectTransform.anchoredPosition.y : ComboLabelRestY;
    // 今の拡大を含めた、数字の下端(右上の基準からの y)。
    public float ComboDigitsBottom => comboRT == null || float.IsNaN(comboDigitsMinY) ? float.NaN
        : comboRT.anchoredPosition.y + comboDigitsMinY * comboRT.localScale.y;

    private void UpdateMaxCombo()
    {
        if (maxComboValue == null) return;
        int maximum = score.MaxCombo;
        if (maximum > lastMaxCombo) maxComboPunchAge = 0f;
        lastMaxCombo = maximum;
        if (maximum != shownMaxCombo)
        {
            shownMaxCombo = maximum;
            maxComboValue.text = maximum.ToString();
            maxComboValue.fontSize = Mathf.Lerp(64f, 84f, Mathf.Clamp01(maximum / 300f));
            maxComboValue.enableVertexGradient = true;
            maxComboValue.colorGradient = ComboBonusPresentation.Gradient(maximum);
        }
        maxComboPunchAge += Time.deltaTime;
        float punch = DisplaySettings.ReducedEffects ? 0f : .10f * Mathf.Clamp01(1f - maxComboPunchAge / .18f);
        maxComboValue.rectTransform.localScale = Vector3.one * (1f + punch);
    }

    private void UpdateProgress()
    {
        // 曲を止めると SongTime は 0 に戻るので、終わったあと(コンボ点の演出の間)は最後の位置のまま残す。
        if (progressFill == null || songPlayer == null || !songPlayer.IsScheduled) return;
        double end = songPlayer.ProgressEndSeconds > 0 ? songPlayer.ProgressEndSeconds : songPlayer.Duration;
        float p = Progress01(songPlayer.SongTime, songPlayer.PlayStartSeconds, end);
        progressFill.anchorMax = new Vector2(p, 1f);
    }

    // ---------------------------------------------------------------
    // フォント
    // ---------------------------------------------------------------
    // HUD 用のソフトグロー(text-shadow 相当)。位置は呼び出し側で Anchor する。
    private Image AddHudGlow(Vector2 pos, Vector2 size, Color color)
    {
        var go = new GameObject("Glow", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        var img = go.GetComponent<Image>();
        img.sprite = UISkinKit.SoftGlow();
        img.color = color;
        img.raycastTarget = false;
        return img;
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
