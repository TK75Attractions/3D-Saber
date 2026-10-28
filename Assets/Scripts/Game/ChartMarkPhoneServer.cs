using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

// スマホのブラウザから譜面の印(ChartMarks)を付け、一言を書くための LAN 向けの小さな HTTP(2026-10-28、
// ユーザー「AI指摘さ、スマホでもできるようにはできる？」)。作りは PhoneSaberControlPanelServer と同じ
// (自前の HTTP・合言葉・キュー・止め方。Thread.Abort は使わない)。違いは次の 3 つ。
// 1. 待ち受けは 127.0.0.1 ではなく全部の IPv4(IPAddress.Any)。スマホは同じ Wi-Fi か、PC のホットスポットから来る。
// 2. 受けた操作はキューに入れ、持ち主(ChartMarkPhone)がメインスレッドで実行して返事を作る。受信側は最大 2 秒待つ。
// 3. 接続ごとに受信スレッドを分ける(スマホのブラウザが先回りで開いて何も送らない接続に、ほかの要求を待たせない)。
// Unity の API は使わない(EditMode のテストでそのまま試せる)。
public sealed class ChartMarkPhoneServer : IDisposable
{
    public const int DefaultFirstPort = 5020, DefaultLastPort = 5029;
    public const int MaximumRequestBytes = 8192;
    public const int MaximumTargetBytes = 2048;
    public const int MaximumIdChars = 200;
    public const int MaximumTextChars = 500;
    public const int MaximumConnections = 8;
    public const int QueueCapacity = 32;
    public const int DefaultRequestsPerSecond = 10;
    public const int DefaultReplyWaitMilliseconds = 2000;
    // 接続してから最初の 1 バイトまで(先回りの接続はしばらく黙っている)と、そこから要求を読み終えるまで。
    public const int IdleMilliseconds = 5000, ReadMilliseconds = 1500;
    public const string PagePath = "/m", StatePath = "/api/state", MarkPath = "/api/mark",
        CommentPath = "/api/comment", CancelPath = "/api/cancel";
    // Windows のモバイルホットスポットで PC 側に付く番地(台B)。あれば一番に選ぶ。
    public const string HotspotAddress = "192.168.137.1";
    const string TextType = "text/plain; charset=utf-8", HtmlType = "text/html; charset=utf-8",
        JsonType = "application/json; charset=utf-8";
    static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    sealed class Connection
    {
        public TcpClient Client;
        public Thread Thread;
    }

    readonly object gate = new object();
    readonly Queue<ChartMarkPhoneJob> jobs = new Queue<ChartMarkPhoneJob>();
    readonly List<Connection> connections = new List<Connection>();
    ChartMarkPhoneRateLimiter limiter;
    TcpListener listener;
    Thread worker;
    volatile bool running;

    public int Port { get; private set; }
    public string Token { get; }
    public bool Running => running;
    public string Error { get; private set; }
    // テストで短く・少なくする(本番は 2 秒・1 秒に 10 回)。回数は Start の前に決める。
    public int ReplyWaitMilliseconds { get; set; } = DefaultReplyWaitMilliseconds;
    public int RequestsPerSecond { get; set; } = DefaultRequestsPerSecond;
    public int ActiveConnections { get { lock (gate) return connections.Count; } }
    public int QueuedJobs { get { lock (gate) return jobs.Count; } }
    // 待ち受けの番地。本番は全部の IPv4(Any)。テストはループバックにして、Windows のファイアウォールの問い合わせを出さない。
    public IPAddress ListenAddress { get; private set; } = IPAddress.Any;
    // 受け付ける相手(LAN の番地だけ。テストで差し替える)。それ以外からの接続は、読む前に閉じる。
    internal Func<IPAddress, bool> AllowRemote { get; set; } = IsLanAddress;

    public ChartMarkPhoneServer(string token)
    {
        if (!IsValidToken(token)) throw new ArgumentException("合言葉は 0-9a-f の 32 文字です", nameof(token));
        Token = token;
    }

    public bool Start(int firstPort = DefaultFirstPort, int lastPort = DefaultLastPort, IPAddress address = null)
    {
        if (running) return true;
        ListenAddress = address ?? IPAddress.Any;
        limiter = new ChartMarkPhoneRateLimiter(Math.Max(1, RequestsPerSecond), Stopwatch.Frequency);
        for (int port = firstPort; port <= lastPort; port++)
        {
            try
            {
                listener = new TcpListener(ListenAddress, port);
                listener.Start(16);
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Error = null;
                running = true;
                worker = new Thread(Serve) { IsBackground = true, Name = "ChartMarks phone" };
                worker.Start();
                return true;
            }
            catch (Exception e)
            {
                Error = e.GetType().Name + ": " + e.Message;
                running = false;
                try { listener?.Stop(); } catch (Exception) { }
                listener = null;
            }
        }
        return false;
    }

