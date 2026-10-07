using System;
using System.Collections.Generic;
using System.Globalization;

// 有効サンプルがないときは Count=0。負の時計差も丸めず残す。
public readonly struct PhoneSaberTimingStats
{
    public int Count { get; }
    public double MedianMs { get; }
    public double P95Ms { get; }
    public double MaxMs { get; }
    public double MinMs { get; }

    public PhoneSaberTimingStats(double[] values)
    {
        Array.Sort(values);
        Count = values.Length;
        if (Count == 0) { MedianMs = P95Ms = MaxMs = MinMs = 0; return; }
        int middle = Count / 2;
        MedianMs = Count % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
        P95Ms = values[(int)Math.Ceiling(Count * 0.95) - 1];
        MinMs = values[0];
        MaxMs = values[Count - 1];
    }
}

// 座標とは別の読み取り専用スナップショット。不正な payload も受信数に含める。
public readonly struct PhoneSaberInputStats
{
    public bool HasPacket { get; }
    public int PacketsPerSecond { get; }
    // 未受信の場合は受信開始からの秒数。それ以外は最後の datagram からの秒数。
    public double SecondsSinceLastPacket { get; }
    public string SenderIP { get; }
    public bool PayloadParsed { get; }
    public PhoneSaberTimingStats ArrivalGaps { get; }
    public PhoneSaberTimingStats OneWayDelay { get; }

    public PhoneSaberInputStats(bool hasPacket, int packetsPerSecond, double secondsSinceLastPacket,
        string senderIP, bool payloadParsed, PhoneSaberTimingStats arrivalGaps = default,
        PhoneSaberTimingStats oneWayDelay = default)
    {
        HasPacket = hasPacket;
        PacketsPerSecond = packetsPerSecond;
        SecondsSinceLastPacket = secondsSinceLastPacket;
        SenderIP = senderIP ?? "";
        PayloadParsed = payloadParsed;
        ArrivalGaps = arrivalGaps;
        OneWayDelay = oneWayDelay;
    }
}

// receive thread と main thread の間で、時刻・送信元・解析結果をまとめて同期する。
// 時計は呼び出し元の monotonic 秒を使い、Unity API は呼ばない。
public sealed class PhoneSaberPacketStatistics
{
    public const double TimingWindowSeconds = 5.0;
    public const int MaximumTimingSamples = 2048;
    readonly object gate = new object();
    readonly Queue<double> recentPackets = new Queue<double>();
    readonly Queue<TimingSample> gaps = new Queue<TimingSample>();
    readonly Queue<TimingSample> delays = new Queue<TimingSample>();
    readonly struct TimingSample
    {
        public readonly double At;
        public readonly double Ms;
        public TimingSample(double at, double ms) { At = at; Ms = ms; }
    }
    double startedAt;
    double lastPacketAt;
    bool hasPacket;
    string senderIP = "";
    bool payloadParsed;

    public void Reset(double now)
    {
        lock (gate)
        {
            recentPackets.Clear();
            gaps.Clear();
            delays.Clear();
            startedAt = now;
            hasPacket = false;
            senderIP = "";
            payloadParsed = false;
        }
    }

    public void Record(double now, string sender, bool parsed,
        double? receiveEpoch = null, double? sentEpoch = null)
    {
        lock (gate)
        {
            Prune(now);
            if (hasPacket && senderIP == sender && now >= lastPacketAt)
                Add(gaps, now, (now - lastPacketAt) * 1000.0);
            else if (hasPacket)
            {
                // 別の端末や時計の巻き戻りをまたいで統計を混ぜない。
                gaps.Clear();
                delays.Clear();
            }
            if (parsed && receiveEpoch.HasValue && sentEpoch.HasValue && sentEpoch.Value > 0)
            {
                double delayMs = (receiveEpoch.Value - sentEpoch.Value) * 1000.0;
                if (!double.IsNaN(delayMs) && !double.IsInfinity(delayMs)) Add(delays, now, delayMs);
            }
            recentPackets.Enqueue(now);
            lastPacketAt = now;
            hasPacket = true;
            senderIP = sender;
            payloadParsed = parsed;
        }
    }

    public PhoneSaberInputStats Read(double now)
    {
        lock (gate)
        {
            Prune(now);
            return new PhoneSaberInputStats(hasPacket, recentPackets.Count,
                Math.Max(0.0, now - (hasPacket ? lastPacketAt : startedAt)), senderIP, payloadParsed,
                Summarize(gaps), Summarize(delays));
        }
    }

    void Prune(double now)
    {
        // 直近1秒の (now - 1, now] を数える。無音になったら表示も0へ戻る。
        while (recentPackets.Count > 0 && recentPackets.Peek() <= now - 1.0)
            recentPackets.Dequeue();
        PruneTiming(gaps, now);
        PruneTiming(delays, now);
    }

