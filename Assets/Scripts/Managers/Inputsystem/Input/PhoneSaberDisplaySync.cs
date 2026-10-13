using UnityEngine;

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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        Mode = PhoneSaberDisplaySyncSettings.Load(PhoneSaberStation.Read());
        var go = new GameObject("PhoneSaber Display Sync");
        go.AddComponent<PhoneSaberDisplaySync>();
        DontDestroyOnLoad(go);
    }

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
    }
}
