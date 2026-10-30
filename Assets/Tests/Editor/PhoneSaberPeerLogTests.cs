using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;

// 実LANへは送らず、ループバックの空きポートだけを使う。
public class PhoneSaberPeerLogTests
{
    string directory;
    readonly List<PhoneSaberPeerLog> peers = new List<PhoneSaberPeerLog>();
    readonly List<PhoneSaberEventLog> logs = new List<PhoneSaberEventLog>();
    readonly List<UdpClient> sockets = new List<UdpClient>();
    readonly string session = Guid.NewGuid().ToString("N");

    [SetUp]
    public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "phonesaber-peer-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        foreach (var log in logs) log.StopAndDrain(3000);
        foreach (var peer in peers) peer.Dispose();
        foreach (var socket in sockets) socket.Close();
        peers.Clear(); logs.Clear(); sockets.Clear();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    PhoneSaberEventLog Log(string name)
    {
        var log = new PhoneSaberEventLog(Path.Combine(directory, name + ".log"));
        logs.Add(log); return log;
    }

    PhoneSaberPeerLog Peer(PhoneSaberEventLog log, string station, string targets = "", double seconds = .05, bool offline = false)
    {
        var peer = new PhoneSaberPeerLog(log, new PhoneSaberPeerLog.Settings
        {
            Station = station, Port = 0, PeerPort = 1, Broadcast = false, LoopbackOnly = true, Peers = targets, SendSeconds = seconds, Offline = offline,
        });
        peers.Add(peer);
        if (!offline) Assert.IsTrue(SpinWait.SpinUntil(() => peer.LocalPort > 0, 3000), peer.LastError);
        return peer;
    }

    UdpClient Socket()
    {
        var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        sockets.Add(socket); return socket;
    }

    static string Row(string kind = "perf", string station = "B", string detail = "fps=30 red-pkt-per-s=60 blue-pkt-per-s=59") =>
        PhoneSaberEventLog.Format(DateTimeOffset.UtcNow, station, "SYSTEM", kind, detail).TrimEnd('\n');

