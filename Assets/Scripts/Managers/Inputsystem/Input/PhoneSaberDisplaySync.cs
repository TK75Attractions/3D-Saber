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

public sealed class PhoneSaberDisplaySync : MonoBehaviour
{
    public static int Mode { get; private set; }
    // 注意の表示確認用(-phonesaberSmokeRefreshHz)。0 なら実際の画面の値を使う。
    public static double RefreshHzOverride;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        Mode = PhoneSaberDisplaySyncSettings.Load(PhoneSaberStation.Read());
        var go = new GameObject("PhoneSaber Display Sync");
        go.AddComponent<PhoneSaberDisplaySync>().notice = go.AddComponent<PhoneSaberDisplayNotice>();
        DontDestroyOnLoad(go);
    }

    PhoneSaberDisplayNotice notice;
    double nextNoticeCheck;

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

    // UnlockFrameRate.Awake より後に効かせるため Update で見る（int の比較1回）。
    void Update()
    {
        int value = PhoneSaberDisplaySyncSettings.VSyncCountToApply(Mode, QualitySettings.vSyncCount);
        if (value >= 0) QualitySettings.vSyncCount = value;
        // 画面の更新が遅い（低電力モードなど）ときの知らせ。確認は秒1回、描画は必要なときだけ有効にする。
        double now = Time.realtimeSinceStartupAsDouble;
        if (notice == null || now < nextNoticeCheck) return;
        nextNoticeCheck = now + 1;
        notice.Text = PhoneSaberDisplayNotice.TextFor(
            PhoneSaberStatsDisplay.DisplayWarning(RefreshHzOverride > 0
                ? RefreshHzOverride : Screen.currentResolution.refreshRateRatio.value),
            SceneManager.GetActiveScene().name);
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
        float height = style.fontSize * 3.6f;
        GUI.Box(new Rect(Screen.width * 0.1f, 8, Screen.width * 0.8f, height), Text, style);
    }
}
