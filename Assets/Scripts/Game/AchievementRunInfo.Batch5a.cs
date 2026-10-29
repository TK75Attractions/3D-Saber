public static partial class AchievementRunInfo
{
    public static bool Batch5aPracticeHistoryKnown { get; private set; }
    public static bool Batch5aPracticeSeen { get; private set; }

    static void BeginBatch5aVisitor()
    {
        Batch5aPracticeHistoryKnown = true;
        Batch5aPracticeSeen = false;
    }

    // 「いいえ」のあとの範囲合わせは練習に含めず、通常の練習は中断しても覚える。
    public static void ObserveBatch5aPractice(bool rangeOnly)
    {
        if (!rangeOnly) Batch5aPracticeSeen = true;
    }
}
