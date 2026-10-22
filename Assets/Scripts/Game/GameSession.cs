using UnityEngine;

// シーン間で譜面選択とリザルトを受け渡す薄い静的ストア。
// 調整値はその回の画面間で共有し、タイトルまたは結果からの新しい選曲で標準へ戻す。
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

    // その回に実際にノーツを切った回数(ScoreManager.CutCount)。向き違いで MISS に下がった切りや、ロングの途中の切りも数える。
    public static int FinalCutCount;

    // 記録してよい回か。1回も切っていない回(無人のまま流れた回など)は、実績・本日のランキング・HI-SCORE のどれにも残さない
    // (仕様面の点検 1、2026-10-21)。判定調整と、数が壊れている回も残さない。
    // 切った回数を持たない経路(結果画面だけを開くテストなど)でも、MISS 以外の判定があれば切った回とみなす。
    public static bool ShouldRecordRun =>
        !IsCalibrationMode && FinalPerfect >= 0 && FinalGreat >= 0 && FinalGood >= 0 && FinalBad >= 0 && FinalMiss >= 0
        && (FinalCutCount > 0 || (long)FinalPerfect + FinalGreat + FinalGood + FinalBad > 0);

    // 最後まで遊んだが、1回も切っていないので記録しなかった回(結果画面で理由を出す)。
    public static bool CompletedWithoutRecord => !IsCalibrationMode && FinalMiss > 0 && !ShouldRecordRun;

    // HI-SCORE は結果画面が残す。同じ回(AchievementRunId)の結果画面が作り直されても二重に残さず、
    // NEW RECORD の比べる相手(残す前の最高点)も最初に読んだ値のままにする。回の ID が無いときは従来どおり毎回残す。
    static string highScoreRunId, highScoreBestRunId;
    static int highScoreBestBefore;
    public static bool ClaimHighScoreRecord()
    {
        if (string.IsNullOrEmpty(AchievementRunId)) return true;
        if (highScoreRunId == AchievementRunId) return false;
        highScoreRunId = AchievementRunId;
        return true;
    }
    public static int HighScoreBestBefore(int storedBest)
    {
        if (string.IsNullOrEmpty(AchievementRunId)) return storedBest;
        if (highScoreBestRunId != AchievementRunId) { highScoreBestRunId = AchievementRunId; highScoreBestBefore = storedBest; }
        return highScoreBestBefore;
    }

    // HI-SCORE を残す(結果画面と、終わりの演出を Esc 2回で抜けたとき)。同じ回は一度だけ、1回も切っていない回は残さない。
    public static void RecordCompletedHighScore()
    {
        if (string.IsNullOrEmpty(SelectedSongId)) return;
        var table = HighScoreStore.Load(SelectedSongId, SelectedDifficulty);
        HighScoreBestBefore(table.entries.Count > 0 ? table.entries[0].score : 0);
        if (!ShouldRecordRun || !ClaimHighScoreRecord()) return;
        HighScoreStore.Record(SelectedSongId, SelectedDifficulty, new HighScoreEntry
        {
            score = FinalScore,
            rank = PlayRankHelper.Label(PlayRankHelper.FromScore(FinalScore)),
            accuracy = PlayRankHelper.Accuracy(FinalPerfect, FinalGreat, FinalGood, FinalBad, FinalMiss),
            date = System.DateTime.Now.ToString("yyyy/MM/dd"),
        }, out _);
    }

    // 通常終了の確定スコアのみ登録。日付や結果UIが変わっても同じプレイを二重登録しない。
    public static void RecordCompletedDailyRanking()
    {
        if (FinalDailyRanking != null || !ShouldRecordRun) return;
        FinalDailyRanking = DailyRankingStore.Record(SelectedSongId, SelectedDifficulty,
            AchievementRunId, FinalScore, System.DateTime.Now);
    }

    public static void RecordCompletedAchievements()
    {
        if (!ShouldRecordRun) return;
        SongAchievementStore.Record(SelectedSongId, SelectedDifficulty, AchievementRunId,
            FinalPerfect, FinalGreat, FinalGood, FinalBad, FinalMiss, FinalScore);
    }

    // 選曲で来場者が操作したか(仕様面の点検 1)。新しい選曲(タイトル・練習から)で消し、判定調整との往復では保つ。
    // 立てるのは選曲の操作(来場者のキー・マウスのボタン・ゲームパッド・照準を的に0.4秒・発射・曲や難易度の変更・RUSH E)と
    // 判定調整に入ったとき。Esc と運営のキーは数えない。
    // 選曲の時間切れで操作がなければ、無人とみなして曲を始めずにタイトルへ戻す(操作した人は今どおり自動で始まる)。
    public static bool SongSelectOperated;

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
    // 音の出力先が変わって選曲を作り直すとき・判定調整から選曲へ戻るとき、RUSH E の秘密の E を解除したままにする(2026-10-22)。
    public static bool RushESecretCarry;

    // 判定オフセット（ミリ秒）。SongSelect でユーザーが調整して PlayerPrefs に保存。
    // GamePlayManager がプレイ開始時にこの値を実効オフセットに加算する。
    // 既定 -20ms: 2026-10-13 の +30ms から「ノーツを早く」をさらに5回分（-10ms×5）早める（2026-10-20、
    // 実機で当たりが中央値 35ms 早かったため）。リセット時もこの値へ戻る。
    private const string JudgmentOffsetMsKey = "judgmentOffsetMs";
    public const int JudgmentOffsetMinMs = -1000;
    public const int JudgmentOffsetMaxMs = 1000;
    public const int JudgmentOffsetDefaultMs = -20;

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

    // 文化祭の1プレイごとの初期値。調整画面との往復や曲の開始では呼ばず、その回の調整は維持する。
    // 出力先別の保存値も消し、切替操作で前の人の判定値が復活しないようにする。
    // 端末の位置補正・表示設定・スコア記録は対象外。
    public static void ResetPlayerSettings()
    {
        PlayerPrefs.DeleteKey(JudgmentOffsetMsKey);
        PlayerPrefs.DeleteKey(NoteApproachTimeKey);
        PlayerPrefs.DeleteKey(CalibrationDraft.ActiveKey);
        PlayerPrefs.DeleteKey(CalibrationDraft.SpeakerKey);
        PlayerPrefs.DeleteKey(CalibrationDraft.HeadphoneKey);
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
        FinalCutCount = 0;
    }
}
