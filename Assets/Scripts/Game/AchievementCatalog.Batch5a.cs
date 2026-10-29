using System.Collections.Generic;

public static partial class AchievementCatalog
{
    // ---- 第4弾(2陣目)の数 ----
    public const int GoodStartNotes = 10, BounceBackFailures = 3, ZanshinScenes = 3;
    public const double ZanshinNextSeconds = 1;
    public const float CornerTolerance = .01f;
    public const int QuietMinNotes = 5, TopFiveMinPlays = 20, TopFiveRank = 5;
    public const int PureGoldMinNotes = 3, SacredKindMinNotes = 3, ArrowRunGroups = 6, ChordMinGroups = 5;
    public const int RhythmMinMeasured = 20, RhythmMissNumerator = 1, RhythmMissDenominator = 5;
    public const int RhythmPerfectNumerator = 4, RhythmPerfectDenominator = 5;
    public const int FinalBestMinMeasured = 30, SteadyMinMeasured = 100;
    public const double SteadyMaxDeviationMs = 15;
    public const int AllGoodMinNotes = 20, EqualJudgmentMinCount = 20;
    public const int ContentsMinAchievements = 3, MatchingOrdinalMin = 10, StepOrdinalMin = 10;
    public const int StepOrdinalCount = 3, TripleGoldCount = 3;
    public const int PatternScoreMin = 10000, PatternScoreMax = 99999, PatternScoreDigits = 5;

    // 全部、通常の実績の同じ写しだけを見る。この一覧と既存の TallyIds は互いに数えない。
    public static readonly IReadOnlyList<string> Batch5aTallyIds =
        new[] { "kengou", "nakami", "soroiban", "medal3", "achsteps", "triplegold" };
}
