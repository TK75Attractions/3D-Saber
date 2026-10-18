using UnityEngine;

// 位置補正・予測と同じ台名を使う。未保存の台は「中」（2026-10-18 実機で ON の具合が良く、ユーザーが既定 ON を指示）。
public static class PhoneSaberFilterSettings
{
    public static int Revision { get; private set; }

    public static string Key(string station) =>
        "PhoneSaber.EndpointFilter." + PhoneSaberStation.Normalize(station) + ".Mode.v1";

    public static int Clamp(int mode) => Mathf.Clamp(mode, PhoneSaberEndpointFilter.Off, PhoneSaberEndpointFilter.Medium);

    public const int DefaultMode = PhoneSaberEndpointFilter.Medium;
    public static int Load(string station) => Clamp(PlayerPrefs.GetInt(Key(station), DefaultMode));

    public static string Label(int mode) => mode == PhoneSaberEndpointFilter.Weak ? "弱" :
        mode == PhoneSaberEndpointFilter.Medium ? "中" : "OFF";

    public static void Save(string station, int mode)
    {
        PlayerPrefs.SetInt(Key(station), Clamp(mode));
        PlayerPrefs.Save();
        Revision++;
    }
}
