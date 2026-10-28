using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// 解除した実績1つと、その番号(○人目。記録できなかったときは 0 で、番号は出さない)。
public readonly struct AchievementUnlock
{
    public readonly AchievementCatalog.Definition Definition;
    public readonly int Number;
    public AchievementUnlock(AchievementCatalog.Definition definition, int number) { Definition = definition; Number = number; }
    public string Id => Definition != null ? Definition.Id : "";
    public string Text => Definition != null ? Definition.Text : "";
}

// 実績の判定(2026-10-29、担当 B)。起動時に1つだけ作り、シーンをまたいで残す。テスト中(AutoStartEnabled が false)・
// テストや試験(smoke・soak・遅延と追従の計測)の起動では作らない。
// ・本編の曲の間は PlayStatsRecorder で演奏を記録する(判定調整・練習では記録しない。曲を始め直したら作り直す)。
// ・選曲で曲を決めた秒と、練習の終わり方を覚えておく(AchievementRunInfo。次に始めた曲が使い切る)。
// ・結果が決まったら(GameSession.RunRecording。記録してよい回だけ、プレイの記録を作って相手の台へ送る前に1回)、全部の条件を見て
//   AchievementStore.Unlock(その回の名前空間・回の ID)し、ID を GameSession.FinalAchievements に入れる(clear を先に)。
//   演奏中には出さず、番号つきで覚えておき、結果画面のランクの札が着地したとき(演出を飛ばしたらそのとき)に上に出す(AchievementToast)。
//   結果画面に行かなかった回は出さない。
public sealed class AchievementTracker : MonoBehaviour
{
    public const string GameScene = "Game", ResultScene = "Result", SongSelectScene = "SongSelect", TitleScene = "Title";
    // Test Runner の窓から回したテストでも作らない(RankingSync と同じ)。
    const string TestRunnerObjectName = "Code-based tests runner";

    public static AchievementTracker Instance { get; private set; }

    bool autoStarted;
    // 今のシーンで見ているもの。
    GamePlayManager manager;
    PlayStatsRecorder recorder;
    bool practiceRecorded;
    SongSelectController select;
    bool selectionTaken, calibrationReturn;
    // 決めた回と、結果画面で出す分。結果の演出(ResultReveal)の時計を聞いて出し始める。
    string decidedRun, pendingRun;
    List<AchievementUnlock> pending;
    ResultReveal reveal;
    double resultEnteredAt;
    // 結果画面に結果の演出が無い(作れなかった)ときは、この秒だけ待って出す。
    const double RevealWaitSeconds = 1;

    // 最後に決めた回の実績(表示の順。テストと確認用)。
    public IReadOnlyList<AchievementUnlock> LastUnlocks { get; private set; } = Array.Empty<AchievementUnlock>();
    public PlayStats CurrentStats => recorder != null ? recorder.Stats : null;
    // 今の曲の記録に、今の X 斬り・コンボ・ボルテージ・しびれを写す(テストと確認用。結果が決まったときと同じ)。
    public PlayStats SnapshotNow() => recorder != null ? recorder.Snapshot() : null;

