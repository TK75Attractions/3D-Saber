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
    // 判定窓（秒）。2026-10-20 の追加指定: 全体は早め100ms / 遅め180ms、PERFECT は早め50ms / 遅め90ms。
    // 片側平均で全体140ms・PERFECT 70ms。合計幅は360ms → 280ms、PERFECT の割合は50%を維持する。
    // GREAT は早め70ms / 遅め125ms、GOOD は早め85ms / 遅め155ms。同じずれでも早め側を厳しくする。
    // カメラの遅れは従来どおり判定の時計だけで差し引く(GamePlayManager)。
    // NoteSpawner の判定可能ウィンドウは GamePlayManager がこの定数から同期する
    // （シーンに古い狭い値が焼き込まれていても、ここを変えれば全体が追従する）。
    public const double LatePerfectSeconds = 0.090;
    public const double LateGreatSeconds = 0.125;
    public const double LateGoodSeconds = 0.155;
    public const double LateBadSeconds = 0.180;
    public const double EarlyPerfectSeconds = 0.050;
    public const double EarlyGreatSeconds = 0.070;
    public const double EarlyGoodSeconds = 0.085;
    public const double EarlyBadSeconds = 0.100;

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

    // 床の縁の帯とゲート発光の強さ。Perfect を最大にして Great/Good にも弱めに出す(Bad/Miss は出さない)。
    // 背景ごとの大きな動きと織り目は Perfect 限定のまま(StageReactiveEffects)。
    public const float StageGainGreat = .70f;
    public const float StageGainGood = .45f;
    public static float StageReactionGain(JudgmentTier t)
    {
        switch (t)
        {
            case JudgmentTier.Perfect: return 1f;
            case JudgmentTier.Great: return StageGainGreat;
            case JudgmentTier.Good: return StageGainGood;
            default: return 0f;
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
