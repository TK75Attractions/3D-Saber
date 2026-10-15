using System;
using System.Collections.Generic;
using System.Globalization;

// receive thread の送信元選択。同期とイベントの書き出しは呼び出し側が担当する。
public sealed class PhoneSaberSenderGate
{
    public const double SilenceSeconds = 1.0;
    // 既定は「検出して知らせるだけ」で、座標は弾かない（2026-10-11 の再レビュー review-merged-20261011.md）。
    // 弾く動作には、1台のスマホが LAN と P2P を短時間に往復すると2台と誤判定して約1秒入力を止める、
    // 不正な payload だけの送信元が正常なスマホを締め出せる、P2P bridge 経由の2台は区別できない、
    // という穴が残っている。実機で確かめるまで本番では使わない。テストだけが true にする。
    public static bool Enforce = false;
    readonly Action<string, string> record;
    readonly List<Departure> departures = new List<Departure>(4);
    readonly struct Departure
    {
        public readonly string Sender;
        public readonly double At;
        public Departure(string sender, double at) { Sender = sender; At = at; }
    }
    string current;
    double lockedLastPacketAt;
    public string LockedSender { get; private set; } = "";
    public long RejectedPackets { get; private set; }

    public PhoneSaberSenderGate(Action<string, string> record = null) { this.record = record; }

    public bool Accept(string sender, double now)
    {
        Poll(now);
        if (LockedSender.Length > 0)
        {
            if (sender != LockedSender)
            {
                if (!Enforce) return true;
                RejectedPackets++;
                return false;
            }
            lockedLastPacketAt = now;
            return true;
        }
        if (sender == current) return true;
        // P2P bridge（loopback）と LAN の入れ替わりは、同じスマホの経路切替でも起きる。競合とは数えない。
        bool pathSwitch = IsLoopback(sender) || IsLoopback(current);
        for (int i = 0; !pathSwitch && i < departures.Count; i++)
        {
            var departure = departures[i];
            if (departure.Sender != sender || now - departure.At >= SilenceSeconds) continue;
            LockedSender = sender;
            lockedLastPacketAt = now;
            record?.Invoke("sender-conflict", "kept=" + sender + " rejected=" + current);
            current = sender;
            departures.Clear();
            return true;
        }
        if (current != null)
        {
            // 期限切れの枠を再利用し、同じ送信元や定常受信では割り当てない。
            int slot = -1;
            for (int i = 0; i < departures.Count; i++)
                if (now - departures[i].At >= SilenceSeconds) { slot = i; break; }
            var departure = new Departure(current, now);
            if (slot >= 0) departures[slot] = departure;
            else departures.Add(departure);
        }
        current = sender;
        return true;
    }

    static bool IsLoopback(string sender) => sender == "127.0.0.1" || sender == "::1";

    public void Poll(double now)
    {
        if (LockedSender.Length > 0 && now - lockedLastPacketAt >= SilenceSeconds) Release();
    }

    void Release()
    {
        record?.Invoke("sender-conflict-end", "kept=" + LockedSender);
        LockedSender = "";
        current = null;
        departures.Clear();
    }

    public void Reset()
    {
        if (LockedSender.Length > 0) Release();
        current = null;
        departures.Clear();
        RejectedPackets = 0;
    }
}

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

    static readonly Comparison<double> CompareTiming = (left, right) => left.CompareTo(right);

    // List の有効範囲を既存の比較順でソートする。Mono の Array.Sort(range) は
    // 呼ぶたびに delegate を作るため、保持済み Comparison を渡せる経路を使う。
    internal PhoneSaberTimingStats(List<double> values)
    {
        values.Sort(CompareTiming);
        Count = values.Count;
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
    public long SenderRejectedPackets { get; }
    public string LockedSender { get; }

    public PhoneSaberInputStats(bool hasPacket, int packetsPerSecond, double secondsSinceLastPacket,
        string senderIP, bool payloadParsed, PhoneSaberTimingStats arrivalGaps = default,
        PhoneSaberTimingStats oneWayDelay = default, long senderRejectedPackets = 0, string lockedSender = "")
    {
        HasPacket = hasPacket;
        PacketsPerSecond = packetsPerSecond;
        SecondsSinceLastPacket = secondsSinceLastPacket;
        SenderIP = senderIP ?? "";
        PayloadParsed = payloadParsed;
        ArrivalGaps = arrivalGaps;
        OneWayDelay = oneWayDelay;
        SenderRejectedPackets = senderRejectedPackets;
        LockedSender = lockedSender ?? "";
    }
}

// receive thread と main thread の間で、時刻・送信元・解析結果をまとめて同期する。
// 時計は呼び出し元の monotonic 秒を使い、Unity API は呼ばない。
public sealed class PhoneSaberPacketStatistics
{
    public const double TimingWindowSeconds = 5.0;
    public const int MaximumTimingSamples = 2048;
    readonly object gate = new object();
    readonly Queue<double> recentPackets = new Queue<double>(256);
    readonly Queue<TimingSample> gaps = new Queue<TimingSample>(MaximumTimingSamples);
    readonly Queue<TimingSample> delays = new Queue<TimingSample>(MaximumTimingSamples);
    readonly struct TimingSample
    {
        public readonly double At;
        public readonly double Ms;
        public TimingSample(double at, double ms) { At = at; Ms = ms; }
    }
    readonly List<double> timingScratch = new List<double>(MaximumTimingSamples);
    double startedAt;
    double lastPacketAt;
    bool hasPacket;
    string senderIP = "";
    bool payloadParsed;
    PhoneSaberSenderGate senderGate = new PhoneSaberSenderGate();
    // 競合の失効処理が必要な間だけ main thread が lock を取る。更新はすべて gate 内。
    volatile bool senderGateNeedsPoll;
    long senderRejectedPackets;
    bool hasTimingPacket;
    double lastTimingPacketAt;
    string timingSenderIP;

