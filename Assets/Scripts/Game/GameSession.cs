using UnityEngine;

// シーン間で譜面選択とリザルトを受け渡す薄い静的ストア。
// 調整値は同じ来場者の曲間でも共有し、タイトルで次の人の標準へ戻す。
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
    // 照準の裏譜面(避、2026-10-24)の回か。FinalPerfect/Great/Good/Miss には的の BULL/INNER/OUTER/MISS が入る。
    public static bool FinalAimMode;
    public static int FinalAimDamage;
    public static int FinalAimDartPoints;
    public static int FinalAimDartTotal;
    // 精度(結果画面の ACCURACY と HI-SCORE の記録)。避は的の点 ÷ 的の満点、ほかは判定の基礎点の割合。
    public static float FinalAccuracy => FinalAimMode
        ? AimDodgeRules.DartAccuracy(FinalAimDartPoints, FinalAimDartTotal)
        : PlayRankHelper.Accuracy(FinalPerfect, FinalGreat, FinalGood, FinalBad, FinalMiss);
    // 結果UIの再構築と、同じ人の次のプレイを区別する保存用ID。
    public static string AchievementRunId;
    public static DailyRankingStore.Result FinalDailyRanking;
    // その回を残す表の名前空間(運営モード、2026-10-29)。曲を始めたとき(ResetResult)の OperatorMode.Namespace。
    // 曲の途中で運営モードを切り替えても、その回は始めたときの表に残す。"" はふだんの表、"fes" は本番用(2台で共有)。
    public static string RunNamespace = "";
    // その回に取った実績の ID。プレイの記録(RankingSync)の ach に入れて相手の台へ送る(この回に AchievementStore で解除した分も足す)。
    // 入れる ID は、この回の AchievementRunId と RunNamespace で Unlock したもの(相手の台は、この回の ID で数に足す)。
    public static System.Collections.Generic.List<string> FinalAchievements = new System.Collections.Generic.List<string>();
    // 記録してよい回の結果が決まり、通算の順位を出したあと・プレイの記録を作って相手の台へ送る前に、その回につき1回だけ
    // (実績を決めて Unlock し、FinalAchievements に入れるため。FinalDailyRanking はもう入っている)。
    public static event System.Action RunRecording;

    // その回に実際にノーツを切った回数(ScoreManager.CutCount)。向き違いで MISS に下がった切りや、ロングの途中の切りも数える。
    public static int FinalCutCount;

    // 記録してよい回か。1回も切っていない回(無人のまま流れた回など)は、実績・通算の順位・HI-SCORE のどれにも残さない
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
    // 残すのは、その回の名前空間(RunNamespace)の表。
    public static void RecordCompletedHighScore()
    {
        if (string.IsNullOrEmpty(SelectedSongId)) return;
        var table = HighScoreStore.Load(RunNamespace, SelectedSongId, SelectedDifficulty);
        HighScoreBestBefore(table.entries.Count > 0 ? table.entries[0].score : 0);
        if (!ShouldRecordRun || !ClaimHighScoreRecord()) return;
        // 同じ回(runId)がもうあれば足さない(台同士の同期の足し直しと重なっても二重にしない)。表が壊れていれば上書きせず、
        // RankingSync が動いている間に足し直す(Failed のとき)。
        var result = HighScoreStore.RecordRun(RunNamespace, SelectedSongId, SelectedDifficulty, new HighScoreEntry
        {
            score = FinalScore,
            rank = PlayRankHelper.Label(PlayRankHelper.FromScore(FinalScore)),
            accuracy = FinalAccuracy,
            date = System.DateTime.Now.ToString("yyyy/MM/dd"),
            // 台同士の同期(RankingSync)で、相手の台から戻ってきた同じ回を二重に足さないため。
            runId = AchievementRunId,
            // 同点の並び(HighScoreStore.Compare)。相手の台へ送った回と同じ時刻にして、2台で同じ並びにする。
            at = completedAtUnixMs > 0 ? completedAtUnixMs : System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, out _, out _);
        if (result == RankingMergeResult.Failed) RankingSync.LocalHighScoreFailed(AchievementRunId);
    }

    // この回を遊び終わった時刻(通算の順位に残したとき。UTC の Unix ミリ秒)。HI-SCORE と同期の記録で同じ値を使う。
    static long completedAtUnixMs;

    // この回の曲が終わった時刻(終わりの演出の前。GamePlayManager.FinishGame)。通算の順位・相手の台へ送る記録・実績で同じ時刻を使う
    // (2台の「となりと同じ曲」を、終わりの演出の長さに左右されずに比べるため)。別の回の時刻は返さない。
    static System.DateTime? runEndedAt;
    static string runEndedRunId;
    public static System.DateTime? RunEndedAt => runEndedAt.HasValue && runEndedRunId == AchievementRunId ? runEndedAt : null;
    public static void MarkRunEnded(System.DateTime localTime)
    {
        if (RunEndedAt.HasValue) return;
        runEndedAt = localTime;
        runEndedRunId = AchievementRunId;
    }

    // 通常終了の確定スコアのみ登録。日付や結果UIが変わっても同じプレイを二重登録しない。
    // 台同士のランキング同期(2026-10-28): 登録した回を RankingSync へ渡し、もう1台へすぐ送る(上の FinalDailyRanking で、この回につき1回だけ)。
    // 同期が無いとき(テスト・Editor の既定・-rankingSync off)は何もしない。
    public static void RecordCompletedDailyRanking()
    {
        if (FinalDailyRanking != null || !ShouldRecordRun) return;
        // 演奏中に届いて待っていたこの曲×難易度の記録を、順位を決める前に表へ足す(結果画面の順位が、届いていた記録を入れた表で決まる)。
        RankingSync.ApplyWaitingFor(RunNamespace, SelectedSongId, SelectedDifficulty);
        var now = RunEndedAt ?? System.DateTime.Now;
        FinalDailyRanking = DailyRankingStore.Record(RunNamespace, SelectedSongId, SelectedDifficulty,
            AchievementRunId, FinalScore, now);
        // この回の実績を決める(担当 B)。決める側の失敗で、順位と同期の記録を止めない。
        try { RunRecording?.Invoke(); }
        catch (System.Exception exception) { Debug.LogException(exception); }
        // ここで通算の表に書けなかった(表が壊れている・保存の失敗)ときも、相手の台へは送り、この台の表には RankingSync が
        // 動いている間に足し直す(FinalDailyRanking は失敗の結果でも入っているので、この回で記録し直すことはない)。
        var play = RankingSyncLog.NewPlay(AchievementRunId, SelectedSongId, SelectedDifficulty, FinalScore,
            PlayRankHelper.Label(PlayRankHelper.FromScore(FinalScore)), FinalAccuracy, now, PhoneSaberStation.Read(),
            RunNamespace, RunAchievementIds());
        completedAtUnixMs = play.at;
        RankingSync.RecordLocal(play, FinalDailyRanking.Available);
    }

    // 相手の台へ送る、この回の実績: FinalAchievements と、この回に解除した記録(AchievementStore。選曲で取った分など)。重ねない。
    static System.Collections.Generic.List<string> RunAchievementIds()
    {
        var ids = new System.Collections.Generic.List<string>();
        if (FinalAchievements != null)
            foreach (string id in FinalAchievements)
                if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
        if (!string.IsNullOrEmpty(AchievementRunId))
            foreach (string id in AchievementStore.UnlockedIn(RunNamespace, AchievementRunId))
                if (!ids.Contains(id)) ids.Add(id);
        return ids;
    }

    public static void RecordCompletedAchievements()
    {
        if (!ShouldRecordRun) return;
        SongAchievementStore.Record(RunNamespace, SelectedSongId, SelectedDifficulty, AchievementRunId,
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
    // チュートリアルで「いいえ」を選んだ人も、スマホで遊ぶときは振る範囲を合わせる段だけ通る
    // (2026-10-26 ユーザー「チュートリアルがなくても範囲検査は行うようにして」)。TutorialPending と一緒に立て、読んだら消す。
    public static bool TutorialRangeOnly;
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

    // 画面遷移テストでは判定値・速度もメモリー内だけで扱う。
    // タイトルでのリセットを通しても、本物の保存値を消さない。
    // 判定調整の下書き・出力先別の値(CalibrationDraft の3キー)は差し替えない。判定調整を往復するテストでは使わないこと。
    static bool playerSettingsForTest;
    static int testJudgmentOffset;
    static float testApproachTime;
    public static void SetPlayerSettingsForTest(bool enabled)
    {
        playerSettingsForTest = enabled;
        testJudgmentOffset = JudgmentOffsetDefaultMs;
        testApproachTime = NoteApproachTimeDefault;
    }

    public static int JudgmentOffsetMs
    {
        get => playerSettingsForTest ? testJudgmentOffset
            : Mathf.Clamp(PlayerPrefs.GetInt(JudgmentOffsetMsKey, JudgmentOffsetDefaultMs), JudgmentOffsetMinMs, JudgmentOffsetMaxMs);
        set
        {
            int clamped = Mathf.Clamp(value, JudgmentOffsetMinMs, JudgmentOffsetMaxMs);
            if (playerSettingsForTest) { testJudgmentOffset = clamped; return; }
            PlayerPrefs.SetInt(JudgmentOffsetMsKey, clamped);
            PlayerPrefs.Save();
        }
    }

    public static void ResetJudgmentOffset()
    {
        if (playerSettingsForTest) { testJudgmentOffset = JudgmentOffsetDefaultMs; return; }
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
        get => playerSettingsForTest ? testApproachTime
            : SafeApproachTime(PlayerPrefs.GetFloat(NoteApproachTimeKey, NoteApproachTimeDefault));
        set
        {
            float clamped = SafeApproachTime(value);
            if (playerSettingsForTest) { testApproachTime = clamped; return; }
            PlayerPrefs.SetFloat(NoteApproachTimeKey, clamped);
            PlayerPrefs.Save();
        }
    }

    private static float SafeApproachTime(float value) => float.IsNaN(value) || float.IsInfinity(value)
        ? NoteApproachTimeDefault : Mathf.Clamp(value, NoteApproachTimeMin, NoteApproachTimeMax);

    public static void ResetNoteApproachTime()
    {
        if (playerSettingsForTest) { testApproachTime = NoteApproachTimeDefault; return; }
        PlayerPrefs.DeleteKey(NoteApproachTimeKey);
        PlayerPrefs.Save();
    }

    // 文化祭の1プレイごとの初期値。調整画面との往復や曲の開始では呼ばず、その回の調整は維持する。
    // 出力先別の保存値も消し、切替操作で前の人の判定値が復活しないようにする。
    // 端末の位置補正・表示設定・スコア記録は対象外。
    public static void ResetPlayerSettings()
    {
        if (playerSettingsForTest)
        {
            ResetJudgmentOffset();
            ResetNoteApproachTime();
            return;
        }
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
        RunNamespace = OperatorMode.Namespace;
        FinalAchievements = new System.Collections.Generic.List<string>();
        FinalDailyRanking = null;
        completedAtUnixMs = 0;
        runEndedAt = null;
        runEndedRunId = null;
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
        FinalAimMode = false;
        FinalAimDamage = FinalAimDartPoints = FinalAimDartTotal = 0;
    }
}
