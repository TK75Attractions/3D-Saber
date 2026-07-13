using UnityEngine;
using UnityEngine.UI;
using TMPro;

// リザルト画面の見た目を強化。
// シーン既存のスタッツ Text は「ラベルと数値が同居」していてフォントを分けられないため
// 非表示にし、GameSession の値からレイアウトを組み直す(シーンは編集しない)。
//   ・大スコア   … Oxanium ExtraBold
//   ・小さい数値 … Rajdhani
//   ・PERFECT 等の判定ラベル … Bangers
//   ・ランク     … RANK ロゴ画像 + ランクバッジ画像(Resources/Ranks、無ければ文字で代替)
// 全要素は ResultReveal に登録し「シャッ、シャッ、デーン」と段階的に出現させる。
public class ResultSkin : MonoBehaviour
{
    // 出現タイミング(秒)。スコア→内訳(シャッシャッ)→タメ→ランク(デーン)→BACK。
    const float DelayHeader = 0.00f;
    const float DelayScoreLabel = 0.12f;
    const float DelayScoreValue = 0.24f;
    const float DelayAccuracy = 0.46f;
    const float DelayRowsStart = 0.66f;
    const float DelayRowsStep = 0.13f;
    const float DelayMaxCombo = 1.36f;
    const float DelayHiscoreTitle = 1.10f;
    const float DelayHiscoreRows = 1.20f;
    const float DelayHiscoreStep = 0.09f;
    const float DelayRankTitle = 1.66f;
    const float DelayRankBadge = 1.82f;
    const float DelayBackButton = 2.30f;

    private ResultReveal reveal;

    void Start()
    {
        var ctl = Object.FindFirstObjectByType<ResultController>();
        if (ctl == null) return;

        var canvas = ctl.GetComponent<Canvas>();
        if (canvas == null) canvas = ctl.GetComponentInParent<Canvas>();
        if (canvas == null) return;

        CyberBackdrop.Ensure(canvas);
        reveal = ResultReveal.Ensure(canvas);
        HideLegacyStats(ctl);
        StyleTitle(ctl);
        BuildStats(canvas);
        StyleBackButton(canvas);
        AddResultHeader(canvas);
    }

    // 精度の百分率表記(テストから直接叩く純関数)
    public static string FormatAccuracy(float accuracy01)
    {
        return (Mathf.Clamp01(accuracy01) * 100f).ToString("F1") + "%";
    }

    // 旧スタッツ(ラベル+数値が同居した Text)は非表示にして作り直す。非破壊(SetActive のみ)。
    void HideLegacyStats(ResultController ctl)
    {
        if (ctl.scoreText != null) ctl.scoreText.gameObject.SetActive(false);
        if (ctl.comboText != null) ctl.comboText.gameObject.SetActive(false);
        if (ctl.perfectText != null) ctl.perfectText.gameObject.SetActive(false);
        if (ctl.greatText != null) ctl.greatText.gameObject.SetActive(false);
        if (ctl.goodText != null) ctl.goodText.gameObject.SetActive(false);
        if (ctl.badText != null) ctl.badText.gameObject.SetActive(false);
        if (ctl.missText != null) ctl.missText.gameObject.SetActive(false);
    }

