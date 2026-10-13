using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

// 座標とは別の診断。時計同期や認識結果を使わず、欠測をゼロで補わない。
public sealed class PhoneSaberMetrics
{
    public string Station, Platform, App, Route, Thermal, C2sEnd;
    public double FpsMode, RedFps, BlueFps;
    public double? CaptureFps;
    public double? C2sMedian, C2sP95, Rtt, RttAge;
    public int C2sCount, RttCount;
    public double? Battery;
    public int LowLatencyTest;
    public double ReceivedAt;
    public const int MaximumBytes = 399;

    public static bool TryParse(byte[] data, int length, out PhoneSaberMetrics metrics)
    {
        metrics = null;
        if (data == null || length <= 0 || length > MaximumBytes || length > data.Length) return false;
        for (int i = 0; i < length; i++) if (data[i] < 32 || data[i] > 126) return false;
        var tokens = Encoding.ASCII.GetString(data, 0, length).Split(' ');
        if (tokens.Length < 3 || tokens[0] != "PHONESABER_METRICS" || tokens[1] != "1") return false;
        var fields = new Dictionary<string, string>();
        for (int i = 2; i < tokens.Length; i++)
        {
            int equals = tokens[i].IndexOf('=');
            if (equals <= 0 || equals == tokens[i].Length - 1) return false;
            string key = tokens[i].Substring(0, equals);
            if (fields.ContainsKey(key)) return false;
            fields.Add(key, tokens[i].Substring(equals + 1));
        }
        try
        {
            var m = new PhoneSaberMetrics {
                Station = Atom(fields["station"], 16), Platform = fields["platform"], App = Atom(fields["app"], 24),
                Route = fields["route"], Thermal = Atom(fields["thermal"], 16), C2sEnd = fields["c2s-end"],
                FpsMode = Number(fields["fps-mode"], 1, 240),
                CaptureFps = Optional(fields["capture-fps"], 1000),
                RedFps = Number(fields["send-red-fps"], 0, 1000), BlueFps = Number(fields["send-blue-fps"], 0, 1000),
                C2sMedian = Optional(fields["c2s-median-ms"], 2000), C2sP95 = Optional(fields["c2s-p95-ms"], 2000),
                C2sCount = Count(fields["c2s-n"]), Rtt = Optional(fields["rtt-ms"], 1000),
                RttCount = Count(fields["rtt-n"]), RttAge = Optional(fields["rtt-age-s"], 10)
            };
            if ((m.Platform != "ios" && m.Platform != "android") || m.Route != "LAN" ||
                (m.C2sEnd != "detection" && m.C2sEnd != "os-send") ||
                (m.C2sMedian.HasValue != m.C2sP95.HasValue) ||
                (m.C2sCount > 0) != m.C2sMedian.HasValue || m.C2sP95 < m.C2sMedian ||
                (m.RttCount > 0) != m.Rtt.HasValue || m.Rtt.HasValue != m.RttAge.HasValue) return false;
            // 電池と試験設定も範囲を確認する。未知の追加キーは使わない。
            m.Battery = Optional(fields["battery"], 100);
            m.LowLatencyTest = (int)Number(fields["low-latency-test"], 0, 1);
            if (fields["low-latency-test"] != "0" && fields["low-latency-test"] != "1") return false;
            if (m.FpsMode != Math.Floor(m.FpsMode)) return false;
            metrics = m; return true;
        }
        catch (Exception e) when (e is KeyNotFoundException || e is FormatException || e is OverflowException) { return false; }
    }

    static string Atom(string text, int maximum)
    {
        if (text.Length == 0 || text.Length > maximum) throw new FormatException();
        foreach (char c in text) if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
            (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.' || c == '?')) throw new FormatException();
        return text;
    }
    static double Number(string text, double minimum, double maximum)
    {
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n) ||
            double.IsNaN(n) || double.IsInfinity(n) || n < minimum || n > maximum) throw new FormatException();
        return n;
    }
    static double? Optional(string text, double maximum) => text == "unknown" ? (double?)null : Number(text, 0, maximum);
    static int Count(string text) { double n = Number(text, 0, 2400); if (n != Math.Floor(n)) throw new FormatException(); return (int)n; }
}

