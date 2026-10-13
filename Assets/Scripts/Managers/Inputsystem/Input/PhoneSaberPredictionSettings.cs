using UnityEngine;

// 台名の正規化は既存の位置補正と同じ。未保存の台は必ず OFF。
public static class PhoneSaberPredictionSettings
{
    public static string Key(string station) =>
        "PhoneSaber.Prediction." + PhoneSaberStation.Normalize(station) + ".HorizonMs.v1";

    public static int Load(string station) => Clamp(PlayerPrefs.GetInt(Key(station), 0));

    // 自動も明示選択した台だけで有効。時計差は片道遅延の測定値として使わない。
    public const int AutomaticFallbackMilliseconds = 0;
    public static string AutomaticKey(string station) => Key(station) + ".Auto";
    public static bool LoadAutomatic(string station) => PlayerPrefs.GetInt(AutomaticKey(station), 0) == 1;

    public static void SaveAutomatic(string station, bool automatic)
    {
        PlayerPrefs.SetInt(AutomaticKey(station), automatic ? 1 : 0);
        PlayerPrefs.Save();
    }

    public static int AutomaticLead(PhoneSaberInputStats stats, bool clocksSynchronized, out bool measured)
    {
        var delay = stats.OneWayDelay;
        measured = clocksSynchronized && stats.HasPacket && stats.PayloadParsed &&
            stats.SecondsSinceLastPacket < PhoneSaberEndpointPredictor.MaximumGapSeconds &&
            delay.Count >= PhoneSaberStatsDisplay.MinimumTimingSamples && delay.MinMs >= 0 &&
            !double.IsNaN(delay.MedianMs) && !double.IsInfinity(delay.MedianMs);
        // 上限を先に適用して、異常な壁時計差を int へ変換しない。
        return measured ? Clamp((int)System.Math.Round(System.Math.Min(delay.MedianMs, 60))) : AutomaticFallbackMilliseconds;
    }

    public static int Clamp(int milliseconds) =>
        Mathf.Clamp(milliseconds, 0, PhoneSaberEndpointPredictor.MaximumHorizonMilliseconds);

    public static void Save(string station, int milliseconds)
    {
        PlayerPrefs.SetInt(Key(station), Clamp(milliseconds));
        PlayerPrefs.Save();
    }
}
