using System;
using System.Collections.Generic;
using System.Globalization;

// 座標とは別の読み取り専用スナップショット。不正な payload も受信数に含める。
public readonly struct PhoneSaberInputStats
{
    public bool HasPacket { get; }
    public int PacketsPerSecond { get; }
    // 未受信の場合は受信開始からの秒数。それ以外は最後の datagram からの秒数。
    public double SecondsSinceLastPacket { get; }
    public string SenderIP { get; }
    public bool PayloadParsed { get; }

    public PhoneSaberInputStats(bool hasPacket, int packetsPerSecond, double secondsSinceLastPacket,
        string senderIP, bool payloadParsed)
    {
        HasPacket = hasPacket;
        PacketsPerSecond = packetsPerSecond;
        SecondsSinceLastPacket = secondsSinceLastPacket;
        SenderIP = senderIP ?? "";
        PayloadParsed = payloadParsed;
    }
}

// receive thread と main thread の間で、時刻・送信元・解析結果をまとめて同期する。
// 時計は呼び出し元の monotonic 秒を使い、Unity API は呼ばない。
public sealed class PhoneSaberPacketStatistics
{
    readonly object gate = new object();
    readonly Queue<double> recentPackets = new Queue<double>();
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
            startedAt = now;
            hasPacket = false;
            senderIP = "";
            payloadParsed = false;
        }
    }

    public void Record(double now, string sender, bool parsed)
    {
        lock (gate)
        {
            Prune(now);
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
                Math.Max(0.0, now - (hasPacket ? lastPacketAt : startedAt)), senderIP, payloadParsed);
        }
    }

    void Prune(double now)
    {
        // 直近1秒の (now - 1, now] を数える。無音になったら表示も0へ戻る。
        while (recentPackets.Count > 0 && recentPackets.Peek() <= now - 1.0)
            recentPackets.Dequeue();
    }
}

// IMGUI に依存しない表示・警告判定。EditMode で境界と文言を検証できる。
public static class PhoneSaberStatsDisplay
{
    public static bool IsStale(PhoneSaberInputStats stats) => stats.SecondsSinceLastPacket > 1.0;

    public static string ClassifyRoute(string senderIP, bool mac)
    {
        if (string.IsNullOrEmpty(senderIP)) return "--";
        return mac && senderIP == "127.0.0.1" ? "P2P bridge" : "LAN";
    }

    public static string Format(string colour, int port, PhoneSaberInputStats stats, bool mac)
    {
        string age = stats.HasPacket
            ? string.Format(CultureInfo.InvariantCulture, "{0:F2} s", stats.SecondsSinceLastPacket)
            : string.Format(CultureInfo.InvariantCulture, "未受信 (開始から {0:F2} s)", stats.SecondsSinceLastPacket);
        return string.Format(CultureInfo.InvariantCulture,
            "{0} UDP {1}  |  {2} pkt/s (直近1秒)  |  最終受信: {3}\n送信元: {4}  |  経路: {5}  |  payload解析: {6}",
            colour, port, stats.PacketsPerSecond, age,
            stats.HasPacket ? stats.SenderIP : "--", ClassifyRoute(stats.SenderIP, mac),
            stats.HasPacket ? (stats.PayloadParsed ? "OK" : "NG") : "--");
    }

    public static string Warning(string colour, PhoneSaberInputStats stats)
    {
        return IsStale(stats) ? $"警告: {colour} のUDP入力が1秒以上届いていません。送信先・接続・ファイアウォールを確認してください。" : "";
    }
}
