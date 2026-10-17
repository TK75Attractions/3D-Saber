using UnityEngine;
using UnityEngine.SceneManagement;

// 本編は UnlockFrameRate が vsync を切り、60 Hz の画面へ約900 fps で描く（2026-10-13 のビルド済みアプリで実測）。
// 画面の破れ・コマ揺れと比べられるよう、台ごとに「画面同期 ON（vsync=1）」を明示選択できるようにする。
// 既定は「ゲームの設定のまま」で、このときは何も変更しない。Scene・ゲームコードには触れない。
public static class PhoneSaberDisplaySyncSettings
{
    public const int GameDefault = 0;
    public const int VSync = 1;

    public static string Key(string station) =>
        "PhoneSaber.DisplaySync." + PhoneSaberStation.Normalize(station) + ".v1";

    public static int Clamp(int mode) => mode == VSync ? VSync : GameDefault;

    public static int Load(string station) => Clamp(PlayerPrefs.GetInt(Key(station), GameDefault));

    public static void Save(string station, int mode)
    {
        PlayerPrefs.SetInt(Key(station), Clamp(mode));
        PlayerPrefs.Save();
    }

    // 書き込む vSyncCount。変更不要なら -1。ゲーム側が後から 0 に戻しても毎フレームの比較で戻す。
    public static int VSyncCountToApply(int mode, int currentVSyncCount) =>
        Clamp(mode) == VSync && currentVSyncCount != 1 ? 1 : -1;
}

// vsync とは別の上限。ゲームは vsync を切って上限なし（targetFrameRate=-1）で描くので、60 Hz の画面でも
// 約1100 fps になり、起動しているだけで Mac が熱くなっていた（2026-10-15 events.log の event=perf、Result 画面）。
// 遅延は vsync の有無で差が出なかった（2026-10-14 実測）ため、未設定の台は「画面の2倍」を既定にする。
// 上限は下げる向きだけに効かせ、ゲームが自分で決めた低い値（メニューの 30 fps など）は上げない。
// 本編（Game）以外のメニュー画面は入力の遅れが効かないので画面と同じ回数（60 Hz なら 60 fps）にする
// （2026-10-16: 上限 120 でもタイトルで CPU 約40%、Mac が熱いまま）。F9 などの計測中は本編と同じ倍率のまま。
public static class PhoneSaberFrameCapSettings
{
    public const int GameDefault = 0;
    public const int FourTimes = 1;
    public const int TwoTimes = 2;

    public static string Key(string station) =>
        "PhoneSaber.FrameCap." + PhoneSaberStation.Normalize(station) + ".v1";

    public static int Clamp(int mode) => mode == FourTimes || mode == TwoTimes ? mode : GameDefault;
    public const int Default = TwoTimes;
    public static int Load(string station) => Clamp(PlayerPrefs.GetInt(Key(station), Default));

    public static void Save(string station, int mode)
    {
        PlayerPrefs.SetInt(Key(station), Clamp(mode));
        PlayerPrefs.Save();
    }

    public static string Label(int mode) => Clamp(mode) == FourTimes ? "画面の4倍" :
        Clamp(mode) == TwoTimes ? "画面の2倍" : "ゲームの設定のまま";

    // 59.94 Hz は 240 / 120 fps（メニューは 60 fps）へ丸める。Hz 不明・範囲外・変更不要・ゲームの値がすでに上限以下なら -1。
    public static int TargetFrameRateToApply(int mode, double refreshHz, int currentTarget, bool menu = false)
    {
        mode = Clamp(mode);
        if (mode == GameDefault || !(refreshHz > 0)) return -1;
        double target = System.Math.Round(refreshHz * (menu ? 1 : mode == FourTimes ? 4 : 2));
        if (!(target >= 1 && target <= int.MaxValue)) return -1;
        return currentTarget > 0 && currentTarget <= (int)target ? -1 : (int)target;
    }
}

