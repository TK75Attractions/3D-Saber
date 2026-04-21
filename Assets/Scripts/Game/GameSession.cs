// シーン間で譜面選択とリザルトを受け渡す薄い静的ストア。
public static class GameSession
{
    public static string SelectedSongId;
    public static string SelectedSongTitle;

    public static int FinalScore;
    public static int FinalMaxCombo;
    public static int FinalHit;
    public static int FinalMiss;
    public static int FinalPerfect;
    public static int FinalGreat;
    public static int FinalGood;
    public static int FinalBad;

    public static void ResetResult()
    {
        FinalScore = 0;
        FinalMaxCombo = 0;
        FinalHit = 0;
        FinalMiss = 0;
        FinalPerfect = 0;
        FinalGreat = 0;
        FinalGood = 0;
        FinalBad = 0;
    }
}
