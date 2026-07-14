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
    // 判定窓（秒）。「だいぶ甘く」の要望(2026-07)で従来値(90/150/210/270ms)の1.5倍に拡大。
    // 早め（error < 0）は遅めの 1/2 のままにして、準備スイングの誤爆だけ引き続き抑える非対称窓。
    // NoteSpawner の判定可能ウィンドウは GamePlayManager がこの定数から同期する
    // （シーンに古い狭い値が焼き込まれていても、ここを変えれば全体が追従する）。
    public const double LatePerfectSeconds = 0.135;
    public const double LateGreatSeconds = 0.225;
    public const double LateGoodSeconds = 0.315;
    public const double LateBadSeconds = 0.405;
    public const double EarlyPerfectSeconds = LatePerfectSeconds * 0.5; // 0.0675
    public const double EarlyGreatSeconds = LateGreatSeconds * 0.5;     // 0.1125
    public const double EarlyGoodSeconds = LateGoodSeconds * 0.5;       // 0.1575
    public const double EarlyBadSeconds = LateBadSeconds * 0.5;         // 0.2025

    // 判定時刻との誤差（秒）からティアを決める。
    public static JudgmentTier Classify(double errorSeconds)
    {
        if (errorSeconds >= 0)
        {
            // 遅め
            if (errorSeconds <= LatePerfectSeconds) return JudgmentTier.Perfect;
            if (errorSeconds <= LateGreatSeconds) return JudgmentTier.Great;
            if (errorSeconds <= LateGoodSeconds) return JudgmentTier.Good;
            if (errorSeconds <= LateBadSeconds) return JudgmentTier.Bad;
            return JudgmentTier.Miss;
        }
        // 早め
        double abs = -errorSeconds;
        if (abs <= EarlyPerfectSeconds) return JudgmentTier.Perfect;
        if (abs <= EarlyGreatSeconds) return JudgmentTier.Great;
        if (abs <= EarlyGoodSeconds) return JudgmentTier.Good;
        if (abs <= EarlyBadSeconds) return JudgmentTier.Bad;
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
