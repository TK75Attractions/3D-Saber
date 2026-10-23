using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 実 Game / 実 UDP / 実曲で、音の読み直し中に以前届いていた両色を見失う並びと、送り続ける対照を確認する。
// 導入: build 終了後、このファイルを Assets/Tests/PlayMode/AudioRecoveryTrackingPlayTests.cs としてコピー。
// まず未修正で Loss=fail / Continued=pass と completion の CueDelta=1 / ResumeLogs=1 を確認する。
// 次に Opus23 の「完了時だけ最新受信を Observe + Step」差分で両方 pass を確認する。
// 音と時計の差は診断値だけ。聞こえる音漏れ・Miss 増加・音ずれをこの試験の結果だけで主張しない。
public sealed class AudioRecoveryTrackingPlayTests
{
    const string Song = "Epilogue", Difficulty = "Normal";
    const string ResumePrefix = "GamePlayManager: 剣の位置が戻ったので再開";
    const BindingFlags StaticFields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags InstanceFields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    readonly Dictionary<FieldInfo, object> savedSession = new Dictionary<FieldInfo, object>();
    readonly Dictionary<FieldInfo, object> savedHaptic = new Dictionary<FieldInfo, object>();
    readonly Dictionary<FieldInfo, object> savedRecovery = new Dictionary<FieldInfo, object>();
    readonly List<Pref> savedPrefs = new List<Pref>();
    readonly HashSet<int> ports = new HashSet<int>();
    InputPoint input, originalInput;
    UdpImuBridge imu, originalImu;
    GamePlayManager game;
    GameObject inputRoot, imuRoot;
    Keyboard keyboard, originalKeyboard;
    Mouse mouse, originalMouse;
    Gamepad gamepad, originalGamepad;
    InputSettings originalSettings, testSettings;
    ImuBleServiceSettings bleSettings;
    FieldInfo bleAutoStart;
    bool originalBleAutoStart, originalP2PAutoStart, originalIgnoreZones;
    bool snapshotsTaken, hooksAdded;
    float originalTimeScale;
    PhoneSaberEventLog originalEventLog;
    Component originalTransition;
    UdpClient sender, commandSink;
    int resumeLogs;
    static readonly byte[] Packet = Encoding.ASCII.GetBytes("-0.2,-0.6,0.2,-0.6");

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        // 以前から動いているシーンの試合/受信機は破壊しない。通常の Test Runner の空シーンから実行する。
        // DDOL の受信機は残し、Instance だけを試験専用受信機に一時的に向ける。
        double deadline = Now + 5;
        while (ScreenTransition.IsBusy && Now < deadline) yield return null;
        Assert.False(ScreenTransition.IsBusy, "試験前の画面遷移が終了している");
        Assert.False(BleBridgeAutoStartPolicy.IsGameFlowScene(SceneManager.GetActiveScene().path),
            "実運用中の Title/SongSelect/Game/Result ではなく Test Runner の空シーンで実行する");
        Assert.IsNull(Object.FindFirstObjectByType<GamePlayManager>(), "進行中の本物の試合がない Test Runner の空シーンで実行する");
        foreach (var old in Object.FindObjectsByType<InputPoint>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            Assert.AreEqual("DontDestroyOnLoad", old.gameObject.scene.name, "既存のシーン所有受信機を巻き込まない");
        foreach (var old in Object.FindObjectsByType<UdpImuBridge>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            Assert.AreEqual("DontDestroyOnLoad", old.gameObject.scene.name, "既存のシーン所有 IMU を巻き込まない");

        CaptureStatics(typeof(GameSession), savedSession); // private の重複記録ガードもすべて含む。
        CaptureStatics(typeof(Haptic), savedHaptic);
        foreach (string name in new[] { "pending", "lastChangeAt", "<RecoveryCount>k__BackingField", "<LastAction>k__BackingField", "OutputChanged", "Restored" })
        {
            var field = RequiredField(typeof(AudioOutputRecovery), name, StaticFields);
            savedRecovery.Add(field, field.GetValue(null));
        }
        SavePref("noteApproachTime", PrefKind.Float);
        SavePref("judgmentOffsetMs", PrefKind.Int);
        // Esc 2 回で Title に戻る際、ResetPlayerSettings がこの 3 キーも消す。
        SavePref(CalibrationDraft.ActiveKey, PrefKind.Int);
        SavePref(CalibrationDraft.SpeakerKey, PrefKind.Int);
        SavePref(CalibrationDraft.HeadphoneKey, PrefKind.Int);
        SavePref(HighScoreStore.Key(Song, Difficulty), PrefKind.String);
        SavePref(SongAchievementStore.Key(Song, Difficulty), PrefKind.String);
        SavePref(DailyRankingStore.Key(Song, Difficulty), PrefKind.String);
        originalInput = InputPoint.Instance;
        originalImu = UdpImuBridge.Instance;
        originalEventLog = PhoneSaberEventLog.Current;
        originalP2PAutoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        originalIgnoreZones = PhoneSaberIgnoreZones.Enabled;
        originalTimeScale = Time.timeScale;
        originalTransition = (Component)RequiredField(typeof(ScreenTransition), "instance", StaticFields).GetValue(null);
        originalSettings = InputSystem.settings;
        originalKeyboard = Keyboard.current;
        originalMouse = Mouse.current;
        originalGamepad = Gamepad.current;
        bleSettings = Resources.Load<ImuBleServiceSettings>("ImuBleServiceSettings");
        bleAutoStart = RequiredField(typeof(ImuBleServiceSettings), "autoStartBleBridge", InstanceFields);
        if (bleSettings != null) originalBleAutoStart = (bool)bleAutoStart.GetValue(bleSettings);
        snapshotsTaken = true;

        PhoneSaberP2PBridgeProcess.AutoStartEnabled = false; // P2P/triage/discovery の自動起動も停止。
        PhoneSaberEventLog.Current = null;                 // 実運用の診断保存先へ書かない。
        PhoneSaberIgnoreZones.Enabled = false;
        if (bleSettings != null) bleAutoStart.SetValue(bleSettings, false); // メモリ上だけ。Asset は保存しない。
        Time.timeScale = 1;
        RequiredField(typeof(AudioOutputRecovery), "pending", StaticFields).SetValue(null, false);
        // 模擬変更を試験外の既存購読者へ伝えない。試験中に作る Game/Title の購読だけが対象。
        RequiredField(typeof(AudioOutputRecovery), "OutputChanged", StaticFields).SetValue(null, null);
        RequiredField(typeof(AudioOutputRecovery), "Restored", StaticFields).SetValue(null, null);

        testSettings = Object.Instantiate(originalSettings);
        InputSystem.settings = testSettings;
        testSettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        testSettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        keyboard = InputSystem.AddDevice<Keyboard>();
        mouse = InputSystem.AddDevice<Mouse>();
        gamepad = InputSystem.AddDevice<Gamepad>();
        InputSystem.QueueStateEvent(mouse, new MouseState { position = Vector2.zero });

        // 古い Haptic のソケット/送り先を所有しない。新しい bridge の終了処理からも切り離す。
        foreach (var field in savedHaptic.Keys)
            field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
        SetInstance(typeof(InputPoint), null);
        inputRoot = new GameObject("AudioRecoveryTrackingInput");
        inputRoot.SetActive(false);
        Object.DontDestroyOnLoad(inputRoot);
        input = inputRoot.AddComponent<InputPoint>();
        input.port = FreePort();
        input.port2 = FreePort();
        inputRoot.SetActive(true);
        InputPoint.EnsureInstance();
        // 実利用者の位置補正値は書き換えず、この受信機だけ生の端点を使う。
        RequiredField(typeof(InputPoint), "positionCalibrationEnabled", InstanceFields).SetValue(input, false);

        // Bootstrap が Game/Title 読込時に EnsurePersistent するので、先に専用 bridge を置く。
        SetInstance(typeof(UdpImuBridge), null);
        commandSink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int sinkPort = ((IPEndPoint)commandSink.Client.LocalEndPoint).Port;
        ports.Add(sinkPort);
        imuRoot = new GameObject("AudioRecoveryTrackingImu");
        imu = imuRoot.AddComponent<UdpImuBridge>();
        imu.ConfigureForTests(FreePort());
        RequiredField(typeof(UdpImuBridge), "commandPort", InstanceFields).SetValue(imu, sinkPort);
        sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        GameSession.SelectedSongId = Song;
        GameSession.SelectedSongTitle = Song;
        GameSession.SelectedDifficulty = Difficulty;
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.NoteApproachTime = 4;
        GameSession.JudgmentOffsetMs = 0;
        Application.logMessageReceived += OnLog;
        SceneManager.sceneLoaded += ConfigureGame;
        hooksAdded = true;
        yield return SceneManager.LoadSceneAsync("Game");
    }

    void ConfigureGame(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Game") return;
        foreach (var root in scene.GetRootGameObjects())
        {
            var found = root.GetComponentInChildren<GamePlayManager>(true);
            if (found == null) continue;
            game = found;
            game.enableStartCountdown = true;
            game.autoEnsureSwing8DirectionLogger = false;
            break;
        }
    }

    void OnLog(string message, string stackTrace, LogType type)
    {
        if (message.StartsWith(ResumePrefix, StringComparison.Ordinal)) resumeLogs++;
    }

    [UnityTest]
    public IEnumerator LostTrackingDuringAudioReloadWaitsWithoutAFalseResume() => Verify(false);

    [UnityTest]
    public IEnumerator ContinuedTrackingDuringAudioReloadResumesOnceUsingLatestInput() => Verify(true);

    IEnumerator Verify(bool keepSending)
    {
        string label = keepSending ? "continued" : "loss";
        double deadline = Now + 25, clockStart = double.NaN;
        int samplesStart = -1;
        AudioSource source = null;
        bool primed = false;
        while (Now < deadline)
        {
            SendBoth();
            yield return null;
            if (game == null || game.songPlayer == null || game.noteSpawner == null || game.TrackingView == null) continue;
            source = game.songPlayer.GetComponent<AudioSource>();
            bool valid = game.songPlayer.IsPlaying && !game.songPlayer.IsPaused &&
                game.songPlayer.SongTime >= Math.Max(.25, game.songPlayer.PlayStartSeconds) &&
                source != null && source.clip != null && source.isPlaying && source.timeSamples > 0 &&
                game.TrackingWatch.Seen(0) && game.TrackingWatch.Seen(1) &&
                game.TrackingWatch.State == PlayTrackingWatch.Phase.Playing &&
                input.HasValidStickEndpoints && input.HasValidStickEndpoints2 &&
                Now - input.LastReceivedTime < .25 && Now - input.LastReceivedTime2 < .25 &&
                PlayTrackingWatch.LiveNeeds(game.noteSpawner.LiveNotes, SaberHand.Any);
            if (!valid) { clockStart = double.NaN; samplesStart = -1; continue; }
            if (double.IsNaN(clockStart)) { clockStart = game.songPlayer.SongTime; samplesStart = source.timeSamples; }
            if (game.songPlayer.SongTime - clockStart >= .08 && source.timeSamples > samplesStart)
            { primed = true; break; }
        }
        Debug.Log("[AUDIO_RECOVERY_TRACKING] " + label + " primed=" + primed +
            " seen=" + (game != null && game.TrackingWatch.Seen(0)) + "," + (game != null && game.TrackingWatch.Seen(1)) +
            " song=" + (game != null && game.songPlayer != null ? F(game.songPlayer.SongTime) : "missing") +
            " samples=" + (source != null ? source.timeSamples : -1));
        Assert.True(primed, "両色を実 UDP で正常受信済み、実曲と時計が進み、未判定ノーツが画面にある並びを作る");
        Assert.AreSame(input, InputPoint.Instance);
        Assert.AreEqual(Song, GameSession.SelectedSongId);
        Assert.AreEqual(Difficulty, GameSession.SelectedDifficulty);
        string runId = GameSession.AchievementRunId;
        int missesBefore = game.scoreManager.MissCount;
        resumeLogs = 0;

        AudioOutputRecovery.SimulateChangeForTest();
        bool heldImmediately = game.AudioChangeHold && game.songPlayer.IsPaused;
        double heldTime = game.songPlayer.SongTime;
        int previousFrameCues = game.TrackingView.CueCount;
        deadline = Now + 15;
        while (game.AudioChangeHold && Now < deadline)
        {
            previousFrameCues = game.TrackingView.CueCount;
            if (keepSending) SendBoth();
            yield return null;
        }
        bool holdEnded = !game.AudioChangeHold;
        Observation completed = Observe(previousFrameCues);
        bool liveAtCompletion = PlayTrackingWatch.LiveNeeds(game.noteSpawner.LiveNotes, SaberHand.Any);
        int completionCues = game.TrackingView.CueCount;
        if (keepSending) SendBoth();
        yield return null;
        Observation next = Observe(completionCues);
        Debug.Log("[AUDIO_RECOVERY_TRACKING] " + label + " heldImmediately=" + heldImmediately +
            " holdEnded=" + holdEnded + " heldTime=" + F(heldTime) + " liveAtCompletion=" + liveAtCompletion +
            " completion={" + completed + "} next={" + next + "}");

        // ここで主要 assert を置かない。旧実装でも、剣が戻った本当の再開まで測ってから失敗させる。
        // loss の旧実装は完了フレームだけ Resume したあと、この時点では次フレームの Pause に入っている。
        bool waitingBeforeReturn = game.TrackingWatch.State == PlayTrackingWatch.Phase.Waiting && game.songPlayer.IsPaused;
        deadline = Now + 11;
        while ((game.songPlayer.IsPaused || game.TrackingWatch.State != PlayTrackingWatch.Phase.Playing) && Now < deadline)
        {
            SendBoth();
            yield return null;
        }
        bool returned = !game.songPlayer.IsPaused && game.TrackingWatch.State == PlayTrackingWatch.Phase.Playing;
        double resumedTime = game.songPlayer.SongTime;
        int finalResumeLogs = resumeLogs;
        double sampleDeadline = Now + .30;
        while (Now < sampleDeadline) { SendBoth(); yield return null; }
        source = game.songPlayer.GetComponent<AudioSource>();
        double cursor = source != null && source.clip != null && source.clip.frequency > 0
            ? source.timeSamples / (double)source.clip.frequency : double.NaN;
        double clockAfter300ms = game.songPlayer.SongTime;
        bool sameRun = GameSession.AchievementRunId == runId;
        bool resumedAudioPlaying = source != null && source.isPlaying;
        int missesAfter = game.scoreManager.MissCount;
        Debug.Log("[AUDIO_RECOVERY_TRACKING] " + label + " waitingBeforeReturn=" + waitingBeforeReturn +
            " returned=" + returned + " resumeLogs=" + finalResumeLogs + " resumedTime=" + F(resumedTime) +
            " after300msSong=" + F(clockAfter300ms) + " sourceSeconds=" + F(cursor) +
            " audioMinusSong=" + F(cursor - clockAfter300ms) + " audioPlaying=" + resumedAudioPlaying +
            " misses=" + missesBefore + "->" + missesAfter + " sameRun=" + sameRun);

        // 正常の退出経路も通してからまとめて判定する。結果保存に到達するほど曲を進めない。
        yield return ExitWithEscape();
        bool exited = SceneManager.GetActiveScene().name == "Title" && !ScreenTransition.IsBusy;
        Debug.Log("[AUDIO_RECOVERY_TRACKING] " + label + " exitedToTitle=" + exited);
        // Unity 同梱の NUnit は Assert.Multiple を提供しないため、同じ意味の検査を一括で報告する。
        var failures = new List<string>();
        void Check(bool condition, string message) { if (!condition) failures.Add(message); }
        Check(heldImmediately, "出力変更のその場で曲を保留する");
        Check(holdEnded, "曲の読み直しと 3 秒の再開待ちが完了する");
        Check(liveAtCompletion, "停止判定を要求する未判定ノーツが完了時にも存在する");
        Check(completed.CueDelta == (keepSending ? 1 : 0), "完了フレーム CueDelta: expected=" + (keepSending ? 1 : 0) + " actual=" + completed.CueDelta);
        Check(completed.ResumeLogs == (keepSending ? 1 : 0), "完了フレーム ResumeLogs: expected=" + (keepSending ? 1 : 0) + " actual=" + completed.ResumeLogs);
        Check(completed.Paused == !keepSending, "完了フレームから正しい停止状態になる: " + completed.Paused);
        var expectedPhase = keepSending ? PlayTrackingWatch.Phase.Playing : PlayTrackingWatch.Phase.Waiting;
        Check(completed.Phase == expectedPhase, "完了フレーム Phase: expected=" + expectedPhase + " actual=" + completed.Phase);
        Check(next.CueDelta == 0, "次フレームに余分な停止/再開の音を鳴らさない: " + next.CueDelta);
        Check(next.Paused == !keepSending, "次フレームの停止状態: " + next.Paused);
        Check(next.Phase == expectedPhase, "次フレーム Phase: expected=" + expectedPhase + " actual=" + next.Phase);
        Check(next.ResumeLogs == (keepSending ? 1 : 0), "次フレーム ResumeLogs: " + next.ResumeLogs);
        if (keepSending)
        {
            Check(completed.RedAge < .25, "完了時に赤の新しい受信が実在する: " + F(completed.RedAge));
            Check(completed.BlueAge < .25, "完了時に青の新しい受信が実在する: " + F(completed.BlueAge));
        }
        else
        {
            Check(completed.RedAge >= PlayTrackingWatch.LostSeconds, "完了時には赤が失われている: " + F(completed.RedAge));
            Check(completed.BlueAge >= PlayTrackingWatch.LostSeconds, "完了時には青が失われている: " + F(completed.BlueAge));
            Check(waitingBeforeReturn, "送信を戻す前は追跡待ちである");
        }
        Check(returned, "構え待ち5秒とカウント3秒のあと、11秒以内に実際の再開へ到達する");
        Check(finalResumeLogs == 1, "実際の再開だけを一度呼ぶ: " + finalResumeLogs);
        // DSP時刻の引き算で生じる丸め誤差だけを許し、実際の巻き戻りは検出する。
        Check(resumedTime >= heldTime - .000001 && resumedTime <= heldTime + .3, "止めた時計の続きから再開する: " + F(heldTime) + " -> " + F(resumedTime));
        Check(clockAfter300ms > resumedTime, "本当の再開後は曲の時計が進む");
        Check(resumedAudioPlaying, "本当の再開後は読み直した音源が再生している");
        Check(sameRun, "同じ試合を続ける");
        Check(exited, "Esc 2 回で通常の Title 退出まで完了する");
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    Observation Observe(int previousCues) => new Observation
    {
        CueDelta = game.TrackingView.CueCount - previousCues,
        ResumeLogs = resumeLogs,
        Paused = game.songPlayer.IsPaused,
        Phase = game.TrackingWatch.State,
        SongTime = game.songPlayer.SongTime,
        RedAge = Now - input.LastReceivedTime,
        BlueAge = Now - input.LastReceivedTime2,
    };

    struct Observation
    {
        public int CueDelta, ResumeLogs;
        public bool Paused;
        public PlayTrackingWatch.Phase Phase;
        public double SongTime, RedAge, BlueAge;
        public override string ToString() => "cueDelta=" + CueDelta + " resumeLogs=" + ResumeLogs +
            " paused=" + Paused + " phase=" + Phase + " song=" + F(SongTime) +
            " ages=" + F(RedAge) + "," + F(BlueAge);
    }

    IEnumerator ExitWithEscape()
    {
        if (keyboard == null || !keyboard.added || SceneManager.GetActiveScene().name != "Game") yield break;
        for (int i = 0; i < 2; i++)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            yield return null; yield return null;
        }
        double deadline = Now + 8;
        while ((SceneManager.GetActiveScene().name == "Game" || ScreenTransition.IsBusy) && Now < deadline) yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (!snapshotsTaken) yield break;
        // 前提条件の失敗でも通常退出を試みる。復元は finally で必ず実施。
        try
        {
            yield return ExitWithEscape();
            if (hooksAdded)
            {
                SceneManager.sceneLoaded -= ConfigureGame;
                Application.logMessageReceived -= OnLog;
                hooksAdded = false;
            }
            // 未完の音読み直し/画面遷移を残してから元の設定を戻さない。
            var transition = (Component)RequiredField(typeof(ScreenTransition), "instance", StaticFields).GetValue(null);
            if (transition != null && transition != originalTransition) Object.DestroyImmediate(transition.gameObject);
            var scene = SceneManager.GetActiveScene();
            SceneManager.SetActiveScene(SceneManager.CreateScene("AudioRecoveryTrackingCleanup"));
            if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        }
        finally
        {
            if (hooksAdded)
            {
                SceneManager.sceneLoaded -= ConfigureGame;
                Application.logMessageReceived -= OnLog;
                hooksAdded = false;
            }
            if (inputRoot != null) Object.DestroyImmediate(inputRoot); // この試験が作った受信 thread だけ停止/join。
            if (imuRoot != null) Object.DestroyImmediate(imuRoot);
            sender?.Dispose(); sender = null;
            commandSink?.Dispose(); commandSink = null;
            var runnerField = RequiredField(typeof(Haptic), "runner", StaticFields);
            var runner = runnerField.GetValue(null) as Component;
            var savedRunner = savedHaptic.TryGetValue(runnerField, out object previousRunner) ? previousRunner as Component : null;
            if (runner != null && runner != savedRunner) Object.DestroyImmediate(runner.gameObject);
            RestoreStatics(savedHaptic);
            SetInstance(typeof(InputPoint), originalInput);
            SetInstance(typeof(UdpImuBridge), originalImu);
            if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
            if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
            if (gamepad != null && gamepad.added) InputSystem.RemoveDevice(gamepad);
            if (originalSettings != null) InputSystem.settings = originalSettings;
            if (testSettings != null) Object.DestroyImmediate(testSettings);
            if (originalKeyboard != null && originalKeyboard.added) originalKeyboard.MakeCurrent();
            if (originalMouse != null && originalMouse.added) originalMouse.MakeCurrent();
            if (originalGamepad != null && originalGamepad.added) originalGamepad.MakeCurrent();
            if (bleSettings != null) bleAutoStart.SetValue(bleSettings, originalBleAutoStart);
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = originalP2PAutoStart;
            PhoneSaberIgnoreZones.Enabled = originalIgnoreZones;
            PhoneSaberEventLog.Current = originalEventLog;
            Time.timeScale = originalTimeScale;
            foreach (var pref in savedPrefs) pref.Restore();
            PlayerPrefs.Save();
            RestoreStatics(savedSession);
            RestoreStatics(savedRecovery);
            savedPrefs.Clear(); ports.Clear(); snapshotsTaken = false;
        }
    }

    void SendBoth()
    {
        sender.Send(Packet, Packet.Length, new IPEndPoint(IPAddress.Loopback, input.port));
        sender.Send(Packet, Packet.Length, new IPEndPoint(IPAddress.Loopback, input.port2));
    }

    int FreePort()
    {
        for (int i = 0; i < 100; i++)
        {
            using (var reserve = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            {
                int port = ((IPEndPoint)reserve.Client.LocalEndPoint).Port;
                if (ports.Add(port)) return port;
            }
        }
        throw new InvalidOperationException("独立した UDP ポートを確保できません");
    }

    static double Now => Time.realtimeSinceStartupAsDouble;
    static string F(double value) => value.ToString("F6", CultureInfo.InvariantCulture);
    static FieldInfo RequiredField(Type type, string name, BindingFlags flags) =>
        type.GetField(name, flags) ?? throw new MissingFieldException(type.FullName, name);
    static void SetInstance(Type type, object value) => RequiredField(type, "<Instance>k__BackingField", StaticFields).SetValue(null, value);
    static void CaptureStatics(Type type, Dictionary<FieldInfo, object> target)
    {
        target.Clear();
        foreach (var field in type.GetFields(StaticFields))
            if (!field.IsLiteral && !field.IsInitOnly) target.Add(field, field.GetValue(null));
    }
    static void RestoreStatics(Dictionary<FieldInfo, object> source)
    {
        foreach (var item in source) item.Key.SetValue(null, item.Value);
        source.Clear();
    }

    enum PrefKind { Int, Float, String }
    sealed class Pref
    {
        public string Key;
        public PrefKind Kind;
        public bool Existed;
        public int IntValue;
        public float FloatValue;
        public string StringValue;
        public void Restore()
        {
            if (!Existed) { PlayerPrefs.DeleteKey(Key); return; }
            switch (Kind)
            {
                case PrefKind.Int: PlayerPrefs.SetInt(Key, IntValue); break;
                case PrefKind.Float: PlayerPrefs.SetFloat(Key, FloatValue); break;
                default: PlayerPrefs.SetString(Key, StringValue); break;
            }
        }
    }
    void SavePref(string key, PrefKind kind)
    {
        var pref = new Pref { Key = key, Kind = kind, Existed = PlayerPrefs.HasKey(key) };
        if (pref.Existed)
        {
            if (kind == PrefKind.Int) pref.IntValue = PlayerPrefs.GetInt(key);
            else if (kind == PrefKind.Float) pref.FloatValue = PlayerPrefs.GetFloat(key);
            else pref.StringValue = PlayerPrefs.GetString(key);
        }
        savedPrefs.Add(pref);
    }
}