    // メインスレッド(ChartMarkPhone.Update)が 1 件ずつ取り出して実行し、Complete で返事を渡す。
    public bool TryDequeue(out ChartMarkPhoneJob job)
    {
        lock (gate)
        {
            job = jobs.Count > 0 ? jobs.Dequeue() : null;
            return job != null;
        }
    }

    void Serve()
    {
        int failures = 0;
        try
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException)
                {
                    // 止めたとき(listener.Stop)は抜ける。たまたまの失敗は少し待って続ける。
                    if (!running || ++failures >= 20) throw;
                    Thread.Sleep(50);
                    continue;
                }
                failures = 0;
                if (!FromLan(client) || !TryHandOff(client)) client.Close();
            }
        }
        catch (Exception e) { if (running) Error = e.GetType().Name; }
        finally
        {
            running = false;
            try { listener.Stop(); } catch (Exception) { }
        }
    }

    bool FromLan(TcpClient client)
    {
        try { return AllowRemote((client.Client.RemoteEndPoint as IPEndPoint)?.Address); }
        catch (Exception) { return false; }
    }

    // 受け付ける相手の番地: ループバック(127/8)・10/8・172.16/12・192.168/16・リンクローカル(169.254/16)。
    // VPN(100.64/10 など)や外の番地からは、合言葉が合っていても受けない。
    public static bool IsLanAddress(IPAddress address)
    {
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] == 127 || b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168 ||
               b[0] == 169 && b[1] == 254;
    }

    bool TryHandOff(TcpClient client)
    {
        lock (gate)
        {
            if (!running || connections.Count >= MaximumConnections) return false;
            var connection = new Connection { Client = client };
            connection.Thread = new Thread(() => Run(connection)) { IsBackground = true, Name = "ChartMarks phone request" };
            connections.Add(connection);
            connection.Thread.Start();
            return true;
        }
    }

    void Run(Connection connection)
    {
        // 1 つの要求の失敗で、ゲームもほかの接続も止めない。
        try { Handle(connection.Client); }
        catch (Exception) { }
        finally
        {
            try { connection.Client.Close(); } catch (Exception) { }
            lock (gate) connections.Remove(connection);
        }
    }

    void Handle(TcpClient client)
    {
        client.NoDelay = true;
        client.SendTimeout = 1000;
        var stream = client.GetStream();
        byte[] bytes = ReadRequest(client, stream);
        if (bytes == null) return; // 何も送らない・途中で切れた・時間切れ
        string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
        ChartMarkPhoneRequest request = null;
        int status = limiter.Allow(remote, Stopwatch.GetTimestamp()) ? ParseRequest(bytes, Port, Token, out request) : 429;
        bool api = IsApiRequest(bytes);
        string type = api ? JsonType : TextType, body;
        if (status != 200) body = api ? Result(false, StatusMessage(status)) : StatusMessage(status);
        else if (request.Action == ChartMarkPhoneAction.Page) { body = ChartMarkPhonePage.Html; type = HtmlType; }
        else body = RunOnMainThread(request, out status);
        Write(stream, status, type, body);
        // 送り切ってから閉じる(読み残しがあると RST になり、スマホが返事を受け取れないことがある)。
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
            Drain(client, stream);
        }
        catch (Exception) { }
    }

    string RunOnMainThread(ChartMarkPhoneRequest request, out int status)
    {
        var job = new ChartMarkPhoneJob(request);
        lock (gate)
        {
            if (!running || jobs.Count >= QueueCapacity)
            {
                status = 503;
                return Result(false, running ? "混み合っています。少し待ってください。" : "止めています。");
            }
            jobs.Enqueue(job);
        }
        int wait = Math.Max(1, ReplyWaitMilliseconds);
        if (job.Wait(wait))
        {
            status = job.Status;
            return job.Json;
        }
        // 待ちきれなければ取りやめる(メインスレッドはもう実行しない。スマホは同じ要求をそのまま送り直してよい)。
        if (job.TryAbandon())
        {
            status = 503;
            return Result(false, "ゲームが応答しません(場面の切り替え中など)。もう一度どうぞ。");
        }
        // 実行は始まっている。終わるまでもう少し待ち、それでも終わらなければ「失敗」ではなく「遅れています」と返す
        // (印は付いたかもしれない。押し直すと二重になるので、一覧で確かめてもらう)。
        if (job.Wait(wait))
        {
            status = job.Status;
            return job.Json;
        }
        if (request.Action == ChartMarkPhoneAction.State)
        {
            status = 503;
            return Result(false, "ゲームの返事が遅れています。");
        }
        status = 202;
        return Pending(request.Action == ChartMarkPhoneAction.Mark
            ? "PC の返事が遅れています。印は付いたかもしれないので一覧を見てください。"
            : "PC の返事が遅れています。一覧で確かめてください。");
    }

    // {"ok":false,"pending":true,"message":"..."}(実行が始まってから時間切れになったとき)。
    public static string Pending(string message) =>
        "{\"ok\":false,\"pending\":true,\"message\":" + PhoneSaberControlPanelState.Quote(message ?? "") + "}";

    // 要求を読む。上限を 1 バイトでも超えたらそこで止める(ParseRequest が 413 にする)。
    static byte[] ReadRequest(TcpClient client, NetworkStream stream)
    {
        var buffer = new byte[MaximumRequestBytes + 1];
        int count = 0, need = -1;
        Stopwatch timer = null;
        client.ReceiveTimeout = IdleMilliseconds;
        while (count < buffer.Length && (need < 0 || count < need))
        {
            if (timer != null)
            {
                long left = ReadMilliseconds - timer.ElapsedMilliseconds;
                if (left <= 0) return null;
                client.ReceiveTimeout = (int)left;
            }
            int read = stream.Read(buffer, count, buffer.Length - count);
            if (read <= 0) return null;
            if (timer == null) timer = Stopwatch.StartNew();
            count += read;
            if (need < 0)
            {
                int end = HeaderEnd(buffer, count);
                if (end >= 0) need = end + DeclaredLength(buffer, end);
            }
        }
        var bytes = new byte[count];
        Buffer.BlockCopy(buffer, 0, bytes, 0, count);
        return bytes;
    }

    static void Drain(TcpClient client, NetworkStream stream)
    {
        client.ReceiveTimeout = 200;
        var buffer = new byte[1024];
        var timer = Stopwatch.StartNew();
        for (int total = 0; total < 65536 && timer.ElapsedMilliseconds < 500;)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) return;
            total += read;
        }
    }

    static void Write(NetworkStream stream, int status, string type, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(status))
            .Append("\r\nContent-Type: ").Append(type)
            .Append("\r\nContent-Length: ").Append(payload.Length.ToString(CultureInfo.InvariantCulture))
            .Append("\r\nConnection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\n")
            .Append("Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; ")
            .Append("img-src data:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'\r\n");
        if (status == 429) head.Append("Retry-After: 1\r\n");
        head.Append("\r\n");
        byte[] headers = Encoding.ASCII.GetBytes(head.ToString());
        stream.Write(headers, 0, headers.Length);
        stream.Write(payload, 0, payload.Length);
    }

    static string Reason(int status)
    {
        switch (status)
        {
            case 200: return "OK";
            case 202: return "Accepted";
            case 400: return "Bad Request";
            case 403: return "Forbidden";
            case 404: return "Not Found";
            case 405: return "Method Not Allowed";
            case 413: return "Payload Too Large";
            case 415: return "Unsupported Media Type";
            case 429: return "Too Many Requests";
            case 500: return "Internal Server Error";
            case 503: return "Service Unavailable";
            default: return "Error";
        }
    }

    public static string StatusMessage(int status)
    {
        switch (status)
        {
            case 403: return "合言葉が違います。結果画面の一言の欄の下か、ゲームのログに出るアドレスを開き直してください。";
            case 404: return "そのページはありません。";
            case 405: return "その送り方は使えません。";
            case 413: return "要求が大きすぎます。";
            case 415: return "JSON で送ってください。";
            case 429: return "送る回数が多すぎます(1 秒に " + DefaultRequestsPerSecond + " 回まで)。少し待ってください。";
            case 503: return "ゲームが応答しません。少し待ってからもう一度どうぞ。";
            default: return "要求の形が正しくありません。";
        }
    }

    // {"ok":true,"message":"..."}(印・一言・取り消しの返事)。
    public static string Result(bool ok, string message) =>
        "{\"ok\":" + (ok ? "true" : "false") + ",\"message\":" + PhoneSaberControlPanelState.Quote(message ?? "") + "}";

    // 完全な要求(ヘッダと本文)を解析する純粋関数。失敗時は HTTP の status を返す。
    public static int ParseRequest(byte[] bytes, int port, string token, out ChartMarkPhoneRequest request)
    {
        request = null;
        if (bytes == null) return 400;
        if (bytes.Length > MaximumRequestBytes) return 413;
        int headerEnd = HeaderEnd(bytes, bytes.Length);
        if (headerEnd < 0) return 400;
        for (int i = 0; i < headerEnd; i++)
        {
            byte b = bytes[i];
            if (b > 126 || (b < 32 && b != 13 && b != 10 && b != 9)) return 400;
        }
        string head = Encoding.ASCII.GetString(bytes, 0, headerEnd - 4);
        // 裸の CR・LF(行の区切りのごまかし)を許さない。
        if (head.Replace("\r\n", "").IndexOfAny(new[] { '\r', '\n' }) >= 0) return 400;
        string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || first[2] != "HTTP/1.1") return 400;
        string method = first[0], target = first[1];
        if (target.Length > MaximumTargetBytes) return 413;
        foreach (char c in target) if (c <= 32 || c == '#') return 400;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            int colon = line.IndexOf(':');
            if (colon <= 0) return 400;
            string key = line.Substring(0, colon);
            foreach (char c in key)
                if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-')) return 400;
            if (headers.ContainsKey(key)) return 400;
            string value = line.Substring(colon + 1).Trim(' ', '\t');
            foreach (char c in value) if (char.IsControl(c)) return 400;
            headers.Add(key, value);
        }
        // 名前で来る要求(DNS の付け替え)を断る。スマホは番地(http://<IPv4>:<port>/...)で来る。
        if (!headers.TryGetValue("Host", out string host) || !IsAllowedHost(host, port)) return 403;
        if (headers.ContainsKey("Transfer-Encoding")) return 400;
        int bodyLength = bytes.Length - headerEnd;
        if (headers.TryGetValue("Content-Length", out string declared))
        {
            if (!TryParseLength(declared, out int length) || length != bodyLength) return 400;
        }
        else if (bodyLength != 0) return 400;
        int question = target.IndexOf('?');
        string path = question < 0 ? target : target.Substring(0, question);
        if (!path.StartsWith("/", StringComparison.Ordinal)) return 400;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (question >= 0)
        {
            foreach (string part in target.Substring(question + 1).Split('&'))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0) return 400;
                if (!Decode(part.Substring(0, equals), out string key) || !Decode(part.Substring(equals + 1), out string value) ||
                    query.ContainsKey(key)) return 400;
                query.Add(key, value);
            }
        }
        if (!query.TryGetValue("t", out string supplied) || !SameToken(token, supplied)) return 403;
        ChartMarkPhoneAction action;
        switch (path)
        {
            case PagePath: action = ChartMarkPhoneAction.Page; break;
            case StatePath: action = ChartMarkPhoneAction.State; break;
            case MarkPath: action = ChartMarkPhoneAction.Mark; break;
            case CommentPath: action = ChartMarkPhoneAction.Comment; break;
            case CancelPath: action = ChartMarkPhoneAction.Cancel; break;
            default: return 404;
        }
        bool post = action == ChartMarkPhoneAction.Mark || action == ChartMarkPhoneAction.Comment || action == ChartMarkPhoneAction.Cancel;
        if (method != (post ? "POST" : "GET")) return 405;
        // 別のサイトのページからの書き込みを断る(ブラウザは POST に Origin を付ける)。
        if (post && headers.TryGetValue("Origin", out string origin) &&
            !string.Equals(origin, "http://" + host, StringComparison.OrdinalIgnoreCase)) return 403;
        string id = null, text = null, requestId = null;
        bool canceled = false;
        bool needsBody = action == ChartMarkPhoneAction.Comment || action == ChartMarkPhoneAction.Cancel;
        if (bodyLength > 0 || needsBody)
        {
            if (!post || bodyLength == 0) return 400;
            if (!headers.TryGetValue("Content-Type", out string type) || !IsJson(type)) return 415;
            string json;
            try { json = StrictUtf8.GetString(bytes, headerEnd, bodyLength); }
            catch (ArgumentException) { return 400; }
            if (!TryParseFlatJson(json, out var fields)) return 400;
            if (needsBody)
            {
                if (!fields.TryGetValue("id", out object rawId) || !(rawId is string markId) || !IsValidMarkId(markId)) return 400;
                id = markId;
            }
            if (action == ChartMarkPhoneAction.Comment)
            {
                if (!fields.TryGetValue("text", out object rawText) || !(rawText is string comment)) return 400;
                if (comment.Length > MaximumTextChars) return 413;
                text = comment;
            }
            if (action == ChartMarkPhoneAction.Cancel)
            {
                if (!fields.TryGetValue("canceled", out object rawCanceled) || !(rawCanceled is bool flag)) return 400;
                canceled = flag;
            }
            // 印の要求 id(押すたびにスマホが作る乱数)。送り直しを同じ印として扱う。無くてもよい。
            if (action == ChartMarkPhoneAction.Mark && fields.TryGetValue("requestId", out object rawRequest))
            {
                if (!(rawRequest is string requestText) || !IsValidRequestId(requestText)) return 400;
                requestId = requestText;
            }
        }
        request = new ChartMarkPhoneRequest(method, path, action, id, text, canceled, requestId);
        return 200;
    }

    // 要求 id は英数字・-・_ の 8〜64 文字。
    public static bool IsValidRequestId(string requestId)
    {
        if (requestId == null || requestId.Length < 8 || requestId.Length > 64) return false;
        foreach (char c in requestId)
            if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c == '-' || c == '_')) return false;
        return true;
    }

    // ヘッダの終わり(\r\n\r\n の次)の位置。まだ無ければ -1。
    static int HeaderEnd(byte[] bytes, int count)
    {
        for (int i = 3; i < count; i++)
            if (bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10) return i + 1;
        return -1;
    }

    // 読むべき本文の長さ(Content-Length)。無い・壊れているときは 0(残りは ParseRequest が断る)。
    static int DeclaredLength(byte[] bytes, int headerEnd)
    {
        string head = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        foreach (string line in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && string.Equals(line.Substring(0, colon), "Content-Length", StringComparison.OrdinalIgnoreCase))
                return TryParseLength(line.Substring(colon + 1).Trim(' ', '\t'), out int length)
                    ? Math.Min(length, MaximumRequestBytes + 1) : 0;
        }
        return 0;
    }

    static bool TryParseLength(string text, out int length)
    {
        length = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 9) return false;
        foreach (char c in text)
        {
            if (c < '0' || c > '9') return false;
            length = length * 10 + (c - '0');
        }
        return true;
    }

    static bool IsApiRequest(byte[] bytes)
    {
        const string prefix = "/api/";
        int space = Array.IndexOf(bytes, (byte)' ');
        if (space < 0 || space > 16 || bytes.Length < space + 1 + prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
            if (bytes[space + 1 + i] != prefix[i]) return false;
        return true;
    }

    static bool IsJson(string type) => type.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase);

    // Host は「IPv4 の番地:ポート」か「localhost:ポート」だけ。
    public static bool IsAllowedHost(string host, int port)
    {
        if (string.IsNullOrEmpty(host)) return false;
        string suffix = ":" + port.ToString(CultureInfo.InvariantCulture);
        if (!host.EndsWith(suffix, StringComparison.Ordinal)) return false;
        string name = host.Substring(0, host.Length - suffix.Length);
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IsDottedIPv4(name);
    }

    public static bool IsDottedIPv4(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string[] parts = text.Split('.');
        if (parts.Length != 4) return false;
        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 3 || part.Length > 1 && part[0] == '0') return false;
            int value = 0;
            foreach (char c in part)
            {
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            if (value > 255) return false;
        }
        return true;
    }

    // 印の id は「ファイル名-番号」。パスとして悪さをする文字は受けない(土台がファイルを探すときの念のため)。
    public static bool IsValidMarkId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaximumIdChars || id.Contains("..")) return false;
        foreach (char c in id)
            if (char.IsControl(c) || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' ||
                c == '<' || c == '>' || c == '|') return false;
        return true;
    }

    static bool Decode(string text, out string decoded)
    {
        decoded = null;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '%' && (i + 2 >= text.Length || !Uri.IsHexDigit(text[++i]) || !Uri.IsHexDigit(text[++i]))) return false;
        try { decoded = Uri.UnescapeDataString(text.Replace("+", " ")); return true; }
        catch (UriFormatException) { return false; }
    }

    // 合言葉の比べ方で長さ以外の手がかりを出さない。
    static bool SameToken(string expected, string supplied)
    {
        if (string.IsNullOrEmpty(expected) || supplied == null || supplied.Length != expected.Length) return false;
        int difference = 0;
        for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ supplied[i];
        return difference == 0;
    }

    // 合言葉は 16 バイトの乱数を 16 進の 32 文字にしたもの(ChartMarkPhone が PlayerPrefs に残し、以後変えない)。
    public static string NewToken()
    {
        var bytes = new byte[16];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    public static bool IsValidToken(string token)
    {
        if (token == null || token.Length != 32) return false;
        foreach (char c in token)
            if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
        return true;
    }

    // 平たい JSON のオブジェクト({"id":"...","text":"...","canceled":true})だけを読む。入れ子・配列・重複したキーは断る。
    // 値は string・bool・double・null(null は値なしとして入れる)。
    public static bool TryParseFlatJson(string text, out Dictionary<string, object> fields)
    {
        fields = null;
        if (text == null) return false;
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        int i = 0;
        SkipSpace(text, ref i);
        if (i >= text.Length || text[i] != '{') return false;
        i++;
        SkipSpace(text, ref i);
        if (i < text.Length && text[i] == '}') i++;
        else
        {
            while (true)
            {
                SkipSpace(text, ref i);
                if (!ReadString(text, ref i, out string key)) return false;
                SkipSpace(text, ref i);
                if (i >= text.Length || text[i] != ':') return false;
                i++;
                SkipSpace(text, ref i);
                if (!ReadValue(text, ref i, out object value) || result.ContainsKey(key)) return false;
                result.Add(key, value);
                SkipSpace(text, ref i);
                if (i >= text.Length) return false;
                if (text[i] == ',') { i++; continue; }
                if (text[i] == '}') { i++; break; }
                return false;
            }
        }
        SkipSpace(text, ref i);
        if (i != text.Length) return false;
        fields = result;
        return true;
    }

    static void SkipSpace(string text, ref int i)
    {
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\n' || text[i] == '\r')) i++;
    }

    static bool ReadValue(string text, ref int i, out object value)
    {
        value = null;
        if (i >= text.Length) return false;
        char c = text[i];
        if (c == '"')
        {
            bool ok = ReadString(text, ref i, out string s);
            value = s;
            return ok;
        }
        if (Literal(text, ref i, "true")) { value = true; return true; }
        if (Literal(text, ref i, "false")) { value = false; return true; }
        if (Literal(text, ref i, "null")) return true;
        if (c == '-' || c >= '0' && c <= '9') return ReadNumber(text, ref i, out value);
        return false;
    }

    static bool Literal(string text, ref int i, string word)
    {
        if (string.CompareOrdinal(text, i, word, 0, word.Length) != 0) return false;
        i += word.Length;
        return true;
    }

    static bool ReadNumber(string text, ref int i, out object value)
    {
        value = null;
        int start = i;
        if (text[i] == '-') i++;
        if (i >= text.Length) return false;
        if (text[i] == '0') i++;
        else if (text[i] >= '1' && text[i] <= '9') while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
        else return false;
        if (i < text.Length && text[i] == '.')
        {
            i++;
            int digits = i;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
            if (i == digits) return false;
        }
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;
            if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
            int digits = i;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
            if (i == digits) return false;
        }
        if (!double.TryParse(text.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return false;
        value = number;
        return true;
    }

    static bool ReadString(string text, ref int i, out string value)
    {
        value = null;
        if (i >= text.Length || text[i] != '"') return false;
        var result = new StringBuilder();
        for (i++; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                i++;
                value = result.ToString();
                return true;
            }
            if (c < 32) return false;
            if (c != '\\') { result.Append(c); continue; }
            if (++i >= text.Length) return false;
            switch (text[i])
            {
                case '"': result.Append('"'); break;
                case '\\': result.Append('\\'); break;
                case '/': result.Append('/'); break;
                case 'b': result.Append('\b'); break;
                case 'f': result.Append('\f'); break;
                case 'n': result.Append('\n'); break;
                case 'r': result.Append('\r'); break;
                case 't': result.Append('\t'); break;
                case 'u':
                    if (i + 4 >= text.Length || !int.TryParse(text.Substring(i + 1, 4), NumberStyles.AllowHexSpecifier,
                            CultureInfo.InvariantCulture, out int code)) return false;
                    result.Append((char)code);
                    i += 4;
                    break;
                default: return false;
            }
        }
        return false;
    }

    // この PC の IPv4 を集める(Mac の Mono では一部の項目が取れないことがあるので、1 つずつ try で守る)。
    public static List<ChartMarkPhoneAddress> LocalAddresses()
    {
        var result = new List<ChartMarkPhoneAddress>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception) { return result; }
        foreach (var network in interfaces)
        {
            try
            {
                if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var status = network.OperationalStatus;
                if (status != OperationalStatus.Up && status != OperationalStatus.Unknown) continue;
                var properties = network.GetIPProperties();
                bool gateway = false;
                try
                {
                    foreach (var route in properties.GatewayAddresses)
                        if (route.Address.AddressFamily == AddressFamily.InterNetwork && !route.Address.Equals(IPAddress.Any))
                            gateway = true;
                }
                catch (Exception) { }
                bool tunnel = network.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                              network.NetworkInterfaceType == NetworkInterfaceType.Ppp;
                foreach (var unicast in properties.UnicastAddresses)
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                        result.Add(new ChartMarkPhoneAddress(unicast.Address.ToString(), gateway, tunnel));
            }
            catch (Exception) { }
        }
        return result;
    }

    // スマホから届きそうな順に並べる。ホットスポットの 192.168.137.1 が一番、次に既定の経路がある LAN の番地。
    // ループバック・リンクローカル(169.254)・マルチキャストは外す。同じ点なら見つかった順。
    public static List<string> RankAddresses(IEnumerable<ChartMarkPhoneAddress> candidates)
    {
        var scored = new List<(int score, int index, string address)>();
        if (candidates != null)
            foreach (var candidate in candidates)
            {
                int score = Score(candidate);
                string address = candidate.Address;
                if (score > 0 && !scored.Exists(s => s.address == address)) scored.Add((score, scored.Count, address));
            }
        scored.Sort((x, y) => x.score != y.score ? y.score.CompareTo(x.score) : x.index.CompareTo(y.index));
        return scored.ConvertAll(s => s.address);
    }

    public static int Score(ChartMarkPhoneAddress candidate)
    {
        if (!IsDottedIPv4(candidate.Address)) return 0;
        if (candidate.Address == HotspotAddress) return 100;
        string[] parts = candidate.Address.Split('.');
        int a = int.Parse(parts[0], CultureInfo.InvariantCulture), b = int.Parse(parts[1], CultureInfo.InvariantCulture);
        if (a == 0 || a == 127 || a >= 224 || a == 169 && b == 254) return 0;
        bool lan = a == 10 || a == 172 && b >= 16 && b <= 31 || a == 192 && b == 168;
        int score = lan ? 40 : 10;
        if (candidate.HasGateway) score += 20;
        // VPN など(スマホからは届かないことが多い)は後ろへ。
        if (candidate.Tunnel) score -= 9;
        return Math.Max(1, score);
    }

    public static string PhoneUrl(string address, int port, string token) =>
        string.IsNullOrEmpty(address) ? null
            : "http://" + address + ":" + port.ToString(CultureInfo.InvariantCulture) + PagePath + "?t=" + token;

    public void Dispose()
    {
        List<Connection> open;
        List<ChartMarkPhoneJob> pending;
        lock (gate)
        {
            running = false;
            open = new List<Connection>(connections);
            pending = new List<ChartMarkPhoneJob>(jobs);
            jobs.Clear();
        }
        try { listener?.Stop(); } catch (Exception) { }
        foreach (var job in pending) job.Complete(503, Result(false, "止めています。"));
        foreach (var connection in open)
            try { connection.Client.Close(); } catch (Exception) { }
        var timer = Stopwatch.StartNew();
        if (worker != null && worker != Thread.CurrentThread && worker.IsAlive) worker.Join(2500);
        foreach (var connection in open)
            if (connection.Thread != Thread.CurrentThread && connection.Thread.IsAlive)
                connection.Thread.Join((int)Math.Max(1, 2500 - timer.ElapsedMilliseconds));
    }
}

