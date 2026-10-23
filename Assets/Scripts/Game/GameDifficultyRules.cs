using System;

// 保存値は変えず、この曲で使う値だけを決める。
public static class GameDifficultyRules
{
    public static bool Easy(string difficulty) =>
        string.Equals(difficulty, "Easy", StringComparison.OrdinalIgnoreCase);

    public static float ApproachSeconds(string difficulty, float saved, bool song) =>
        song && Easy(difficulty) && saved == NoteSpeedPreset.SecondsFor(1) ? 1.25f : saved;

    public static float DirectionDegrees(string difficulty) =>
        Easy(difficulty) ? 80f : SaberCutJudge.DefaultDirectionToleranceDegrees;

    public static void ConfigureJudges(string difficulty, bool song,
        SaberCutJudge first, SaberCutJudge second, CameraImuJudgment imu)
    {
        // 通常の設定・調整画面・チュートリアルには触れない。
        if (!song || !Easy(difficulty)) return;
        float degrees = DirectionDegrees(difficulty);
        if (first != null) first.directionToleranceDegrees = degrees;
        if (second != null) second.directionToleranceDegrees = degrees;
        if (imu != null) imu.directionToleranceDegrees = degrees;
    }
}