// 受信 thread は最大8送信元だけ保持し、全体で毎秒8件まで解析する。探索はこの制限の対象外。
public sealed class PhoneSaberMetricsStore
{
    readonly object gate = new object();
    readonly Dictionary<string, PhoneSaberMetrics> latest = new Dictionary<string, PhoneSaberMetrics>();
    double budgetAt = double.NegativeInfinity;
    int parsed;
    public const double FreshSeconds = 15;
    public void Receive(byte[] data, int length, string sender, double now)
        => Receive(data, length, sender, null, now);
    public void Receive(byte[] data, int length, IPAddress sender, double now)
        => Receive(data, length, null, sender, now);
    void Receive(byte[] data, int length, string sender, IPAddress address, double now)
    {
        lock (gate)
        {
            if (now - budgetAt >= 1) { budgetAt = now; parsed = 0; }
            if (++parsed > 8 || !PhoneSaberMetrics.TryParse(data, length, out var metrics)) return;
            sender = address?.ToString() ?? sender;
            if (sender == null) return;
            if (!latest.ContainsKey(sender) && latest.Count >= 8)
            {
                string oldest = null; double at = double.PositiveInfinity;
                foreach (var pair in latest) if (pair.Value.ReceivedAt < at) { oldest = pair.Key; at = pair.Value.ReceivedAt; }
                if (now - at < FreshSeconds) return;
                latest.Remove(oldest);
            }
            metrics.ReceivedAt = now;
            latest[sender] = metrics;
        }
    }
    public PhoneSaberMetrics Read(string sender, double now)
    {
        lock (gate) return sender != null && latest.TryGetValue(sender, out var m) && now - m.ReceivedAt <= FreshSeconds ? m : null;
    }
    public int Count { get { lock (gate) return latest.Count; } }
}