    void Send(UdpClient socket, PhoneSaberPeerLog peer, string body, int lines, long seq, string station = "B", string id = null, bool reverse = false)
    {
        var packets = new List<byte[]>(PhoneSaberPeerLog.Encode(station, id ?? session, seq, body, lines));
        if (reverse) packets.Reverse();
        foreach (byte[] packet in packets)
        {
            Assert.LessOrEqual(packet.Length, 1200);
            socket.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, peer.LocalPort));
        }
    }

    static void AwaitLines(PhoneSaberPeerLog peer, int count) =>
        Assert.IsTrue(SpinWait.SpinUntil(() => peer.ReceivedLines >= count, 3000), "received=" + peer.ReceivedLines + " error=" + peer.LastError);

    [Test]
    public void ClipRelayUsesVerifiedStationASourceBeforeManualAndRankingIps()
    {
        var b = Peer(Log("b"), "B", "127.0.0.3:5018");
        b.SetRankingPeers(new[] { IPAddress.Parse("127.0.0.2") });
        CollectionAssert.AreEqual(new[] { IPAddress.Parse("127.0.0.3"), IPAddress.Parse("127.0.0.2") }, PhoneSaberPeerLog.ClipRelayTargets);
        Send(Socket(), b, Row(station: "A") + "\n", 1, 1, station: "A");
        AwaitLines(b, 1);
        CollectionAssert.AreEqual(new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.3"), IPAddress.Parse("127.0.0.2") }, PhoneSaberPeerLog.ClipRelayTargets);
        b.Dispose();
        Assert.IsEmpty(PhoneSaberPeerLog.ClipRelayTargets, "停止したpeerの発見先は引き継がない");
    }

    [Test]
    public void SavedDiagnosticsRoundTripLearnPeerAndNeverEcho()
    {
        var aLog = Log("a"); var bLog = Log("b");
        var a = Peer(aLog, "A");
        var b = Peer(bLog, "B", "127.0.0.1:" + a.LocalPort);
        bLog.Enqueue(DateTimeOffset.UtcNow, "B", "RED", "latency-estimate", "total-ms=180 pc-median-ms=60");
        Assert.IsTrue(bLog.FlushAndWait(3000));
        AwaitLines(a, 1);
        Assert.IsTrue(aLog.FlushAndWait(3000));
        string text = File.ReadAllText(aLog.LogPath);
        StringAssert.Contains("station=A colour=PEER event=peer-log from-station=B ", text);
        StringAssert.Contains("station=B colour=RED event=latency-estimate total-ms=180", text);
        aLog.Enqueue(DateTimeOffset.UtcNow, "A", "SYSTEM", "input-gap", "gap-seconds=0.5");
        Assert.IsTrue(aLog.FlushAndWait(3000));
        AwaitLines(b, 1); // A は B の送信元を学び、手入力なしで返す。
        Thread.Sleep(150);
        Assert.AreEqual(1, a.ReceivedLines);
        Assert.AreEqual(1, b.ReceivedLines);
    }

    [Test]
    public void FragmentedLongJapaneseLineIsRestoredExactlyAndPartsCanArriveBackwards()
    {
        var log = Log("a"); var a = Peer(log, "A"); var socket = Socket();
        string row = Row(detail: "scene=Game song=曲01 detail=" + new string('剣', 850));
        Send(socket, a, row + "\n", 1, 1, reverse: true);
        AwaitLines(a, 1);
        Assert.IsTrue(log.FlushAndWait(3000));
        StringAssert.Contains("from-station=B " + row + "\n", File.ReadAllText(log.LogPath));
        Send(socket, a, row + "\n", 1, 1);
        Thread.Sleep(100);
        Assert.AreEqual(1, a.ReceivedLines);
    }

    [Test]
    public void SequenceDedupAllowsReorderingAndNewSessionAfterRestart()
    {
        var a = Peer(Log("a"), "A"); var socket = Socket(); string body = Row() + "\n";
        Send(socket, a, body, 1, 4); Send(socket, a, body, 1, 2); Send(socket, a, body, 1, 4);
        Send(socket, a, body, 1, 2); Send(socket, a, body, 1, 1, id: Guid.NewGuid().ToString("N"));
        AwaitLines(a, 3); Thread.Sleep(100);
        Assert.AreEqual(3, a.ReceivedLines);
    }

    [Test]
    public void StationKindMalformedAndOversizeAreFiltered()
    {
        var a = Peer(Log("a"), "A"); var socket = Socket();
        Send(socket, a, Row(station: "A") + "\n", 1, 1, station: "A");
        Send(socket, a, Row(station: "C") + "\n", 1, 2); // ヘッダと元の台名が違う。
        Send(socket, a, Row("peer-log", detail: "from-station=C injected") + "\n", 1, 3);
        Send(socket, a, Row("unrelated") + "\n", 1, 4);
        Send(socket, a, Row() + new string('x', 4100) + "\n", 1, 5);
        Send(socket, a, Row() + "\n", 2, 6); // 行数が一致しない。
        byte[] oversized = new byte[1201]; socket.Send(oversized, oversized.Length, new IPEndPoint(IPAddress.Loopback, a.LocalPort));
        byte[] malformed = Encoding.UTF8.GetBytes("PHONESABER_PEERLOG 2 station=B\n");
        socket.Send(malformed, malformed.Length, new IPEndPoint(IPAddress.Loopback, a.LocalPort));
        Thread.Sleep(150); Assert.AreEqual(0, a.ReceivedLines);
        Send(socket, a, Row() + "\n", 1, 7); AwaitLines(a, 1);
    }

    [Test]
    public void ReceiverRateIsBoundedAndIncompleteFragmentsDoNotBlockOtherMessages()
    {
        var a = Peer(Log("a"), "A"); var socket = Socket();
        var fragments = PhoneSaberPeerLog.Encode("B", session, 1, Row(detail: new string('x', 2000)) + "\n", 1);
        socket.Send(fragments[0], fragments[0].Length, new IPEndPoint(IPAddress.Loopback, a.LocalPort));
        string body = string.Concat(new[] { Row(), "\n", Row(), "\n", Row(), "\n", Row(), "\n" });
        for (int i = 2; i < 20; i++) Send(socket, a, body, 4, i);
        AwaitLines(a, PhoneSaberPeerLog.MaximumLinesPerSecond);
        Assert.IsTrue(SpinWait.SpinUntil(() => a.DroppedLines > 0, 1000));
        Assert.AreEqual(PhoneSaberPeerLog.MaximumLinesPerSecond, a.ReceivedLines);
    }

    [Test]
    public void SenderQueueIsBoundedAndDisposeFlushesQuitThenReleasesSocket()
    {
        var aLog = Log("a"); var a = Peer(aLog, "A");
        var bLog = Log("b"); var b = Peer(bLog, "B", "127.0.0.1:" + a.LocalPort, 60);
        for (int i = 0; i < 300; i++) bLog.Enqueue(DateTimeOffset.UtcNow, "B", "SYSTEM", "input-gap", "gap-seconds=0.1");
        Assert.IsTrue(bLog.FlushAndWait(3000));
        Assert.AreEqual(PhoneSaberPeerLog.QueueCapacity, b.PendingLines);
        Assert.AreEqual(44, b.DroppedLines);
        bLog.Enqueue(DateTimeOffset.UtcNow, "B", "SYSTEM", "session-quit", "exit-code=0");
        Assert.IsTrue(bLog.StopAndDrain(3000));
        int boundPort = b.LocalPort;
        b.Dispose(); b.Dispose(); Assert.IsFalse(b.IsRunning);
        using (var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, boundPort))) { }
        // 受信上限の検証と終了行の送信検証は別台で行う。
        var cLog = Log("c"); var c = Peer(cLog, "C", "127.0.0.1:" + a.LocalPort, 60);
        cLog.Enqueue(DateTimeOffset.UtcNow, "C", "SYSTEM", "session-quit", "exit-code=0");
        Assert.IsTrue(cLog.StopAndDrain(3000));
        Thread.Sleep(1100); c.Dispose();
        Assert.IsTrue(SpinWait.SpinUntil(() => { aLog.FlushAndWait(100); return File.Exists(aLog.LogPath) && File.ReadAllText(aLog.LogPath).Contains("from-station=C"); }, 3000));
        StringAssert.Contains("event=session-quit exit-code=0", File.ReadAllText(aLog.LogPath));
    }

    [Test]
    public void RankingSnapshotAndManualTargetsCannotEscapeLoopbackInTests()
    {
        var peer = Peer(Log("a"), "A", "192.0.2.1,255.255.255.255");
        peer.SetRankingPeers(new[] { IPAddress.Parse("192.0.2.2"), IPAddress.Broadcast });
        peer.Dispose(); Assert.IsFalse(peer.IsRunning);
        Assert.Throws<ArgumentException>(() => new PhoneSaberPeerLog(Log("empty"), new PhoneSaberPeerLog.Settings { Station = "", Broadcast = false, LoopbackOnly = true }));
    }
    static void Process(PhoneSaberPeerLog peer, byte[] packet, double now = .1)
    {
        typeof(PhoneSaberPeerLog).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(peer,
            new object[] { packet, packet.Length, new IPEndPoint(IPAddress.Loopback, 5018), now });
    }

    void Deliver(PhoneSaberPeerLog peer, string row, long seq, double now = .1, string station = "B", string id = null)
    {
        foreach (var packet in PhoneSaberPeerLog.Encode(station, id ?? session, seq, row + "\n", 1)) Process(peer, packet, now);
    }

    [Test]
    public void OfflineReassemblyDedupReorderAndRestartPreserveOriginalLine()
    {
        var log = Log("offline"); var peer = Peer(log, "A", offline: true);
        string row = Row(detail: "fps=30 detail=" + new string('剣', 850));
        var packets = new List<byte[]>(PhoneSaberPeerLog.Encode("B", session, 5, row + "\n", 1));
        packets.Reverse();
        foreach (var packet in packets) { Assert.LessOrEqual(packet.Length, 1200); Process(peer, packet); Process(peer, packet); }
        Deliver(peer, Row(), 3); Deliver(peer, Row(), 3);
        Deliver(peer, Row(), 1, id: Guid.NewGuid().ToString("N"));
        Assert.AreEqual(3, peer.ReceivedLines);
        Assert.IsTrue(log.FlushAndWait(3000));
        StringAssert.Contains("from-station=B " + row + "\n", File.ReadAllText(log.LogPath));
    }

    [Test]
    public void OfflineFiltersStationKindsLengthAndMalformedMessages()
    {
        var peer = Peer(Log("offline"), "A", offline: true);
        Deliver(peer, Row(station: "A"), 1, station: "A");
        Deliver(peer, Row(station: "C"), 2);
        Deliver(peer, Row("peer-log"), 3);
        Deliver(peer, Row("unrelated"), 4);
        Deliver(peer, Row() + new string('x', 4100), 5);
        Deliver(peer, Row() + "\tbroken", 6);
        foreach (var packet in PhoneSaberPeerLog.Encode("B", session, 7, Row() + "\n", 2)) Process(peer, packet);
        Process(peer, Encoding.UTF8.GetBytes("PHONESABER_PEERLOG 2 station=B\n"));
        Assert.AreEqual(0, peer.ReceivedLines);
        Deliver(peer, Row(), 8); Assert.AreEqual(1, peer.ReceivedLines);
    }

    [Test]
    public void OfflineLineRateResetsAndMissingFragmentCannotBlockNextMessage()
    {
        var peer = Peer(Log("offline"), "A", offline: true);
        var fragments = PhoneSaberPeerLog.Encode("B", session, 1, Row(detail: new string('x', 2000)) + "\n", 1);
        Process(peer, fragments[0]);
        for (int i = 2; i < 70; i++) Deliver(peer, Row(), i);
        Assert.AreEqual(PhoneSaberPeerLog.MaximumLinesPerSecond, peer.ReceivedLines);
        Assert.AreEqual(68 - PhoneSaberPeerLog.MaximumLinesPerSecond, peer.DroppedLines);
        Deliver(peer, Row(), 70, now: 1.2);
        Assert.AreEqual(PhoneSaberPeerLog.MaximumLinesPerSecond + 1, peer.ReceivedLines);
        // 窓より古い seq は新しい番号の後で再送されても採用しない。
        Deliver(peer, Row(), 2, now: 1.3);
        Assert.AreEqual(PhoneSaberPeerLog.MaximumLinesPerSecond + 1, peer.ReceivedLines);
    }

    [Test]
    public void OfflineSavedLineQueueIsBoundedDoesNotEchoAndStops()
    {
        var log = Log("offline"); var peer = Peer(log, "A", offline: true);
        peer.ObserveContext("Game", "曲 01"); peer.ObserveContext("Game", "曲 01");
        for (int i = 0; i < 300; i++) log.Enqueue(DateTimeOffset.UtcNow, "A", "SYSTEM", "input-gap", "gap-seconds=.1");
        Assert.IsTrue(log.FlushAndWait(3000));
        Assert.AreEqual(PhoneSaberPeerLog.QueueCapacity, peer.PendingLines);
        Assert.AreEqual(45, peer.DroppedLines); // context 1行と gap 300行。
        Deliver(peer, Row(), 1);
        Assert.IsTrue(log.FlushAndWait(3000));
        Assert.AreEqual(45, peer.DroppedLines); // peer-log は再転送しない。
        StringAssert.Contains("song=%E6%9B%B2%2001", File.ReadAllText(log.LogPath));
        peer.Dispose(); peer.Dispose(); Assert.IsFalse(peer.IsRunning);
        Assert.IsTrue(log.StopAndDrain(3000));
    }

}
