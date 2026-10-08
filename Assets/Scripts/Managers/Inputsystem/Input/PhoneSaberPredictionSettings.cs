using UnityEngine;

// 台名の正規化は既存の位置補正と同じ。未保存の台は必ず OFF。
public static class PhoneSaberPredictionSettings
{
    public static string Key(string station) =>
        "PhoneSaber.Prediction." + PhoneSaberStation.Normalize(station) + ".HorizonMs.v1";

    public static int Load(string station) => Clamp(PlayerPrefs.GetInt(Key(station), 0));

    public static int Clamp(int milliseconds) =>
        Mathf.Clamp(milliseconds, 0, PhoneSaberEndpointPredictor.MaximumHorizonMilliseconds);

    public static void Save(string station, int milliseconds)
    {
        PlayerPrefs.SetInt(Key(station), Clamp(milliseconds));
        PlayerPrefs.Save();
    }
}
