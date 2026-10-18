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
    // 未保存の台は「自動」（2026-10-18 ユーザー: 予測なしは遅延がひどい。既定 ON を指示）。手動・OFF を選ぶと保存される。
    public static bool LoadAutomatic(string station) => PlayerPrefs.GetInt(AutomaticKey(station), 1) == 1;

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

    public static double? AutomaticEstimate(PhoneSaberLatencyEstimate estimate) =>
        estimate != null && estimate.Status == "estimate" ? estimate.TotalMs : null;

    public static int Clamp(int milliseconds) =>
        Mathf.Clamp(milliseconds, 0, PhoneSaberEndpointPredictor.MaximumManualHorizonMilliseconds);

    public static void Save(string station, int milliseconds)
    {
        PlayerPrefs.SetInt(Key(station), Clamp(milliseconds));
        PlayerPrefs.Save();
    }
}

// 色ごとに保持する純粋な方針。欠測の期限と送信元変更で履歴を破棄する。
public sealed class PhoneSaberAutomaticLeadPolicy
{
    readonly System.Collections.Generic.List<(double at, double value)> samples =
        new System.Collections.Generic.List<(double, double)>();
    string identity, deviceIdentity;
    double validUntil = double.NegativeInfinity;
    double changedAt = double.NegativeInfinity, chosenMedian;
    string sampleSource;
    public int Lead { get; private set; }
    public string Source { get; private set; } = "fallback";
    public bool Clamped { get; private set; }
    public double Median { get; private set; }
    public bool NeedsExpiry(double now) => Source != "fallback" && now >= validUntil;

    public bool Update(double now, string sender, double? estimate, double? oneWay,
        double? expiresAt = null, string device = null)
    {
        bool fresh = !expiresAt.HasValue || now < expiresAt.Value;
        string source = fresh && Valid(estimate) ? "automatic-estimate" : fresh && Valid(oneWay) ? "one-way-ts" : "fallback";
        double? value = source == "automatic-estimate" ? estimate : source == "one-way-ts" ? oneWay : null;
        int oldLead = Lead; string oldSource = Source; bool oldClamp = Clamped;
        if (identity != sender || device != null && deviceIdentity != null && deviceIdentity != device)
        {
            samples.Clear(); identity = sender; deviceIdentity = device; sampleSource = null;
            Median = 0; validUntil = double.NegativeInfinity;
            // 新しい送信元や測定経路へ、前の色/端末の補正量を持ち越さない。
            Lead = 0; Source = "fallback"; Clamped = false;
            changedAt = double.NegativeInfinity;
        }
        if (device != null) deviceIdentity = device;
        if (value.HasValue)
        {
            // 元の計測の期限を超えて保持しない。PC統計窓の短い切替だけを吸収する。
            validUntil = System.Math.Min(now + 15, expiresAt ?? now + 15);
            if (sampleSource != source) { samples.Clear(); sampleSource = source; }
            samples.RemoveAll(s => now - s.at > 30);
            samples.Add((now, value.Value));
            var sorted = new double[samples.Count];
            for (int i = 0; i < sorted.Length; i++) sorted[i] = samples[i].value;
            System.Array.Sort(sorted);
            Median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] :
                (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
            if (now - changedAt >= 10 && (Source != source || System.Math.Abs(Median - chosenMedian) > 5))
            {
                chosenMedian = Median; changedAt = now; Source = source;
                Clamped = Median > PhoneSaberEndpointPredictor.MaximumHorizonMilliseconds;
                Lead = (int)System.Math.Round(
                    System.Math.Min(Median, PhoneSaberEndpointPredictor.MaximumHorizonMilliseconds));
            }
        }
        else if (now >= System.Math.Min(validUntil, expiresAt ?? validUntil))
        {
            Lead = 0; Source = "fallback"; Clamped = false; Median = 0; samples.Clear();
        }
        return oldLead != Lead || oldSource != Source || oldClamp != Clamped;
    }

    static bool Valid(double? n) => n.HasValue && n >= 0 && !double.IsNaN(n.Value) && !double.IsInfinity(n.Value);
}
