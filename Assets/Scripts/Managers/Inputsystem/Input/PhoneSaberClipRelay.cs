using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// 診断クリップ専用。ゲーム状態・座標 socket・ファイルには触れず、固定長バッファで直列中継する。
public sealed class PhoneSaberClipRelay : IDisposable
{
    public const int DefaultPort = 8765;
    public const int ChunkBytes = 16 * 1024;
    public const int BytesPerSecond = 2 * 1024 * 1024;
    public const long MaximumBundleBytes = 512L * 1024 * 1024;
    const int HeaderLimit = 16 * 1024, ReplyLimit = 64 * 1024;
    const string HopHeader = "X-PhoneSaber-Relay";
    public static volatile bool AutoStartEnabled = true;

    public sealed class Settings
    {
        public IPAddress ListenAddress = IPAddress.Any;
        public int Port = DefaultPort;
        public string Station = "", Target;
        public int ConnectTimeoutMilliseconds = 1000, IoTimeoutMilliseconds = 5000;
        public int StartupDelayMilliseconds;
        public bool FollowAutoStartEnabled;
        public double HealthSeconds = 5, HealthFreshSeconds = 15;
        public Func<IPAddress[]> Peers = () => PhoneSaberPeerLog.ClipRelayTargets;
        public Action<string> Diagnostic = message => UnityEngine.Debug.Log(message);
    }

    sealed class Message
    {
        public string First;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public long Length;
        public int Status;
        public byte[] Body;
    }

    readonly Settings settings;
    readonly Thread worker;
    readonly object gate = new object();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly HashSet<IPAddress> localAddresses = new HashSet<IPAddress>();
    volatile bool stopping;
    int localPort;
    TcpListener listener;
    TcpClient incoming, outgoing;
    IPEndPoint healthyTarget;
    Message healthyResponse;
    double healthAt = double.NegativeInfinity, nextHealth;
    int healthCursor;
    public int LocalPort => Volatile.Read(ref localPort);
    public bool IsRunning => worker.IsAlive;

    public static PhoneSaberClipRelay StartForInput(string station)
    {
        // 中継が要るのは受信機の無い Windows の台だけ。Mac は Python 受信機が 8765 を使うので、
        // 受信機の再起動の隙に 8765 を奪わないよう、Mac では起動しない(2026-10-30)。
#if !(UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN)
        return null;
#else
        if (!AutoStartEnabled || Environment.GetEnvironmentVariable("PHONESABER_CLIP_RELAY") == "0") return null;
        return new PhoneSaberClipRelay(new Settings
        {
            Station = station, Target = Environment.GetEnvironmentVariable("PHONESABER_CLIP_RELAY_TARGET"),
            FollowAutoStartEnabled = true,
        });
#endif
    }

