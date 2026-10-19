using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

// Unity API を使わない loopback 専用 HTTP。状態の作成と操作の実行は所有者の main thread に任せる。
public sealed class PhoneSaberControlPanelServer : IDisposable
{
    public const int MaximumRequestBytes = 8192;
    public const int MaximumTargetBytes = 2048;
    public const int QueueCapacity = 32;
    readonly object gate = new object();
    readonly Queue<PhoneSaberControlPanelRequest> actions = new Queue<PhoneSaberControlPanelRequest>();
    TcpListener listener;
    TcpClient activeClient;
    Thread worker;
    volatile bool running;
    string state = "{\"sections\":[]}";
    public int Port { get; private set; }
    public string Token { get; }
    public bool Running => running;
    public string Url => "http://127.0.0.1:" + Port + "/?t=" + Token;
    public string Error { get; private set; }

    public PhoneSaberControlPanelServer()
    {
        var bytes = new byte[16];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        Token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    public bool Start(int firstPort = 5010, int lastPort = 5019)
    {
        if (running) return true;
        for (int port = firstPort; port <= lastPort; port++)
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start(8);
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Error = null;
                running = true;
                worker = new Thread(Serve) { IsBackground = true, Name = "PhoneSaber control panel" };
                worker.Start();
                return true;
            }
            catch (Exception e)
            {
                Error = e.GetType().Name;
                running = false;
                listener?.Stop();
            }
        }
        return false;
    }

    long lastStateRequestTicks;
    // ブラウザが2秒以内に状態を取りに来たか。閉じている間はゲーム側で状態を作らない。
    public bool RecentlyPolled => Stopwatch.GetTimestamp() - Interlocked.Read(ref lastStateRequestTicks) < Stopwatch.Frequency * 2;

    public void Publish(string json) { lock (gate) state = json; }
    public bool TryDequeue(out PhoneSaberControlPanelRequest action)
    {
        lock (gate)
        {
            action = actions.Count > 0 ? actions.Dequeue() : null;
            return action != null;
        }
    }

    void Serve()
    {
        try
        {
            while (running)
            {
                using (var client = listener.AcceptTcpClient())
                {
                    lock (gate)
                    {
                        if (!running) break;
                        activeClient = client;
                    }
                    try { Handle(client); }
                    catch (IOException) { }
                    catch (SocketException) { }
                    finally { lock (gate) activeClient = null; }
                }
            }
        }
        catch (Exception e) { Error = e.GetType().Name; }
        finally { running = false; listener.Stop(); }
    }

    void Handle(TcpClient client)
    {
        client.ReceiveTimeout = 1000;
        client.SendTimeout = 1000;
        using (var stream = client.GetStream())
        {
            var bytes = new List<byte>();
            var timer = Stopwatch.StartNew();
            // 短い総期限とサイズ上限。body / chunked / keep-alive は受け付けない。
            while (bytes.Count <= MaximumRequestBytes && timer.ElapsedMilliseconds < 1500)
            {
                int next = stream.ReadByte();
                if (next < 0) return;
                bytes.Add((byte)next);
                int n = bytes.Count;
                if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
            }
            int status = ParseRequest(bytes.ToArray(), Port, Token, out var request);
            string body = "要求を受け付けられません。";
            string type = "text/plain; charset=utf-8";
            if (status == 200)
            {
                if (request.Path == "/") { body = PhoneSaberControlPanelPage.Html; type = "text/html; charset=utf-8"; }
                else if (request.Path == "/state") { Interlocked.Exchange(ref lastStateRequestTicks, Stopwatch.GetTimestamp()); lock (gate) body = state; type = "application/json; charset=utf-8"; }
                else
                {
                    lock (gate)
                    {
                        if (actions.Count >= QueueCapacity) status = 503;
                        else { actions.Enqueue(request); status = 202; }
                    }
                    body = status == 202 ? "操作を受け付けました。" : "操作が混み合っています。";
                }
            }
            byte[] payload = Encoding.UTF8.GetBytes(body);
            string reason = status == 200 ? "OK" : status == 202 ? "Accepted" : status == 403 ? "Forbidden" :
                status == 404 ? "Not Found" : status == 405 ? "Method Not Allowed" : status == 413 ? "Payload Too Large" :
                status == 503 ? "Service Unavailable" : "Bad Request";
            byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: " + type +
                "\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\nCache-Control: no-store\r\n" +
                "Referrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\n" +
                "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; form-action 'none'\r\n\r\n");
            stream.Write(headers, 0, headers.Length);
            stream.Write(payload, 0, payload.Length);
        }
    }

    // 完全なヘッダだけを解析する純粋関数。失敗時は HTTP status を返す。
    public static int ParseRequest(byte[] bytes, int port, string token, out PhoneSaberControlPanelRequest request)
    {
        request = null;
        if (bytes == null) return 400;
        if (bytes.Length > MaximumRequestBytes) return 413;
        foreach (byte b in bytes) if (b > 126 || (b < 32 && b != 13 && b != 10 && b != 9)) return 400;
        string raw = Encoding.ASCII.GetString(bytes);
        if (!raw.EndsWith("\r\n\r\n", StringComparison.Ordinal) ||
            raw.Replace("\r\n", "").IndexOfAny(new[] { '\r', '\n' }) >= 0) return 400;
        string[] lines = raw.Substring(0, raw.Length - 4).Split(new[] { "\r\n" }, StringSplitOptions.None);
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || first[2] != "HTTP/1.1") return 400;
        if (first[1].Length > MaximumTargetBytes) return 413;
        foreach (char c in first[1]) if (c <= 32 || c == '#') return 400;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) return 400;
            string key = lines[i].Substring(0, colon);
            foreach (char c in key) if (!(char.IsLetterOrDigit(c) || c == '-')) return 400;
            if (headers.ContainsKey(key)) return 400;
            string value = lines[i].Substring(colon + 1).Trim(' ', '\t');
            foreach (char c in value) if (char.IsControl(c)) return 400;
            headers.Add(key, value);
        }
        if (!headers.TryGetValue("Host", out string host) ||
            !(host.Equals("127.0.0.1:" + port, StringComparison.OrdinalIgnoreCase) ||
              host.Equals("localhost:" + port, StringComparison.OrdinalIgnoreCase))) return 403;
        if (headers.ContainsKey("Transfer-Encoding") ||
            (headers.TryGetValue("Content-Length", out string length) && length != "0")) return 400;
        string target = first[1];
        int question = target.IndexOf('?');
        string path = question < 0 ? target : target.Substring(0, question);
        if (!path.StartsWith("/", StringComparison.Ordinal) || path.Contains("#")) return 400;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (question >= 0)
        {
            foreach (string part in target.Substring(question + 1).Split('&'))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0) return 400;
                if (!Decode(part.Substring(0, equals), out string key) || !Decode(part.Substring(equals + 1), out string value) || query.ContainsKey(key)) return 400;
                query.Add(key, value);
            }
        }
        if (string.IsNullOrEmpty(token) || !query.TryGetValue("t", out string supplied) || supplied != token) return 403;
        if (path != "/" && path != "/state" && path != "/action") return 404;
        if (first[0] != (path == "/action" ? "POST" : "GET")) return 405;
        query.TryGetValue("name", out string name);
        query.TryGetValue("value", out string actionValue);
        actionValue = actionValue ?? "";
        if (path == "/action" && !PhoneSaberControlPanelActions.IsValid(name, actionValue)) return 400;
        request = new PhoneSaberControlPanelRequest(first[0], path, query, name, actionValue);
        return 200;
    }

    static bool Decode(string text, out string decoded)
    {
        decoded = null;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '%' && (i + 2 >= text.Length || !Uri.IsHexDigit(text[++i]) || !Uri.IsHexDigit(text[++i]))) return false;
        try { decoded = Uri.UnescapeDataString(text.Replace("+", " ")); return true; }
        catch (UriFormatException) { return false; }
    }

    public void Dispose()
    {
        running = false;
        listener?.Stop();
        lock (gate) { activeClient?.Close(); actions.Clear(); }
        if (worker != null && worker != Thread.CurrentThread && worker.IsAlive) worker.Join(2500);
    }
}

public sealed class PhoneSaberControlPanelRequest
{
    public string Method { get; }
    public string Path { get; }
    public IReadOnlyDictionary<string, string> Query { get; }
    public string Name { get; }
    public string Value { get; }
    public PhoneSaberControlPanelRequest(string method, string path, Dictionary<string, string> query, string name, string value)
    { Method = method; Path = path; Query = query; Name = name; Value = value; }
}