public enum ChartMarkPhoneAction { Page, State, Mark, Comment, Cancel }

public sealed class ChartMarkPhoneRequest
{
    public string Method { get; }
    public string Path { get; }
    public ChartMarkPhoneAction Action { get; }
    public string Id { get; }          // 一言・取り消しの相手(印の id)
    public string Text { get; }        // 一言
    public bool Canceled { get; }      // true で取り消し、false で戻す
    public string RequestId { get; }   // 印の要求 id(送り直しを見分ける。無ければ null)
    public ChartMarkPhoneRequest(string method, string path, ChartMarkPhoneAction action, string id = null, string text = null,
        bool canceled = false, string requestId = null)
    { Method = method; Path = path; Action = action; Id = id; Text = text; Canceled = canceled; RequestId = requestId; }
}

// 最近の印の要求 id → 返事(32 件まで)。同じ id の送り直しは、もう一度付けずに同じ返事を返す(メインスレッドだけで使う)。
public sealed class ChartMarkPhoneReplies
{
    public const int Capacity = 32;
    readonly Dictionary<string, string> replies = new Dictionary<string, string>(StringComparer.Ordinal);
    readonly Queue<string> order = new Queue<string>();

    public int Count => replies.Count;

    public bool TryGet(string requestId, out string reply)
    {
        reply = null;
        return requestId != null && replies.TryGetValue(requestId, out reply);
    }

