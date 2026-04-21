using UnityEngine;
using UnityEngine.UI;

// ゲーム中のスコア/コンボ/直近ティア表示。
public class ScoreHUD : MonoBehaviour
{
    public ScoreManager score;
    public Text scoreText;
    public Text comboText;
    public Text tierText;

    public Color perfectColor = new Color(0.35f, 1f, 0.85f);
    public Color greatColor = new Color(0.9f, 1f, 0.4f);
    public Color goodColor = new Color(1f, 0.8f, 0.3f);
    public Color badColor = new Color(1f, 0.5f, 0.3f);
    public Color missColor = new Color(0.6f, 0.6f, 0.7f);
    public float flashDuration = 0.4f;

    private float tierFlashAge = 999f;

    void Start()
    {
        if (score != null) score.OnJudgment += OnJudgment;
    }

    void OnDestroy()
    {
        if (score != null) score.OnJudgment -= OnJudgment;
    }

    void OnJudgment(JudgmentTier tier, int awarded)
    {
        if (tierText != null)
        {
            tierText.text = JudgmentTierHelper.Label(tier);
            tierText.color = ColorFor(tier);
        }
        tierFlashAge = 0f;
    }

    void Update()
    {
        if (score != null)
        {
            if (scoreText != null) scoreText.text = $"Score {score.Score}";
            if (comboText != null) comboText.text = score.Combo > 0 ? $"Combo {score.Combo}" : "";
        }
        if (tierText != null)
        {
            tierFlashAge += Time.deltaTime;
            float a = Mathf.Clamp01(1f - tierFlashAge / flashDuration);
            Color c = tierText.color;
            c.a = a;
            tierText.color = c;
        }
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