    // bind・探索・接続もすべて背景で行う。使用中なら代替portへ移らず終了する。
    public PhoneSaberClipRelay(Settings settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        worker = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "PhoneSaber clip relay" };
        worker.Start();
    }

    void Run()
    {
        try
        {
            double startAt = clock.Elapsed.TotalMilliseconds + settings.StartupDelayMilliseconds;
            while (!stopping && clock.Elapsed.TotalMilliseconds < startAt && (!settings.FollowAutoStartEnabled || AutoStartEnabled)) Thread.Sleep(20);
            if (stopping || settings.FollowAutoStartEnabled && !AutoStartEnabled) return;
            var server = new TcpListener(settings.ListenAddress, settings.Port);
            lock (gate)
            {
                if (stopping) return;
                listener = server;
                server.Server.ExclusiveAddressUse = true;
                server.Start(2);
                Volatile.Write(ref localPort, ((IPEndPoint)server.LocalEndpoint).Port);
            }
            FindLocalAddresses();
            while (!stopping && (!settings.FollowAutoStartEnabled || AutoStartEnabled))
            {
                if (clock.Elapsed.TotalSeconds >= nextHealth) RefreshHealth();
                if (!server.Pending()) { Thread.Sleep(20); continue; }
                var client = server.AcceptTcpClient();
                lock (gate) { if (stopping) { client.Close(); break; } incoming = client; }
                try { Configure(client); Handle(client.GetStream()); }
                catch (Exception) { /* 接続単位の失敗で listener を止めない。 */ }
                finally { lock (gate) { incoming?.Close(); incoming = null; outgoing?.Close(); outgoing = null; } }
            }
        }
        catch (SocketException e)
        {
            if (!stopping) settings.Diagnostic?.Invoke("[PhoneSaber] clip-relay listen skipped port=" + settings.Port + " reason=" + e.SocketErrorCode);
        }
        catch (Exception e)
        {
            if (!stopping) settings.Diagnostic?.Invoke("[PhoneSaber] clip-relay stopped reason=" + e.GetType().Name);
        }
        finally
        {
            lock (gate) { listener?.Stop(); listener = null; incoming?.Close(); outgoing?.Close(); }
            Volatile.Write(ref localPort, 0);
        }
    }

    void Configure(TcpClient client)
    {
        client.ReceiveTimeout = settings.IoTimeoutMilliseconds;
        client.SendTimeout = settings.IoTimeoutMilliseconds;
        client.SendBufferSize = ChunkBytes;
        client.ReceiveBufferSize = ChunkBytes;
    }

    TcpClient Connect(IPEndPoint target)
    {
        var client = new TcpClient(target.AddressFamily);
        lock (gate)
        {
            if (stopping) { client.Close(); throw new IOException("stopped"); }
            outgoing = client;
        }
        try
        {
            Configure(client);
            var pending = client.BeginConnect(target.Address, target.Port, null, null);
            using (var ready = pending.AsyncWaitHandle)
                if (!ready.WaitOne(settings.ConnectTimeoutMilliseconds)) throw new IOException("connect timeout");
            client.EndConnect(pending);
            return client;
        }
        catch { client.Close(); throw; }
    }

    public static bool TryParseTarget(string value, out IPEndPoint endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] parts = value.Trim().Split(':');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] >= 224) return false;
        int port = DefaultPort;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)) return false;
        endpoint = new IPEndPoint(address, port);
        return true;
    }

    void FindLocalAddresses()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var item in nic.GetIPProperties().UnicastAddresses) localAddresses.Add(item.Address);
        }
        catch { /* 明示targetは探索なしでも使える。 */ }
    }

    void RefreshHealth()
    {
        nextHealth = clock.Elapsed.TotalSeconds + settings.HealthSeconds;
        var targets = new List<IPEndPoint>();
        if (!string.IsNullOrWhiteSpace(settings.Target))
        {
            // 明示指定が誤っている場合は、別の台へ勝手に送らない。
            if (TryParseTarget(settings.Target, out var configured)) targets.Add(configured);
        }
        else
        {
            foreach (var address in settings.Peers() ?? Array.Empty<IPAddress>())
            {
                if (address == null || IPAddress.IsLoopback(address) || localAddresses.Contains(address)) continue;
                if (TryParseTarget(address.ToString(), out var target) && !targets.Contains(target)) targets.Add(target);
                if (targets.Count >= PhoneSaberPeerLog.MaximumPeers) break;
            }
        }
        healthyTarget = null;
        healthyResponse = null;
        double deadline = clock.Elapsed.TotalSeconds + 3;
        int start = targets.Count == 0 ? 0 : healthCursor % targets.Count;
        for (int i = 0; i < targets.Count && clock.Elapsed.TotalSeconds < deadline; i++)
        {
            if (stopping) return;
            int index = (start + i) % targets.Count;
            var target = targets[index];
            healthCursor = (index + 1) % targets.Count;
            try
            {
                using (var client = Connect(target))
                {
                    // 探索が不通peerの数だけ長引かないよう、healthは短い期限にする。
                    client.ReceiveTimeout = client.SendTimeout = Math.Min(1000, settings.IoTimeoutMilliseconds);
                    var stream = client.GetStream();
                    WriteAscii(stream, "GET /health HTTP/1.1\r\nHost: " + target + "\r\n" + HopHeader + ": 1\r\nConnection: close\r\n\r\n");
                    var response = ReadResponse(stream);
                    string body = Encoding.UTF8.GetString(response.Body);
                    if (response.Status != 200 || !JsonField(body, "status", "ready") || !JsonField(body, "service", "phonesaber-triage")) continue;
                    healthyTarget = target; healthyResponse = response;
                    healthAt = clock.Elapsed.TotalSeconds; healthCursor = index;
                    break;
                }
            }
            catch { /* 未接続は正常。端末に残して次回の探索を待つ。 */ }
            finally { lock (gate) { outgoing?.Close(); outgoing = null; } }
        }
    }

    static bool JsonField(string body, string key, string value)
        => Regex.IsMatch(body, "\"" + key + "\"\\s*:\\s*\"" + value + "\"");

    void Handle(NetworkStream phone)
    {
        long bytes = 0;
        int status = 503;
        bool post = false, replyStarted = false;
        IPEndPoint target = null;
        try
        {
            var request = ReadHeaders(phone);
            string[] first = request.First.Split(' ');
            if (first.Length != 3) { Reply(phone, 400, "{\"error\":\"invalid request\"}"); return; }
            post = first[0] == "POST" && first[1] == "/v1/bundle";
            if (request.Headers.ContainsKey(HopHeader)) { Reply(phone, 503, "{\"error\":\"relay loop\"}"); return; }
            if (!post && !(first[0] == "GET" && first[1] == "/health")) { Reply(phone, 404, "{\"error\":\"not found\"}"); return; }
            if (healthyTarget == null || clock.Elapsed.TotalSeconds - healthAt > settings.HealthFreshSeconds)
            { Reply(phone, 503, "{\"error\":\"receiver unavailable\"}"); return; }
            target = healthyTarget;
            if (!post)
            {
                // service/statusだけを模倣せず、Pythonのhealth本文も同じ形で返す。
                replyStarted = true;
                SendResponse(phone, healthyResponse);
                return;
            }
            if (!request.Headers.ContainsKey("Content-Length")) { status = 411; Reply(phone, status, "{\"error\":\"Content-Length required\"}"); return; }
            if (request.Length < 8 || request.Length > MaximumBundleBytes) { status = 413; Reply(phone, status, "{\"error\":\"bundle exceeds receiver limits\"}"); return; }
            using (var mac = Connect(target))
            {
                var upstream = mac.GetStream();
                var header = new StringBuilder("POST /v1/bundle HTTP/1.1\r\nHost: " + target + "\r\nConnection: close\r\n" + HopHeader + ": 1\r\n");
                foreach (var field in request.Headers)
                    if (field.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) || field.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        field.Key.StartsWith("X-PhoneSaber-", StringComparison.OrdinalIgnoreCase)) header.Append(field.Key).Append(": ").Append(field.Value).Append("\r\n");
                header.Append("\r\n"); WriteAscii(upstream, header.ToString());
                // 期待応答はheader転送後に返す。bundle全体のbufferingはしない。
                if (request.Headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                    WriteAscii(phone, "HTTP/1.1 100 Continue\r\n\r\n");
                var buffer = new byte[ChunkBytes];
                while (bytes < request.Length && !stopping)
                {
                    // 507 / duplicate 等、Macが本体の途中で返した応答もそのまま返す。
                    if (upstream.DataAvailable) break;
                    int count = phone.Read(buffer, 0, (int)Math.Min(buffer.Length, request.Length - bytes));
                    if (count == 0) throw new IOException("incomplete upload");
                    try { upstream.Write(buffer, 0, count); bytes += count; }
                    catch (IOException) { if (upstream.DataAvailable) break; throw; }
                    // 1 chunkごとに必ず休み、長時間停滞後のburstも避ける。
                    Thread.Sleep((count * 1000 + BytesPerSecond - 1) / BytesPerSecond);
                }
                if (stopping) return;
                var response = ReadResponse(upstream);
                status = response.Status;
                replyStarted = true;
                SendResponse(phone, response);
            }
        }
        catch (Exception)
        {
            status = 503;
            healthyTarget = null; nextHealth = clock.Elapsed.TotalSeconds;
            if (!replyStarted && !stopping) try { Reply(phone, status, "{\"error\":\"relay interrupted; retry later\"}"); } catch { }
        }
        finally
        {
            if (post) PhoneSaberEventLog.Record(settings.Station, "SYSTEM", "clip-relay",
                "result=" + (status >= 200 && status < 300 && replyStarted ? "ok" : "fail") + " bytes=" + bytes.ToString(CultureInfo.InvariantCulture) +
                " status=" + status + " target=" + (target?.ToString() ?? "unknown"));
        }
    }

    static Message ReadHeaders(Stream stream)
    {
        // ReadByteはbodyを先読みしない。headerのみ16 KiBを上限にする。
        var buffer = new byte[HeaderLimit];
        int count = 0;
        while (count < buffer.Length)
        {
            int value = stream.ReadByte();
            if (value < 0) throw new IOException("incomplete header");
            buffer[count++] = (byte)value;
            if (count >= 4 && buffer[count - 4] == 13 && buffer[count - 3] == 10 && buffer[count - 2] == 13 && buffer[count - 1] == 10) break;
        }
        if (count == buffer.Length) throw new IOException("header too large");
        string[] lines = Encoding.ASCII.GetString(buffer, 0, count - 4).Split(new[] { "\r\n" }, StringSplitOptions.None);
        var message = new Message { First = lines[0] };
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) throw new IOException("invalid header");
            string name = lines[i].Substring(0, colon), value = lines[i].Substring(colon + 1).Trim();
            foreach (char c in name) if (!(char.IsLetterOrDigit(c) || c == '-')) throw new IOException("invalid header name");
            foreach (char c in value) if (char.IsControl(c)) throw new IOException("invalid header value");
            if (message.Headers.ContainsKey(name)) throw new IOException("duplicate header");
            message.Headers.Add(name, value);
        }
        if (message.Headers.ContainsKey("Transfer-Encoding")) throw new IOException("chunked not supported");
        if (message.Headers.TryGetValue("Content-Length", out var length) &&
            !long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out message.Length)) throw new IOException("invalid length");
        return message;
    }

    static Message ReadResponse(Stream stream)
    {
        var response = ReadHeaders(stream);
        string[] first = response.First.Split(' ');
        if (first.Length < 2 || !int.TryParse(first[1], out response.Status) || response.Status < 200 || response.Status > 599 ||
            !response.Headers.ContainsKey("Content-Length") || response.Length > ReplyLimit) throw new IOException("invalid reply");
        response.Body = new byte[(int)response.Length];
        int at = 0;
        while (at < response.Body.Length)
        {
            int count = stream.Read(response.Body, at, response.Body.Length - at);
            if (count == 0) throw new IOException("incomplete reply");
            at += count;
        }
        return response;
    }

    static void WriteAscii(Stream stream, string text)
    { byte[] data = Encoding.ASCII.GetBytes(text); stream.Write(data, 0, data.Length); }

    static void SendResponse(Stream stream, Message response)
    {
        WriteAscii(stream, response.First + "\r\nContent-Type: " +
            (response.Headers.TryGetValue("Content-Type", out var type) ? type : "application/json; charset=utf-8") +
            "\r\nContent-Length: " + response.Body.Length + "\r\nConnection: close\r\n\r\n");
        stream.Write(response.Body, 0, response.Body.Length);
    }

    static void Reply(Stream stream, int status, string body)
    {
        SendResponse(stream, new Message { First = "HTTP/1.1 " + status + " " + (status == 200 ? "OK" : "Unavailable"), Body = Encoding.UTF8.GetBytes(body) });
    }

    // 終了時もmain threadではJoinしない。closeでconnect/read/writeを解除し、背景で自走終了させる。
    public void Dispose()
    {
        lock (gate)
        {
            stopping = true;
            listener?.Stop(); incoming?.Close(); outgoing?.Close();
        }
    }
}