public sealed class PhoneSaberLatencyEstimate
{
    public readonly PhoneSaberMetrics Phone;
    public readonly PhoneSaberTimingStats Pc;
    public readonly double At, RefreshHz, CoordinateAge;
    public readonly int Vsync;
    public readonly string Sender, Status, DisplaySource;
    public readonly double DisplayAgeMinutes;
    public readonly double? NetworkMs, DisplayMs, TotalMs;
    public PhoneSaberLatencyEstimate(PhoneSaberMetrics phone, PhoneSaberTimingStats pc, string sender,
        double coordinateAge, double now, double refreshHz, int vsync, double? measuredDisplay = null, double displayAgeMinutes = 0)
    {
        Phone = phone; Pc = pc; At = now; RefreshHz = refreshHz; Vsync = vsync; Sender = sender ?? "unknown";
        CoordinateAge = coordinateAge;
        DisplaySource = measuredDisplay.HasValue && PhoneSaberDisplayMeasurement.IsPlausible(measuredDisplay.Value) ? "measured" : "configured-half-refresh";
        DisplayAgeMinutes = displayAgeMinutes;
        DisplayMs = DisplaySource == "measured" ? measuredDisplay : refreshHz > 0 && !double.IsInfinity(refreshHz) ? 500.0 / refreshHz : (double?)null;
        NetworkMs = phone?.Rtt / 2;
        Status = phone == null || Sender == "127.0.0.1" || Sender == "::1" ||
            now - phone.ReceivedAt > PhoneSaberMetricsStore.FreshSeconds ? "missing-phone" :
            phone.C2sMedian == null ? "missing-c2s" : phone.Rtt == null || now - phone.ReceivedAt + phone.RttAge > 10 ? "missing-rtt" :
            pc.Count == 0 || coordinateAge > 1 ? "missing-pc" : DisplayMs == null ? "missing-display" : "estimate";
        if (Status == "estimate") TotalMs = phone.C2sMedian + NetworkMs + pc.MedianMs + DisplayMs;
    }
    static string N(double? n) => n.HasValue ? n.Value.ToString("0.0", CultureInfo.InvariantCulture) : "unknown";
    public string Line(string colour, double now)
    {
        string head = "遅延の目安 " + colour + ": ";
        if (Phone == null || now - Phone.ReceivedAt > PhoneSaberMetricsStore.FreshSeconds ||
            Sender == "127.0.0.1" || Sender == "::1")
            return head + "スマホからの計測が届いていません（旧アプリ / P2P 経由）";
        if (!TotalMs.HasValue || now - At > 15 || Phone.RttAge + now - Phone.ReceivedAt > 10)
            return head + "計測不足（スマホ / 通信 / PC / 画面）";
        return head + string.Format(CultureInfo.InvariantCulture,
            "約{0:0} ms（スマホ {1:0} + 通信 約{2:0} + PC {3:0} + 画面 約{4:0}）/ {5:0}秒前まで・{6}",
            TotalMs, Phone.C2sMedian, NetworkMs, Pc.MedianMs, DisplayMs, Math.Max(0, now - Phone.ReceivedAt), DisplaySource == "measured" ?
            $"画面は実測（{DisplayAgeMinutes:0}分前）" : "画面は半周期の推定・投影機処理は含まず");
    }
    public string Log() => "status=" + Status + " sender=" + Sender + " platform=" + (Phone?.Platform ?? "unknown") +
        " app=" + (Phone?.App ?? "unknown") + " fps-mode=" + N(Phone?.FpsMode) + " route=" + (Phone?.Route ?? "unknown") +
        " c2s-end=" + (Phone?.C2sEnd ?? "unknown") + " c2s-median-ms=" + N(Phone?.C2sMedian) +
        " c2s-p95-ms=" + N(Phone?.C2sP95) + " c2s-n=" + (Phone?.C2sCount ?? 0) +
        " rtt-ms=" + N(Phone?.Rtt) + " rtt-n=" + (Phone?.RttCount ?? 0) +
        " rtt-age-s=" + N(Phone?.RttAge + At - Phone?.ReceivedAt) + " network-ms=" + N(NetworkMs) +
        " pc-median-ms=" + N(Pc.Count > 0 ? (double?)Pc.MedianMs : null) +
        " pc-p95-ms=" + N(Pc.Count > 0 ? (double?)Pc.P95Ms : null) + " pc-n=" + Pc.Count +
        " display-ms=" + N(DisplayMs) + " display-source=" + DisplaySource + " display-age-min=" + N(DisplaySource == "measured" ? (double?)DisplayAgeMinutes : null) + " glass-hz=unknown" +
        " refresh-hz=" + N(RefreshHz) + " vsync=" + Vsync + " total-ms=" + N(TotalMs) +
        " phone-age-s=" + N(Phone == null ? (double?)null : At - Phone.ReceivedAt) +
        " capture-fps=" + N(Phone?.CaptureFps) + " send-red-fps=" + N(Phone?.RedFps) +
        " send-blue-fps=" + N(Phone?.BlueFps) + " thermal=" + (Phone?.Thermal ?? "unknown") +
        " battery=" + N(Phone?.Battery) + " low-latency-test=" + (Phone?.LowLatencyTest ?? 0) +
        " pc-coordinate-age-s=" + N(double.IsInfinity(CoordinateAge) ? (double?)null : CoordinateAge);
}

