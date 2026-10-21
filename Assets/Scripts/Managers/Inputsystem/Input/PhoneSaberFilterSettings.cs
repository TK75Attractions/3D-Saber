using UnityEngine;

// 位置補正・予測と同じ台名を使う。未保存の台は「中」（2026-10-18 実機で ON の具合が良く、ユーザーが既定 ON を指示）。
public static class PhoneSaberFilterSettings
{
    public static int Revision { get; private set; }

    public static string Key(string station) =>
        "PhoneSaber.EndpointFilter." + PhoneSaberStation.Normalize(station) + ".Mode.v1";

    public static int Clamp(int mode) => mode == PhoneSaberEndpointFilter.Steady || mode == PhoneSaberEndpointFilter.Responsive ? mode :
        Mathf.Clamp(mode, PhoneSaberEndpointFilter.Off, PhoneSaberEndpointFilter.Medium);

    public const int DefaultMode = PhoneSaberEndpointFilter.Medium;
    public static bool CanUseSteady(string station) => PhoneSaberPredictionSettings.LoadAutomatic(station) ||
        PhoneSaberPredictionSettings.Load(station) == 0;

    public static int Load(string station)
    {
        int mode = Clamp(PlayerPrefs.GetInt(Key(station), DefaultMode));
        return mode == PhoneSaberEndpointFilter.Steady && !CanUseSteady(station) ? DefaultMode : mode;
    }

    public static string Label(int mode) => mode == PhoneSaberEndpointFilter.Weak ? "弱" :
        mode == PhoneSaberEndpointFilter.Medium ? "中" : mode == PhoneSaberEndpointFilter.Steady ? "形状安定" :
        mode == PhoneSaberEndpointFilter.Responsive ? "低遅延（試験）" : "OFF";

    public static void Save(string station, int mode)
    {
        mode = Clamp(mode);
        if (mode == PhoneSaberEndpointFilter.Steady && !CanUseSteady(station)) mode = DefaultMode;
        PlayerPrefs.SetInt(Key(station), mode);
        PlayerPrefs.Save();
        Revision++;
    }

    // 手動/OFFの変更は自動フラグ→時間の順に保存される。最終的な時間を読むまで選択は消さない。
    public static void PredictionModeChanged(string station)
    {
        if (PlayerPrefs.GetInt(Key(station), DefaultMode) == PhoneSaberEndpointFilter.Steady) Revision++;
    }

    // 手動の端点別予測と半軸平滑化を併用すると中点の揺れが増える。設定変更時に履歴も再開する。
    public static void RestoreMediumForManualPrediction(string station)
    {
        if (PlayerPrefs.GetInt(Key(station), DefaultMode) == PhoneSaberEndpointFilter.Steady && !CanUseSteady(station))
            Save(station, DefaultMode);
    }
}
