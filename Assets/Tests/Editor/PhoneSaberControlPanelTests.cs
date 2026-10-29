using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;

public class PhoneSaberControlPanelTests
{
    [Test]
    public void F8DefaultsIsAnExplicitAction()
    {
        int calls = 0;
        var handlers = new Dictionary<string, Action<string>> {
            { "phone-defaults", _ => { calls++; PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.DefaultsCommand); } }
        };
        Assert.IsTrue(PhoneSaberControlPanelActions.Dispatch("phone-defaults", "", handlers));
        Assert.AreEqual(1, calls);
        Assert.IsTrue(PhoneSaberDiscoveryResponder.Acknowledgements.Latest(out _, out string fields, out _, out bool automatic));
        Assert.AreEqual("defaults=1", fields);
        Assert.IsFalse(automatic);
        Assert.IsFalse(PhoneSaberControlPanelActions.Dispatch("phone-defaults", "1", handlers));
        Assert.AreEqual(1, calls);
    }

    [TestCase("A")]
    [TestCase("B")]
    public void ReconnectedPhoneReportsSavedChoicesWithoutDefaultsAck(string station)
    {
        var phone = new PhoneSaberControlPanelPhone(station, "10.0.0.1");
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Controls = "0025", ReceivedAt = 100 });
        Assert.AreEqual("OFF", phone.Value("trackLock"));
        Assert.AreEqual("0", phone.Value("exposureEv"));
        Assert.AreEqual("1/1000", phone.Value("shutter"));
        Assert.AreEqual("100", phone.Value("iso"));
        Assert.IsNull(phone.Value("colouredEmitter"), "ctlにない項目を既定ONと推測しない");
        // 操作ACKでOFFを確認してから再接続しても、その値を既定ONへ戻さない。
        phone.Acknowledge(new PhoneSaberAcknowledgement(1, station, "ios", "shutter-emitter", "off", "10.0.0.1"), 101, 101);
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Controls = "0025", ReceivedAt = 200 });
        Assert.AreEqual("100", phone.Value("iso"));
        Assert.AreEqual("OFF", phone.Value("colouredEmitter"));
    }

    [Test] public void PhoneIdentityAndStationCollisionUseFreshDistinctSenders() {
        PhoneSaberControlPanelPhone Phone(string sender, string station, double at, string model = "iPhone14_5") {
            var phone = new PhoneSaberControlPanelPhone(station, sender);
            phone.Metrics(new PhoneSaberMetrics { Station = station, Platform = "ios", Model = model,
                App = "1.0.20261024", ReceivedAt = at });
            return phone;
        }
        var a = Phone(".119", "A", 10);
        var b = Phone(".129", "B", 10, "iPhone16_2");
        Assert.AreEqual("iPhone 13", a.ModelLabel);
        Assert.AreEqual("iPhone 15 Pro Max", b.ModelLabel);
        Assert.IsEmpty(PhoneSaberControlPanelPhone.StationWarnings(new[] { a, b }, 11));
        var secondA = Phone(".129", "A", 10, "iPhone16_2");
        var warnings = PhoneSaberControlPanelPhone.StationWarnings(new[] { a, secondA }, 11);
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains("同じ台 A", warnings[0]);
        StringAssert.Contains("iPhone 13 (.119)", warnings[0]);
        StringAssert.Contains("iPhone 15 Pro Max (.129)", warnings[0]);
        Assert.IsEmpty(PhoneSaberControlPanelPhone.StationWarnings(new[] { a, secondA }, 25.01));
        Assert.IsEmpty(PhoneSaberControlPanelPhone.StationWarnings(new[] { a, a }, 11));
        // 同じ端末がBへ変更したら古いAの報告は数えない。
        var moved = Phone(".129", "B", 11, "iPhone16_2");
        Assert.IsEmpty(PhoneSaberControlPanelPhone.StationWarnings(new[] { a, secondA, moved }, 11));
        var json = new StringBuilder(); a.AppendJson(json, 11);
        StringAssert.Contains("\"model\":\"iPhone 13\"", json.ToString());
        StringAssert.Contains("\"app\":\"1.0.20261024\"", json.ToString());
        StringAssert.Contains("\"metricsAgeSeconds\":1.0", json.ToString());
        Assert.AreEqual("機種未報告", new PhoneSaberControlPanelPhone("A", null).ModelLabel);
    }

    [TestCase("A")]
    [TestCase("B")]
    public void PcStationMismatchWithOnePhoneIsAWarningInPanelJson(string pcStation)
    {
        string reported = pcStation == "A" ? "B" : "A";
        var phone = new PhoneSaberControlPanelPhone(reported, "10.0.0.1");
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = reported, Model = "iPhone14_5", ReceivedAt = 10 });
        var warnings = PhoneSaberControlPanelPhone.StationWarnings(new[] { phone }, 11, pcStation);
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains("このPCの台 " + pcStation + " とiPhoneの報告台 " + reported + " が一致していません", warnings[0]);
        StringAssert.Contains("iPhone 13 (10.0.0.1)", warnings[0]);
        StringAssert.Contains("iPhoneの台・送信先・手動IP優先を確認", warnings[0]);
        StringAssert.DoesNotContain("混線確定", warnings[0]);
        var panel = new PhoneSaberControlPanelState { Now = 11 };
        panel.BeginSection("いまの設定");
        foreach (string warning in warnings) panel.Label(warning, warning: true);
        Assert.IsTrue(panel.Sections[0].Items[0].Warning);
        string json = panel.ToJson();
        StringAssert.Contains("\"text\":" + PhoneSaberControlPanelState.Quote(warnings[0]), json);
        StringAssert.Contains("\"warning\":true", json);
    }

    [TestCase("A")]
    [TestCase("B")]
    public void StationMismatchRequiresExplicitStationsAndMetrics(string pcStation)
    {
        foreach (string reported in new[] { pcStation, "none", "?", "", null })
        {
            var phone = new PhoneSaberControlPanelPhone(pcStation, "10.0.0.1");
            phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = reported, ReceivedAt = 10 }, pcStation);
            Assert.IsEmpty(PhoneSaberControlPanelPhone.StationWarnings(new[] { phone }, 11, pcStation));
        }
        var explicitPhone = new PhoneSaberControlPanelPhone(pcStation, "10.0.0.1");
        explicitPhone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = pcStation, ReceivedAt = 10 });
        foreach (string unspecifiedPc in new[] { "none", "?", "", null })
            Assert.IsEmpty(PhoneSaberControlPanelPhone.StationWarnings(new[] { explicitPhone }, 11, unspecifiedPc));
        var missing = new PhoneSaberControlPanelPhone(pcStation == "A" ? "B" : "A", "10.0.0.1");
        Assert.IsEmpty(PhoneSaberControlPanelPhone.StationWarnings(new[] { missing }, 11, pcStation));
        Assert.IsEmpty(PhoneSaberControlPanelPhone.StationWarnings(new PhoneSaberControlPanelPhone[0], 11, pcStation));
        // ACK等に台があっても、metricsの計測がなければ不一致を推測しない。
        missing.ReportedStation = missing.Station;
        Assert.IsEmpty(PhoneSaberControlPanelPhone.StationWarnings(new[] { missing }, 11, pcStation));
        var second = new PhoneSaberControlPanelPhone(pcStation, "10.0.0.2");
        second.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = pcStation, ReceivedAt = 10 });
        var collision = PhoneSaberControlPanelPhone.StationWarnings(new[] { explicitPhone, second }, 11, pcStation);
        Assert.AreEqual(1, collision.Count);
        StringAssert.Contains("同じ台 " + pcStation, collision[0]);
    }

    [TestCase("A")]
    [TestCase("B")]
    public void StationMismatchUsesOnlyMetricsWithinFifteenSeconds(string pcStation)
    {
        string reported = pcStation == "A" ? "B" : "A";
        var phone = new PhoneSaberControlPanelPhone(reported, "10.0.0.1");
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = reported, ReceivedAt = 10 });
        foreach (double now in new[] { 10d, 25d })
            Assert.AreEqual(1, PhoneSaberControlPanelPhone.StationWarnings(new[] { phone }, now, pcStation).Count);
        foreach (double now in new[] { 25.01, 9.99 })
            Assert.IsEmpty(PhoneSaberControlPanelPhone.StationWarnings(new[] { phone }, now, pcStation));
    }

    [TestCase("A")]
    [TestCase("B")]
    public void StationMismatchUsesLatestSenderReportRegardlessOfEnumerationOrder(string pcStation)
    {
        string reported = pcStation == "A" ? "B" : "A";
        var old = new PhoneSaberControlPanelPhone(reported, "10.0.0.1");
        old.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = reported, ReceivedAt = 10 });
        Assert.AreEqual(1, PhoneSaberControlPanelPhone.StationWarnings(new[] { old }, 11, pcStation).Count);
        foreach (string latestStation in new[] { pcStation, "none" })
        {
            var latest = new PhoneSaberControlPanelPhone(pcStation, old.Sender);
            latest.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = latestStation, ReceivedAt = 11 }, pcStation);
            foreach (var phones in new[] { new[] { old, latest }, new[] { latest, old } })
                Assert.IsEmpty(PhoneSaberControlPanelPhone.StationWarnings(phones, 11, pcStation));
        }
        // 逆向きの変更では、新しい不一致を古い一致報告で隠さない。
        var same = new PhoneSaberControlPanelPhone(pcStation, old.Sender);
        same.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = pcStation, ReceivedAt = 9 });
        foreach (var phones in new[] { new[] { same, old }, new[] { old, same } })
            Assert.AreEqual(1, PhoneSaberControlPanelPhone.StationWarnings(phones, 11, pcStation).Count);
    }

    [TestCase("A")]
    [TestCase("B")]
    public void EmitterMetricsConfirmValueSourceAndTimeWithoutAckOrControls(string station)
    {
        foreach (bool emitter in new[] { true, false })
        {
            var phone = new PhoneSaberControlPanelPhone(station, "10.0.0.1");
            phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, ReceivedAt = 9 });
            Assert.IsFalse(phone.Settings.ContainsKey("colouredEmitter"));
            phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Emitter = emitter, ReceivedAt = 10 });
            Assert.AreEqual(emitter ? "ON" : "OFF", phone.Value("colouredEmitter"));
            var report = phone.Settings["colouredEmitter"];
            Assert.AreEqual("metrics", report.Source);
            Assert.AreEqual(10, report.At); Assert.AreEqual(10, report.Order);
            Assert.IsNull(phone.Value("trackLock"));
            var json = new StringBuilder(); phone.AppendJson(json, 12);
            StringAssert.Contains("\"colouredEmitter\":{\"value\":\"" + (emitter ? "ON" : "OFF") +
                "\",\"ageSeconds\":2.0,\"source\":\"metrics\"}", json.ToString());
            // 旧アプリの省略報告は、確認済みの値・時刻・出典を変えない。
            phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Controls = "1233", ReceivedAt = 11 });
            Assert.AreSame(report, phone.Settings["colouredEmitter"]);
        }
    }

    [Test]
    public void AutoIsoAckIsShownAndCommandIsChecked()
    {
        var phone = new PhoneSaberControlPanelPhone("A", "10.0.0.1");
        Assert.IsNull(phone.Value("autoIso"));
        phone.Acknowledge(Ack(1, "auto-iso", "on", "A"), 9, 8);
        Assert.AreEqual("ON", phone.Value("autoIso"));
        phone.Acknowledge(Ack(2, "auto-iso", "off", "A"), 11, 10);
        Assert.AreEqual("OFF", phone.Value("autoIso"));
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(5, "auto-iso=1", 100);
        Assert.IsTrue(store.Receive(new PhoneSaberAcknowledgement(5, "A", "ios", "auto-iso", "on", "10.0.0.1"), "10.0.0.1"));
    }

    [TestCase("A")]
    [TestCase("B")]
    public void EmitterMetricsAndAcksPreserveNewestReport(string station)
    {
        var phone = new PhoneSaberControlPanelPhone(station, "10.0.0.1");
        phone.Acknowledge(Ack(1, "shutter-emitter", "on", station), 9, 8);
        Assert.AreEqual("ON", phone.Value("colouredEmitter"));
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Emitter = false, ReceivedAt = 10 });
        Assert.AreEqual("OFF", phone.Value("colouredEmitter"));
        var report = phone.Settings["colouredEmitter"];
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Emitter = true, ReceivedAt = 7 });
        foreach (double commandAt in new[] { 8, double.NegativeInfinity })
        {
            phone.Acknowledge(Ack(1, "shutter-emitter", "on", station), 12, commandAt);
            phone.Acknowledge(Ack(2, "defaults", "tracklock-on-ev-2-shutter-2000", station), 13, commandAt);
        }
        Assert.AreSame(report, phone.Settings["colouredEmitter"]);
        phone.Acknowledge(Ack(3, "shutter-emitter", "on", station), 15, 14);
        report = phone.Settings["colouredEmitter"];
        Assert.AreEqual("ON", report.Value); Assert.AreEqual("ACK確認", report.Source);
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Emitter = false, ReceivedAt = 13 });
        Assert.AreSame(report, phone.Settings["colouredEmitter"]);
        phone.Acknowledge(Ack(4, "defaults", "tracklock-on-ev-2-shutter-2000", station), 17, 16);
        Assert.IsNull(phone.Value("colouredEmitter"));
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Emitter = true, ReceivedAt = 10 });
        Assert.IsNull(phone.Value("colouredEmitter"), "リセット前のmetricsを再読込しても復活しない");
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Emitter = false, ReceivedAt = 18 });
        Assert.AreEqual("OFF", phone.Value("colouredEmitter"));
        Assert.AreEqual(18, phone.Settings["colouredEmitter"].At);
        Assert.AreEqual("metrics", phone.Settings["colouredEmitter"].Source);
    }

    [TestCase("A")]
    [TestCase("B")]
    public void EmitterMetricsStayWithSenderAndEffectiveStation(string station)
    {
        var store = new PhoneSaberMetricsStore();
        string legacy = Encoding.ASCII.GetString(PhoneSaberLatencyEstimateTests.Valid).Replace("station=A", "station=" + station);
        void Receive(string suffix, string sender, double at)
        {
            var bytes = Encoding.ASCII.GetBytes(legacy + suffix);
            store.Receive(bytes, bytes.Length, sender, at);
        }
        Receive(" emitter=1", "10.0.0.1", 10);
        Receive(" emitter=0", "10.0.0.2", 11);
        var phone = new PhoneSaberControlPanelPhone(station, "10.0.0.1");
        var other = new PhoneSaberControlPanelPhone(station, "10.0.0.2");
        phone.Metrics(store.Read(phone.Sender, 12));
        other.Metrics(store.Read(other.Sender, 12));
        Assert.AreEqual("ON", phone.Value("colouredEmitter"));
        Assert.AreEqual("OFF", other.Value("colouredEmitter"));
        Assert.AreEqual(10, phone.Settings["colouredEmitter"].At);
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station == "A" ? "B" : "A", Emitter = false, ReceivedAt = 13 });
        phone.Metrics(new PhoneSaberMetrics { Platform = "android", Station = station, Emitter = false, ReceivedAt = 13 });
        phone.Acknowledge(Ack(1, "shutter-emitter", "off", station, "10.0.0.2"), 14, 13);
        Assert.AreEqual("ON", phone.Value("colouredEmitter"));
        // 未指定台は既存の呼出側と同じく、このPCの実効台に紐づける。
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = "none", Emitter = false, ReceivedAt = 15 }, station);
        Assert.AreEqual("OFF", phone.Value("colouredEmitter"));
        Assert.AreEqual("none", phone.ReportedStation);
        Receive("", "10.0.0.3", 16);
        var unreported = new PhoneSaberControlPanelPhone(station, "10.0.0.3");
        unreported.Metrics(store.Read(unreported.Sender, 16));
        Assert.IsFalse(unreported.Settings.ContainsKey("colouredEmitter"));
    }

    const string Token = "0123456789abcdef0123456789abcdef";
    static byte[] Request(string method = "GET", string target = "/?t=" + Token, string headers = "Host: 127.0.0.1:5010\r\n")
        => Encoding.ASCII.GetBytes(method + " " + target + " HTTP/1.1\r\n" + headers + "\r\n");
    static int Parse(byte[] bytes) => PhoneSaberControlPanelServer.ParseRequest(bytes, 5010, Token, out _);

    [Test]
    public void ShutterButtonsDispatchOnlySupportedValues()
    {
        string seen = null;
        var handlers = new Dictionary<string, Action<string>> { { "phone-shutter", value => seen = value } };
        foreach (string value in new[] { "0", "500", "1000", "2000" })
        {
            Assert.IsTrue(PhoneSaberControlPanelActions.Dispatch("phone-shutter", value, handlers));
            Assert.AreEqual(value, seen);
        }
        foreach (string value in new[] { "off", "250", "-1", "1000&clip=1" })
            Assert.IsFalse(PhoneSaberControlPanelActions.Dispatch("phone-shutter", value, handlers));
        handlers["phone-shutter-iso"] = value => seen = value;
        handlers["phone-shutter-emitter"] = value => seen = value;
        foreach (string value in new[] { "2000", "250", "200", "160", "125", "100", "80", "64" })
        {
            Assert.IsTrue(PhoneSaberControlPanelActions.Dispatch("phone-shutter-iso", value, handlers));
            Assert.AreEqual(value, seen);
        }
        foreach (string value in new[] { "0", "1" })
            Assert.IsTrue(PhoneSaberControlPanelActions.Dispatch("phone-shutter-emitter", value, handlers));
        foreach (string value in new[] { "0", "1000", "500", "off", "500&clip=1" })
            Assert.IsFalse(PhoneSaberControlPanelActions.Dispatch("phone-shutter-iso", value, handlers));
        Assert.IsFalse(PhoneSaberControlPanelActions.Dispatch("phone-shutter-emitter", "on", handlers));
    }

    [Test]
    public void ParsesMethodPathQueryAndEncodedValues()
    {
        Assert.AreEqual(200, PhoneSaberControlPanelServer.ParseRequest(Request("POST",
            "/action?t=" + Token + "&name=phone-exposure&value=%2D1"), 5010, Token, out var request));
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual("/action", request.Path);
        Assert.AreEqual(Token, request.Query["t"]);
        Assert.AreEqual("phone-exposure", request.Name);
        Assert.AreEqual("-1", request.Value);
        Assert.AreEqual(200, Parse(Request(target: "/state?t=" + Token)));
    }

    [TestCase("/")]
    [TestCase("/?t=wrong")]
    [TestCase("/state")]
    [TestCase("/action?name=phone-clip")]
    public void EveryEndpointRequiresToken(string target) => Assert.AreEqual(403, Parse(Request(target: target)));

    [TestCase("127.0.0.1:5010", 200)]
    [TestCase("localhost:5010", 200)]
    [TestCase("LOCALHOST:5010", 200)]
    [TestCase("localhost", 403)]
    [TestCase("127.0.0.1:5011", 403)]
    [TestCase("attacker.example:5010", 403)]
    [TestCase("127.0.0.1.attacker.example:5010", 403)]
    [TestCase("127.0.0.1:5010@attacker.example", 403)]
    public void RejectsRebindingHosts(string host, int expected) => Assert.AreEqual(expected, Parse(Request(headers: "Host: " + host + "\r\n")));

    [TestCase("POST", "/", 405)]
    [TestCase("POST", "/state", 405)]
    [TestCase("GET", "/action", 405)]
    [TestCase("PUT", "/action", 405)]
    [TestCase("DELETE", "/", 405)]
    [TestCase("GET", "/missing", 404)]
    public void EnforcesMethodsAndRoutes(string method, string path, int expected) => Assert.AreEqual(expected,
        Parse(Request(method, path + "?t=" + Token + "&name=phone-clip")));

    [TestCase("Host: localhost:5010\r\nHost: localhost:5010\r\n", 400)]
    [TestCase("", 403)]
    [TestCase("Host: localhost:5010\r\nContent-Length: 1\r\n", 400)]
    [TestCase("Host: localhost:5010\r\nContent-Length: -1\r\n", 400)]
    [TestCase("Host: localhost:5010\r\nTransfer-Encoding: chunked\r\n", 400)]
    [TestCase("Host: localhost:5010\r\nContent-Length: 0\r\n", 200)]
    [TestCase("Host: localhost:5010\r\n folded\r\n", 400)]
    [TestCase("Host: localhost:5010\r\nX-Test: a\nb\r\n", 400)]
    public void RejectsAmbiguousHeadersAndBodies(string headers, int expected) => Assert.AreEqual(expected, Parse(Request(headers: headers)));

    [TestCase("&t=duplicate")]
    [TestCase("&name=x&name=y")]
    [TestCase("&value=%")]
    [TestCase("&value=%GG")]
    [TestCase("&value=%1")]
    [TestCase("&value=a\nb")]
    [TestCase("&value=\t")]
    [TestCase("&value=#fragment")]
    public void RejectsMalformedOrDuplicateQuery(string suffix) => Assert.AreEqual(400, Parse(Request(target: "/?t=" + Token + suffix)));

    [Test]
    public void EnforcesSizeAndFramingLimits()
    {
        Assert.AreEqual(413, Parse(new byte[PhoneSaberControlPanelServer.MaximumRequestBytes + 1]));
        Assert.AreEqual(413, Parse(Request(target: "/?t=" + Token + "&padding=" + new string('a', PhoneSaberControlPanelServer.MaximumTargetBytes))));
        Assert.AreEqual(400, Parse(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost:5010\r\n")));
        Assert.AreEqual(400, Parse(Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\n\r\n")));
        Assert.AreEqual(400, Parse(Encoding.ASCII.GetBytes("GET /?t=" + Token + " HTTP/1.1\r\nHost: localhost:5010\r\n\r\nbody")));
        Assert.AreEqual(400, Parse(null));
        Assert.AreEqual(403, PhoneSaberControlPanelServer.ParseRequest(Request(), 5010, "", out _));
    }

    // 個々の選択肢を列挙し、削除・値の対応漏れを検出する。
    public static IEnumerable<TestCaseData> ActionCases()
    {
        var expected = new Dictionary<string, string[]>
        {
            { "calibration-start", new[] { "" } }, { "calibration-advance", new[] { "" } },
            { "calibration-cancel", new[] { "" } }, { "calibration-reset", new[] { "" } },
            { "calibration-blue", new[] { "0", "1" } }, { "calibration-enabled", new[] { "0", "1" } },
            { "prediction", new[] { "auto", "0", "20", "40", "60" } },
            { "display-latency-adjust", new[] { "-10", "10" } }, { "display-latency-centre", new[] { "" } },
            { "display-latency-clear", new[] { "" } },
            { "display-sync", new[] { "0", "1" } }, { "frame-cap", new[] { "0", "1", "2" } },
            { "judge-mode", new[] { "0", "1" } }, { "judge-adjust", new[] { "-10", "10" } }, { "judge-centre", new[] { "" } },
            { "filter", new[] { "0", "1", "2", "3", "4" } }, { "stabilizer", new[] { "0", "1" } },
            { "phone-ignore", new[] { "red", "blue", "both" } }, { "phone-clear-ignore", new[] { "" } },
            { "phone-learn-area", new[] { "" } }, { "phone-clear-area", new[] { "" } },
            { "phone-shutter-iso", new[] { "2000", "250", "200", "160", "125", "100", "80", "64" } },
            { "phone-shutter-emitter", new[] { "0", "1" } }, { "phone-auto-iso", new[] { "0", "1" } },
            { "phone-shutter", new[] { "0", "500", "1000", "2000" } },
            { "phone-exposure", new[] { "0", "-1", "-2" } }, { "phone-track-lock", new[] { "", "0", "1" } }, { "phone-clip", new[] { "" } },
            { "pc-ignore", new[] { "red", "blue" } }, { "pc-clear-ignore", new[] { "" } }, { "clocks-synchronized", new[] { "0", "1" } }
        };
        foreach (var pair in expected)
            foreach (string value in pair.Value) yield return new TestCaseData(pair.Key, value);
    }

    [TestCaseSource(nameof(ActionCases))]
    public void EveryChoiceRoutesToItsHandler(string name, string value)
    {
        string seen = null;
        var handlers = new Dictionary<string, Action<string>> { { name, v => seen = v } };
        Assert.IsTrue(PhoneSaberControlPanelActions.Dispatch(name, value, handlers, confirmed: true));
        Assert.AreEqual(value, seen);
        Assert.AreEqual(200, Parse(Request("POST", "/action?t=" + Token + "&name=" + name + "&value=" + Uri.EscapeDataString(value))));
    }

    [TestCase("phone-shutter-iso", "0")]
    [TestCase("phone-shutter-iso", "500")]
    [TestCase("phone-shutter-iso", "1000")]
    [TestCase("phone-shutter-emitter", "on")]
    [TestCase("unknown", "")]
    [TestCase("prediction", "1000")]
    [TestCase("filter", "-1")]
    [TestCase("phone-ignore", "red&clip=1")]
    [TestCase("phone-clip", "1")]
    [TestCase(null, "")]
    public void RejectsUnknownActionsAndValues(string name, string value)
    {
        Assert.IsFalse(PhoneSaberControlPanelActions.Dispatch(name, value, new Dictionary<string, Action<string>>()));
        Assert.AreEqual(400, Parse(Request("POST", "/action?t=" + Token + "&name=" + Uri.EscapeDataString(name ?? "") + "&value=" + Uri.EscapeDataString(value))));
    }

    [Test]
    public void StationSetupButtonAndKeyQueueTheSameTenSecondLearningOnce()
    {
        var go = new GameObject("station setup test");
        try
        {
            var overlay = go.AddComponent<PhoneSaberOperatorOverlay>();
            var state = (PhoneSaberControlPanelState)typeof(PhoneSaberOperatorOverlay)
                .GetMethod("BuildPanel", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(overlay, new object[] { 0d });
            var button = state.Sections.SelectMany(s => s.Items).Single(i => i.Name == "phone-learn-area");
            Assert.AreEqual("範囲を覚える（10秒、振りやすい範囲で上下左右に振る）", button.Text);
            Assert.IsTrue(button.Enabled);
            var advanced = state.Sections.Single(s => s.Items.Any(i => i.Name == "calibration-start"));
            StringAssert.StartsWith("詳細", advanced.Title);
            var key = PhoneSaberOperatorKeys.CommandFor(UnityEngine.InputSystem.Key.P, 0, out _).Value;
            Assert.AreEqual("learn-area=10", key.command);
            int before = PhoneSaberDiscoveryResponder.Acknowledgements.Revision;
            Assert.IsTrue(PhoneSaberControlPanelActions.Dispatch(button.Name, button.Value, overlay.ControlPanelHandlers));
            Assert.IsTrue(PhoneSaberDiscoveryResponder.Acknowledgements.Latest(out int after, out string fields, out _));
            Assert.AreEqual(before + 1, PhoneSaberDiscoveryResponder.Acknowledgements.Revision);
            Assert.AreEqual(key.command, fields);
            StringAssert.EndsWith(" learn-area=10", PhoneSaberDiscoveryResponder.BuildCommand(after, fields));
            var reset = state.Sections.SelectMany(s => s.Items).Single(i => i.Name == "phone-defaults");
            Assert.IsTrue(reset.Enabled);
            Assert.IsTrue(PhoneSaberControlPanelActions.Dispatch(reset.Name, reset.Value, overlay.ControlPanelHandlers));
            Assert.IsTrue(PhoneSaberDiscoveryResponder.Acknowledgements.Latest(out _, out fields, out _, out bool automatic));
            Assert.AreEqual(PhoneSaberDiscoveryResponder.DefaultsCommand, fields);
            Assert.IsFalse(automatic, "F8で押したときだけ明示リセットを送る");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    [Test]
    public void OverlayHasExactlyTheApprovedHandlersWithoutStartingSockets()
    {
        var go = new GameObject("control panel test");
        try
        {
            var overlay = go.AddComponent<PhoneSaberOperatorOverlay>();
            CollectionAssert.AreEquivalent(PhoneSaberControlPanelActions.Names, overlay.ControlPanelHandlers.Keys);
            Assert.IsNull(typeof(PhoneSaberOperatorOverlay).GetField("controlPanel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay));
            Assert.NotNull(typeof(PhoneSaberOperatorKeys).GetField("lastSentTrackLock", BindingFlags.NonPublic | BindingFlags.Static));
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    [TestCase(false, true, true)]
    [TestCase(true, true, false)]
    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    public void F8BrowserDecision(bool shift, bool serverRunning, bool browser)
        => Assert.AreEqual(browser, PhoneSaberControlPanelActions.ShouldOpenBrowser(shift, serverRunning));

    // Windows の台で「Macの画面に開きました」と出していた。
    [Test]
    public void F8BrowserNoticeNamesTheMacOnlyOnMac()
    {
        StringAssert.Contains("Mac", PhoneSaberControlPanelActions.BrowserOpenedNotice(mac: true));
        string other = PhoneSaberControlPanelActions.BrowserOpenedNotice(mac: false);
        StringAssert.DoesNotContain("Mac", other);
        StringAssert.Contains("Shift+F8", other);
    }

    [Test]
    public void JsonEscapesQuotesBackslashesControlsAndUnicode()
    {
        Assert.AreEqual("\"台\\\"\\\\\\n\\r\\t\\u0000\\u001f\\u2028\\ud800\"",
            PhoneSaberControlPanelState.Quote("台\"\\\n\r\t\0\u001f\u2028\ud800"));
        Assert.AreEqual("null", PhoneSaberControlPanelState.Quote(null));
        var state = new PhoneSaberControlPanelState();
        state.BeginSection("台\"A");
        state.Label("スマホ\n返事", true);
        state.Button("20ms", "prediction", "20", false, true);
        string json = state.ToJson();
        StringAssert.Contains("\"title\":\"台\\\"A\"", json);
        StringAssert.Contains("\"text\":\"スマホ\\n返事\"", json);
        StringAssert.Contains("\"enabled\":false,\"selected\":true", json);
        StringAssert.Contains("\"warning\":true", json);
        Assert.Throws<ArgumentException>(() => state.Button("bad", "unknown"));
    }

    [TestCase("judge-mode", "0")]
    [TestCase("phone-clear-ignore", "")]
    [TestCase("pc-clear-ignore", "")]
    [TestCase("phone-clear-area", "")]
    [TestCase("calibration-reset", "")]
    [TestCase("display-latency-clear", "")]
    public void DangerousActionsRequireConfirmationEvenOutsideBrowser(string name, string value)
    {
        int calls = 0;
        var handlers = new Dictionary<string, Action<string>> { { name, _ => calls++ } };
        Assert.IsFalse(PhoneSaberControlPanelActions.Dispatch(name, value, handlers));
        Assert.AreEqual(0, calls);
        Assert.IsTrue(PhoneSaberControlPanelActions.Dispatch(name, value, handlers, confirmed: true));
        Assert.AreEqual(1, calls);
        var state = new PhoneSaberControlPanelState();
        state.BeginSection("通常"); state.Button("危険", name, value); state.Button("自動", "judge-mode", "1");
        state.MoveDangerousControls();
        Assert.AreEqual(1, state.Sections[0].Items.Count);
        Assert.AreEqual("judge-mode", state.Sections[0].Items[0].Name);
        Assert.IsTrue(state.Sections[1].Dangerous);
        Assert.IsTrue(state.Sections[1].Items[0].RequiresConfirmation);
        StringAssert.Contains("\"requiresConfirmation\":true", state.ToJson());
        StringAssert.Contains("\"dangerous\":true", state.ToJson());
        Assert.IsFalse(PhoneSaberControlPanelActions.RequiresConfirmation("judge-mode", "1"));
    }

    static PhoneSaberAcknowledgement Ack(int id, string result, string detail, string station = "A", string sender = "10.0.0.1")
        => new PhoneSaberAcknowledgement(id, station, "ios", result, detail, sender);

    [Test]
    public void PhoneJsonIncludesEveryReportedSettingUnknownsSourcesAndAge()
    {
        var phone = new PhoneSaberControlPanelPhone("A", "10.0.0.1");
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = "A", Controls = "1231", ReceivedAt = 10 });
        phone.Acknowledge(Ack(1, "shutter-emitter", "on"), 12, 11);
        phone.Acknowledge(Ack(2, "cleared", "ignore"), 13, 12);
        phone.Acknowledge(Ack(3, "learned", "samples42"), 14, 13);
        var state = new PhoneSaberControlPanelState { Now = 20 };
        state.Phones.Add(phone); state.Phones.Add(new PhoneSaberControlPanelPhone("B", null));
        string json = state.ToJson();
        foreach (string key in PhoneSaberControlPanelPhone.Keys) StringAssert.Contains("\"" + key + "\":{", json);
        StringAssert.Contains("\"trackLock\":{\"value\":\"ON\",\"ageSeconds\":10.0,\"source\":\"metrics\"}", json);
        StringAssert.Contains("\"exposureEv\":{\"value\":\"-2\"", json);
        StringAssert.Contains("\"shutter\":{\"value\":\"1/2000\"", json);
        StringAssert.Contains("\"iso\":{\"value\":\"250\"", json);
        StringAssert.Contains("\"colouredEmitter\":{\"value\":\"ON\",\"ageSeconds\":8.0,\"source\":\"ACK確認\"}", json);
        StringAssert.Contains("\"ignoreRed\":{\"value\":\"0\"", json);
        StringAssert.Contains("\"ignoreBlue\":{\"value\":\"0\"", json);
        StringAssert.Contains("\"playArea\":{\"value\":\"設定済み\"", json);
        StringAssert.Contains("\"trackLock\":{\"value\":null,\"ageSeconds\":null,\"source\":null}", json);
    }

    [Test]
    public void PhoneReportsFollowMetricsAndAcksWithoutGuessingUnreportedTotals()
    {
        var phone = new PhoneSaberControlPanelPhone("A", "10.0.0.1");
        phone.Acknowledge(Ack(1, "defaults", "tracklock-on-ev-2-shutter-2000"), 8, 7);
        Assert.IsNull(phone.Value("colouredEmitter"));
        Assert.IsNull(phone.Value("iso"));
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = "A", Controls = "0011", ReceivedAt = 10 });
        Assert.AreEqual("OFF", phone.Value("trackLock"));
        Assert.AreEqual("1/500", phone.Value("shutter"));
        Assert.AreEqual("250", phone.Value("iso"));
        phone.Acknowledge(Ack(2, "tracklock", "on"), 12, 9);
        Assert.AreEqual("OFF", phone.Value("trackLock"), "古い操作のACKで新しいmetricsを戻さない");
        phone.Acknowledge(Ack(3, "tracklock", "on"), 13, 11);
        Assert.AreEqual("ON", phone.Value("trackLock"));
        phone.Acknowledge(Ack(4, "shutter", "off", "B"), 14, 13);
        phone.Acknowledge(Ack(4, "shutter", "off", sender: "10.0.0.2"), 14, 13);
        Assert.AreEqual("1/500", phone.Value("shutter"), "別台・別端末を混ぜない");
        phone.Acknowledge(Ack(5, "cleared", "ignore"), 15, 14);
        phone.Acknowledge(Ack(6, "ignored", "red2-blue1"), 16, 15);
        Assert.IsNull(phone.Value("ignoreRed"), "追加数は総数ではない");
        Assert.IsNull(phone.Value("ignoreBlue"));
        phone.Acknowledge(Ack(7, "cleared", "area"), 17, 16);
        phone.Acknowledge(Ack(8, "failed", "samples0"), 18, 17);
        Assert.AreEqual("なし", phone.Value("playArea"));
    }

    [TestCase("A")]
    [TestCase("B")]
    public void PhoneDefaultsAckLeavesUnreportedISOAndEmitterUnknown(string station)
    {
        foreach (string detail in new[] { "tracklock-on-ev-2-shutter-2000", "tracklock-on-ev-2" })
        {
            var phone = new PhoneSaberControlPanelPhone(station, "10.0.0.1");
            phone.Acknowledge(Ack(1, "defaults", detail, station), 8, 7);
            Assert.AreEqual("ON", phone.Value("trackLock"));
            Assert.AreEqual("-2", phone.Value("exposureEv"));
            Assert.AreEqual(detail.EndsWith("shutter-2000") ? "1/2000" : null, phone.Value("shutter"));
            Assert.IsNull(phone.Value("iso"));
            Assert.IsNull(phone.Value("colouredEmitter"));
            var json = new StringBuilder(); phone.AppendJson(json, 9);
            StringAssert.Contains("\"station\":\"" + station + "\"", json.ToString());
            StringAssert.Contains("\"iso\":{\"value\":null", json.ToString());
            StringAssert.Contains("\"colouredEmitter\":{\"value\":null", json.ToString());
        }
    }

    [TestCase("A")]
    [TestCase("B")]
    public void PhoneDefaultsAckInvalidatesOnlyPreResetISOAndEmitterReports(string station)
    {
        foreach (string detail in new[] { "tracklock-on-ev-2-shutter-2000", "tracklock-on-ev-2" })
        {
            var phone = new PhoneSaberControlPanelPhone(station, "10.0.0.1");
            var before = new PhoneSaberMetrics { Platform = "ios", Station = station, Controls = "0025", ReceivedAt = 5 };
            phone.Metrics(before);
            phone.Acknowledge(Ack(1, "shutter-emitter", "off", station), 6, 6);
            Assert.AreEqual("100", phone.Value("iso"));
            Assert.AreEqual("OFF", phone.Value("colouredEmitter"));
            var reset = Ack(2, "defaults", detail, station);
            phone.Acknowledge(reset, 8, 7);
            Assert.IsNull(phone.Value("iso"));
            Assert.IsNull(phone.Value("colouredEmitter"));
            Assert.AreEqual(7, phone.Settings["iso"].Order);
            Assert.AreEqual(7, phone.Settings["colouredEmitter"].Order);
            // パネルが同じ古いmetricsを再読込しても、リセット前の値を復活させない。
            phone.Metrics(before);
            Assert.IsNull(phone.Value("iso"));
            var json = new StringBuilder(); phone.AppendJson(json, 9);
            StringAssert.Contains("\"iso\":{\"value\":null", json.ToString());
            StringAssert.Contains("\"colouredEmitter\":{\"value\":null", json.ToString());
            phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Controls = "1234", ReceivedAt = 10 });
            Assert.AreEqual("125", phone.Value("iso"));
            Assert.IsNull(phone.Value("colouredEmitter"), "通常metricsから発光体ONを推測しない");
            phone.Acknowledge(reset, 11, 7);
            Assert.AreEqual("125", phone.Value("iso"));
            Assert.IsNull(phone.Value("colouredEmitter"));
        }
    }

    [TestCase("A")]
    [TestCase("B")]
    public void PhoneDefaultsAckPreservesNewerMetricsAndIndividualAcks(string station)
    {
        foreach (string emitter in new[] { "on", "off" })
        {
            var phone = new PhoneSaberControlPanelPhone(station, "10.0.0.1");
            phone.Acknowledge(Ack(1, "defaults", "tracklock-on-ev-2-shutter-2000", station), 8, 7);
            phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Controls = "0024", ReceivedAt = 10 });
            phone.Acknowledge(Ack(3, "shutter-emitter", emitter, station), 12, 11);
            Assert.AreEqual(emitter == "on" ? "ON" : "OFF", phone.Value("colouredEmitter"));
            // 遅延初着と重複到着の両方で、新しい報告とその時刻・出典を保持する。
            var iso = phone.Settings["iso"];
            var light = phone.Settings["colouredEmitter"];
            foreach (int id in new[] { 2, 2, 1 })
            {
                phone.Acknowledge(Ack(id, "defaults", "tracklock-on-ev-2-shutter-2000", station), 14, 9);
                Assert.AreSame(iso, phone.Settings["iso"]);
                Assert.AreSame(light, phone.Settings["colouredEmitter"]);
                Assert.AreEqual("OFF", phone.Value("trackLock"));
                Assert.AreEqual("0", phone.Value("exposureEv"));
                Assert.AreEqual("1/1000", phone.Value("shutter"));
            }
            // ISOの個別ACKも、後から観測した古いdefaultsより優先する。
            phone.Acknowledge(Ack(4, "shutter-iso", "100", station), 16, 15);
            phone.Acknowledge(Ack(2, "defaults", "tracklock-on-ev-2-shutter-2000", station), 17, 9);
            Assert.AreEqual("100", phone.Value("iso"));
            Assert.AreSame(light, phone.Settings["colouredEmitter"]);
            var json = new StringBuilder(); phone.AppendJson(json, 18);
            StringAssert.Contains("\"iso\":{\"value\":\"100\"", json.ToString());
            StringAssert.Contains("\"colouredEmitter\":{\"value\":\"" + (emitter == "on" ? "ON" : "OFF") + "\"", json.ToString());
        }
    }

    [TestCase("A")]
    [TestCase("B")]
    public void PhoneDefaultsAckWithUnknownCommandTimePreservesReports(string station)
    {
        var phone = new PhoneSaberControlPanelPhone(station, "10.0.0.1");
        var reset = Ack(1, "defaults", "tracklock-on-ev-2-shutter-2000", station);
        phone.Acknowledge(reset, 8, double.NegativeInfinity);
        Assert.IsNull(phone.Value("iso"));
        Assert.IsNull(phone.Value("colouredEmitter"));
        // 操作時刻不明のACK観測時刻より前でも、metricsを排除しない。
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Controls = "0024", ReceivedAt = 7 });
        Assert.AreEqual("125", phone.Value("iso"));
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Controls = "0024", ReceivedAt = 10 });
        phone.Acknowledge(Ack(2, "shutter-emitter", "off", station), 12, 11);
        var iso = phone.Settings["iso"];
        var light = phone.Settings["colouredEmitter"];
        phone.Acknowledge(reset, 13, double.NegativeInfinity);
        phone.Acknowledge(reset, 14, double.NegativeInfinity);
        Assert.AreSame(iso, phone.Settings["iso"]);
        Assert.AreSame(light, phone.Settings["colouredEmitter"]);
        Assert.AreEqual("125", phone.Value("iso"));
        Assert.AreEqual("OFF", phone.Value("colouredEmitter"));
        Assert.AreEqual("OFF", phone.Value("trackLock"));
        Assert.AreEqual("1/1000", phone.Value("shutter"));
        phone.Acknowledge(Ack(3, "shutter-iso", "100", station), 16, 15);
        phone.Acknowledge(reset, 17, double.NegativeInfinity);
        Assert.AreEqual("100", phone.Value("iso"));
    }

    [TestCase("A")]
    [TestCase("B")]
    public void PhoneDefaultsAckPreservesReportsAtTheCommandTime(string station)
    {
        var phone = new PhoneSaberControlPanelPhone(station, "10.0.0.1");
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Controls = "1234", ReceivedAt = 10 });
        phone.Acknowledge(Ack(2, "shutter-emitter", "off", station), 11, 10);
        phone.Acknowledge(Ack(1, "defaults", "tracklock-on-ev-2-shutter-2000", station), 12, 10);
        Assert.AreEqual("125", phone.Value("iso"));
        Assert.AreEqual("OFF", phone.Value("colouredEmitter"));
    }

    [TestCase("A")]
    [TestCase("B")]
    public void PhoneDefaultsAndIndividualAcksAreIsolatedByStationAndSender(string station)
    {
        var phone = new PhoneSaberControlPanelPhone(station, "10.0.0.1");
        phone.Metrics(new PhoneSaberMetrics { Platform = "ios", Station = station, Controls = "0025", ReceivedAt = 5 });
        phone.Acknowledge(Ack(1, "shutter-emitter", "off", station), 6, 6);
        var before = new StringBuilder(); phone.AppendJson(before, 20);
        foreach (string result in new[] { "defaults", "shutter-iso", "shutter-emitter" })
        {
            string detail = result == "defaults" ? "tracklock-on-ev-2-shutter-2000" : result == "shutter-iso" ? "160" : "on";
            phone.Acknowledge(Ack(2, result, detail, station == "A" ? "B" : "A"), 9, 8);
            phone.Acknowledge(Ack(2, result, detail, station, "10.0.0.2"), 9, 8);
        }
        var after = new StringBuilder(); phone.AppendJson(after, 20);
        Assert.AreEqual(before.ToString(), after.ToString());
    }

    [Test]
    public void PhonePendingWaitsForEachTargetAckAndNotLearningOrOtherStation()
    {
        string[] targets = { "10.0.0.1", "10.0.0.2" };
        Assert.IsFalse(PhoneSaberControlPanelPhone.HasAcknowledged("A", targets, new[] { Ack(1, "shutter", "500") }));
        Assert.IsFalse(PhoneSaberControlPanelPhone.HasAcknowledged("A", Array.Empty<string>(), new[] { Ack(1, "learning", "3s") }));
        Assert.IsFalse(PhoneSaberControlPanelPhone.HasAcknowledged("A", Array.Empty<string>(), new[] { Ack(1, "shutter", "500", "B") }));
        Assert.IsTrue(PhoneSaberControlPanelPhone.HasAcknowledged("A", targets,
            new[] { Ack(1, "shutter", "500"), Ack(1, "shutter", "500", sender: "10.0.0.2") }));
        var state = new PhoneSaberControlPanelState(); state.BeginSection("iPhone"); state.Button("1/500", "phone-shutter", "500");
        state.Sections[0].Items[0].Pending = true; state.Sections[0].Items[0].CommandId = 42;
        StringAssert.Contains("\"commandId\":42,\"pending\":true", state.ToJson());
    }

    static PhoneSaberControlPanelState.Item ResultItem(string name = "phone-learn-area", string value = "")
        => new PhoneSaberControlPanelState.Item { Name = name, Value = value, Enabled = true };

    [TestCase("A")]
    [TestCase("B")]
    public void OperationResultsKeepLateLearningFailureBesideISOSuccess(string station)
    {
        var store = new PhoneSaberAcknowledgementStore();
        var learn = new PhoneSaberOperatorKeys.PhoneAction(1, 0, "", station, "learn-area=10", new[] { "10.0.0.1" });
        var iso = new PhoneSaberOperatorKeys.PhoneAction(2, 1, "125", station, "shutter-iso=125", new[] { "10.0.0.1" });
        store.Queue(1, "learn-area=10", 0);
        store.Receive(Ack(1, "learning", "10s", station), "10.0.0.1");
        var learning = ResultItem(); learn.Apply(learning, 1, store.Read(1));
        Assert.IsTrue(learning.Pending);
        store.Queue(2, "shutter-iso=125", 1);
        store.Receive(Ack(2, "shutter-iso", "125", station), "10.0.0.1");
        store.Receive(Ack(1, "failed", "samples0", station), "10.0.0.1");
        var failed = ResultItem(); learn.Apply(failed, 11, store.Read(1));
        var success = ResultItem("phone-shutter-iso", "125"); iso.Apply(success, 11, store.Read(2));
        Assert.IsTrue(failed.Acknowledged); Assert.IsFalse(failed.Succeeded);
        Assert.IsTrue(failed.ResultWarning); Assert.IsFalse(failed.Pending); Assert.IsTrue(failed.Enabled);
        StringAssert.Contains("範囲を覚えられませんでした", failed.Result);
        StringAssert.DoesNotContain("応答なし", failed.Result);
        Assert.IsTrue(success.Succeeded); Assert.IsFalse(success.ResultWarning);
        StringAssert.Contains("ISO 125", success.Result);
        var state = new PhoneSaberControlPanelState(); state.BeginSection("iPhone");
        state.Sections[0].Items.Add(failed); state.Sections[0].Items.Add(success);
        string json = state.ToJson();
        StringAssert.Contains("範囲を覚えられませんでした", json); StringAssert.Contains("ISO 125", json);
        StringAssert.Contains("\"acknowledged\":true,\"succeeded\":false", json);
        StringAssert.Contains("\"resultWarning\":true", json);
        // ACK履歴が32指示を超えても、別操作で過去の結果を消さない。
        for (int id = 3; id <= 40; id++) store.Queue(id, "shutter-iso=100", 100 + id);
        Assert.IsEmpty(store.Read(1));
        var retained = ResultItem(); learn.Apply(retained, 200, store.Read(1));
        Assert.AreEqual(failed.Result, retained.Result); Assert.IsTrue(retained.ResultWarning);
    }

    [TestCase("A")]
    [TestCase("B")]
    public void OperationLearningWaitsButFailureIsTerminalAndLearnedDoesNotClaimPCApply(string station)
    {
        var action = new PhoneSaberOperatorKeys.PhoneAction(7, 10, "", station, "learn-area=10", new[] { "10.0.0.1" });
        var item = ResultItem(); action.Apply(item, 11, new[] { Ack(7, "learning", "10s", station) });
        Assert.IsTrue(item.Pending); Assert.IsFalse(item.Enabled);
        Assert.IsFalse(item.Acknowledged); Assert.IsFalse(item.Succeeded); Assert.IsFalse(item.ResultWarning);
        var overdue = ResultItem(); action.Apply(overdue, 40, Array.Empty<PhoneSaberAcknowledgement>());
        Assert.IsFalse(overdue.Pending); Assert.IsTrue(overdue.Enabled); Assert.IsTrue(overdue.ResultWarning);
        StringAssert.Contains(PhoneSaberOperatorKeys.MissingCompletionMessage, overdue.Result);
        var failed = ResultItem(); action.Apply(failed, 12, new[] { Ack(7, "failed", "samples0", station) });
        Assert.IsFalse(failed.Pending); Assert.IsTrue(failed.Enabled); Assert.IsTrue(failed.Acknowledged);
        Assert.IsFalse(failed.Succeeded); Assert.IsTrue(failed.ResultWarning);
        var learned = ResultItem(); action.Apply(learned, 13, new[] { Ack(7, "learned", "samples60", station) });
        Assert.IsTrue(learned.Acknowledged); Assert.IsTrue(learned.Succeeded); Assert.IsFalse(learned.ResultWarning);
        StringAssert.Contains("プレイ範囲を覚えました", learned.Result);
        StringAssert.DoesNotContain("PC", learned.Result); StringAssert.DoesNotContain("適用成功", learned.Result);
    }

    [TestCase("A")]
    [TestCase("B")]
    public void OperationNothingIsSpecificTerminalWarningWithoutAddingIgnoreArea(string station)
    {
        foreach (string colour in new[] { "red", "blue", "both" })
        {
            var action = new PhoneSaberOperatorKeys.PhoneAction(3, 0, colour, station, "ignore=" + colour, new[] { "10.0.0.1" });
            var item = ResultItem("phone-ignore", colour);
            action.Apply(item, 4, new[] { Ack(3, "nothing", "red0-blue0", station) });
            Assert.IsTrue(item.Acknowledged); Assert.IsTrue(item.Enabled); Assert.IsFalse(item.Pending);
            Assert.IsTrue(item.ResultWarning); Assert.IsFalse(item.Succeeded);
            StringAssert.Contains("無視する対象なし", item.Result);
            StringAssert.DoesNotContain("無視しました", item.Result);
        }
    }

    [TestCase("A")]
    [TestCase("B")]
    public void OperationResultsSeparateStationSenderCommandAndMixedTargets(string station)
    {
        string other = station == "A" ? "B" : "A";
        var action = new PhoneSaberOperatorKeys.PhoneAction(8, 0, "", station, "learn-area=10",
            new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" });
        var wrong = ResultItem(); action.Apply(wrong, 1, new[] {
            Ack(7, "failed", "samples0", station), Ack(8, "failed", "samples0", other),
            Ack(8, "failed", "samples0", station, "10.0.0.9"),
            new PhoneSaberAcknowledgement(8, station, "android", "failed", "samples0", "10.0.0.1") });
        Assert.IsFalse(wrong.Acknowledged); Assert.IsFalse(wrong.Succeeded); Assert.IsFalse(wrong.ResultWarning);
        StringAssert.DoesNotContain("覚えられませんでした", wrong.Result);
        var mixed = ResultItem(); action.Apply(mixed, 11, new[] {
            Ack(8, "learned", "samples60", station), Ack(8, "failed", "samples0", station, "10.0.0.2") });
        Assert.IsFalse(mixed.Acknowledged); Assert.IsFalse(mixed.Succeeded); Assert.IsTrue(mixed.Pending);
        Assert.IsTrue(mixed.ResultWarning);
        StringAssert.Contains("台" + station + " / 10.0.0.1 / 指示 #8: iPhone", mixed.Result);
        StringAssert.Contains("覚えました", mixed.Result); StringAssert.Contains("覚えられませんでした", mixed.Result);
        StringAssert.Contains("10.0.0.3 / 指示 #8: iPhoneの返事を待っています", mixed.Result);
        var timeout = ResultItem(); action.Apply(timeout, 21, Array.Empty<PhoneSaberAcknowledgement>());
        Assert.IsFalse(timeout.Pending); Assert.IsTrue(timeout.Enabled); Assert.IsFalse(timeout.Acknowledged);
        StringAssert.Contains("未確認・応答なし", timeout.Result);
        var terminal = ResultItem(); action.Apply(terminal, 22, new[] { Ack(8, "learned", "samples60", "none", "10.0.0.3") });
        Assert.IsTrue(terminal.Acknowledged); Assert.IsFalse(terminal.Succeeded); Assert.IsTrue(terminal.ResultWarning);
        // 送信元未確認でも、他台・Androidの返事を成功に数えない。
        var unknown = new PhoneSaberOperatorKeys.PhoneAction(9, 0, "", station, "learn-area=10", Array.Empty<string>());
        var empty = ResultItem(); unknown.Apply(empty, 1, new[] { Ack(9, "learned", "samples60", other) });
        Assert.IsFalse(empty.Acknowledged);
        var matched = ResultItem(); unknown.Apply(matched, 2, new[] { Ack(9, "learned", "samples60", "none") });
        Assert.IsTrue(matched.Succeeded); StringAssert.Contains("台" + station, matched.Result);
        var second = ResultItem(); unknown.Apply(second, 3, new[] { Ack(9, "learning", "10s", station, "10.0.0.2") });
        Assert.IsFalse(second.Acknowledged); Assert.IsFalse(second.Succeeded); Assert.IsTrue(second.Pending);
        var secondFailed = ResultItem(); unknown.Apply(secondFailed, 4, new[] { Ack(9, "failed", "samples0", station, "10.0.0.2") });
        Assert.IsTrue(secondFailed.Acknowledged); Assert.IsFalse(secondFailed.Succeeded); Assert.IsTrue(secondFailed.ResultWarning);
    }

    [TestCase("A")]
    [TestCase("B")]
    public void RepeatingOperationReplacesOldResultAndOnlyChosenValueHasResult(string station)
    {
        var old = new PhoneSaberOperatorKeys.PhoneAction(1, 0, "red", station, "ignore=red", new[] { "10.0.0.1" });
        var failed = ResultItem("phone-ignore", "red"); old.Apply(failed, 4, new[] { Ack(1, "nothing", "red0-blue0", station) });
        Assert.IsTrue(failed.ResultWarning);
        var retry = new PhoneSaberOperatorKeys.PhoneAction(2, 5, "blue", station, "ignore=blue", new[] { "10.0.0.1" });
        var waiting = ResultItem("phone-ignore", "blue"); retry.Apply(waiting, 6, new[] { Ack(1, "nothing", "red0-blue0", station) });
        Assert.IsTrue(waiting.Pending); Assert.IsFalse(waiting.ResultWarning);
        StringAssert.DoesNotContain("対象なし", waiting.Result); Assert.AreEqual(2, waiting.CommandId);
        var success = ResultItem("phone-ignore", "blue"); retry.Apply(success, 9, new[] { Ack(2, "ignored", "red0-blue1", station) });
        Assert.IsTrue(success.Succeeded); Assert.IsFalse(success.ResultWarning);
        StringAssert.Contains("青を無視しました", success.Result);
        var sibling = ResultItem("phone-ignore", "red"); retry.Apply(sibling, 9, Array.Empty<PhoneSaberAcknowledgement>());
        Assert.IsNull(sibling.Result); Assert.IsTrue(sibling.Enabled); Assert.IsFalse(sibling.Succeeded);
    }

    [Test]
    public void OperationResultJsonAndWebBindingUseResultAndWarning()
    {
        var state = new PhoneSaberControlPanelState(); state.BeginSection("iPhone"); state.Button("学習", "phone-learn-area");
        var action = new PhoneSaberOperatorKeys.PhoneAction(1, 0, "", "A", "learn-area=10", new[] { "sender\"\n<test>" });
        var item = state.Sections[0].Items[0];
        action.Apply(item, 1, new[] { Ack(1, "failed", "samples0", sender: "sender\"\n<test>") });
        StringAssert.Contains("\"result\":" + PhoneSaberControlPanelState.Quote(item.Result), state.ToJson());
        StringAssert.Contains("\"resultWarning\":true", state.ToJson());
        StringAssert.Contains("el.resultNode.textContent=i.result", PhoneSaberControlPanelPage.Html);
        StringAssert.Contains("i.resultWarning", PhoneSaberControlPanelPage.Html);
        StringAssert.Contains("message.textContent=item.result", PhoneSaberControlPanelPage.Html);
        StringAssert.DoesNotContain("iPhoneの返事を反映しました", PhoneSaberControlPanelPage.Html);
    }

    // ソケットはこの1件のみ。固定ポートを使用せず、終了時に停止・破棄する。
    [Test]
    public void LoopbackSocketServesStateQueuesActionsAndStops()
    {
        using (var server = new PhoneSaberControlPanelServer())
        {
            Assert.IsTrue(server.Start(0, 0), server.Error);
            Assert.That(server.Port, Is.GreaterThan(0));
            Assert.That(server.Token, Does.Match("^[0-9a-f]{32}$"));
            server.Publish("{\"test\":true}");
            string response = Exchange(server, "GET", "/state?t=" + server.Token);
            StringAssert.StartsWith("HTTP/1.1 200", response);
            StringAssert.EndsWith("{\"test\":true}", response);
            StringAssert.Contains("Connection: close", response);
            StringAssert.Contains("PhoneSaber 操作画面", Exchange(server, "GET", "/?t=" + server.Token));
            StringAssert.StartsWith("HTTP/1.1 400", Exchange(server, "POST", "/action?t=" + server.Token + "&name=unknown"));
            StringAssert.StartsWith("HTTP/1.1 403", Exchange(server, "GET", "/state"));
            StringAssert.StartsWith("HTTP/1.1 202", Exchange(server, "POST", "/action?t=" + server.Token + "&name=phone-clip"));
            Assert.IsTrue(server.TryDequeue(out var action));
            Assert.AreEqual("phone-clip", action.Name);
            Assert.IsFalse(server.TryDequeue(out _));
            for (int i = 0; i < PhoneSaberControlPanelServer.QueueCapacity; i++)
                StringAssert.StartsWith("HTTP/1.1 202", Exchange(server, "POST", "/action?t=" + server.Token + "&name=phone-clip"));
            StringAssert.StartsWith("HTTP/1.1 503", Exchange(server, "POST", "/action?t=" + server.Token + "&name=phone-clip"));
            server.Dispose();
            Assert.IsFalse(server.Running);
        }
    }

    static string Exchange(PhoneSaberControlPanelServer server, string method, string target)
    {
        using (var client = new TcpClient())
        {
            client.ReceiveTimeout = 3000;
            client.Connect(IPAddress.Loopback, server.Port);
            using (var stream = client.GetStream())
            {
                byte[] bytes = Request(method, target, "Host: 127.0.0.1:" + server.Port + "\r\n");
                stream.Write(bytes, 0, bytes.Length);
                using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
    }
}
