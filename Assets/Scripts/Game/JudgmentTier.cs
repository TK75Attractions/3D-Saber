public enum JudgmentTier
{
    Perfect,
    Great,
    Good,
    Bad,
    Miss
}

public static class JudgmentTierHelper
{
    // 判定時刻との誤差（秒）からティアを決める。
    // 対称窓：±50ms = Perfect, ±100ms = Great, ±150ms = Good, ±200ms = Bad, それ以上 = Miss
    public static JudgmentTier Classify(double errorSeconds)
    {
        double abs = errorSeconds < 0 ? -errorSeconds : errorSeconds;
        if (abs <= 0.05) return JudgmentTier.Perfect;
        if (abs <= 0.10) return JudgmentTier.Great;
        if (abs <= 0.15) return JudgmentTier.Good;
        if (abs <= 0.20) return JudgmentTier.Bad;
        return JudgmentTier.Miss;
    }

    public static int BasePoints(JudgmentTier t)
    {
        switch (t)
        {
            case JudgmentTier.Perfect: return 300;
            case JudgmentTier.Great: return 200;
            case JudgmentTier.Good: return 100;
            case JudgmentTier.Bad: return 50;
            default: return 0;
        }
    }

    public static string Label(JudgmentTier t)
    {
        switch (t)
        {
            case JudgmentTier.Perfect: return "PERFECT";
            case JudgmentTier.Great: return "GREAT";
            case JudgmentTier.Good: return "GOOD";
            case JudgmentTier.Bad: return "BAD";
            default: return "MISS";
        }
    }
}