    // 再 bind 時は固定だけ解除。拒否の累計は受信セッションの Reset まで残す。
    public void ResetSenderGate(Action<string, string> record = null)
    {
        lock (gate)
        {
            senderGate.Reset();
            senderGate = new PhoneSaberSenderGate(record);
            senderGateNeedsPoll = false;
        }
    }

    public void PollSenderGate(double now)
    {
        if (!senderGateNeedsPoll) return;
        lock (gate)
        {
            senderGate.Poll(now);
            senderGateNeedsPoll = senderGate.LockedSender.Length > 0;
        }
    }

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
            hasTimingPacket = false;
            senderRejectedPackets = 0;
            senderGate.Reset();
            senderGateNeedsPoll = false;
        }
    }

    public bool Record(double now, string sender, bool parsed,
        double? receiveEpoch = null, double? sentEpoch = null)
    {
        lock (gate)
        {
            Prune(now);
            // 不正な payload は到着としては数えるが、送信元の判定には使わない（不正な送信元が正常なスマホを締め出さない）。
            bool accepted = !parsed || senderGate.Accept(sender, now);
            if (!accepted) senderRejectedPackets++;
            if (accepted && hasTimingPacket && timingSenderIP == sender && now >= lastTimingPacketAt)
                Add(gaps, now, (now - lastTimingPacketAt) * 1000.0);
            else if (accepted && hasTimingPacket)
            {
                // 別の端末や時計の巻き戻りをまたいで統計を混ぜない。
                gaps.Clear();
                delays.Clear();
            }
            if (accepted && parsed && receiveEpoch.HasValue && sentEpoch.HasValue && sentEpoch.Value > 0)
            {
                double delayMs = (receiveEpoch.Value - sentEpoch.Value) * 1000.0;
                if (!double.IsNaN(delayMs) && !double.IsInfinity(delayMs)) Add(delays, now, delayMs);
            }
            recentPackets.Enqueue(now);
            lastPacketAt = now;
            hasPacket = true;
            senderIP = sender;
            payloadParsed = parsed && accepted;
            if (accepted)
            {
                hasTimingPacket = true;
                lastTimingPacketAt = now;
                timingSenderIP = sender;
            }
            senderGateNeedsPoll = senderGate.LockedSender.Length > 0;
            return accepted;
        }
    }

    public PhoneSaberInputStats Read(double now)
    {
        lock (gate)
        {
            Prune(now);
            return new PhoneSaberInputStats(hasPacket, recentPackets.Count,
                Math.Max(0.0, now - (hasPacket ? lastPacketAt : startedAt)), senderIP, payloadParsed,
                Summarize(gaps), Summarize(delays), senderRejectedPackets, senderGate.LockedSender);
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

    PhoneSaberTimingStats Summarize(Queue<TimingSample> samples)
    {
        timingScratch.Clear();
        foreach (var sample in samples) timingScratch.Add(sample.Ms);
        return new PhoneSaberTimingStats(timingScratch);
    }

    // 座標解析から独立。座標 parser と同じ2種類の prefix を読み、壊れた時刻は統計に入れない。
    public static double? ReadSendEpoch(string message)
        => message == null ? null : ReadSendEpoch(message.AsSpan());

    public static double? ReadSendEpoch(ReadOnlySpan<char> message)
    {
        int prefixLength = message.StartsWith("ts=".AsSpan(), StringComparison.Ordinal) ? 3 :
            message.StartsWith("timestamp=".AsSpan(), StringComparison.Ordinal) ? 10 : 0;
        if (prefixLength == 0) return null;
        int separator = message.Slice(prefixLength).IndexOf(';') + prefixLength;
        if (separator <= prefixLength || !double.TryParse(message.Slice(prefixLength, separator - prefixLength),
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
            $"\n判定: {Verdict(stats, clocksSynchronized)}（{(clocksSynchronized && stats.OneWayDelay.Count > 0 ? "間隔＋片道" : "間隔のみ")}）" +
            $"\n送信元競合による拒否: {stats.SenderRejectedPackets} pkt";
    }

    // 画面の更新が毎秒 50 回未満だと、ゲーム全体が 30 コマに見えて「もっさり」する。
    // 2026-10-13: Mac の低電力モード(バッテリー駆動)で Player の更新が 30Hz になっていた。
    public static string DisplayWarning(double refreshHz)
        => refreshHz > 1 && refreshHz < 50
            ? $"警告: 画面の更新が毎秒 {refreshHz:0} 回しかありません。PC を電源につなぎ、低電力モード(省電力)を切ってください。"
            : "";

    public static string Warning(string colour, PhoneSaberInputStats stats)
    {
        string conflict = string.IsNullOrEmpty(stats.LockedSender) ? "" :
            $"警告: {colour} は別のスマホもこのPCへ送信中です。そのスマホの台・送信先を確認してください（先にいた送信元: {stats.LockedSender}）。";
        string silence = IsStale(stats) ? $"警告: {colour} のUDP入力が1秒以上届いていません。送信先・接続・ファイアウォールを確認してください。" : "";
        return conflict.Length == 0 ? silence : silence.Length == 0 ? conflict : conflict + "\n" + silence;
    }
}
