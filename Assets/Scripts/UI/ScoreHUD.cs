using UnityEngine;
using UnityEngine.UI;

// ゲーム中のスコア / コンボ / 直近 tier 表示。
// - tier 表示は判定された瞬間に下から上にふわっと浮かび上がるアニメ
// - コンボは右側に大きく表示し、コンボ数に応じて色変化＋増えた瞬間にスケールパンチ
public class ScoreHUD : MonoBehaviour
{
    public ScoreManager score;
    public Text scoreText;
    public Text comboText;
    public Text tierText;
    public Text flickWarningText;

    [Header("Tier float-up animation")]
    public float tierFlashDuration = 0.6f;
    public float tierRiseOffsetY = 90f;
    public Vector2 tierBaseAnchored = new Vector2(0f, 220f);
    public float tierPunchScale = 1.35f;

    [Header("Combo")]
    public float comboPunchScale = 1.30f;
    public float comboPunchDuration = 0.18f;
    public bool comboUpperCase = true;

    // tier ごとの色（インスペクタ調整可）
    public Color perfectColor = new Color(0.27f, 1f, 0.97f);
    public Color greatColor   = new Color(1f, 0.92f, 0.35f);
    public Color goodColor    = new Color(1f, 0.60f, 0.25f);
    public Color badColor     = new Color(1f, 0.30f, 0.55f);
    public Color missColor    = new Color(0.55f, 0.60f, 0.75f);
    public Color flickWarningColor = new Color(1f, 0.70f, 0.20f);

    private float tierFlashAge = 999f;
    private bool currentWasFlickFail;
    private RectTransform tierRT;

    private int lastCombo = 0;
    private float comboPunchAge = 999f;
    private RectTransform comboRT;
    private Outline comboOutline;

    void Awake()
    {
        if (tierText != null)
        {
            tierRT = tierText.rectTransform;
            tierBaseAnchored = tierRT.anchoredPosition;
        }
        if (comboText != null)
        {
            comboRT = comboText.rectTransform;
            ReanchorComboToRight(comboRT);
            ConfigureComboFont(comboText);
            EnsureComboOutline();
        }
        if (scoreText != null)
        {
            ConfigureScoreFont(scoreText);
        }
    }

    // コンボ表示を画面右の中央寄り（縦中央）に再アンカーする。
    // 既存シーンは UpperRight 角 (720, 450) に小さく置いてあるが、もっと目立つ右側中央へ移動。
    void ReanchorComboToRight(RectTransform rt)
    {
        rt.anchorMin = new Vector2(1f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(-60f, 60f);
        rt.sizeDelta = new Vector2(560f, 360f);
    }

    void Start()
    {
        if (score != null) score.OnJudgmentEx += OnJudgmentEx;
    }

    void OnDestroy()
    {
        if (score != null) score.OnJudgmentEx -= OnJudgmentEx;
    }

    void ConfigureComboFont(Text t)
    {
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 120;            // 右側に置くので存在感を強く
        t.lineSpacing = 0.85f;
        t.fontStyle = FontStyle.Bold;
        t.alignment = TextAnchor.MiddleRight;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
    }

    void ConfigureScoreFont(Text t)
    {
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 50;
        t.fontStyle = FontStyle.Bold;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
    }

    void EnsureComboOutline()
    {
        if (comboText == null) return;
        comboOutline = comboText.GetComponent<Outline>();
        if (comboOutline == null) comboOutline = comboText.gameObject.AddComponent<Outline>();
        comboOutline.effectColor = new Color(0f, 0f, 0f, 0.5f);
        comboOutline.effectDistance = new Vector2(2f, -2f);
    }

    void OnJudgmentEx(JudgmentTier tier, int awarded, bool wasWrongFlick)
    {
        if (tierText != null)
        {
            tierText.text = JudgmentTierHelper.Label(tier);
            tierText.color = ColorFor(tier);
        }
        currentWasFlickFail = wasWrongFlick;
        if (flickWarningText != null)
        {
            flickWarningText.text = wasWrongFlick ? "⚠ FLICK" : "";
            flickWarningText.color = flickWarningColor;
        }
        tierFlashAge = 0f;
    }

    void Update()
    {
        if (score != null)
        {
            if (scoreText != null) scoreText.text = $"SCORE  {score.Score:N0}";
            UpdateCombo();
        }
        UpdateTierAnimation();
    }

    void UpdateTierAnimation()
    {
        if (tierText == null || tierRT == null) return;
        tierFlashAge += Time.deltaTime;
        float t = Mathf.Clamp01(tierFlashAge / tierFlashDuration);
        // 位置：下からふわっと上昇（ease-out）
        float ease = 1f - (1f - t) * (1f - t);
        Vector2 pos = tierBaseAnchored + new Vector2(0f, tierRiseOffsetY * ease);
        tierRT.anchoredPosition = pos;
        // スケールパンチ：瞬間的に大きくなって戻る
        float punch = 1f + (tierPunchScale - 1f) * Mathf.Max(0f, 1f - tierFlashAge / 0.18f);
        tierRT.localScale = Vector3.one * punch;
        // フェード（ease-in 後半）
        float alpha = Mathf.Clamp01(1f - t);
        Color c = tierText.color; c.a = alpha; tierText.color = c;
        if (flickWarningText != null)
        {
            Color w = flickWarningText.color;
            w.a = currentWasFlickFail ? alpha : 0f;
            flickWarningText.color = w;
        }
    }

    void UpdateCombo()
    {
        if (comboText == null) return;
        int combo = score.Combo;

        // 増加検知でパンチを発動
        if (combo > lastCombo) comboPunchAge = 0f;
        // 切れた瞬間は表示を消す
        if (combo == 0 && lastCombo > 0)
        {
            comboText.text = "";
            lastCombo = 0;
            return;
        }
        lastCombo = combo;

        if (combo <= 0)
        {
            comboText.text = "";
            return;
        }

        // フォーマット
        string label = comboUpperCase ? "COMBO" : "combo";
        comboText.text = $"{combo}\n<size=36>{label}</size>";
        comboText.supportRichText = true;

        // 色階調
        Color baseC = ColorByComboTier(combo);
        comboText.color = baseC;
        if (comboOutline != null) comboOutline.effectColor = new Color(0f, 0f, 0f, 0.55f);

        // パンチアニメ
        comboPunchAge += Time.deltaTime;
        float p = Mathf.Clamp01(comboPunchAge / comboPunchDuration);
        // bounce: 0 で最大、終わりで 1.0
        float scale = Mathf.Lerp(comboPunchScale, 1f, EaseOutBack(p));
        if (comboRT != null) comboRT.localScale = Vector3.one * scale;
    }

    static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
    }

    public Color ColorByComboTier(int combo)
    {
        if (combo >= 100) return new Color(1f, 0.30f, 0.85f);   // マゼンタ
        if (combo >= 60)  return new Color(1f, 0.55f, 0.25f);   // オレンジ
        if (combo >= 30)  return new Color(1f, 0.92f, 0.35f);   // 黄
        if (combo >= 10)  return new Color(0.27f, 1f, 0.97f);   // シアン
        return new Color(0.91f, 0.93f, 1f);                     // オフホワイト
    }

    private Color ColorFor(JudgmentTier t)
    {
        switch (t)
        {
            case JudgmentTier.Perfect: return perfectColor;
            case JudgmentTier.Great: return greatColor;
            case JudgmentTier.Good: return goodColor;
            case JudgmentTier.Bad: return badColor;
            default: return missColor;
        }
    }
}