    // 曲名(日本語を含み得るので legacy Text のまま)を上部中央へ
    void StyleTitle(ResultController ctl)
    {
        if (ctl.titleText == null) return;
        ctl.titleText.color = UISkinPalette.OffWhite;
        ctl.titleText.fontStyle = FontStyle.Bold;
        ctl.titleText.fontSize = 30;
        ctl.titleText.alignment = TextAnchor.MiddleCenter;
        ctl.titleText.horizontalOverflow = HorizontalWrapMode.Overflow;
        ctl.titleText.verticalOverflow = VerticalWrapMode.Overflow;
        var rt = ctl.titleText.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, 425f);
        Reveal(ctl.titleText.gameObject, DelayHeader, 0.25f, new Vector2(0f, 14f));
    }

    void BuildStats(Canvas canvas)
    {
        var oxanium = UISkinKit.FontAsset("Oxanium-ExtraBold");
        if (oxanium == null) oxanium = UISkinKit.FontAsset("Oxanium-Bold");
        var rajBold = UISkinKit.FontAsset("Rajdhani-Bold");
        var rajSemi = UISkinKit.FontAsset("Rajdhani-SemiBold");
        var bangers = UISkinKit.FontAsset("Bangers-Regular");

        var root = new GameObject("ResultStats", typeof(RectTransform));
        root.transform.SetParent(canvas.transform, false);
        var rootRT = root.GetComponent<RectTransform>();
        rootRT.anchorMin = rootRT.anchorMax = new Vector2(0.5f, 0.5f);
        rootRT.sizeDelta = Vector2.zero;

        float accuracy = PlayRankHelper.Accuracy(
            GameSession.FinalPerfect, GameSession.FinalGreat, GameSession.FinalGood,
            GameSession.FinalBad, GameSession.FinalMiss);
        PlayRank rank = PlayRankHelper.FromAccuracy(accuracy);

        // --- スコア(中央上・最重要) ---
        var scoreLabel = UISkinKit.MakeTMP(root.transform, "ScoreLabel", "SCORE", 26f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Center,
            new Vector2(0f, 338f), new Vector2(400f, 32f), FontStyles.Normal, 8f, rajSemi);
        Reveal(scoreLabel.gameObject, DelayScoreLabel, 0.16f, new Vector2(-70f, 0f));

        var scoreValue = UISkinKit.MakeTMP(root.transform, "ScoreValue", GameSession.FinalScore.ToString("N0"), 104f,
            UISkinPalette.Cyan, TextAlignmentOptions.Center,
            new Vector2(0f, 262f), new Vector2(900f, 120f), FontStyles.Normal, 2f, oxanium);
        Reveal(scoreValue.gameObject, DelayScoreValue, 0.20f, new Vector2(-120f, 0f));

        var accText = UISkinKit.MakeTMP(root.transform, "Accuracy", "ACCURACY  " + FormatAccuracy(accuracy), 26f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Center,
            new Vector2(0f, 190f), new Vector2(500f, 32f), FontStyles.Normal, 3f, rajSemi);
        Reveal(accText.gameObject, DelayAccuracy, 0.16f, new Vector2(-70f, 0f));

        // --- ランク(右カラム、最後にデーンと出る) ---
        BuildRankBadge(root.transform, rank, oxanium, rajSemi);

        // --- 判定内訳(中央、シャッシャッと順に) ---
        float y = 104f;
        const float step = 62f;
        int row = 0;
        MakeStatRow(root.transform, "PERFECT", GameSession.FinalPerfect, UISkinPalette.Cyan, y, bangers, rajBold, row++); y -= step;
        MakeStatRow(root.transform, "GREAT", GameSession.FinalGreat, UISkinPalette.Yellow, y, bangers, rajBold, row++); y -= step;
        MakeStatRow(root.transform, "GOOD", GameSession.FinalGood, UISkinPalette.Orange, y, bangers, rajBold, row++); y -= step;
        MakeStatRow(root.transform, "BAD", GameSession.FinalBad, UISkinPalette.Magenta, y, bangers, rajBold, row++); y -= step;
        MakeStatRow(root.transform, "MISS", GameSession.FinalMiss, UISkinPalette.SubtleGray, y, bangers, rajBold, row++); y -= step;

        // --- 最大コンボ ---
        var comboLabel = UISkinKit.MakeTMP(root.transform, "MaxComboLabel", "MAX COMBO", 24f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.MidlineRight,
            new Vector2(-180f, y - 14f), new Vector2(300f, 32f), FontStyles.Normal, 4f, rajSemi);
        Reveal(comboLabel.gameObject, DelayMaxCombo, 0.16f, new Vector2(0f, -26f));
        var comboValue = UISkinKit.MakeTMP(root.transform, "MaxComboValue", GameSession.FinalMaxCombo.ToString("N0"), 36f,
            UISkinPalette.OffWhite, TextAlignmentOptions.MidlineLeft,
            new Vector2(120f, y - 14f), new Vector2(240f, 44f), FontStyles.Normal, 1f, rajBold);
        Reveal(comboValue.gameObject, DelayMaxCombo + 0.08f, 0.16f, new Vector2(0f, -26f));

        // --- ハイスコア(左カラム) ---
        BuildHighScores(root.transform, accuracy, rank, rajSemi, rajBold);
    }

    // 今回の結果をハイスコアに記録し、左カラムに上位5件のランキング表を出す。
    // 今回の記録が入った行はシアンで強調する。曲IDが無い(シーン直起動等)ときは何も出さない。
    void BuildHighScores(Transform parent, float accuracy, PlayRank rank,
        TMP_FontAsset labelFont, TMP_FontAsset valueFont)
    {
        string songId = GameSession.SelectedSongId;
        if (string.IsNullOrEmpty(songId)) return;

        var entry = new HighScoreEntry
        {
            score = GameSession.FinalScore,
            rank = PlayRankHelper.Label(rank),
            accuracy = accuracy,
            date = System.DateTime.Now.ToString("yyyy/MM/dd"),
        };
        int newIndex = HighScoreStore.Record(songId, GameSession.SelectedDifficulty, entry, out HighScoreTable table);

        var header = UISkinKit.MakeTMP(parent, "HiscoreTitle", "HIGH SCORE", 26f,
            UISkinPalette.SubtleGray, TextAlignmentOptions.Center,
            new Vector2(-560f, 332f), new Vector2(320f, 32f), FontStyles.Normal, 6f, labelFont);
        Reveal(header.gameObject, DelayHiscoreTitle, 0.16f, new Vector2(-70f, 0f));

        for (int i = 0; i < table.entries.Count && i < HighScoreStore.MaxEntries; i++)
        {
            HighScoreEntry e = table.entries[i];
            bool isNew = i == newIndex;
            float rowY = 272f - i * 46f;
            float delay = DelayHiscoreRows + i * DelayHiscoreStep;
            Color scoreColor = isNew ? UISkinPalette.Cyan : UISkinPalette.OffWhite;
            Color numColor = isNew ? UISkinPalette.Cyan : UISkinPalette.SubtleGray;

            var num = UISkinKit.MakeTMP(parent, $"Hiscore{i}Num", (i + 1).ToString(), 24f,
                numColor, TextAlignmentOptions.MidlineRight,
                new Vector2(-668f, rowY), new Vector2(44f, 34f), FontStyles.Normal, 0f, valueFont);
            Reveal(num.gameObject, delay, 0.14f, new Vector2(-80f, 0f));

            var score = UISkinKit.MakeTMP(parent, $"Hiscore{i}Score", e.score.ToString("N0"), isNew ? 32f : 30f,
                scoreColor, TextAlignmentOptions.MidlineRight,
                new Vector2(-548f, rowY), new Vector2(200f, 38f), FontStyles.Normal, 1f, valueFont);
            Reveal(score.gameObject, delay, 0.14f, new Vector2(-80f, 0f));

            var grade = UISkinKit.MakeTMP(parent, $"Hiscore{i}Grade", e.rank ?? "", 24f,
                PlayRankHelper.RankColor(PlayRankHelper.FromLabel(e.rank)), TextAlignmentOptions.MidlineLeft,
                new Vector2(-404f, rowY), new Vector2(64f, 34f), FontStyles.Normal, 1f, labelFont);
            Reveal(grade.gameObject, delay + 0.04f, 0.14f, new Vector2(-80f, 0f));
        }
    }

    // 判定内訳の1行(ラベル=Bangers / 数値=Rajdhani)。右からシャッと入る。
    void MakeStatRow(Transform parent, string label, int count, Color labelColor, float y,
        TMP_FontAsset labelFont, TMP_FontAsset valueFont, int rowIndex)
    {
        float delay = DelayRowsStart + rowIndex * DelayRowsStep;
        var l = UISkinKit.MakeTMP(parent, "Stat" + label, label, 46f,
            labelColor, TextAlignmentOptions.MidlineRight,
            new Vector2(-180f, y), new Vector2(340f, 56f), FontStyles.Normal, 2f, labelFont);
        Reveal(l.gameObject, delay, 0.14f, new Vector2(90f, 0f));
        var v = UISkinKit.MakeTMP(parent, "Stat" + label + "Value", count.ToString("N0"), 44f,
            UISkinPalette.OffWhite, TextAlignmentOptions.MidlineLeft,
            new Vector2(120f, y), new Vector2(240f, 56f), FontStyles.Normal, 1f, valueFont);
        Reveal(v.gameObject, delay + 0.05f, 0.14f, new Vector2(90f, 0f));
    }

    // RANK ロゴ画像 + ランクバッジ画像(無ければ文字フォールバック)。バッジは「デーン」(Slam)。
    void BuildRankBadge(Transform parent, PlayRank rank, TMP_FontAsset badgeFont, TMP_FontAsset labelFont)
    {
        Color c = PlayRankHelper.RankColor(rank);

        GameObject titleGo;
        var logo = UISkinKit.LoadSprite("Ranks/Rank_Title");
        if (logo != null)
        {
            titleGo = new GameObject("RankTitle", typeof(RectTransform), typeof(Image));
            titleGo.transform.SetParent(parent, false);
            var lrt = titleGo.GetComponent<RectTransform>();
            lrt.anchoredPosition = new Vector2(560f, 332f);
            lrt.sizeDelta = new Vector2(250f, 125f);
            var li = titleGo.GetComponent<Image>();
            li.sprite = logo;
            li.preserveAspect = true;
            li.raycastTarget = false;
        }
        else
        {
            titleGo = UISkinKit.MakeTMP(parent, "RankTitle", "RANK", 30f,
                UISkinPalette.SubtleGray, TextAlignmentOptions.Center,
                new Vector2(560f, 332f), new Vector2(260f, 38f), FontStyles.Normal, 10f, labelFont).gameObject;
        }
        Reveal(titleGo, DelayRankTitle, 0.18f, new Vector2(0f, 26f));

        GameObject badgeGo;
        var sprite = UISkinKit.LoadSprite(PlayRankHelper.SpriteResourceName(rank));
        if (sprite != null)
        {
            badgeGo = new GameObject("RankBadge", typeof(RectTransform), typeof(Image));
            badgeGo.transform.SetParent(parent, false);
            var rt = badgeGo.GetComponent<RectTransform>();
            rt.anchoredPosition = new Vector2(560f, 168f);
            rt.sizeDelta = new Vector2(180f, 180f);
            var img = badgeGo.GetComponent<Image>();
            img.sprite = sprite;
            img.preserveAspect = true;
            img.raycastTarget = false;
        }
        else
        {
            badgeGo = UISkinKit.MakeTMP(parent, "RankBadgeText", PlayRankHelper.Label(rank), 120f,
                c, TextAlignmentOptions.Center,
                new Vector2(560f, 168f), new Vector2(320f, 150f), FontStyles.Normal, 0f, badgeFont).gameObject;
        }
        if (reveal != null) reveal.Add(badgeGo, DelayRankBadge, 0.42f, ResultReveal.Kind.Slam, Vector2.zero);
    }

    void StyleBackButton(Canvas canvas)
    {
        foreach (var btn in canvas.GetComponentsInChildren<Button>())
        {
            var label = btn.GetComponentInChildren<Text>();
            if (label != null && label.text == "BACK")
            {
                SongSelectSkin.ApplyNeon(btn, UISkinPalette.Magenta, 0.20f);
                Reveal(btn.gameObject, DelayBackButton, 0.25f, new Vector2(0f, -18f));
            }
        }
    }

    void AddResultHeader(Canvas canvas)
    {
        var header = new GameObject("ResultHeader", typeof(RectTransform), typeof(Text));
        header.transform.SetParent(canvas.transform, false);
        var rt = header.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(800, 60);
        rt.anchoredPosition = new Vector2(0, 480);
        var t = header.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 28;
        t.fontStyle = FontStyle.Bold;
        t.alignment = TextAnchor.MiddleCenter;
        t.text = "// RESULT";
        t.color = UISkinPalette.SubtleGray;
        t.raycastTarget = false;
        Reveal(header, DelayHeader, 0.25f, new Vector2(0f, 10f));
    }

    void Reveal(GameObject go, float delay, float duration, Vector2 from)
    {
        if (reveal != null) reveal.Add(go, delay, duration, ResultReveal.Kind.Slide, from);
    }
}
