using System;
using System.IO;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

public class PhoneSaberLatencyEstimateTests
{
    static string FixturePath {
        get {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null) {
                string path = Path.Combine(dir.FullName, "PhoneSaber/tools/fixtures/protocol_metrics.tsv");
                if (File.Exists(path)) return path;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("protocol_metrics.tsv");
        }
    }
    public static byte[] Valid => Encoding.ASCII.GetBytes(File.ReadAllLines(FixturePath)[1].Split('\t')[1]);
    [Test] public void SharedContractVectors() {
        foreach (var row in File.ReadAllLines(FixturePath)) {
            var f = row.Split('\t'); if (f[0] == "id") continue;
            var bytes = Encoding.UTF8.GetBytes(f[1]);
            Assert.AreEqual(f[2] == "1", PhoneSaberMetrics.TryParse(bytes, bytes.Length, out _), f[0]);
            Assert.IsFalse(PhoneSaberDiscoveryResponder.IsRequest(bytes, bytes.Length), "旧PCは計測に返信しない");
        }
    }
    static PhoneSaberMetrics Phone(double at = 0) {
        Assert.IsTrue(PhoneSaberMetrics.TryParse(Valid, Valid.Length, out var m)); m.ReceivedAt = at; return m;
    }
    [Test] public void TermsAndUnknownsNeverBecomeZeroLatency() {
        var m = Phone(); var pc = new PhoneSaberTimingStats(new double[] { 7, 7, 7 });
        var e = new PhoneSaberLatencyEstimate(m, pc, "10.0.0.1", .1, 1, 60, 0);
        Assert.AreEqual(36.9 + 1.6 + 7 + 500.0 / 60, e.TotalMs.Value, 1e-9);
        StringAssert.Contains("画面 約8", e.Line("赤", 1));
        Assert.IsNull(new PhoneSaberLatencyEstimate(m, default, "x", .1, 1, 60, 1).TotalMs);
        Assert.IsNull(new PhoneSaberLatencyEstimate(null, pc, "x", .1, 1, 60, 1).TotalMs);
        Assert.AreEqual("missing-phone", new PhoneSaberLatencyEstimate(m, pc, "127.0.0.1", .1, 1, 60, 1).Status);
        Assert.IsNull(new PhoneSaberLatencyEstimate(m, pc, "x", 2, 1, 60, 1).TotalMs);
        Assert.IsNull(new PhoneSaberLatencyEstimate(m, pc, "x", 0, 16, 60, 1).TotalMs);
        m.Rtt = null; Assert.IsNull(new PhoneSaberLatencyEstimate(m, pc, "x", 0, 1, 60, 1).TotalMs);
        StringAssert.Contains("届いていません", e.Line("赤", 16));
    }
    [Test] public void BoundedStoreSourceExpiryAndRateLimit() {
        var store = new PhoneSaberMetricsStore();
        for (int i = 0; i < 100; i++) store.Receive(Valid, Valid.Length, "ip" + i, 0);
        Assert.AreEqual(8, store.Count); Assert.IsNull(store.Read("ip9", 0));
        Assert.IsNull(store.Read("other-phone", 0)); Assert.IsNotNull(store.Read("ip0", 15));
        Assert.IsNull(store.Read("ip0", 15.001));
        store.Receive(Valid, Valid.Length, "replacement", 16); Assert.AreEqual(8, store.Count);
        Assert.IsNotNull(store.Read("replacement", 16));
    }
    [Test] public void FullEstimateSurvivesActualEventLogFormatter() {
        var phone = Phone(); phone.App = new string('x', 24);
        var e = new PhoneSaberLatencyEstimate(phone, new PhoneSaberTimingStats(new double[] { 7 }),
            "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", 0, 1, 60, 1);
        string log = PhoneSaberEventLog.Format(DateTimeOffset.UtcNow, "A", "RED", "latency-estimate", e.Log());
        StringAssert.Contains("total-ms=53.8", log);
        StringAssert.Contains("phone-age-s=1.0", log);
        StringAssert.Contains("thermal=nominal", log);
        StringAssert.EndsWith(e.Log() + "\n", log);
        Assert.Less(e.Log().Length, 2048);
    }
    // 5007や座標portを使わず、実受信Loopを一時port上で動かす。
    [Test] public void LoopbackMetricsFloodDoesNotSuppressDiscoveryReply() {
        using (var responder = new PhoneSaberDiscoveryResponder())
        using (var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) {
            receiver.Client.ReceiveTimeout = 500; sender.Client.ReceiveTimeout = 1000;
            var endpoint = (IPEndPoint)receiver.Client.LocalEndPoint;
            typeof(PhoneSaberDiscoveryResponder).GetField("running", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(responder, true);
            var reply = Encoding.ASCII.GetBytes(PhoneSaberDiscoveryResponder.BuildReply(5005, 5006, "test", "A"));
            var loop = typeof(PhoneSaberDiscoveryResponder).GetMethod("Loop", BindingFlags.Instance | BindingFlags.NonPublic);
            var thread = new Thread(() => loop.Invoke(responder, new object[] { receiver, reply }));
            thread.IsBackground = true; thread.Start();
            try {
                sender.Send(Valid, Valid.Length, endpoint);
                var remote = new IPEndPoint(IPAddress.Any, 0);
                Assert.Throws<SocketException>(() => sender.Receive(ref remote), "診断は返信しない");
                Assert.IsNotNull(responder.Metrics.Read("127.0.0.1", SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp)));
                for (int i = 0; i < 100; i++) sender.Send(Valid, Valid.Length, endpoint);
                byte[] large = new byte[2000]; sender.Send(large, large.Length, endpoint);
                var request = Encoding.ASCII.GetBytes(PhoneSaberDiscoveryResponder.Request);
                sender.Send(request, request.Length, endpoint);
                Assert.AreEqual(Encoding.ASCII.GetString(reply), Encoding.ASCII.GetString(sender.Receive(ref remote)));
                Assert.AreEqual(1, responder.Metrics.Count);
            } finally { responder.Stop(); receiver.Close(); Assert.IsTrue(thread.Join(2000)); }
        }
    }
}
