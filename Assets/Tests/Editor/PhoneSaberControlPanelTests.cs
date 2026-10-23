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
            { "display-sync", new[] { "0", "1" } }, { "frame-cap", new[] { "0", "1", "2" } },
            { "judge-mode", new[] { "0", "1" } }, { "judge-adjust", new[] { "-10", "10" } }, { "judge-centre", new[] { "" } },
            { "filter", new[] { "0", "1", "2", "3", "4" } }, { "stabilizer", new[] { "0", "1" } },
            { "phone-ignore", new[] { "red", "blue", "both" } }, { "phone-clear-ignore", new[] { "" } },
            { "phone-learn-area", new[] { "" } }, { "phone-clear-area", new[] { "" } },
            { "phone-shutter-iso", new[] { "2000", "250", "200", "160", "125", "100", "80", "64" } },
            { "phone-shutter-emitter", new[] { "0", "1" } },
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
            Assert.AreEqual("範囲を覚える（10秒、いっぱいに振る）", button.Text);
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
        Assert.AreEqual("OFF", phone.Value("colouredEmitter"));
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
