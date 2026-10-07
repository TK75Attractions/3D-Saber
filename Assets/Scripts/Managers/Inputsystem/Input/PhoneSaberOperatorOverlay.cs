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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        // 当日用の設定は PhoneSaber 起動時に保証する。PlayerSettings は変更しない。
        Application.runInBackground = true;
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
            visible = !visible;
            nextRefresh = 0;
        }
        if (!visible) return;
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        if (now < nextRefresh) return;
        nextRefresh = now + 0.2;
        Refresh(now);
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