    static void PruneTiming(Queue<TimingSample> samples, double now)
    {
        // 間隔は後の datagram の受信時刻で窓に入れる。
        while (samples.Count > 0 && samples.Peek().At <= now - TimingWindowSeconds) samples.Dequeue();
    }

    static void Add(Queue<TimingSample> samples, double now, double ms)
    {
        if (samples.Count >= MaximumTimingSamples) samples.Dequeue();
        samples.Enqueue(new TimingSample(now, ms));
    }

    static PhoneSaberTimingStats Summarize(Queue<TimingSample> samples)
    {
        var values = new double[samples.Count];
        int index = 0;
        foreach (var sample in samples) values[index++] = sample.Ms;
        return new PhoneSaberTimingStats(values);
    }

    // 座標解析から独立。壊れた ts は遅延統計に入れず、既存の座標受信は変えない。
    public static double? ReadSendEpoch(string message)
    {
        if (message == null || !message.StartsWith("ts=", StringComparison.Ordinal)) return null;
        int separator = message.IndexOf(';', 3);
        if (separator <= 3 || !double.TryParse(message.Substring(3, separator - 3),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double epoch) ||
            double.IsNaN(epoch) || double.IsInfinity(epoch) || epoch <= 0) return null;
        return epoch;
    }
}

// IMGUI に依存しない表示・警告判定。EditMode で境界と文言を検証できる。
public static class PhoneSaberStatsDisplay
{
    public const int MinimumTimingSamples = 20;
    public static bool IsStale(PhoneSaberInputStats stats) => stats.SecondsSinceLastPacket > 1.0;

    // 30fps の当日用目安。NTP 同期を確認するまでは片道時計差を判定に使わない。
    public static string Verdict(PhoneSaberInputStats stats, bool clocksSynchronized = false)
    {
        double maxGap = Math.Max(stats.ArrivalGaps.MaxMs, stats.SecondsSinceLastPacket * 1000.0);
        if (!stats.HasPacket || !stats.PayloadParsed || maxGap >= 500 || stats.ArrivalGaps.P95Ms >= 100)
            return "不良";
        bool useDelay = clocksSynchronized && stats.OneWayDelay.Count > 0;
        if (useDelay && stats.OneWayDelay.MinMs < 0) return "注意（時計差を確認）";
        if (useDelay && (stats.OneWayDelay.P95Ms >= 100 || stats.OneWayDelay.MaxMs >= 500)) return "不良";
        if (stats.ArrivalGaps.Count < MinimumTimingSamples) return "注意（計測中）";
        if (maxGap >= 150 || stats.ArrivalGaps.P95Ms >= 50 ||
            (useDelay && (stats.OneWayDelay.P95Ms >= 50 || stats.OneWayDelay.MaxMs >= 150))) return "注意";
        if (useDelay && stats.OneWayDelay.Count < MinimumTimingSamples) return "注意（計測中）";
        return "良好";
    }

    static string Timing(PhoneSaberTimingStats stats) => stats.Count == 0 ? "--（サンプルなし）" :
        string.Format(CultureInfo.InvariantCulture, "中央値 {0:F1} / p95 {1:F1} / 最大 {2:F1} ms (n={3})",
            stats.MedianMs, stats.P95Ms, stats.MaxMs, stats.Count);

    public static string ClassifyRoute(string senderIP, bool mac)
    {
        if (string.IsNullOrEmpty(senderIP)) return "--";
        return mac && senderIP == "127.0.0.1" ? "P2P bridge" : "LAN";
    }

    public static string Format(string colour, int port, PhoneSaberInputStats stats, bool mac,
        bool clocksSynchronized = false)
    {
        string age = stats.HasPacket
            ? string.Format(CultureInfo.InvariantCulture, "{0:F2} s", stats.SecondsSinceLastPacket)
            : string.Format(CultureInfo.InvariantCulture, "未受信 (開始から {0:F2} s)", stats.SecondsSinceLastPacket);
        return string.Format(CultureInfo.InvariantCulture,
            "{0} UDP {1}  |  {2} pkt/s (直近1秒)  |  最終受信: {3}\n送信元: {4}  |  経路: {5}  |  payload解析: {6}",
            colour, port, stats.PacketsPerSecond, age,
            stats.HasPacket ? stats.SenderIP : "--", ClassifyRoute(stats.SenderIP, mac),
            stats.HasPacket ? (stats.PayloadParsed ? "OK" : "NG") : "--") +
            $"\n受信間隔（直近5秒）: {Timing(stats.ArrivalGaps)}" +
            $"\n片道時計差（tsあり）: {Timing(stats.OneWayDelay)}" +
            $"\n判定: {Verdict(stats, clocksSynchronized)}（{(clocksSynchronized && stats.OneWayDelay.Count > 0 ? "間隔＋片道" : "間隔のみ")}）";
    }

    public static string Warning(string colour, PhoneSaberInputStats stats)
    {
        return IsStale(stats) ? $"警告: {colour} のUDP入力が1秒以上届いていません。送信先・接続・ファイアウォールを確認してください。" : "";
    }
}
