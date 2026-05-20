using UnityEngine;

// シーン間で譜面選択とリザルトを受け渡す薄い静的ストア。
// プレイヤー固有の判定オフセットは PlayerPrefs で永続化する。
public static class GameSession
{
    public static string SelectedSongId;
    public static string SelectedSongTitle;
    public static string SelectedDifficulty = "Normal";

    public static int FinalScore;
    public static int FinalMaxCombo;
    public static int FinalHit;
    public static int FinalMiss;
    public static int FinalPerfect;
    public static int FinalGreat;
    public static int FinalGood;
    public static int FinalBad;

    // 判定オフセット（ミリ秒）。SongSelect でユーザーが調整して PlayerPrefs に保存。
    // GamePlayManager がプレイ開始時にこの値を実効オフセットに加算する。
    private const string JudgmentOffsetMsKey = "judgmentOffsetMs";
    public const int JudgmentOffsetMinMs = -1000;
    public const int JudgmentOffsetMaxMs = 1000;

    public static int JudgmentOffsetMs
    {
        get => PlayerPrefs.GetInt(JudgmentOffsetMsKey, 0);
        set
        {
            int clamped = Mathf.Clamp(value, JudgmentOffsetMinMs, JudgmentOffsetMaxMs);
            PlayerPrefs.SetInt(JudgmentOffsetMsKey, clamped);
            PlayerPrefs.Save();
        }
    }

    public static void ResetJudgmentOffset()
    {
        PlayerPrefs.DeleteKey(JudgmentOffsetMsKey);
        PlayerPrefs.Save();
    }

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
