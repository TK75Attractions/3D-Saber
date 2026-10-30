using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;

public sealed class PhoneSaberClipRelayTests
{
    // 偽物も背景の単一接続。Pythonと同じContent-Length付きのclose応答を使う。
    sealed class FakeMac : IDisposable
    {
        readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        readonly Thread worker;
        public readonly ManualResetEvent Health = new ManualResetEvent(false), FirstChunk = new ManualResetEvent(false);
        public readonly ConcurrentQueue<Dictionary<string, string>> Headers = new ConcurrentQueue<Dictionary<string, string>>();
        public byte[] Received;
        public int Status = 201;
        public string Body = "{\"accepted\":true,\"bundle\":\"clip_test\"}";
        public bool Truncate, EarlyReply;
        public volatile bool HealthReady = true;
        volatile bool stopping;
        TcpClient active;
        public string Target => "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        public FakeMac()
        {
            listener.Start();
            worker = new Thread(Run) { IsBackground = true };
            worker.Start();
        }
        void Run()
        {
            try
            {
                while (!stopping)
                {
                    using (var client = listener.AcceptTcpClient())
                    {
                        active = client;
                        client.ReceiveTimeout = client.SendTimeout = 3000;
                        var stream = client.GetStream();
                        string header = ReadHeader(stream);
                        if (header.StartsWith("GET /health ", StringComparison.Ordinal))
                        {
                            Send(stream, HealthReady ? 200 : 503, HealthReady ? "{\"status\":\"ready\",\"service\":\"phonesaber-triage\"}" : "{}", false);
                            Health.Set();
                            continue;
                        }
                        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        string[] rows = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                        for (int i = 1; i < rows.Length; i++)
                        {
                            int colon = rows[i].IndexOf(':');
                            if (colon > 0) fields.Add(rows[i].Substring(0, colon), rows[i].Substring(colon + 1).Trim());
                        }
                        Headers.Enqueue(fields);
                        if (!EarlyReply)
                        {
                            Received = new byte[int.Parse(fields["Content-Length"])];
                            int at = 0;
                            while (at < Received.Length)
                            {
                                int count = stream.Read(Received, at, Received.Length - at);
                                if (count == 0) break;
                                at += count; FirstChunk.Set();
                            }
                        }
                        Send(stream, Status, Body, Truncate);
                    }
                }
            }
            catch (Exception) { if (!stopping) FirstChunk.Set(); }
        }
        public void Dispose()
        {
            stopping = true; listener.Stop(); active?.Close();
            Assert.IsTrue(worker.Join(4000), "偽Macも停止する");
            Health.Dispose(); FirstChunk.Dispose();
        }
    }

    static string ReadHeader(Stream stream)
    {
        var text = new StringBuilder();
        while (text.Length < 16384)
        {
            int next = stream.ReadByte();
            if (next < 0) throw new IOException("header EOF");
            text.Append((char)next);
            if (text.Length >= 4 && text.ToString(text.Length - 4, 4) == "\r\n\r\n") return text.ToString();
        }
        throw new IOException("header limit");
    }

