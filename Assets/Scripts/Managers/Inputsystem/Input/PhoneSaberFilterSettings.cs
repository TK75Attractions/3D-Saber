using UnityEngine;

// 位置補正・予測と同じ台名を使う。未保存の台は必ず OFF。
public static class PhoneSaberFilterSettings
{
    public static int Revision { get; private set; }

    public static string Key(string station) =>
        "PhoneSaber.EndpointFilter." + PhoneSaberStation.Normalize(station) + ".Mode.v1";

    public static int Clamp(int mode) => Mathf.Clamp(mode, PhoneSaberEndpointFilter.Off, PhoneSaberEndpointFilter.Medium);

    public static int Load(string station) => Clamp(PlayerPrefs.GetInt(Key(station), PhoneSaberEndpointFilter.Off));

    public static string Label(int mode) => mode == PhoneSaberEndpointFilter.Weak ? "弱" :
        mode == PhoneSaberEndpointFilter.Medium ? "中" : "OFF";

    public static void Save(string station, int mode)
    {
        PlayerPrefs.SetInt(Key(station), Clamp(mode));
        PlayerPrefs.Save();
        Revision++;
    }
}
