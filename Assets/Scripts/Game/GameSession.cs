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
    public static int FinalComboBonus;
    public static int FinalHit;
    public static int FinalMiss;
    public static int FinalPerfect;
    public static int FinalGreat;
    public static int FinalGood;
    public static int FinalBad;
    // 結果UIの再構築と、同じ人の次のプレイを区別する保存用ID。
    public static string AchievementRunId;
    public static DailyRankingStore.Result FinalDailyRanking;

    // 通常終了の確定スコアのみ登録。日付や結果UIが変わっても同じプレイを二重登録しない。
    public static void RecordCompletedDailyRanking()
    {
        if (FinalDailyRanking != null || IsCalibrationMode) return;
        if (FinalPerfect < 0 || FinalGreat < 0 || FinalGood < 0 || FinalBad < 0 || FinalMiss < 0) return;
        if ((long)FinalPerfect + FinalGreat + FinalGood + FinalBad + FinalMiss == 0) return;
        FinalDailyRanking = DailyRankingStore.Record(SelectedSongId, SelectedDifficulty,
            AchievementRunId, FinalScore, System.DateTime.Now);
    }

    public static void RecordCompletedAchievements()
    {
        if (IsCalibrationMode) return;
        SongAchievementStore.Record(SelectedSongId, SelectedDifficulty, AchievementRunId,
            FinalPerfect, FinalGreat, FinalGood, FinalBad, FinalMiss);
    }

    // キャリブレーション（判定調整）モード。SongSelect から該当ボタンで true にして
    // Game シーンへ遷移する。Game シーンの GamePlayManager がこれを見て、
    // 合成譜面 + メトロノームを再生する。
    public static bool IsCalibrationMode;
    // タイトルの問いかけで「はい」を選んだとき true。Game シーンの GamePlayManager が読んだら消し、
    // 曲の代わりに練習(TutorialController)を回してから選曲へ進む。得点・実績は残さない。
    public static bool TutorialPending;
    // 判定調整との往復だけで引き継ぐ選曲時間。時間切れ・受け取り後は null に戻す。
    public static double? CalibrationSelectionSeconds;
    // 判定調整から戻るときだけ復元する曲と難易度。並び順の変更に備えて名前で保持する。
    public static string CalibrationSelectionSongId;
    public static string CalibrationSelectionDifficulty;

    // 判定オフセット（ミリ秒）。SongSelect でユーザーが調整して PlayerPrefs に保存。
    // GamePlayManager がプレイ開始時にこの値を実効オフセットに加算する。
    // 既定 +60ms: 実測で譜面が曲とずれており、+60ms で合致した(2026-07)。リセット時もこの値へ戻る。
    private const string JudgmentOffsetMsKey = "judgmentOffsetMs";
    public const int JudgmentOffsetMinMs = -1000;
    public const int JudgmentOffsetMaxMs = 1000;
    public const int JudgmentOffsetDefaultMs = 60;

    public static int JudgmentOffsetMs
    {
        get => Mathf.Clamp(PlayerPrefs.GetInt(JudgmentOffsetMsKey, JudgmentOffsetDefaultMs), JudgmentOffsetMinMs, JudgmentOffsetMaxMs);
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

    // ノーツの流れる速度（approachTime, 秒）。小さいほど速い。
    // NoteSpawner.approachTime に適用される。GamePlayManager.Start で読み込み、
    // calibration では UpdateCalibration が毎フレ更新（即時反映）。
    private const string NoteApproachTimeKey = "noteApproachTime";
    public const float NoteApproachTimeMin = 0.5f;
    public const float NoteApproachTimeMax = 4.0f;
    public const float NoteApproachTimeDefault = 1.0f;

    public static float NoteApproachTime
    {
        get => SafeApproachTime(PlayerPrefs.GetFloat(NoteApproachTimeKey, NoteApproachTimeDefault));
        set
        {
            float clamped = SafeApproachTime(value);
            PlayerPrefs.SetFloat(NoteApproachTimeKey, clamped);
            PlayerPrefs.Save();
        }
    }

    private static float SafeApproachTime(float value) => float.IsNaN(value) || float.IsInfinity(value)
        ? NoteApproachTimeDefault : Mathf.Clamp(value, NoteApproachTimeMin, NoteApproachTimeMax);

    public static void ResetNoteApproachTime()
    {
        PlayerPrefs.DeleteKey(NoteApproachTimeKey);
        PlayerPrefs.Save();
    }

    public static void ResetResult()
    {
        AchievementRunId = System.Guid.NewGuid().ToString("N");
        FinalDailyRanking = null;
        FinalScore = 0;
        FinalMaxCombo = 0;
        FinalComboBonus = 0;
        FinalHit = 0;
        FinalMiss = 0;
        FinalPerfect = 0;
        FinalGreat = 0;
        FinalGood = 0;
        FinalBad = 0;
    }
}
