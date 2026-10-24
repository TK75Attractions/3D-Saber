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

// 実 Game / 実 UDP / 実曲で、音の出力先が変わったあとの読み直し中に、以前届いていた両色を見失う並びと、送り続ける対照を確認する。
// 2026-10-24 から曲は止めずに裏で読み直すので、送り続ける回は一度も止まらず、見失う回はふつうの見張りが止めて一度だけ再開する。
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
    public IEnumerator ContinuedTrackingDuringAudioReloadKeepsPlaying() => Verify(true);

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
        // 2026-10-24 から曲は止めない(ユーザー「音の出力先がかわったとき曲止めないで上の表示だけにして」)。
        // 時計とノーツはそのまま進み、0.5 秒後の立て直しで曲の音を裏で読み直して、今の位置から鳴らし直す。
        bool keptPlaying = !game.songPlayer.IsPaused;
        double changedTime = game.songPlayer.SongTime;
        int cuesBefore = game.TrackingView.CueCount;
        bool reloadSeen = false, pausedDuringReload = false;
        deadline = Now + 15;
        while (Now < deadline)
        {
            if (keepSending) SendBoth();
            yield return null;
            reloadSeen |= game.AudioReloading;
            pausedDuringReload |= game.songPlayer.IsPaused;
            if (reloadSeen && !game.AudioReloading) break;
        }
        bool reloaded = reloadSeen && !game.AudioReloading;
        double reloadedTime = game.songPlayer.SongTime;
        Debug.Log("[AUDIO_RECOVERY_TRACKING] " + label + " keptPlaying=" + keptPlaying + " reloaded=" + reloaded +
            " pausedDuringReload=" + pausedDuringReload + " changedTime=" + F(changedTime) + " reloadedTime=" + F(reloadedTime));

        // 送らない回は、ふつうの見張り(3 秒見えなければ止める)が止めるまで待つ。送り続ける回は止まらないことを確かめる。
        bool waitingBeforeReturn = false;
        deadline = Now + PlayTrackingWatch.LostSeconds + 3;
        while (Now < deadline)
        {
            if (keepSending) SendBoth();
            yield return null;
            pausedDuringReload |= keepSending && game.songPlayer.IsPaused;
            if (!keepSending && game.TrackingWatch.State == PlayTrackingWatch.Phase.Waiting && game.songPlayer.IsPaused)
            { waitingBeforeReturn = true; break; }
            if (keepSending && Now >= deadline - PlayTrackingWatch.LostSeconds - 2) break;
        }
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
        int cueDelta = game.TrackingView.CueCount - cuesBefore;
        Debug.Log("[AUDIO_RECOVERY_TRACKING] " + label + " waitingBeforeReturn=" + waitingBeforeReturn +
            " returned=" + returned + " resumeLogs=" + finalResumeLogs + " cueDelta=" + cueDelta + " resumedTime=" + F(resumedTime) +
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
        Check(keptPlaying, "出力が変わったその場で曲を止めない");
        Check(reloaded, "曲の音を裏で読み直し終える");
        Check(reloadedTime > changedTime, "読み直す間も曲の時計は進む: " + F(changedTime) + " -> " + F(reloadedTime));
        if (keepSending)
        {
            Check(!pausedDuringReload, "剣が見えている回は一度も止まらない");
            Check(finalResumeLogs == 0, "止めていないので再開もしない: " + finalResumeLogs);
            Check(cueDelta == 0, "止める・数える・再開の音を鳴らさない: " + cueDelta);
        }
        else
        {
            Check(waitingBeforeReturn, "剣を見失ったら、ふつうの見張りが止めて待つ");
            Check(finalResumeLogs == 1, "剣が戻ったら一度だけ再開する: " + finalResumeLogs);
        }
        Check(returned, "最後は曲が進んでいる");
        Check(clockAfter300ms > resumedTime, "曲の時計が進み続ける");
        Check(resumedAudioPlaying, "読み直した音源が鳴っている");
        Check(Math.Abs(cursor - clockAfter300ms) < .2, "読み直した音は今の位置で鳴る: audio-song=" + F(cursor - clockAfter300ms));
        Check(sameRun, "同じ試合を続ける");
        Check(exited, "Esc 2 回で通常の Title 退出まで完了する");
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
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