    // 同期の記録(この台と相手の台の回。RankingSync.KnownPlays は読むたびに写すので、1回の結果で1回だけ読む)。テストで差し替える。
    public static Func<IReadOnlyList<RankingPlay>> PlaysSource = SyncedPlays;
    // その名前空間で、ある時刻から後に clear を取った回の数(一番乗り。2台合わせて。-1 は分からない)。テストで差し替える。
    public static Func<string, long, int> ClearsSince = DefaultClearsSince;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        PlaysSource = SyncedPlays;
        ClearsSince = DefaultClearsSince;
    }

    static IReadOnlyList<RankingPlay> SyncedPlays() => RankingSync.KnownPlays;

    static int DefaultClearsSince(string ns, long sinceUnixMs) => AchievementStore.CountSince(ns, AchievementCatalog.ClearId, sinceUnixMs);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled || !EnabledHere(Environment.GetCommandLineArgs())) return; // テスト中は作らない
        if (Instance != null || GameObject.Find(TestRunnerObjectName) != null) return;
        var go = new GameObject("AchievementTracker");
        DontDestroyOnLoad(go);
        go.AddComponent<AchievementTracker>().autoStarted = true;
    }

    // この起動で実績を決めるか。テストを回す起動(-runTests)と試験の起動(smoke・soak・遅延と追従の計測)では決めない
    // (soak は表だけ戻して実績の記録は戻さないため)。
    public static bool EnabledHere(string[] args)
    {
        if (args == null) return true;
        foreach (string arg in args)
            if (arg == RankingSync.TestRunArgument || arg == PhoneSaberPlayerSmoke.Argument || arg == PhoneSaberSoakModel.Argument
                || arg == PhoneSaberLatencyProbe.AutoArgument || arg == PhoneSaberTrackingProbe.Argument) return false;
        return true;
    }

    // テスト用: 自動では作らない場面で1つ作る(シーンをまたいで残す。テストの後で消すこと)。
    public static AchievementTracker CreateForTest()
    {
        var go = new GameObject("AchievementTracker (test)");
        if (Application.isPlaying) DontDestroyOnLoad(go);
        var tracker = go.AddComponent<AchievementTracker>();
        if (Instance == null) Instance = tracker;
        return tracker;
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
    }

    void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        GameSession.RunRecording += OnRunRecording;
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        GameSession.RunRecording -= OnRunRecording;
    }

    void OnDestroy()
    {
        ResetSceneState();
        if (Instance == this) Instance = null;
    }

    // 作ったときに開いていたシーン(起動の最初のシーン)も、読み込んだときと同じに扱う(覚えている知らせは消さない)。
    void Start() => EnterScene(SceneManager.GetActiveScene().name);

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single) return;
        // 結果画面のほかへ移ったら、出していない知らせは出さない(Esc 2回でタイトルへ抜けた回など)。
        if (scene.name != ResultScene) DropPending();
        EnterScene(scene.name);
    }

    void EnterScene(string scene)
    {
        ResetSceneState();
        resultEnteredAt = Time.realtimeSinceStartupAsDouble;
        if (scene == TitleScene) AchievementRunInfo.BeginVisitor();
        // 判定調整から戻った選曲か(選曲の見た目 SongSelectSkin が1コマあとに読んで消す前に見る)。
        if (scene == SongSelectScene) calibrationReturn = GameSession.CalibrationSelectionSeconds.HasValue;
    }

    void ResetSceneState()
    {
        if (recorder != null) recorder.Unbind();
        recorder = null;
        StopWatchingReveal();
        manager = null;
        practiceRecorded = false;
        select = null;
        selectionTaken = false;
    }

    void DropPending()
    {
        pending = null;
        pendingRun = null;
    }

    void Update()
    {
        // 作ったあとにテストが始まった(SetUpFixture が AutoStartEnabled を切った)ら、すぐ消える。
        if (autoStarted && !PhoneSaberP2PBridgeProcess.AutoStartEnabled) { Destroy(gameObject); return; }
        string scene = SceneManager.GetActiveScene().name;
        if (scene == GameScene) WatchGame();
        else if (scene == SongSelectScene) WatchSongSelect();
        else if (scene == ResultScene) ShowPending();
    }

    // 本編の曲が準備できたら記録を結び付ける。練習は終わり方だけを覚える。判定調整は見ない。
    void WatchGame()
    {
        if (manager == null)
        {
            manager = FindFirstObjectByType<GamePlayManager>();
            if (manager == null) return;
        }
        if (GameSession.IsCalibrationMode || manager.Calibration != null) return;
        var tutorial = manager.Tutorial;
        if (tutorial != null)
        {
            if (!practiceRecorded && tutorial.IsFinished)
            {
                practiceRecorded = true;
                AchievementRunInfo.PracticeFinished(tutorial.RangeOnly, tutorial.FinishReason, tutorial.Records);
            }
            return;
        }
        if (recorder != null && !recorder.BoundTo(manager)) { recorder.Unbind(); recorder = null; }
        string run = GameSession.AchievementRunId;
        if (recorder == null) recorder = PlayStatsRecorder.TryBind(manager, run);
        if (recorder == null) return;
        // 曲を始め直した(回が変わった)ら、記録を作り直す(譜面の置き直しは記録の側が OnChartReset で作り直す)。
        if (!recorder.Stale && recorder.Stats.RunId != run) recorder.Rebuild(run);
        if (run != AchievementRunInfo.RunId) AchievementRunInfo.BeginRun(run);
        recorder.Tick();
    }

    // 選曲で曲を決めた瞬間の残り時間を覚える(決めたあとは幕の間なので残り時間は進まない)。
    void WatchSongSelect()
    {
        if (selectionTaken) return;
        if (select == null)
        {
            select = FindFirstObjectByType<SongSelectController>();
            if (select == null) return;
        }
        if (!select.StartAccepted) return;
        selectionTaken = true;
        var skin = FindFirstObjectByType<SongSelectSkin>();
        AchievementRunInfo.SongChosen(skin != null && skin.IsReady ? skin.RemainingSeconds : (double?)null, calibrationReturn);
    }

    // 結果画面: 結果の演出の時計がランクの札の着地(演出を飛ばすと 999)に届いたら出す。演出は幕の間は進まない。
    void ShowPending()
    {
        if (pending == null) return;
        if (pendingRun != GameSession.AchievementRunId || pending.Count == 0) { DropPending(); return; }
        if ((object)reveal != null) return;
        reveal = FindFirstObjectByType<ResultReveal>();
        if (reveal != null) reveal.TimeChanged += RevealAdvanced;
        else if (Time.realtimeSinceStartupAsDouble - resultEnteredAt >= RevealWaitSeconds) ShowNow();
    }

    void RevealAdvanced(float time)
    {
        if (time >= AchievementToast.ResultLandingSeconds) ShowNow();
    }

    void ShowNow()
    {
        if (pending != null && pending.Count > 0) AchievementToast.Show(pending);
        DropPending();
        StopWatchingReveal();
    }

    void StopWatchingReveal()
    {
        if ((object)reveal != null) reveal.TimeChanged -= RevealAdvanced;
        reveal = null;
    }

    // GameSession.RunRecording(記録してよい回の結果が決まり、通算の順位を出したあと)。その回につき1回だけ。
    void OnRunRecording()
    {
        // 自動で作ったあとにテストが始まった(まだ消える前のコマ)なら、テストの回を決めない。
        if (autoStarted && !PhoneSaberP2PBridgeProcess.AutoStartEnabled) return;
        if (!GameSession.ShouldRecordRun) return;
        string run = GameSession.AchievementRunId;
        if (string.IsNullOrEmpty(run) || run == decidedRun) return;
        decidedRun = run;
        var stats = recorder != null && !recorder.Stale && recorder.Stats.RunId == run ? recorder.Snapshot() : null;
        LastUnlocks = Record(BuildContext(stats, DateTime.Now));
        pending = new List<AchievementUnlock>(LastUnlocks);
        pendingRun = run;
    }

    // 今の回の結果(GameSession)・演奏の記録・その回の情報・解除と同期の記録から、条件が見る写しを作る。
    // 曲を終えた時刻は、曲が終わったとき(終わりの演出の前。GameSession.RunEndedAt で、相手の台へ送る記録の時刻と同じ。
    // 無ければ演奏の記録が覚えたコマ)。分からなければ nowLocal。
    public static AchievementContext BuildContext(PlayStats stats, DateTime nowLocal)
    {
        var ranking = GameSession.FinalDailyRanking;
        string run = GameSession.AchievementRunId ?? "";
        string ns = GameSession.RunNamespace ?? "";
        AchievementRunInfo.ForRun(run, out var selection, out bool practiced);
        var plays = PlaysSource != null ? PlaysSource() ?? Array.Empty<RankingPlay>() : Array.Empty<RankingPlay>();
        DateTime ended = GameSession.RunEndedAt ?? (stats != null && stats.EndedAt.HasValue ? stats.EndedAt.Value : nowLocal);
        return new AchievementContext
        {
            SongId = GameSession.SelectedSongId ?? "",
            Difficulty = GameSession.SelectedDifficulty ?? "",
            Score = GameSession.FinalScore,
            MaxCombo = GameSession.FinalMaxCombo,
            Hit = GameSession.FinalHit,
            Miss = GameSession.FinalMiss,
            Perfect = GameSession.FinalPerfect,
            Great = GameSession.FinalGreat,
            Good = GameSession.FinalGood,
            Bad = GameSession.FinalBad,
            CutCount = GameSession.FinalCutCount,
            Accuracy = GameSession.FinalAccuracy,
            AimMode = GameSession.FinalAimMode,
            AimDamage = GameSession.FinalAimDamage,
            RankingAvailable = ranking != null && ranking.Available,
            DailyRank = ranking != null ? ranking.Rank : 0,
            TotalPlays = ranking != null ? ranking.TotalPlays : 0,
            TiedPlays = ranking != null ? ranking.TiedPlays : 0,
            Stats = stats,
            StartedLocal = stats != null && stats.StartedAt.HasValue ? stats.StartedAt.Value : ended,
            EndedLocal = ended,
            EndedUnixMs = new DateTimeOffset(ended).ToUnixTimeMilliseconds(),
            Station = PhoneSaberStation.Read(),
            Namespace = ns,
            RunId = run,
            PracticedBefore = practiced,
            SelectionSeconds = selection,
            ClearsToday = ClearsSince != null ? ClearsSince(ns, new DateTimeOffset(ended.Date).ToUnixTimeMilliseconds()) : -1,
            Plays = plays,
        };
    }

    // 写し context で実績を決めて解除し、番号つきで返す(表示の順)。ID は GameSession.FinalAchievements に入れる(clear を先に。
    // 相手の台へ送る記録の数に上限があっても、来場の通し番号になる clear を落とさない)。同じ回で2回呼んでも数は増えない。
    // clear を先に解除して、その番号でキリ番を見る。残りはまとめて解除する(保存は合わせて2回まで)。
    public static List<AchievementUnlock> Record(AchievementContext context)
    {
        var unlocks = new List<AchievementUnlock>();
        if (context == null || string.IsNullOrEmpty(context.RunId)) return unlocks;
        var earned = AchievementCatalog.Earned(context);
        long at = context.EndedUnixMs > 0 ? context.EndedUnixMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var clear = earned.Find(d => d.Id == AchievementCatalog.ClearId);
        int clearNumber = 0;
        if (clear != null)
        {
            clearNumber = Unlock(context, clear.Id, at);
            context.ClearNumber = clearNumber;
            var kiriban = AchievementCatalog.Find(AchievementCatalog.KiribanId);
            if (kiriban != null && !earned.Contains(kiriban) && kiriban.Met(context))
            {
                earned.Add(kiriban);
                earned.Sort((a, b) => a.Order.CompareTo(b.Order));
            }
        }
        var rest = new List<string>();
        foreach (var definition in earned) if (definition != clear) rest.Add(definition.Id);
        int[] numbers = AchievementStore.UnlockMany(context.Namespace, context.RunId, rest, context.Station, at);
        if (GameSession.FinalAchievements == null) GameSession.FinalAchievements = new List<string>();
        var ids = GameSession.FinalAchievements;
        if (clear != null && !ids.Contains(clear.Id)) ids.Insert(0, clear.Id);
        int k = 0;
        foreach (var definition in earned)
        {
            int number = definition == clear ? clearNumber : numbers[k++];
            unlocks.Add(new AchievementUnlock(definition, number));
            if (!ids.Contains(definition.Id)) ids.Add(definition.Id);
        }
        return unlocks;
    }

    static int Unlock(AchievementContext context, string id, long at) =>
        AchievementStore.Unlock(context.Namespace, context.RunId, id, context.Station, at);
}
