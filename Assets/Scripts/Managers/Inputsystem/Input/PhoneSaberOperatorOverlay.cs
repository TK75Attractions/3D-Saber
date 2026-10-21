using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

// 運営専用の受信表示。シーンや prefab を変更せず、通常の Player でも F8 で開ける。
public sealed class PhoneSaberOperatorOverlay : MonoBehaviour
{
    bool initialized;
    bool visible;
    PhoneSaberControlPanelServer controlPanel;
    PhoneSaberControlPanelState panel;
    Dictionary<string, Action<string>> actionHandlers;
    PhoneSaberOperatorToast browserToast;
    // 変更対象を Overlay に限定しつつ、既存 T キー・defaults と同じ反転状態を使う。
    static readonly FieldInfo TrackLockState = typeof(PhoneSaberOperatorKeys).GetField(
        "lastSentTrackLock", BindingFlags.NonPublic | BindingFlags.Static);
    PhoneSaberGuiRenderer guiRenderer;
    double nextLogFlush;
    string quitMarker;
    bool clocksSynchronized;
    Vector2 scrollPosition;
    double startedAt;
    double nextRefresh;
    readonly PhoneSaberControlPanelRates healthRates = new PhoneSaberControlPanelRates();
    PhoneSaberControlPanelHealth.GameWindow healthGame;
    List<PhoneSaberControlPanelHealth.Check> healthChecks;
    string fallbackStation;
    string station;
    string services;
    string redText;
    string blueText;
    string redWarning;
    string blueWarning;
    string displayWarning;
    GUIStyle textStyle;
    GUIStyle warningStyle;
    GUIStyle buttonStyle;
    InputPoint calibrationInput;
    string calibrationStation;
    bool calibrationBlue;
    int calibrationCorner = -1;
    double captureStarted = -1;
    string calibrationMessage = "未設定時は従来どおりの座標です。";
    readonly Vector2[] calibrationCorners = new Vector2[4];
    static readonly string[] CornerNames = { "左上", "右上", "右下", "左下" };
    static readonly int[] PredictionHorizons = { 0, 20, 40, 60 };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        // 当日用の設定は PhoneSaber 起動時に保証する。PlayerSettings は変更しない。
        Application.runInBackground = true;
        // 描画の先行キューを1フレームに制限し、剣の位置が画面に出るまでの待ちを減らす（既定は2）。
        // 対応しないグラフィックスAPIでは Unity が無視する。
        QualitySettings.maxQueuedFrames = 1;
        if (Screen.sleepTimeout != SleepTimeout.NeverSleep)
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        try
        {
            PhoneSaberEventLog.Current = new PhoneSaberEventLog(
                Path.Combine(Application.persistentDataPath, "PhoneSaber", "events.log"));
        }
        catch { PhoneSaberEventLog.Current = null; }
        var overlay = FindFirstObjectByType<PhoneSaberOperatorOverlay>();
        if (overlay == null)
            overlay = new GameObject("PhoneSaber Operator Overlay").AddComponent<PhoneSaberOperatorOverlay>();
        DontDestroyOnLoad(overlay.gameObject);
        // Domain Reload / Scene Reload を無効にした Editor でも毎回非表示で始める。
        overlay.CancelCalibration();
        overlay.calibrationBlue = false;
        overlay.calibrationMessage = "未設定時は従来どおりの座標です。";
        overlay.SetVisible(false);
        overlay.clocksSynchronized = false;
        overlay.startedAt = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        overlay.fallbackStation = PhoneSaberStation.Read();
        overlay.nextLogFlush = 0;
        overlay.quitMarker = null;
        overlay.StopControlPanel();
        overlay.initialized = true;
        overlay.StartControlPanel();
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 1; i + 1 < args.Length; i++)
            if (args[i] == "-phonesaberQuitMarker") overlay.quitMarker = args[++i];
        PhoneSaberEventLog.Record(overlay.fallbackStation, "SYSTEM", "session-start",
            "run-in-background=true sleep=never");
        PhoneSaberEventLog.Current?.Flush();
    }

    void Update()
    {
        double logNow = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        if (logNow >= nextLogFlush)
        {
            PhoneSaberEventLog.Current?.Flush();
            nextLogFlush = logNow + (string.IsNullOrEmpty(PhoneSaberEventLog.Current?.LastError) ? 0.25 : 5.0);
        }
        PollCalibration(logNow);
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.f8Key.wasPressedThisFrame)
        {
            bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            if (PhoneSaberControlPanelActions.ShouldOpenBrowser(shift, controlPanel != null && controlPanel.Running))
            {
                SetVisible(false);
                try
                {
                    Application.OpenURL(controlPanel.Url);
                    if (browserToast == null) browserToast = gameObject.AddComponent<PhoneSaberOperatorToast>();
                    browserToast.Show(PhoneSaberControlPanelActions.BrowserOpenedNotice(
                        Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor));
                    LogControlPanel("open port=" + controlPanel.Port);
                }
                catch (Exception) { SetVisible(true); }
            }
            else
            {
                SetVisible(!visible);
                if (!visible && calibrationCorner >= 0) ExecuteAction("calibration-cancel", "");
            }
            nextRefresh = 0;
        }
        if (keyboard != null && keyboard.f7Key.wasPressedThisFrame)
        {
            // Web が使えない実行では従来どおり予備画面も開く。
            if (controlPanel == null || !controlPanel.Running) SetVisible(true);
            ExecuteAction("calibration-advance", "");
        }
        // キューは1フレーム最大8件。Unity API と PlayerPrefs はここからだけ触る。
        if (controlPanel != null)
        {
            for (int i = 0; i < 8 && controlPanel.TryDequeue(out var action); i++)
                ExecuteAction(action.Name, action.Value);
            if (!controlPanel.Running)
            {
                LogControlPanel("stopped error=" + controlPanel.Error);
                StopControlPanel();
            }
        }
        if (!visible && (controlPanel == null || !controlPanel.RecentlyPolled)) return;
        ObserveHealthFrame(logNow);
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        if (now < nextRefresh) return;
        nextRefresh = now + 0.2;
        Refresh(now);
        panel = BuildPanel(now);
        controlPanel?.Publish(panel.ToJson());
    }

    void Awake() => EnsureGuiRenderer();
    void OnEnable()
    {
        SetVisible(visible);
        if (initialized && Application.isPlaying) StartControlPanel();
    }
    void OnDestroy() => StopControlPanel();

    void StartControlPanel()
    {
        if (controlPanel != null || !Application.isPlaying || Application.isBatchMode ||
            !PhoneSaberP2PBridgeProcess.AutoStartEnabled) return;
        controlPanel = new PhoneSaberControlPanelServer();
        if (controlPanel.Start())
        {
            Debug.Log("PhoneSaber control-panel port=" + controlPanel.Port);
            LogControlPanel("start port=" + controlPanel.Port);
            nextRefresh = 0;
        }
        else
        {
            Debug.LogWarning("PhoneSaber 操作画面を開始できません: " + controlPanel.Error);
            LogControlPanel("start-failed error=" + controlPanel.Error);
            StopControlPanel();
        }
    }

    void StopControlPanel() { controlPanel?.Dispose(); controlPanel = null; }
    void LogControlPanel(string detail) => PhoneSaberEventLog.Record(
        InputPoint.Instance != null ? InputPoint.Instance.StationLabel : fallbackStation, "SYSTEM", "control-panel", detail);

    // 通信・IMGUI・F7 の全操作はこの同じ表を通る。実行時にも現在の有効条件を確認する。
    public IReadOnlyDictionary<string, Action<string>> ControlPanelHandlers => actionHandlers ?? (actionHandlers = CreateActionHandlers());
    bool ExecuteAction(string name, string value)
    {
        if (!PhoneSaberControlPanelActions.Dispatch(name, value, ControlPanelHandlers)) return false;
        LogControlPanel("action name=" + name + " value=" + value);
        nextRefresh = 0;
        return true;
    }

    Dictionary<string, Action<string>> CreateActionHandlers()
    {
        return new Dictionary<string, Action<string>>(StringComparer.Ordinal)
        {
            { "calibration-start", _ => { if (calibrationCorner < 0) AdvanceCalibration(); } },
            { "calibration-advance", _ => AdvanceCalibration() },
            { "calibration-cancel", _ => { CancelCalibration(); calibrationMessage = "測定をキャンセルしました。"; } },
            { "calibration-reset", _ => {
                if (calibrationCorner >= 0 || InputPoint.Instance == null) return;
                InputPoint.Instance.ResetPositionCalibration();
                calibrationMessage = "この台の位置補正を削除し、従来の座標に戻しました。";
            } },
            { "calibration-blue", v => { if (calibrationCorner < 0) calibrationBlue = v == "1"; } },
            { "calibration-enabled", v => { if (calibrationCorner < 0) InputPoint.Instance?.SetPositionCalibrationEnabled(v == "1"); } },
            { "prediction", v => {
                var input = InputPoint.Instance;
                if (input == null || calibrationCorner >= 0) return;
                if (v == "auto") input.SetPredictionAutomatic();
                else input.SetPredictionHorizonMilliseconds(int.Parse(v, CultureInfo.InvariantCulture));
            } },
            { "display-sync", v => PhoneSaberDisplaySync.SetMode(int.Parse(v, CultureInfo.InvariantCulture)) },
            { "frame-cap", v => PhoneSaberDisplaySync.SetFrameCapMode(int.Parse(v, CultureInfo.InvariantCulture)) },
            { "judge-mode", v => PhoneSaberJudgeLatency.Set(int.Parse(v, CultureInfo.InvariantCulture), PhoneSaberJudgeLatency.OffsetMs) },
            { "judge-adjust", v => PhoneSaberJudgeLatency.Set(PhoneSaberJudgeLatency.Mode, PhoneSaberJudgeLatency.OffsetMs + int.Parse(v, CultureInfo.InvariantCulture)) },
            { "judge-centre", _ => {
                var median = PhoneSaberJudgeTimingStats.Median();
                if (PhoneSaberJudgeLatency.Mode != PhoneSaberJudgeLatencySettings.Automatic || !median.HasValue ||
                    PhoneSaberJudgeTimingStats.Count < PhoneSaberJudgeLatencySettings.MinimumHitsToCentre) return;
                PhoneSaberJudgeLatency.Set(PhoneSaberJudgeLatencySettings.Automatic,
                    PhoneSaberJudgeLatencySettings.CentredOffset(PhoneSaberJudgeLatency.OffsetMs, median.Value));
                PhoneSaberJudgeTimingStats.Clear();
            } },
            { "filter", v => PhoneSaberFilterSettings.Save(CurrentStation, int.Parse(v, CultureInfo.InvariantCulture)) },
            { "stabilizer", v => PhoneSaberBladeStabilizerSettings.Save(CurrentStation, v == "1") },
            { "phone-ignore", v => PhoneSaberDiscoveryResponder.QueueCommand("ignore=" + v) },
            { "phone-clear-ignore", _ => PhoneSaberDiscoveryResponder.QueueCommand("clear-ignore=1") },
            { "phone-learn-area", _ => PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.LearnAreaCommand) },
            { "phone-clear-area", _ => PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.ClearAreaCommand) },
            { "phone-exposure", v => PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.ExposureBiasCommand(int.Parse(v, CultureInfo.InvariantCulture))) },
            { "phone-track-lock", _ => {
                bool last = (bool)TrackLockState.GetValue(null);
                var command = PhoneSaberOperatorKeys.TrackLockCommand(last);
                TrackLockState.SetValue(null, !last);
                PhoneSaberDiscoveryResponder.QueueCommand(command.command);
            } },
            { "phone-clip", value => PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberOperatorKeys.CommandFor(Key.K, 0, out _).Value.command) },
            { "pc-ignore", v => {
                var input = InputPoint.Instance;
                if (input == null) return;
                if (v == "red" && input.IsRecentlyActive(.5f)) PhoneSaberIgnoreZones.Add(CurrentStation, input.LastRawEndsA, input.LastRawEndsB);
                if (v == "blue" && input.IsRecentlyActive2(.5f)) PhoneSaberIgnoreZones.Add(CurrentStation, input.LastRawEndsA2, input.LastRawEndsB2);
            } },
            { "pc-clear-ignore", _ => PhoneSaberIgnoreZones.Clear(CurrentStation) },
            { "clocks-synchronized", v => {
                clocksSynchronized = v == "1";
                if (InputPoint.Instance != null) InputPoint.Instance.PredictionClocksSynchronized = clocksSynchronized;
            } }
        };
    }
    string CurrentStation => InputPoint.Instance != null ? InputPoint.Instance.StationLabel : fallbackStation;
    void OnDisable()
    {
        StopControlPanel();
        CancelCalibration();
        if (guiRenderer != null) guiRenderer.enabled = false;
    }

    void EnsureGuiRenderer()
    {
        if (guiRenderer != null) return;
        guiRenderer = gameObject.AddComponent<PhoneSaberGuiRenderer>();
        guiRenderer.Initialize(DrawGUI, true);
    }

    void SetVisible(bool value)
    {
        visible = value;
        EnsureGuiRenderer();
        guiRenderer.enabled = (value || calibrationCorner >= 0) && isActiveAndEnabled;
    }

    void CancelCalibration()
    {
        calibrationInput?.PositionCapture.Cancel();
        calibrationInput = null;
        calibrationCorner = -1;
        captureStarted = -1;
        if (guiRenderer != null) guiRenderer.enabled = visible && isActiveAndEnabled;
    }

    void AdvanceCalibration()
    {
        if (captureStarted >= 0) return;
        if (calibrationCorner < 0)
        {
            var input = InputPoint.Instance;
            if (input == null || !input.isActiveAndEnabled)
            {
                calibrationMessage = "受信機がありません。ゲームを起動して送信を開始してください。";
                return;
            }
            calibrationInput = input;
            calibrationStation = input.StationLabel;
            calibrationCorner = 0;
            SetVisible(visible);
            calibrationMessage = "プレイヤーは所定の立ち位置に立ち、点灯した剣の中点を指定の隅で静止させてください。";
            return;
        }
        captureStarted = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        calibrationInput.PositionCapture.Begin(calibrationBlue, captureStarted);
        calibrationMessage = "採取中：剣を動かさず、そのまま約1秒待ってください。";
    }

    void PollCalibration(double now)
    {
        if (calibrationCorner < 0) return;
        if (calibrationInput == null || calibrationInput != InputPoint.Instance ||
            !calibrationInput.isActiveAndEnabled || calibrationInput.StationLabel != calibrationStation)
        {
            CancelCalibration();
            calibrationMessage = "受信機または台が変わりました。最初から測定してください。";
            return;
        }
        if (captureStarted < 0 || now < captureStarted + PhoneSaberPositionCapture.Duration) return;
        captureStarted = -1;
        if (!calibrationInput.PositionCapture.Finish(out var point, out var error))
        {
            calibrationMessage = error;
            return;
        }
        calibrationCorners[calibrationCorner++] = point;
        if (calibrationCorner < 4)
        {
            calibrationMessage = $"採取済み ({point.x:F0}, {point.y:F0})。次の隅へ剣を移動して静止させてください。";
            return;
        }
        bool saved = calibrationInput.SavePositionCalibration(calibrationCorners, out error);
        CancelCalibration();
        calibrationMessage = saved ? "4隅を保存し、位置補正をONにしました。剣を動かして届く範囲を確認してください。" :
            "保存できません：" + error + " 最初から測定し直してください。";
        nextRefresh = 0;
    }

    void BuildCalibration()
    {
        var input = InputPoint.Instance;
        panel.Label("台ごとの位置補正 [F7: 開始 / 隅を採取]");
        panel.Label("状態: " + (input != null && input.PositionCalibrationEnabled ? "ON" : "OFF") +
            (input != null && input.HasPositionCalibration ? "（保存済み）" : "（未設定・従来の座標）"));
        panel.Label("カメラは画面下の箱からプレイヤーを撮影。立ち位置は画面から約2.7〜3.1m、プレイ範囲は直径1.5m。\n" +
            "点灯した剣の中点で左上→右上→右下→左下を測定します。画面の剣の位置ではなく、実際に振る範囲の隅に合わせてください。");
        panel.Label(calibrationMessage, true);
        panel.Toggle("BLUEの剣を採取（OFFならRED）", calibrationBlue, "calibration-blue", calibrationCorner < 0);
        if (calibrationCorner >= 0)
        {
            panel.Label($"{calibrationCorner + 1}/4: {CornerNames[calibrationCorner]}で剣を静止 → F7で1秒採取");
            panel.Button("この隅を採取 [F7]", "calibration-advance", enabled: captureStarted < 0);
            panel.Button("測定をキャンセル（保存済みの設定は維持）", "calibration-cancel");
        }
        else
        {
            panel.Button("4隅の測定を開始 [F7]", "calibration-start");
            panel.Toggle("位置補正をON（この台だけ）", input != null && input.PositionCalibrationEnabled,
                "calibration-enabled", input != null && input.HasPositionCalibration);
            panel.Button("この台の位置補正をリセット（OFFに戻す）", "calibration-reset", enabled: input != null && input.HasPositionCalibration);
        }
    }

    static string PredictionSourceLabel(string source) => source == "automatic-estimate" ? "自動計測" :
        source == "one-way-ts" ? "同期済 ts" : "計測なし・OFF";

    void BuildPrediction()
    {
        var input = InputPoint.Instance;
        int horizon = input != null ? input.PredictionHorizonMilliseconds : 0;
        bool automatic = input != null && input.PredictionAutomatic;
        string choice = automatic ? $"自動 赤 {input.PredictionLead(false)} ms（{PredictionSourceLabel(input.PredictionSource(false))}） / 青 {input.PredictionLead(true)} ms（{PredictionSourceLabel(input.PredictionSource(true))}）" :
            horizon == 0 ? "OFF" : horizon + " ms";
        panel.Label($"剣の遅延補正: {choice}（この台に保存）");
        bool enabled = input != null && calibrationCorner < 0;
        panel.Button("自動", "prediction", "auto", enabled, automatic);
        foreach (int milliseconds in PredictionHorizons)
            panel.Button(milliseconds == 0 ? "0 ms / OFF" : milliseconds + " ms", "prediction",
                milliseconds.ToString(CultureInfo.InvariantCulture), enabled, !automatic && horizon == milliseconds);
        panel.Label("初期値は自動（未設定の台）。手動なら20 msから試してください。折り返しでは行き過ぎが増えます。\n" +
            $"手動: 最大{PhoneSaberEndpointPredictor.MaximumManualHorizonMilliseconds} ms・移動上限{PhoneSaberEndpointPredictor.DefaultMaximumDisplacement} world。\n" +
            $"自動: 最大{PhoneSaberEndpointPredictor.MaximumHorizonMilliseconds} ms・移動上限{PhoneSaberEndpointPredictor.AutomaticMaximumDisplacement} world。受信が100 ms止まると予測量はゼロに戻ります。");
        panel.Label("自動: スマホ＋通信＋PC＋画面＋露光の目安。未計測時はOFF。\n" +
            "同期確認済みのts片道値は第2候補。30秒中央値が5 ms超変化した時、10秒以上あけて更新。");
    }

    void BuildDisplaySync()
    {
        bool vsync = PhoneSaberDisplaySync.Mode == PhoneSaberDisplaySyncSettings.VSync;
        panel.Label($"画面同期: {(vsync ? "ON（画面の更新に合わせて描く）" : "ゲームの設定のまま")}（この台に保存）");
        panel.Button("ゲームの設定のまま", "display-sync", "0", selected: !vsync);
        panel.Button("ON", "display-sync", "1", selected: vsync);
        panel.Label("本編はゲームの設定だと画面の更新を待たずに描きます（60 Hz の画面に数百 fps）。\n" +
            "絵が横にずれて見える・動きがガタつくときは ON と比べてください。元へ戻すと次の曲から反映されます。");
        int cap = PhoneSaberDisplaySync.FrameCapMode;
        panel.Label($"フレーム上限: {PhoneSaberFrameCapSettings.Label(cap)}（この台に保存）");
        for (int mode = PhoneSaberFrameCapSettings.GameDefault; mode <= PhoneSaberFrameCapSettings.TwoTimes; mode++)
            panel.Button(PhoneSaberFrameCapSettings.Label(mode), "frame-cap", mode.ToString(CultureInfo.InvariantCulture), selected: cap == mode);
        panel.Label("画面同期の設定はそのまま。同期ONでは上限が効かない場合があります。\n" +
            "ゲームの設定のままへ戻すと次の曲から反映されます。");
    }

    void BuildJudgeLatency()
    {
        bool auto = PhoneSaberJudgeLatency.Mode == PhoneSaberJudgeLatencySettings.Automatic;
        int offset = PhoneSaberJudgeLatency.OffsetMs;
        string applied = PhoneSaberJudgeLatency.AppliedMs.HasValue ? $"{PhoneSaberJudgeLatency.AppliedMs.Value:0} ms" : "未適用（本編で反映）";
        string measured = PhoneSaberJudgeLatency.AutomaticMs.HasValue ? $"{PhoneSaberJudgeLatency.AutomaticMs.Value:0} ms" : "未測定";
        string game = PhoneSaberJudgeLatency.GameMs.HasValue ? $"{PhoneSaberJudgeLatency.GameMs.Value:0} ms" : "120 ms";
        panel.Label($"判定の遅れ補正: {(auto ? "自動（測った遅れ）" : "ゲームの設定")}  いま {applied}（測定 {measured}・調整 {offset:+0;-0;0} ms・ゲーム {game}{(PhoneSaberJudgeLatency.PredictionLeadMs > 0 && auto ? $"・予測の先読み −{PhoneSaberJudgeLatency.PredictionLeadMs:0} ms" : "")}）（この台に保存）");
        panel.Button("ゲームの設定", "judge-mode", "0", selected: !auto);
        panel.Button("自動", "judge-mode", "1", selected: auto);
        panel.Button("調整 −10", "judge-adjust", "-10");
        panel.Button("調整 +10", "judge-adjust", "10");
        double? median = PhoneSaberJudgeTimingStats.Median();
        if (auto && median.HasValue && PhoneSaberJudgeTimingStats.Count >= PhoneSaberJudgeLatencySettings.MinimumHitsToCentre)
            panel.Button($"当たりの中央に合わせる（調整 {offset:+0;-0;0} → {PhoneSaberJudgeLatencySettings.CentredOffset(offset, median.Value):+0;-0;0} ms）", "judge-centre");
        panel.Label(median.HasValue
            ? $"直近 {PhoneSaberJudgeTimingStats.Count} 回の当たりのずれ（中央）: {median.Value:+0;-0;0} ms（＋は遅め・−は早め）。" +
              "±20 ms を超えて偏るなら、調整をその向きへ動かす（遅めなら＋）。"
            : "当たりのずれ: まだ記録なし（本編で切ると出ます）。");
        if (!string.IsNullOrEmpty(PhoneSaberDisplayNotice.PhoneWarning))
            panel.Label(PhoneSaberDisplayNotice.PhoneWarning, true);
    }

    void BuildFilter()
    {
        var input = InputPoint.Instance;
        string name = input != null ? input.StationLabel : fallbackStation;
        int mode = PhoneSaberFilterSettings.Load(name);
        panel.Label($"剣の揺れ補正: {PhoneSaberFilterSettings.Label(mode)}（この台の両色に保存）");
        for (int value = PhoneSaberEndpointFilter.Off; value <= PhoneSaberEndpointFilter.Medium; value++)
            panel.Button(PhoneSaberFilterSettings.Label(value), "filter", value.ToString(CultureInfo.InvariantCulture), selected: mode == value);
        panel.Label("初期値は「中」（未設定の台）。静止時の揺れを少し抑えます。速い振りにも小さな遅れが加わります。");
        bool stable = PhoneSaberBladeStabilizerSettings.Load(name);
        panel.Label($"剣の揺れ止め（伸び縮み・誤認識の跳び）: {(stable ? "ON" : "OFF")}（この台に保存）");
        panel.Button("OFF", "stabilizer", "0", selected: !stable);
        panel.Button("ON", "stabilizer", "1", selected: stable);
        panel.Label("既定ON。剣の中心と向きはそのまま（遅れなし）、急な伸び縮みと一瞬の跳びだけを抑えて、勝手に切れるのを減らします。");
    }

    // 2026-10-18 台B: AQUOS がほぼ毎コマ コーンを送り、PC 側の無視で全部捨てて剣が止まった。
    // 直近の受信の大半を捨てているときは、スマホ側の無視が必要だと知らせる。
    int ignoreSeenRed, ignoreSeenBlue, receivedSeenRed, receivedSeenBlue;
    double ignoreCheckedAt;
    string ignoreWarning = "";
    string IgnoreDominanceWarning(InputPoint input)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (now - ignoreCheckedAt < 2) return ignoreWarning;
        int ignoredRed = input.IgnoredSamples - ignoreSeenRed, ignoredBlue = input.IgnoredSamples2 - ignoreSeenBlue;
        int receivedRed = input.ReceivedPacketCount - receivedSeenRed, receivedBlue = input.ReceivedPacketCount2 - receivedSeenBlue;
        bool first = ignoreCheckedAt == 0;
        ignoreSeenRed = input.IgnoredSamples; ignoreSeenBlue = input.IgnoredSamples2;
        receivedSeenRed = input.ReceivedPacketCount; receivedSeenBlue = input.ReceivedPacketCount2;
        ignoreCheckedAt = now;
        if (first) return ignoreWarning = "";
        ignoreWarning = PhoneSaberIgnoreZones.DominanceWarning(ignoredRed, receivedRed, ignoredBlue, receivedBlue);
        return ignoreWarning;
    }

    void BuildIgnoreZones(InputPoint input, string name)
    {
        int count = PhoneSaberIgnoreZones.Load(name).Count;
        string ignored = input != null ? $"・無視した受信 赤 {input.IgnoredSamples} / 青 {input.IgnoredSamples2}" : "";
        // 本命は iPhone 側（候補を選ぶ前に外すので、同じコマの剣が選ばれる）。PC 側はその予備。
        double left = PhoneSaberDiscoveryResponder.CommandSecondsLeft(SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
        panel.Label("キーでも操作できます（F8 を開かずに）: Ctrl+Shift+R 赤を無視 / B 青を無視 / I 赤と青を無視 / C 無視を消す / K 今の場面を保存（iPhone） / " +
            "P プレイ範囲を覚える / X プレイ範囲を消す / E 露出 −2→0→−1→−2 / T 剣を追いかける ON→OFF→ON");
        panel.Label("スマホに無視させる（iPhone・Android。剣の電源を切り、コーンなどだけが映っている状態で押す）" +
            (left > 0 ? $"　送信中 あと {left:0} 秒" : ""));
        if (!string.IsNullOrEmpty(PhoneSaberOperatorKeys.LastResultText))
            panel.Label(PhoneSaberOperatorKeys.LastResultText);
        panel.Button("スマホ: 今の赤を無視", "phone-ignore", "red");
        panel.Button("スマホ: 今の青を無視", "phone-ignore", "blue");
        panel.Button("スマホ: 赤と青を無視", "phone-ignore", "both");
        panel.Button("スマホ: 無視をすべて消す", "phone-clear-ignore");
        panel.Button("剣を追いかける ON/OFF [T]", "phone-track-lock");
        panel.Button("今の場面を保存 [K]", "phone-clip");
        // プレイ範囲（2026-10-18）: 赤い服の人など動く物は無視範囲では外せないので、剣を振る範囲の外の候補を選ばせない。
        panel.Label("スマホのプレイ範囲（赤い服の人など、動く赤・青の物の対策。押してから10秒、遊ぶ範囲いっぱいに剣を振る）");
        panel.Button("スマホ: プレイ範囲を覚える（10秒、範囲いっぱいに剣を振る）", "phone-learn-area");
        panel.Button("スマホ: プレイ範囲を消す", "phone-clear-area");
        // 露出補正（試験、既定 0 = OFF）。−1・−2 で光らない服などが暗くなる。認識に入る画像が変わるので比べて使う。
        foreach (int ev in PhoneSaberDiscoveryResponder.ExposureBiasChoices)
            panel.Button(ev == 0 ? "スマホ露出: 0" : $"スマホ露出: −{-ev}", "phone-exposure", ev.ToString(CultureInfo.InvariantCulture));
        panel.Label($"PC 側で無視（予備）: {count} か所（この台に保存）{ignored}");
        string warning = input != null ? IgnoreDominanceWarning(input) : "";
        if (warning.Length > 0) panel.Label(warning, true);
        bool red = input != null && input.IsRecentlyActive(.5f);
        bool blue = input != null && input.IsRecentlyActive2(.5f);
        panel.Button("今「赤」に映っている物を無視", "pc-ignore", "red", red);
        panel.Button("今「青」に映っている物を無視", "pc-ignore", "blue", blue);
        panel.Button("無視をすべて消す", "pc-clear-ignore", enabled: count > 0);
        panel.Label("剣をカメラの外に出し（電源OFF）、コーンなど赤い物だけが認識されている状態で押します。" +
            "その物を囲む範囲に両端とも入った受信は使いません。剣がそこを横切るだけなら影響しません。");
    }

    void Refresh(double now)
    {
        var input = InputPoint.Instance;
        var empty = new PhoneSaberInputStats(false, 0, now - startedAt, "", false);
        var red = input != null ? input.ReadInputStats() : empty;
        var blue = input != null ? input.ReadInputStats(true) : empty;
        healthRates.Sample(now, input != null ? input.ReceivedPacketCount : 0, input != null ? input.ReceivedPacketCount2 : 0,
            (input != null ? input.GetInstanceID().ToString() : "none") + ":" + input?.RedCoordinateSender + ":" + input?.BlueCoordinateSender);
        var acknowledgements = new List<PhoneSaberAcknowledgement>();
        var store = PhoneSaberDiscoveryResponder.Acknowledgements;
        if (store.Latest(out int latestId, out _, out _))
            // QueueCommand の連番。store が保持する最大32指示を読み、別操作後もdefaultsのACKを探す。
            for (int offset = 0; offset < 32 && latestId - offset > 0; offset++) acknowledgements.AddRange(store.Read(latestId - offset));
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.name == "Game") healthGame = PhoneSaberControlPanelHealth.ObserveGaps(healthGame, now,
            red.SecondsSinceLastPacket, blue.SecondsSinceLastPacket);
        healthChecks = PhoneSaberControlPanelHealth.Evaluate(now, red, blue,
            input != null ? PhoneSaberPerfLog.Estimate(input, false, now, input.RedCoordinateTiming.Shown) : null,
            input != null ? PhoneSaberPerfLog.Estimate(input, true, now, input.BlueCoordinateTiming.Shown) : null,
            healthRates.Red, healthRates.Blue, acknowledgements.ToArray(), scene.name == "Game", healthGame.BothGaps, healthGame.FramesOver50, healthGame.WholeSong);
        bool mac = PhoneSaberBonjourPublisher.IsSupported;
        station = input != null ? input.StationLabel : fallbackStation;
        redText = PhoneSaberStatsDisplay.Format("RED", input != null ? input.port : 5005, red, mac, clocksSynchronized);
        blueText = PhoneSaberStatsDisplay.Format("BLUE", input != null ? input.port2 : 5006, blue, mac, clocksSynchronized);
        redWarning = PhoneSaberStatsDisplay.Warning("RED", red);
        blueWarning = PhoneSaberStatsDisplay.Warning("BLUE", blue);
        displayWarning = PhoneSaberStatsDisplay.DisplayWarning(Screen.currentResolution.refreshRateRatio.value);
        services = $"探索 UDP 5007: {State(true, input != null && input.DiscoveryResponderRunning)}\n" +
            $"Bonjour: {State(mac, input != null && input.BonjourPublisherRunning)}  |  " +
            $"P2P bridge: {State(PhoneSaberP2PBridgeProcess.IsSupported, PhoneSaberP2PBridgeProcess.IsRunning)}\n" +
            $"UDP受信機: RED {(input != null && input.ReceiverAlive ? "ON" : "停止")} / " +
            $"BLUE {(input != null && input.ReceiverAlive2 ? "ON" : "停止")}";
    }

    void OnApplicationQuit()
    {
        StopControlPanel();
        PhoneSaberEventLog.Record(fallbackStation, "SYSTEM", "session-quit", "exit-code=" + Environment.ExitCode);
        // 満杯のキューも終了時は全部書き出す(通常時は1回128件まで)。
        for (int i = 0; i < 5; i++) PhoneSaberEventLog.Current?.Flush();
        // open -W はアプリの crash status を返さないため、正常終了の印を launcher に返す。
        try
        {
            if (!string.IsNullOrEmpty(quitMarker) && Environment.ExitCode == 0)
                File.WriteAllText(quitMarker, "clean-quit\n");
        }
        catch { /* launcher の診断失敗でも正常終了を妨げない。 */ }
    }

    static string State(bool supported, bool running) => !supported ? "未対応" : running ? "ON" : "停止";

    void ObserveHealthFrame(double now)
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        healthGame = PhoneSaberControlPanelHealth.ObserveGame(healthGame, now, Time.frameCount, scene.handle, scene.name == "Game",
            Time.unscaledDeltaTime * 1000);
    }

    PhoneSaberControlPanelState BuildPanel(double now)
    {
        panel = new PhoneSaberControlPanelState();
        panel.BeginSection("状態チェック · good-20261020");
        if (healthChecks != null)
            foreach (var check in healthChecks) panel.HealthLabel(check);
        panel.BeginSection("台: " + (string.IsNullOrEmpty(station) ? "指定なし" : station));
        panel.Label(redText);
        panel.Label(PhoneSaberPerfLog.Verdict);
        panel.Label(PhoneSaberPerfLog.LatencyLine(false, now));
        panel.Label(PhoneSaberPerfLog.LatencyLine(true, now));
        if (!string.IsNullOrEmpty(displayWarning)) panel.Label(displayWarning, true);
        if (!string.IsNullOrEmpty(redWarning)) panel.Label(redWarning, true);
        panel.Label(blueText);
        if (!string.IsNullOrEmpty(blueWarning)) panel.Label(blueWarning, true);
        panel.BeginSection("剣の揺れ補正・揺れ止め"); BuildFilter();
        panel.BeginSection("スマホの操作・PCの無視範囲"); BuildIgnoreZones(InputPoint.Instance, CurrentStation);
        panel.BeginSection("剣の遅延補正"); BuildPrediction();
        panel.BeginSection("画面同期・フレーム上限"); BuildDisplaySync();
        panel.BeginSection("判定の遅れ補正"); BuildJudgeLatency();
        panel.BeginSection("台ごとの位置補正"); BuildCalibration();
        panel.BeginSection("接続・イベントログ");
        panel.Label(services);
        panel.Toggle("スマホとPCのNTP同期を確認済み（片道遅延も判定）", clocksSynchronized, "clocks-synchronized");
        var log = PhoneSaberEventLog.Current;
        panel.Label("イベントログ: " + (log?.LogPath ?? "作成できません"));
        if (log != null && !string.IsNullOrEmpty(log.LastError)) panel.Label("ログ書込失敗: " + log.LastError, true);
        panel.Label("片道時計差=PC受信壁時計−ts。NTP同期時のみ有効（負値は時計差）。\n" +
            "受信間隔は同期不要。良好 p95<50/最大<150 ms、注意 p95<100/最大<500 ms。\n" +
            "最大間隔は現在の無受信時間も判定。両色を認識させ20サンプル以上で確認。");
        return panel;
    }

    void DrawGUI()
    {
        if (!visible && calibrationCorner < 0) return;
        if (textStyle == null)
        {
            // OS の既定フォントに頼らず、Windows Player でも日本語の警告を表示する。
            var font = Resources.Load<Font>("Fonts/NotoSansJP-Light");
            textStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 18, wordWrap = true };
            textStyle.normal.textColor = Color.white;
            warningStyle = new GUIStyle(textStyle);
            warningStyle.normal.textColor = new Color(1f, 0.75f, 0.2f);
            buttonStyle = new GUIStyle(GUI.skin.button) { font = font, fontSize = 18, wordWrap = true };
        }
        // Web 操作中も測定目印は投影画面へ描く。通常の F8 はパネルを描かない。
        if (calibrationCorner >= 0 && calibrationCorner < 4)
        {
            bool right = calibrationCorner == 1 || calibrationCorner == 2;
            bool bottom = calibrationCorner >= 2;
            float x = right ? Screen.width - 80 : 40, y = bottom ? Screen.height - 80 : 40;
            Color old = GUI.color;
            GUI.color = calibrationBlue ? Color.cyan : Color.red;
            GUI.DrawTexture(new Rect(x, y, 40, 40), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(Screen.width * .2f, Screen.height * .03f, Screen.width * .6f, 90),
                $"{calibrationCorner + 1}/4 {CornerNames[calibrationCorner]} — {(captureStarted >= 0 ? "採取中（静止）" : "剣を静止 → F7 / 操作画面で採取")}", textStyle);
        }
        if (!visible) return;
        Matrix4x4 previousMatrix = GUI.matrix;
        int previousDepth = GUI.depth;
        Color previousColour = GUI.color;
        float scale = Mathf.Min(1f, Screen.width / 840f, Screen.height / 730f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);
        GUI.depth = -10000;
        GUI.color = new Color(0.03f, 0.03f, 0.03f, 0.96f);
        GUI.DrawTexture(new Rect(10, 10, 820, 710), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUILayout.BeginArea(new Rect(10, 10, 820, 710), GUI.skin.box);
        GUILayout.Label("PhoneSaber 運営表示 [F8: Web / Shift+F8: 開閉]", textStyle);
        scrollPosition = GUILayout.BeginScrollView(scrollPosition);
        // 最初の Update 前でも内容を表示できる。
        if (panel == null)
        {
            double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
            Refresh(now);
            panel = BuildPanel(now);
        }
        foreach (var section in panel.Sections)
        {
            GUILayout.Space(8);
            GUILayout.Label(section.Title, warningStyle);
            foreach (var item in section.Items)
            {
                if (item.Name == null)
                {
                    var colour = GUI.color;
                    if (item.Grade != null) GUI.color = item.Grade == "OK" ? new Color(.5f, 1f, .6f) : item.Grade == "BAD" ? new Color(1f, .5f, .5f) : new Color(1f, .8f, .5f);
                    GUILayout.Label(item.Text, item.Warning ? warningStyle : textStyle);
                    GUI.color = colour;
                }
                else
                {
                    bool previousEnabled = GUI.enabled;
                    GUI.enabled = previousEnabled && item.Enabled;
                    if (GUILayout.Button((item.Selected ? "● " : "") + item.Text, buttonStyle)) ExecuteAction(item.Name, item.Value);
                    GUI.enabled = previousEnabled;
                }
            }
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
        GUI.matrix = previousMatrix;
        GUI.depth = previousDepth;
        GUI.color = previousColour;
    }
}
