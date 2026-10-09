using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

// 運営専用の受信表示。シーンや prefab を変更せず、通常の Player でも F8 で開ける。
public sealed class PhoneSaberOperatorOverlay : MonoBehaviour
{
    bool visible;
    double nextLogFlush;
    string quitMarker;
    bool clocksSynchronized;
    Vector2 scrollPosition;
    double startedAt;
    double nextRefresh;
    string fallbackStation;
    string station;
    string services;
    string redText;
    string blueText;
    string redWarning;
    string blueWarning;
    GUIStyle textStyle;
    GUIStyle warningStyle;
    GUIStyle toggleStyle;
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
        overlay.visible = false;
        overlay.clocksSynchronized = false;
        overlay.startedAt = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        overlay.fallbackStation = PhoneSaberStation.Read();
        overlay.nextLogFlush = 0;
        overlay.quitMarker = null;
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
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.f8Key.wasPressedThisFrame)
        {
            // F8 は開閉だけ（スタッフマニュアルの操作）。揺れ補正は表示内のボタンでだけ変える。
            visible = !visible;
            if (!visible && calibrationCorner >= 0)
            {
                CancelCalibration();
                calibrationMessage = "測定をキャンセルしました。";
            }
            nextRefresh = 0;
        }
        if (keyboard != null && keyboard.f7Key.wasPressedThisFrame)
        {
            visible = true;
            AdvanceCalibration();
            nextRefresh = 0;
        }
        PollCalibration(logNow);
        if (!visible) return;
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        if (now < nextRefresh) return;
        nextRefresh = now + 0.2;
        Refresh(now);
    }

    void CancelCalibration()
    {
        calibrationInput?.PositionCapture.Cancel();
        calibrationInput = null;
        calibrationCorner = -1;
        captureStarted = -1;
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

    void DrawCalibration()
    {
        var input = InputPoint.Instance;
        GUILayout.Label("台ごとの位置補正 [F7: 開始 / 隅を採取]", textStyle);
        GUILayout.Label("状態: " + (input != null && input.PositionCalibrationEnabled ? "ON" : "OFF") +
            (input != null && input.HasPositionCalibration ? "（保存済み）" : "（未設定・従来の座標）"), textStyle);
        GUILayout.Label("カメラは画面下の箱からプレイヤーを撮影。立ち位置は画面から約2.7〜3.1m、プレイ範囲は直径1.5m。\n" +
            "点灯した剣の中点で左上→右上→右下→左下を測定します。画面の剣の位置ではなく、実際に振る範囲の隅に合わせてください。", textStyle);
        GUILayout.Label(calibrationMessage, warningStyle);
        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && calibrationCorner < 0;
        calibrationBlue = GUILayout.Toggle(calibrationBlue, "BLUEの剣を採取（OFFならRED）", toggleStyle);
        GUI.enabled = previousEnabled;
        if (calibrationCorner >= 0)
        {
            GUILayout.Label($"{calibrationCorner + 1}/4: {CornerNames[calibrationCorner]}で剣を静止 → F7で1秒採取", textStyle);
            GUI.enabled = previousEnabled && captureStarted < 0;
            if (GUILayout.Button("この隅を採取 [F7]", buttonStyle)) AdvanceCalibration();
            GUI.enabled = previousEnabled;
            if (GUILayout.Button("測定をキャンセル（保存済みの設定は維持）", buttonStyle))
            {
                CancelCalibration();
                calibrationMessage = "測定をキャンセルしました。";
            }
        }
        else
        {
            if (GUILayout.Button("4隅の測定を開始 [F7]", buttonStyle)) AdvanceCalibration();
            GUI.enabled = previousEnabled && input != null && input.HasPositionCalibration;
            bool enabled = input != null && input.PositionCalibrationEnabled;
            bool selected = GUILayout.Toggle(enabled, "位置補正をON（この台だけ）", toggleStyle);
            if (selected != enabled && input != null) input.SetPositionCalibrationEnabled(selected);
            if (GUILayout.Button("この台の位置補正をリセット（OFFに戻す）", buttonStyle) && input != null)
            {
                input.ResetPositionCalibration();
                calibrationMessage = "この台の位置補正を削除し、従来の座標に戻しました。";
            }
            GUI.enabled = previousEnabled;
        }
    }

    void DrawPrediction()
    {
        var input = InputPoint.Instance;
        int horizon = input != null ? input.PredictionHorizonMilliseconds : 0;
        GUILayout.Label($"剣の遅延補償: {(horizon == 0 ? "OFF" : horizon + " ms")}（この台に保存）", textStyle);
        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && input != null && calibrationCorner < 0;
        GUILayout.BeginHorizontal();
        foreach (int milliseconds in PredictionHorizons)
        {
            string label = milliseconds == 0 ? "0 ms / OFF" : milliseconds + " ms";
            if (GUILayout.Button((horizon == milliseconds ? "● " : "") + label, buttonStyle) &&
                horizon != milliseconds)
                input.SetPredictionHorizonMilliseconds(milliseconds);
        }
        GUILayout.EndHorizontal();
        GUI.enabled = previousEnabled;
        GUILayout.Label("初期値はOFF。20 msから試してください。折り返しでは行き過ぎが増えます。\n" +
            "端点の移動上限は0.35、受信が100 ms止まると予測量はゼロに戻ります。", textStyle);
    }

    void DrawFilter()
    {
        var input = InputPoint.Instance;
        string name = input != null ? input.StationLabel : fallbackStation;
        int mode = PhoneSaberFilterSettings.Load(name);
        GUILayout.Label($"剣の揺れ補正: {PhoneSaberFilterSettings.Label(mode)}（この台の両色に保存）", textStyle);
        GUILayout.BeginHorizontal();
        for (int value = PhoneSaberEndpointFilter.Off; value <= PhoneSaberEndpointFilter.Medium; value++)
            if (GUILayout.Button((mode == value ? "● " : "") + PhoneSaberFilterSettings.Label(value), buttonStyle) && mode != value)
                PhoneSaberFilterSettings.Save(name, value);
        GUILayout.EndHorizontal();
        GUILayout.Label("初期値はOFF。静止時の揺れを少し抑えます。速い振りにも小さな遅れが加わります。", textStyle);
    }

    void Refresh(double now)
    {
        var input = InputPoint.Instance;
        var empty = new PhoneSaberInputStats(false, 0, now - startedAt, "", false);
        var red = input != null ? input.ReadInputStats() : empty;
        var blue = input != null ? input.ReadInputStats(true) : empty;
        bool mac = PhoneSaberBonjourPublisher.IsSupported;
        station = input != null ? input.StationLabel : fallbackStation;
        redText = PhoneSaberStatsDisplay.Format("RED", input != null ? input.port : 5005, red, mac, clocksSynchronized);
        blueText = PhoneSaberStatsDisplay.Format("BLUE", input != null ? input.port2 : 5006, blue, mac, clocksSynchronized);
        redWarning = PhoneSaberStatsDisplay.Warning("RED", red);
        blueWarning = PhoneSaberStatsDisplay.Warning("BLUE", blue);
        services = $"探索 UDP 5007: {State(true, input != null && input.DiscoveryResponderRunning)}\n" +
            $"Bonjour: {State(mac, input != null && input.BonjourPublisherRunning)}  |  " +
            $"P2P bridge: {State(PhoneSaberP2PBridgeProcess.IsSupported, PhoneSaberP2PBridgeProcess.IsRunning)}\n" +
            $"UDP受信機: RED {(input != null && input.ReceiverAlive ? "ON" : "停止")} / " +
            $"BLUE {(input != null && input.ReceiverAlive2 ? "ON" : "停止")}";
    }

    void OnApplicationQuit()
    {
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

    void OnGUI()
    {
        if (!visible) return;
        if (textStyle == null)
        {
            // OS の既定フォントに頼らず、Windows Player でも日本語の警告を表示する。
            var font = Resources.Load<Font>("Fonts/NotoSansJP-Light");
            textStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 18, wordWrap = true };
            textStyle.normal.textColor = Color.white;
            warningStyle = new GUIStyle(textStyle);
            warningStyle.normal.textColor = new Color(1f, 0.75f, 0.2f);
            toggleStyle = new GUIStyle(GUI.skin.toggle) { font = font, fontSize = 18, wordWrap = true };
            toggleStyle.normal.textColor = Color.white;
            toggleStyle.onNormal.textColor = Color.white;
            buttonStyle = new GUIStyle(GUI.skin.button) { font = font, fontSize = 18, wordWrap = true };
        }
        Matrix4x4 previousMatrix = GUI.matrix;
        int previousDepth = GUI.depth;
        Color previousColour = GUI.color;
        float scale = Mathf.Min(1f, Screen.width / 840f, Screen.height / 730f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);
        GUI.depth = -10000;
        // 明るいゲーム背景でも受信状態と警告が読めるよう、表示部分を暗く覆う。
        GUI.color = new Color(0.03f, 0.03f, 0.03f, 0.96f);
        GUI.DrawTexture(new Rect(10, 10, 820, 710), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUILayout.BeginArea(new Rect(10, 10, 820, 710), GUI.skin.box);
        GUILayout.Label($"PhoneSaber 運営表示 [F8: 開閉]  |  台: {(string.IsNullOrEmpty(station) ? "指定なし" : station)}", textStyle);
        // 日本語の折り返しが増えても、下の色や警告を切り落とさない。
        scrollPosition = GUILayout.BeginScrollView(scrollPosition);
        DrawFilter();
        GUILayout.Space(8);
        DrawPrediction();
        GUILayout.Space(8);
        DrawCalibration();
        GUILayout.Space(8);
        GUILayout.Label(services, textStyle);
        bool synchronized = GUILayout.Toggle(clocksSynchronized,
            "スマホとPCのNTP同期を確認済み（片道遅延も判定）", toggleStyle);
        if (synchronized != clocksSynchronized) { clocksSynchronized = synchronized; nextRefresh = 0; }
        GUILayout.Space(8);
        GUILayout.Label(redText, textStyle);
        if (!string.IsNullOrEmpty(redWarning)) GUILayout.Label(redWarning, warningStyle);
        GUILayout.Space(8);
        GUILayout.Label(blueText, textStyle);
        if (!string.IsNullOrEmpty(blueWarning)) GUILayout.Label(blueWarning, warningStyle);
        GUILayout.Space(8);
        var log = PhoneSaberEventLog.Current;
        GUILayout.Label("イベントログ: " + (log?.LogPath ?? "作成できません"), textStyle);
        if (log != null && !string.IsNullOrEmpty(log.LastError))
            GUILayout.Label("ログ書込失敗: " + log.LastError, warningStyle);
        GUILayout.Label("片道時計差=PC受信壁時計−ts。NTP同期時のみ有効（負値は時計差）。\n" +
            "受信間隔は同期不要。良好 p95<50/最大<150 ms、注意 p95<100/最大<500 ms。\n" +
            "最大間隔は現在の無受信時間も判定。両色を認識させ20サンプル以上で確認。", textStyle);
        GUILayout.EndScrollView();
        GUILayout.EndArea();
        GUI.matrix = previousMatrix;
        GUI.depth = previousDepth;
        GUI.color = previousColour;
    }
}