// F9 の残差は露光位相を半周期とみなした推定。GPU/present/投影機も残差に含む。
public sealed class PhoneSaberDisplayMeasurement
{
    public readonly double LoopMs, PhoneMs, NetworkMs, ExposureMs, PresentMs, DisplayMs;
    public readonly int LoopCount, PhoneCount, RttCount, PresentCount;
    public readonly string Reason;
    public bool Valid => Reason == null;
    public PhoneSaberDisplayMeasurement(PhoneSaberTimingStats loop, PhoneSaberMetrics phone,
        PhoneSaberTimingStats present, double now, bool conditionsChanged = false)
    {
        LoopMs = loop.MedianMs; LoopCount = loop.Count; PresentMs = present.MedianMs; PresentCount = present.Count;
        PhoneMs = phone?.C2sMedian ?? double.NaN; PhoneCount = phone?.C2sCount ?? 0;
        NetworkMs = (phone?.Rtt ?? double.NaN) / 2; RttCount = phone?.RttCount ?? 0;
        ExposureMs = phone?.CaptureFps > 0 ? 500 / phone.CaptureFps.Value : double.NaN;
        DisplayMs = LoopMs - PhoneMs - NetworkMs - ExposureMs - PresentMs;
        Reason = conditionsChanged ? "conditions-changed" : LoopCount < 15 || PresentCount != LoopCount ? "too-few-samples" :
            phone == null || now - phone.ReceivedAt > 15 || phone.Route != "LAN" || PhoneCount < 20 ||
            RttCount < 2 || phone.RttAge + now - phone.ReceivedAt > 10 ||
            double.IsNaN(PhoneMs) || double.IsNaN(NetworkMs) || double.IsNaN(ExposureMs) ? "missing-phone-metrics" :
            !IsPlausible(DisplayMs) || !FiniteNonnegative(LoopMs) || !FiniteNonnegative(PhoneMs) ||
            !FiniteNonnegative(NetworkMs) || !FiniteNonnegative(ExposureMs) || !FiniteNonnegative(PresentMs) ? "implausible-result" : null;
    }
    static bool FiniteNonnegative(double n) => n >= 0 && !double.IsNaN(n) && !double.IsInfinity(n);
    public static bool IsPlausible(double n) => n >= 0 && n <= 500 && !double.IsNaN(n) && !double.IsInfinity(n);
    public string Result => Valid ? string.Format(CultureInfo.InvariantCulture,
        "画面の遅延: 約{0:0} ms（この PC とこの画面）", DisplayMs) :
        Reason == "conditions-changed" ? "画面の遅延: 測定中に画面設定・接続・撮影設定が変わりました（再試行）" :
        Reason == "too-few-samples" ? "画面の遅延: 測定回数が足りません（カメラを画面に向けて再試行）" :
        Reason == "missing-phone-metrics" ? "画面の遅延: スマホの計測が届いていません（更新したアプリで LAN 接続）" :
        "画面の遅延: 計測が不整合です（0〜500 ms の範囲外、再試行してください）";
    public string Log() => string.Format(CultureInfo.InvariantCulture,
        "status={0} reason={1} display-ms={2:0.0} loop-median-ms={3:0.0} loop-n={4} c2s-median-ms={5:0.0} c2s-n={6} " +
        "network-ms={7:0.0} rtt-n={8} exposure-phase-ms={9:0.0} exposure-source=half-camera-frame-estimate " +
        "pc-present-ms={10:0.0} pc-present-n={11} pc-present-source=update-to-gui-repaint",
        Valid ? "valid" : "rejected", Reason ?? "none", DisplayMs, LoopMs, LoopCount, PhoneMs, PhoneCount,
        NetworkMs, RttCount, ExposureMs, PresentMs, PresentCount);
}

// Unity に依存する保存だけを分け、数値・時刻・設定一致の検証は純粋関数でも実行する。
public static class PhoneSaberDisplayMeasurementSettings
{
    public static string Key(string station) => "PhoneSaber.DisplayMeasured." + PhoneSaberStation.Normalize(station) + ".v1";
    public static string Encode(string configuration, double value, double unixSeconds) =>
        configuration + "|" + value.ToString("R", CultureInfo.InvariantCulture) + "|" + unixSeconds.ToString("R", CultureInfo.InvariantCulture);
    public static bool Decode(string saved, string configuration, double now, out double value, out double ageMinutes)
    {
        value = ageMinutes = 0;
        var fields = (saved ?? "").Split('|');
        if (fields.Length != 3 || fields[0] != configuration ||
            !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double at) ||
            !PhoneSaberDisplayMeasurement.IsPlausible(value) || double.IsNaN(at) || double.IsInfinity(at) ||
            at <= 0 || at > now || double.IsNaN(now) || double.IsInfinity(now)) return false;
        ageMinutes = (now - at) / 60; return true;
    }
    public static void Save(string station, string configuration, double value, double unixSeconds)
    {
        if (!PhoneSaberDisplayMeasurement.IsPlausible(value) || unixSeconds <= 0 || double.IsNaN(unixSeconds) || double.IsInfinity(unixSeconds)) return;
        UnityEngine.PlayerPrefs.SetString(Key(station) + "." + configuration, Encode(configuration, value, unixSeconds));
        UnityEngine.PlayerPrefs.Save();
    }
    public static bool Load(string station, string configuration, double now, out double value, out double ageMinutes) =>
        Decode(UnityEngine.PlayerPrefs.GetString(Key(station) + "." + configuration, ""), configuration, now, out value, out ageMinutes);
}
