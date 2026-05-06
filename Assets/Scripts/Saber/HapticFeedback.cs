using UnityEngine;

// ScoreManager.OnJudgment を購読して、ティアごとに長さを変えてハプティック発火。
public class HapticFeedback : MonoBehaviour
{
    public ScoreManager scoreManager;

    public float perfectDuration = 0.10f;
    public float greatDuration = 0.06f;
    public float goodDuration = 0.06f;
    public float badDuration = 0.03f;
    public float missDuration = 0f;

    void OnEnable()
    {
        if (scoreManager != null) scoreManager.OnJudgment += OnJudgment;
    }

    void OnDisable()
    {
        if (scoreManager != null) scoreManager.OnJudgment -= OnJudgment;
    }

    private void OnJudgment(JudgmentTier tier, int award)
    {
        float dur = DurationFor(tier);
        if (dur > 0f) Haptic.Vibrate(dur);
    }

    public float DurationFor(JudgmentTier tier)
    {
        switch (tier)
        {
            case JudgmentTier.Perfect: return perfectDuration;
            case JudgmentTier.Great:   return greatDuration;
            case JudgmentTier.Good:    return goodDuration;
            case JudgmentTier.Bad:     return badDuration;
            default:                   return missDuration;
        }
    }
}
