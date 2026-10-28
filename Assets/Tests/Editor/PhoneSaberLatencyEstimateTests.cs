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
    // SwiftのworstCaseMetricsと同じ。RTT/capture/batteryの欠測は数値最大より長い。
    public static string WorstCaseLine =>
        "PHONESABER_METRICS 1 station=ssssssssssssssss platform=ios app=1234567890.1234567890.12" +
        " fps-mode=240 capture-fps=unknown send-red-fps=1000.0 send-blue-fps=1000.0" +
        " c2s-median-ms=2000.0 c2s-p95-ms=2000.0 c2s-n=2400 rtt-ms=unknown rtt-n=0 rtt-age-s=unknown" +
        " route=LAN thermal=tttttttttttttttt battery=unknown low-latency-test=1 c2s-end=detection" +
        " ctl=1237 emitter=1 model=mmmmmmmmmmmmmmmm";
    [Test] public void WorstCaseIPhoneMetricsAndByteBoundaryParse() {
        var data = Encoding.ASCII.GetBytes(WorstCaseLine);
        Assert.AreEqual(382, data.Length);
        Assert.IsTrue(PhoneSaberMetrics.TryParse(data, data.Length, out var phone));
        Assert.AreEqual("ssssssssssssssss", phone.Station);
        Assert.AreEqual("1234567890.1234567890.12", phone.App);
        Assert.AreEqual("mmmmmmmmmmmmmmmm", phone.Model);
        Assert.AreEqual("1237", phone.Controls);
        Assert.AreEqual(true, phone.Emitter);
        Assert.AreEqual(2400, phone.C2sCount);
        Assert.IsNull(phone.Rtt); Assert.IsNull(phone.CaptureFps); Assert.IsNull(phone.Battery);
        // 上限を緩めず、399 byteを受理し400 byteを拒否する。
        string boundary = WorstCaseLine + " future=" + new string('x', 399 - data.Length - 8);
        data = Encoding.ASCII.GetBytes(boundary);
        Assert.AreEqual(PhoneSaberMetrics.MaximumBytes, data.Length);
        Assert.IsTrue(PhoneSaberMetrics.TryParse(data, data.Length, out _));
        data = Encoding.ASCII.GetBytes(boundary + "x");
        Assert.IsFalse(PhoneSaberMetrics.TryParse(data, data.Length, out _));
    }
    [Test] public void SharedContractVectors() {
        foreach (var row in File.ReadAllLines(FixturePath)) {
            var f = row.Split('\t'); if (f[0] == "id") continue;
            var bytes = Encoding.UTF8.GetBytes(f[1]);
            Assert.AreEqual(f[2] == "1", PhoneSaberMetrics.TryParse(bytes, bytes.Length, out _), f[0]);
            Assert.IsFalse(PhoneSaberDiscoveryResponder.IsRequest(bytes, bytes.Length), "旧PCは計測に返信しない");
        }
    }
    [Test] public void AutoISOParsesLegacyAndLogsOnlyChangesPerStation() {
        var events = new System.Collections.Generic.List<string>();
        var store = new PhoneSaberMetricsStore((station, colour, kind, detail) =>
            events.Add(station + ":" + colour + ":" + kind + ":" + detail));
        double time = 0;
        void Receive(string suffix, string station = "A") {
            var bytes = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Valid).Replace("station=A", "station=" + station) + suffix);
            Assert.IsTrue(PhoneSaberMetrics.TryParse(bytes, bytes.Length, out _));
            store.Receive(bytes, bytes.Length, station, time++);
        }
        Receive(""); Assert.AreEqual(0, events.Count);
        Receive(" auto-iso=160"); Receive(" auto-iso=160"); Receive(""); Receive(" auto-iso=160");
        Receive(" auto-iso=125"); Receive(" auto-iso=off"); Receive(" auto-iso=off");
        Receive(" auto-iso=160", "B");
        CollectionAssert.AreEqual(new[] {
            "A:SYSTEM:phone-auto-iso:iso=160 prev=unknown", "A:SYSTEM:phone-auto-iso:iso=125 prev=160",
            "A:SYSTEM:phone-auto-iso:iso=off prev=125", "B:SYSTEM:phone-auto-iso:iso=160 prev=unknown" }, events);
        foreach (string value in new[] { "0", "01", "-1", "2001", "32.0", "ON", "unknown", "32 auto-iso=40" }) {
            var bytes = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Valid) + " auto-iso=" + value);
            Assert.IsFalse(PhoneSaberMetrics.TryParse(bytes, bytes.Length, out _), value);
        }
    }
    static PhoneSaberMetrics Phone(double at = 0) {
        Assert.IsTrue(PhoneSaberMetrics.TryParse(Valid, Valid.Length, out var m)); m.ReceivedAt = at; return m;
    }
    [Test] public void OptionalModelAcceptsLegacyAndFutureFieldsWithinByteLimit() {
        Assert.IsNull(Phone().Model);
        var data = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Valid) + " model=iPhone16_2 future=ok");
        Assert.LessOrEqual(data.Length, PhoneSaberMetrics.MaximumBytes);
        Assert.IsTrue(PhoneSaberMetrics.TryParse(data, data.Length, out var phone));
        Assert.AreEqual("iPhone16_2", phone.Model);
        foreach (string suffix in new[] { " model=", " model=x model=y", " model=" + new string('x', 17), " model=x=y" }) {
            var bad = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Valid) + suffix);
            Assert.IsFalse(PhoneSaberMetrics.TryParse(bad, bad.Length, out _));
        }
    }
    [Test] public void OptionalEmitterAcceptsOnlyExactBitsAndNeverInfersMissingValues() {
        foreach (string platform in new[] { "ios", "android" }) {
            string legacy = Encoding.ASCII.GetString(Valid).Replace("platform=ios", "platform=" + platform);
            var bytes = Encoding.ASCII.GetBytes(legacy);
            Assert.IsTrue(PhoneSaberMetrics.TryParse(bytes, bytes.Length, out var old));
            Assert.IsNull(old.Emitter);
            foreach (string value in new[] { "0", "1" }) {
                bytes = Encoding.ASCII.GetBytes(legacy + " emitter=" + value);
                Assert.IsTrue(PhoneSaberMetrics.TryParse(bytes, bytes.Length, out var phone));
                Assert.AreEqual(value == "1", phone.Emitter);
                Assert.IsNull(phone.Controls, "emitterはctlから独立した任意項目");
            }
            foreach (string suffix in new[] { " emitter=", " emitter=2", " emitter=-1", " emitter=01", " emitter=00",
                " emitter=1.0", " emitter=true", " emitter=unknown", " emitter=+1", " emitter=1=0",
                " emitter=0 emitter=1", " emitter=1 emitter=1", " ctl=12371" }) {
                bytes = Encoding.ASCII.GetBytes(legacy + suffix);
                Assert.IsFalse(PhoneSaberMetrics.TryParse(bytes, bytes.Length, out _), suffix);
            }
        }
    }
    [Test] public void FreshSnapshotKeepsBothPhonesWithoutCoordinateTrafficAndExpiresThem() {
        var store = new PhoneSaberMetricsStore();
        store.Receive(Valid, Valid.Length, "192.168.1.119", 1);
        store.Receive(Valid, Valid.Length, "192.168.1.129", 2);
        Assert.AreEqual(2, store.ReadFresh(3).Count);
        Assert.AreEqual(1, store.ReadFresh(17).Count);
        Assert.AreEqual(0, store.ReadFresh(17.01).Count);
        Assert.AreEqual(0, store.ReadFresh(0).Count);
    }
    [Test] public void TermsAndUnknownsNeverBecomeZeroLatency() {
        var m = Phone(); var pc = new PhoneSaberTimingStats(new double[] { 7, 7, 7 });
        var e = new PhoneSaberLatencyEstimate(m, pc, "10.0.0.1", .1, 1, 60, 0);
        Assert.AreEqual(36.9 + 1.6 + 7 + 500.0 / 60 + 500.0 / m.CaptureFps.Value, e.TotalMs.Value, 1e-9);
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
        StringAssert.Contains("total-ms=62.2", log);
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
            typeof(PhoneSaberDiscoveryResponder).GetField("socket", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(responder, receiver);
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