    public void Remember(string requestId, string reply)
    {
        if (requestId == null || replies.ContainsKey(requestId)) return;
        replies.Add(requestId, reply);
        order.Enqueue(requestId);
        while (order.Count > Capacity) replies.Remove(order.Dequeue());
    }
}

// 受信スレッドとメインスレッドの受け渡し。待ち → 実行中 → 済み。待ちのまま受信側が待ちきれなければ「取りやめ」で、
// メインスレッドは実行しない(スマホには失敗と返したのに印だけ付く、を起こさない)。
public sealed class ChartMarkPhoneJob
{
    const int Queued = 0, Running = 1, Done = 2, Abandoned = 3;
    readonly object sync = new object();
    int state;
    public ChartMarkPhoneRequest Request { get; }
    public int Status { get; private set; }
    public string Json { get; private set; }

    public ChartMarkPhoneJob(ChartMarkPhoneRequest request) { Request = request; }

    public bool IsDone { get { lock (sync) return state == Done; } }

    // メインスレッドが実行を始める。取りやめになった操作は false(実行しない)。
    public bool TryBegin()
    {
        lock (sync)
        {
            if (state != Queued) return false;
            state = Running;
            return true;
        }
    }

    public void Complete(int status, string json)
    {
        lock (sync)
        {
            if (state == Done) return;
            Status = status;
            Json = json ?? "";
            state = Done;
            Monitor.PulseAll(sync);
        }
    }