    static void Send(Stream stream, int status, string body, bool truncate)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        Write(stream, "HTTP/1.1 " + status + " Test\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " +
            (bytes.Length + (truncate ? 10 : 0)) + "\r\nConnection: close\r\n\r\n");
        stream.Write(bytes, 0, bytes.Length);
    }
    static void Write(Stream stream, string text)
    { byte[] bytes = Encoding.ASCII.GetBytes(text); stream.Write(bytes, 0, bytes.Length); }
    static TcpClient Phone(int port)
    {
        var phone = new TcpClient(); phone.ReceiveTimeout = phone.SendTimeout = 4000;
        phone.Connect(IPAddress.Loopback, port); return phone;
    }
    static string Response(Stream stream)
    {
        using (var body = new MemoryStream()) { stream.CopyTo(body); return Encoding.UTF8.GetString(body.ToArray()); }
    }
    static string Request(int port, string request, byte[] body = null)
    {
        using (var phone = Phone(port))
        {
            var stream = phone.GetStream(); Write(stream, request);
            if (body != null) stream.Write(body, 0, body.Length);
            return Response(stream);
        }
    }
    static string PostHeader(int length) => "POST /v1/bundle HTTP/1.1\r\nHost: phone\r\nContent-Type: application/vnd.phonesaber.triage-v1\r\nContent-Length: " + length +
        "\r\nX-PhoneSaber-Session-ID: clip_test\r\nX-PhoneSaber-Transfer-ID: transfer_test\r\nX-PhoneSaber-Attempt: 3\r\nX-PhoneSaber-Future: keep\r\n\r\n";

    static PhoneSaberClipRelay Start(string target = null, int port = 0, Action<string> diagnostic = null, double healthSeconds = 60)
    {
        var relay = new PhoneSaberClipRelay(new PhoneSaberClipRelay.Settings
        {
            ListenAddress = IPAddress.Loopback, Port = port, Target = target, Station = "B",
            Peers = () => Array.Empty<IPAddress>(), Diagnostic = diagnostic, HealthSeconds = healthSeconds,
            ConnectTimeoutMilliseconds = 250, IoTimeoutMilliseconds = 500
        });
        Assert.IsTrue(SpinWait.SpinUntil(() => relay.LocalPort != 0 || !relay.IsRunning, 3000));
        return relay;
    }
    static void Stop(PhoneSaberClipRelay relay)
    {
        relay.Dispose(); Assert.IsTrue(SpinWait.SpinUntil(() => !relay.IsRunning, 2000), "中継threadが終了する");
    }

    [TestCase(201, "{\"accepted\":true,\"bundle\":\"clip_test\"}")]
    [TestCase(200, "{\"status\":\"duplicate\",\"sessionID\":\"clip_test\"}")]
    [TestCase(507, "{\"error\":\"insufficient storage on the receiver\"}")]
    public void StreamsBodyAndHeadersAndReturnsExactMacReceiptWithoutFiles(int status, string receipt)
    {
        // CWDとtempを空の監視先にして確認。イベントログは既存のキューだけでflushしない。
        string folder = Path.Combine(Path.GetTempPath(), "PhoneSaberClipRelay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string cwd = Environment.CurrentDirectory, temp = Environment.GetEnvironmentVariable("TMPDIR");
        try
        {
            Environment.CurrentDirectory = folder; Environment.SetEnvironmentVariable("TMPDIR", folder);
            using (var mac = new FakeMac { Status = status, Body = receipt })
            {
                var relay = Start(mac.Target);
                try
                {
                    Assert.IsTrue(mac.Health.WaitOne(3000));
                    StringAssert.Contains("\"service\":\"phonesaber-triage\"", Request(relay.LocalPort, "GET /health HTTP/1.1\r\nHost: phone\r\n\r\n"));
                    byte[] payload = new byte[2 * PhoneSaberClipRelay.ChunkBytes + 53];
                    new Random(1).NextBytes(payload);
                    using (var phone = Phone(relay.LocalPort))
                    {
                        var stream = phone.GetStream(); Write(stream, PostHeader(payload.Length));
                        stream.Write(payload, 0, PhoneSaberClipRelay.ChunkBytes);
                        Assert.IsTrue(mac.FirstChunk.WaitOne(3000), "全体を受け終わる前にMacへ流れている");
                        stream.Write(payload, PhoneSaberClipRelay.ChunkBytes, payload.Length - PhoneSaberClipRelay.ChunkBytes);
                        string response = Response(stream);
                        StringAssert.StartsWith("HTTP/1.1 " + status + " Test\r\n", response);
                        Assert.AreEqual(receipt, response.Substring(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4));
                    }
                    CollectionAssert.AreEqual(payload, mac.Received);
                    Assert.IsTrue(mac.Headers.TryDequeue(out var headers));
                    Assert.AreEqual("application/vnd.phonesaber.triage-v1", headers["Content-Type"]);
                    Assert.AreEqual(payload.Length.ToString(), headers["Content-Length"]);
                    Assert.AreEqual("clip_test", headers["X-PhoneSaber-Session-ID"]);
                    Assert.AreEqual("transfer_test", headers["X-PhoneSaber-Transfer-ID"]);
                    Assert.AreEqual("3", headers["X-PhoneSaber-Attempt"]);
                    Assert.AreEqual("keep", headers["X-PhoneSaber-Future"]);
                    Assert.IsEmpty(Directory.GetFileSystemEntries(folder), "クリップや一時ファイルを保存しない");
                }
                finally { Stop(relay); }
            }
        }
        finally { Environment.CurrentDirectory = cwd; Environment.SetEnvironmentVariable("TMPDIR", temp); Directory.Delete(folder, true); }
    }

    [Test]
    public void NoTargetReturnsRetryForHealthAndPost()
    {
        var relay = Start();
        try
        {
            StringAssert.StartsWith("HTTP/1.1 503 ", Request(relay.LocalPort, "GET /health HTTP/1.1\r\nHost: phone\r\n\r\n"));
            StringAssert.StartsWith("HTTP/1.1 503 ", Request(relay.LocalPort, PostHeader(8)));
        }
        finally { Stop(relay); }
    }

    [Test]
    public void Port8765InUseSkipsOnceWithoutAlternativePort()
    {
        var busy = new TcpListener(IPAddress.Any, 8765);
        bool owns = false;
        var lines = new ConcurrentQueue<string>();
        try
        {
            try { busy.Server.ExclusiveAddressUse = true; busy.Start(); owns = true; }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse) { /* 稼働中Pythonも占有の証拠。 */ }
            var relay = Start(port: 8765, diagnostic: lines.Enqueue);
            try
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => !relay.IsRunning, 3000));
                Assert.AreEqual(0, relay.LocalPort);
                Assert.AreEqual(1, lines.Count);
                StringAssert.Contains("listen skipped port=8765", lines.ToArray()[0]);
            }
            finally { Stop(relay); }
        }
        finally { if (owns) busy.Stop(); }
    }

    [Test]
    public void MacEarlyRejectionIsForwardedBeforePhoneFinishesBody()
    {
        using (var mac = new FakeMac { EarlyReply = true, Status = 507, Body = "{\"error\":\"storage\"}" })
        {
            var relay = Start(mac.Target);
            try
            {
                Assert.IsTrue(mac.Health.WaitOne(3000));
                string response = Request(relay.LocalPort, PostHeader(1000000), new byte[PhoneSaberClipRelay.ChunkBytes]);
                StringAssert.StartsWith("HTTP/1.1 507 Test\r\n", response);
                StringAssert.EndsWith(mac.Body, response);
            }
            finally { Stop(relay); }
        }
    }

    [Test]
    public void TruncatedMacSuccessBecomes503()
    {
        using (var mac = new FakeMac { Truncate = true })
        {
            var relay = Start(mac.Target);
            try
            {
                Assert.IsTrue(mac.Health.WaitOne(3000));
                StringAssert.StartsWith("HTTP/1.1 503 ", Request(relay.LocalPort, PostHeader(8), new byte[8]));
            }
            finally { Stop(relay); }
        }
    }

    [Test]
    public void UnreachableTargetIsNotAdvertisedAsReady()
    {
        var unused = new TcpListener(IPAddress.Loopback, 0); unused.Start();
        string target = "127.0.0.1:" + ((IPEndPoint)unused.LocalEndpoint).Port; unused.Stop();
        var relay = Start(target);
        try { StringAssert.StartsWith("HTTP/1.1 503 ", Request(relay.LocalPort, "GET /health HTTP/1.1\r\nHost: phone\r\n\r\n")); }
        finally { Stop(relay); }
    }

    [Test]
    public void IncomingRelayHopCannotLoop()
    {
        var relay = Start();
        try { StringAssert.Contains("relay loop", Request(relay.LocalPort, "GET /health HTTP/1.1\r\nHost: phone\r\nX-PhoneSaber-Relay: 1\r\n\r\n")); }
        finally { Stop(relay); }
    }

    [Test]
    public void DisposeInterruptsBlockedUploadWithoutWaitingOnCaller()
    {
        using (var mac = new FakeMac())
        {
            var relay = Start(mac.Target);
            try
            {
                Assert.IsTrue(mac.Health.WaitOne(3000));
                using (var phone = Phone(relay.LocalPort))
                {
                    Write(phone.GetStream(), PostHeader(1000000));
                    phone.GetStream().Write(new byte[PhoneSaberClipRelay.ChunkBytes], 0, PhoneSaberClipRelay.ChunkBytes);
                    Assert.IsTrue(mac.FirstChunk.WaitOne(3000));
                    var timer = Stopwatch.StartNew(); relay.Dispose();
                    Assert.Less(timer.ElapsedMilliseconds, 250, "main threadにJoin待ちを置かない");
                    Assert.IsTrue(SpinWait.SpinUntil(() => !relay.IsRunning, 2000));
                }
            }
            finally { Stop(relay); }
        }
    }

    [TestCase("192.168.1.2", true, 8765)]
    [TestCase("172.20.10.1:9000", true, 9000)]
    [TestCase("host:8765", false, 0)]
    [TestCase("127.0.0.1:0", false, 0)]
    [TestCase("127.0.0.1:65536", false, 0)]
    [TestCase("http://192.168.1.2", false, 0)]
    public void ParsesExplicitIpOverride(string input, bool valid, int port)
    {
        Assert.AreEqual(valid, PhoneSaberClipRelay.TryParseTarget(input, out var target));
        if (valid) Assert.AreEqual(port, target.Port);
    }

    [Test]
    public void AutomaticStartHonorsDisableAndTestFixtureSwitch()
    {
        bool saved = PhoneSaberClipRelay.AutoStartEnabled;
        string value = Environment.GetEnvironmentVariable("PHONESABER_CLIP_RELAY");
        try
        {
            PhoneSaberClipRelay.AutoStartEnabled = false;
            Assert.IsNull(PhoneSaberClipRelay.StartForInput("B"));
            PhoneSaberClipRelay.AutoStartEnabled = true;
            Environment.SetEnvironmentVariable("PHONESABER_CLIP_RELAY", "0");
            Assert.IsNull(PhoneSaberClipRelay.StartForInput("B"));
        }
        finally { PhoneSaberClipRelay.AutoStartEnabled = saved; Environment.SetEnvironmentVariable("PHONESABER_CLIP_RELAY", value); }
    }

    [Test]
    public void Utf8ReceiptIsValidatedCompletelyBeforeReturningTheOriginalStatusAndBytes()
    {
        const string body = "{\"accepted\":true,\"bundle\":\"日本語のクリップ\"}";
        using (var source = new MemoryStream())
        using (var forwarded = new MemoryStream())
        {
            Send(source, 201, body, false); source.Position = 0;
            var message = typeof(PhoneSaberClipRelay).GetMethod("ReadResponse", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { source });
            typeof(PhoneSaberClipRelay).GetMethod("SendResponse", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new[] { forwarded, message });
            CollectionAssert.AreEqual(source.ToArray(), forwarded.ToArray());
        }
    }

    [TestCase("HTTP/1.1 201 Created\r\nContent-Length: 50\r\n\r\n{}")]
    [TestCase("HTTP/1.1 200 OK\r\nContent-Length: 65537\r\n\r\n")]
    [TestCase("HTTP/1.1 201 Created\r\nContent-Length: -1\r\n\r\n")]
    [TestCase("HTTP/1.1 201 Created\r\nContent-Length: 2\r\nContent-Length: 2\r\n\r\n{}")]
    [TestCase("HTTP/1.1 201 Created\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n")]
    [TestCase("HTTP/1.1 201 Created\r\n\r\n{}")]
    public void IncompleteOrUnboundedMacReceiptCannotBeAcknowledgedAsSuccess(string wire)
    {
        using (var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire)))
        {
            var exception = Assert.Throws<TargetInvocationException>(() =>
                typeof(PhoneSaberClipRelay).GetMethod("ReadResponse", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { stream }));
            Assert.IsInstanceOf<IOException>(exception.InnerException);
        }
    }
}
