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
    // 早め（error < 0）は厳しく、遅め（error >= 0）は緩めの非対称窓。
    //   遅め: Perfect=90 / Great=150 / Good=210 / Bad=270 ms
    //   早め: Perfect=45 / Great=75  / Good=105 / Bad=135 ms （遅めの 1/2、比率は維持）
    public static JudgmentTier Classify(double errorSeconds)
    {
        if (errorSeconds >= 0)
        {
            // 遅め
            if (errorSeconds <= 0.09) return JudgmentTier.Perfect;
            if (errorSeconds <= 0.15) return JudgmentTier.Great;
            if (errorSeconds <= 0.21) return JudgmentTier.Good;
            if (errorSeconds <= 0.27) return JudgmentTier.Bad;
            return JudgmentTier.Miss;
        }
        // 早め
        double abs = -errorSeconds;
        if (abs <= 0.045) return JudgmentTier.Perfect;
        if (abs <= 0.075) return JudgmentTier.Great;
        if (abs <= 0.105) return JudgmentTier.Good;
        if (abs <= 0.135) return JudgmentTier.Bad;
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