    public bool TryAbandon()
    {
        lock (sync)
        {
            if (state != Queued) return false;
            state = Abandoned;
            return true;
        }
    }

    public bool Wait(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        lock (sync)
        {
            while (state != Done)
            {
                long left = milliseconds - timer.ElapsedMilliseconds;
                if (left <= 0) return false;
                Monitor.Wait(sync, (int)left);
            }
            return true;
        }
    }
}

// 相手(IP)ごとに、直近の窓(1 秒)の要求を数える。上限を超えた分は数えないので、待てばすぐ戻る。
public sealed class ChartMarkPhoneRateLimiter
{
    sealed class Window
    {
        public long[] Times;
        public int Next, Count;
        public long Last;
    }

    readonly int limit;
    readonly long window;
    readonly Dictionary<string, Window> clients = new Dictionary<string, Window>(StringComparer.Ordinal);

    public ChartMarkPhoneRateLimiter(int limit, long windowTicks)
    {
        this.limit = Math.Max(1, limit);
        window = Math.Max(1, windowTicks);
    }

    public bool Allow(string client, long now)
    {
        client = client ?? "";
        lock (clients)
        {
            if (!clients.TryGetValue(client, out var times))
            {
                if (clients.Count >= 256) Prune(now);
                clients[client] = times = new Window { Times = new long[limit] };
            }
            // 窓いっぱいなら、いちばん古い記録(次に上書きする場所)が窓の外に出るまで断る。
            if (times.Count == limit && now - times.Times[times.Next] < window) return false;
            times.Times[times.Next] = now;
            times.Next = (times.Next + 1) % limit;
            if (times.Count < limit) times.Count++;
            times.Last = now;
            return true;
        }
    }

    void Prune(long now)
    {
        var stale = new List<string>();
        foreach (var pair in clients)
            if (now - pair.Value.Last >= window) stale.Add(pair.Key);
        foreach (string key in stale) clients.Remove(key);
    }
}

public struct ChartMarkPhoneAddress
{
    public string Address;
    public bool HasGateway;    // 既定の経路がある(ふだんの LAN。仮想の網やホストオンリーには無いことが多い)
    public bool Tunnel;        // VPN など
    public ChartMarkPhoneAddress(string address, bool hasGateway, bool tunnel = false)
    {
        Address = address;
        HasGateway = hasGateway;
        Tunnel = tunnel;
    }
}