public sealed class PhoneSaberDisplaySync : MonoBehaviour
{
    public static int Mode { get; private set; }
    public static int FrameCapMode { get; private set; }
    // 注意の表示確認用(-phonesaberSmokeRefreshHz)。0 なら実際の画面の値を使う。
    public static double RefreshHzOverride;
    // 遅延の計測（F9・追従計測）中は true。メニューでも本編と同じ回数で描く（計測値を本編と揃える）。
    public static bool Measuring;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        Mode = PhoneSaberDisplaySyncSettings.Load(PhoneSaberStation.Read());
        FrameCapMode = PhoneSaberFrameCapSettings.Load(PhoneSaberStation.Read());
        var go = new GameObject("PhoneSaber Display Sync");
        go.AddComponent<PhoneSaberDisplaySync>().notice = go.AddComponent<PhoneSaberDisplayNotice>();
        DontDestroyOnLoad(go);
    }

    PhoneSaberDisplayNotice notice;
    double nextNoticeCheck;
    int sceneHandle;
    bool inGameScene;

    // OFF へ戻したときは値を書き戻さない（ゲームが次のシーンで自分の設定を入れ直す）。
    public static void SetMode(int mode)
    {
        mode = PhoneSaberDisplaySyncSettings.Clamp(mode);
        if (mode == Mode) return;
        Mode = mode;
        string station = PhoneSaberStation.Read();
        PhoneSaberDisplaySyncSettings.Save(station, mode);
        PhoneSaberEventLog.Record(station, "SYSTEM", "display-sync",
            mode == PhoneSaberDisplaySyncSettings.VSync ? "mode=vsync" : "mode=game-default");
    }

    // ゲーム既定へ戻すときは上書きを止めるだけ。次のシーンでゲーム自身の設定が入る。
    public static void SetFrameCapMode(int mode)
    {
        mode = PhoneSaberFrameCapSettings.Clamp(mode);
        if (mode == FrameCapMode) return;
        FrameCapMode = mode;
        string station = PhoneSaberStation.Read();
        PhoneSaberFrameCapSettings.Save(station, mode);
        PhoneSaberEventLog.Record(station, "SYSTEM", "frame-cap", "mode=" + mode);
    }

    // UnlockFrameRate.Awake より後に効かせるため Update で見る（int の比較1回）。
    void Update()
    {
        int value = PhoneSaberDisplaySyncSettings.VSyncCountToApply(Mode, QualitySettings.vSyncCount);
        if (value >= 0) QualitySettings.vSyncCount = value;
        if (FrameCapMode != PhoneSaberFrameCapSettings.GameDefault)
        {
            // シーン名は切り替わったときだけ読む（毎フレームの文字列確保を避ける）。
            Scene scene = SceneManager.GetActiveScene();
            if (scene.handle != sceneHandle)
            {
                sceneHandle = scene.handle;
                inGameScene = scene.name == PhoneSaberDisplayNotice.GameSceneName;
            }
            int target = PhoneSaberFrameCapSettings.TargetFrameRateToApply(FrameCapMode,
                Screen.currentResolution.refreshRateRatio.value, Application.targetFrameRate, !inGameScene && !Measuring);
            if (target >= 0) Application.targetFrameRate = target;
        }
        // 画面の更新が遅い（低電力モードなど）ときの知らせ。確認は秒1回、描画は必要なときだけ有効にする。
        double now = Time.realtimeSinceStartupAsDouble;
        if (notice == null || now < nextNoticeCheck) return;
        nextNoticeCheck = now + 1;
        string sceneName = SceneManager.GetActiveScene().name;
        notice.Text = PhoneSaberDisplayNotice.Join(PhoneSaberDisplayNotice.TextFor(
            PhoneSaberStatsDisplay.DisplayWarning(RefreshHzOverride > 0
                ? RefreshHzOverride : Screen.currentResolution.refreshRateRatio.value),
            sceneName), sceneName == PhoneSaberDisplayNotice.GameSceneName ? "" : PhoneSaberDisplayNotice.PhoneWarning);
        notice.enabled = notice.Text.Length > 0;
    }
}

// F8 を開かなくても気づけるよう、メニュー画面の上端に出す（2026-10-13: 低電力モードのまま起動して
// 「アプリにすると重い」となることが続いた）。本編（Game）では出さない（プレイ画面を隠さない）。
public sealed class PhoneSaberDisplayNotice : MonoBehaviour
{
    public const string GameSceneName = "Game";
    public string Text = "";
    GUIStyle style;

    // スマホの発熱・テザリング親機など（PhoneSaberJudgeLatency が数秒ごとに更新）。メニュー画面だけに出す。
    public static string PhoneWarning = "";

    public static string Join(string a, string b) =>
        string.IsNullOrEmpty(a) ? b ?? "" : string.IsNullOrEmpty(b) ? a : a + "\n" + b;

    // 2026-10-17 実測: iPhone がテザリングの親機（172.20.10.1）のまま thermal=serious になり、ずっと 30 fps だった。
    public static string PhoneWarningFor(string sender, string thermal, double fpsMode)
    {
        string text = "";
        if (thermal == "serious" || thermal == "critical")
            text = Join(text, $"スマホが熱い（{thermal}）: カメラが30fpsに落ちて遅れが増えます。ケースを外す・充電を外す・風を当てて冷ましてください。");
        else if (fpsMode > 0 && fpsMode < 50)
            text = Join(text, "スマホのカメラが30fpsです（60fpsより遅れが約15ms増）。発熱か設定を確認してください。");
        if (sender == "172.20.10.1")
            text = Join(text, "スマホがテザリングの親機です。Wi-Fi で親機をすると発熱します。Wi-Fiルーター・別の端末を親機にするか、USBケーブルでつないでください。");
        return text;
    }

    public static string TextFor(string displayWarning, string sceneName) =>
        string.IsNullOrEmpty(displayWarning) || sceneName == GameSceneName ? "" :
            displayWarning + "\n（Mac: システム設定 > バッテリー > 低電力モード を「しない」に）";

    void Awake() => enabled = false;

    void OnGUI()
    {
        if (Text.Length == 0) return;
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            style.normal.textColor = Color.yellow;
        }
        style.fontSize = Mathf.Max(14, Screen.height / 40);
        int lines = 1;
        foreach (char c in Text) if (c == '\n') lines++;
        float height = style.fontSize * (1.2f + 1.3f * lines);
        GUI.Box(new Rect(Screen.width * 0.1f, 8, Screen.width * 0.8f, height), Text, style);
    }
}
