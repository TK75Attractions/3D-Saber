using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// 診断専用。Unity API・入力・判定には触れず、保存済みの行だけを背景で共有する。
public sealed class PhoneSaberPeerLog : IDisposable
{
    public const int DefaultPort = 5018;
    public const int MaximumDatagramBytes = 1200;
    public const int MaximumLineBytes = 4096;
    public const int MaximumLinesPerSecond = 32;
    public const int QueueCapacity = 256;
    public const int MaximumPeers = 16;
    const int ChunkBytes = 960, MaximumParts = 5, MaximumBatchLines = 8;
    const string Magic = "PHONESABER_PEERLOG 1 ";
    static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    static readonly HashSet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "perf", "latency-estimate", "input-gap", "input-silent", "input-back", "sender-change",
        "hitch", "judge-latency", "judge-latency-setting", "judge-timing", "blade-stability",
        "phone-auto-iso", "application-state", "session-start", "session-quit", "peer-context",
        "receiver-start", "receiver-stop", "receiver-failed", "receiver-restart", "idle", "previous-run-end",
    };

    public sealed class Settings
    {
        public string Station = "";
        public int Port = DefaultPort, PeerPort = DefaultPort;
        public bool Broadcast = true, LoopbackOnly;
        public bool Offline; // 解析・キューだけを試す。ソケットも探索も開かない。
        public string Peers = ""; // RankingSync と同じ IP / IP:port の列。通常は IP のみ。
        public double SendSeconds = 3;
    }

    sealed class Assembly
    {
        public byte[][] Parts;
        public int Lines;
        public double At;
    }
    sealed class Seen
    {
        public double At;
        public long Highest;
        public ulong Bits;
        public bool Contains(long seq) => seq <= Highest && (Highest - seq >= 64 || (Bits & (1UL << (int)(Highest - seq))) != 0);
        public void Add(long seq)
        {
            if (seq > Highest)
            {
                long shift = seq - Highest;
                Bits = shift >= 64 ? 0 : Bits << (int)shift;
                Highest = seq;
            }
            Bits |= 1UL << (int)(Highest - seq);
        }
    }

    readonly PhoneSaberEventLog log;
    readonly string station, session = Guid.NewGuid().ToString("N");
    readonly int port, peerPort;
    readonly bool broadcast, loopbackOnly, offline;
    readonly double sendSeconds;
    readonly object gate = new object();
    readonly Queue<string> pending = new Queue<string>();
    readonly ManualResetEvent stop = new ManualResetEvent(false);
    readonly Thread worker;
    // 以下の辞書・ソケットは worker だけで操作する。
    readonly List<IPEndPoint> manual = new List<IPEndPoint>();
    readonly Dictionary<string, IPEndPoint> learned = new Dictionary<string, IPEndPoint>();
    readonly Dictionary<string, Seen> seen = new Dictionary<string, Seen>();
    readonly Dictionary<string, Assembly> assembling = new Dictionary<string, Assembly>();
    IPAddress[] rankingPeers = Array.Empty<IPAddress>(), broadcasts = { IPAddress.Broadcast };
    volatile UdpClient socket;
    long sequence;
    int localPort, received, dropped, disposed;
    double rateAt;
    int rateLines;
    volatile bool stopping;
    volatile string lastError = "", latest = "相手の診断: 未受信";
    static PhoneSaberPeerLog relayDiscovery;
    volatile IPAddress stationAAddress;
    // worker の辞書は公開せず、確定済み台Aと不変の設定・rankingの写しだけを渡す。
    public static IPAddress[] ClipRelayTargets
    {
        get
        {
            var source = Volatile.Read(ref relayDiscovery);
            if (source == null) return Array.Empty<IPAddress>();
            var result = new List<IPAddress>();
            var known = source.stationAAddress;
            if (known != null) result.Add(known);
            foreach (var peer in source.manual) if (!result.Contains(peer.Address)) result.Add(peer.Address);
            foreach (var peer in Volatile.Read(ref source.rankingPeers)) if (!result.Contains(peer)) result.Add(peer);
            return result.ToArray();
        }
    }
    string lastScene, lastSong;
    public int LocalPort => Volatile.Read(ref localPort);
    public int ReceivedLines => Volatile.Read(ref received);
    public int DroppedLines => Volatile.Read(ref dropped);
    public int PendingLines { get { lock (gate) return pending.Count; } }
    public bool IsRunning => worker.IsAlive;
    public string LastError => lastError;
    public string Latest => latest;

    public PhoneSaberPeerLog(PhoneSaberEventLog log, Settings settings)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        station = PhoneSaberStation.Normalize(settings.Station);
        if (station.Length == 0) throw new ArgumentException("station is required");
        port = settings.Port; peerPort = settings.PeerPort;
        broadcast = settings.Broadcast; loopbackOnly = settings.LoopbackOnly; offline = settings.Offline;
        sendSeconds = Math.Max(.05, double.IsNaN(settings.SendSeconds) || double.IsInfinity(settings.SendSeconds) ? 3 : settings.SendSeconds);
        foreach (var target in RankingSyncLog.ParsePeers(settings.Peers, peerPort))
            if (Allowed(target.Address) && manual.Count < MaximumPeers) manual.Add(target);
        worker = new Thread(Run) { IsBackground = true, Name = "PhoneSaber peer log" };
        if (!offline) Volatile.Write(ref relayDiscovery, this);
        log.LineWritten += Capture;
        worker.Start();
    }

    bool Allowed(IPAddress address) => address != null && address.AddressFamily == AddressFamily.InterNetwork &&
        (!loopbackOnly || IPAddress.IsLoopback(address));

    // メインスレッドで作った読み取り専用スナップショットだけを受け取る。背景で Unity に問い合わせない。
    public void SetRankingPeers(IReadOnlyList<IPAddress> addresses)
    {
        var copy = new List<IPAddress>();
        if (addresses != null)
            foreach (var address in addresses)
                if (Allowed(address) && copy.Count < MaximumPeers && !copy.Contains(address)) copy.Add(address);
        Volatile.Write(ref rankingPeers, copy.ToArray());
    }

    // 曲の境目だけ記録し、相手側で perf / latency を曲へ割り当てられるようにする。
    public void ObserveContext(string scene, string song)
    {
        if (scene == lastScene && song == lastSong || stopping) return;
        lastScene = scene; lastSong = song;
        log.Enqueue(DateTimeOffset.UtcNow, station, "SYSTEM", "peer-context", "scene=" + scene + " song=" + Uri.EscapeDataString(song ?? ""));
    }

    void Capture(string raw)
    {
        if (stopping) return;
        string line = raw.TrimEnd('\n');
        if (!ValidLine(line, station)) return; // peer-log は対象外なので再転送しない。
        lock (gate)
        {
            if (stopping) return;
            if (pending.Count >= QueueCapacity) { pending.Dequeue(); Interlocked.Increment(ref dropped); }
            pending.Enqueue(line);
        }
    }

    static bool ValidLine(string line, string from)
    {
        if (string.IsNullOrEmpty(line) || line.Length > MaximumLineBytes || Utf8.GetByteCount(line) > MaximumLineBytes) return false;
        foreach (char c in line) if (char.IsControl(c)) return false;
        int stationAt = line.IndexOf(" station=", StringComparison.Ordinal);
        if (stationAt != 24 || !line.Substring(stationAt).StartsWith(" station=" + from + " colour=", StringComparison.Ordinal)) return false;
        if (!DateTimeOffset.TryParseExact(line.Substring(0, 24), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out _)) return false;
        int eventAt = line.IndexOf(" event=", stationAt, StringComparison.Ordinal);
        if (eventAt < 0) return false;
        int end = line.IndexOf(' ', eventAt + 7);
        return end >= 0 && Kinds.Contains(line.Substring(eventAt + 7, end - eventAt - 7));
    }

    void Run()
    {
        var clock = Stopwatch.StartNew();
        double nextSend = sendSeconds, nextScan = 0, nextBind = 0;
        var buffer = new byte[MaximumDatagramBytes + 1];
        var expired = new List<string>();
        try
        {
            while (!stop.WaitOne(20))
            {
                double now = clock.Elapsed.TotalSeconds;
                if (offline) continue;
                if (socket == null && now >= nextBind)
                {
                    nextBind = now + 10;
                    Open();
                }
                if (broadcast && now >= nextScan) { nextScan = now + 15; broadcasts = FindBroadcasts(); }
                if (socket == null) continue;
                for (int i = 0; i < 64 && socket.Client.Poll(0, SelectMode.SelectRead); i++)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    try
                    {
                        int count = socket.Client.ReceiveFrom(buffer, ref from);
                        if (count <= MaximumDatagramBytes && from is IPEndPoint endpoint && Allowed(endpoint.Address))
                            Receive(buffer, count, endpoint, now);
                    }
                    catch (SocketException e) { lastError = e.SocketErrorCode.ToString(); }
                    catch (DecoderFallbackException) { }
                }
                if (now >= nextSend) { nextSend = now + sendSeconds; SendPending(); }
                expired.Clear();
                foreach (var item in assembling) if (now - item.Value.At > 10) expired.Add(item.Key);
                foreach (string key in expired) assembling.Remove(key);
                expired.Clear();
                foreach (var item in seen) if (now - item.Value.At > 600) expired.Add(item.Key);
                foreach (string key in expired) seen.Remove(key);
            }
            // 終了行まで writer が排出した後、残りを1回だけ送って閉じる。
            if (socket != null) SendPending();
        }
        catch (Exception e) { lastError = e.GetType().Name; }
        finally { socket?.Close(); socket = null; }
    }

    void Open()
    {
        UdpClient client = null;
        try
        {
            client = new UdpClient(AddressFamily.InterNetwork);
            client.EnableBroadcast = broadcast;
            client.Client.SendTimeout = 100;
            client.Client.ReceiveTimeout = 100;
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            client.ExclusiveAddressUse = true;
            try { client.Client.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch (Exception) { }
#endif
            client.Client.Bind(new IPEndPoint(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port));
            socket = client;
            Volatile.Write(ref localPort, ((IPEndPoint)client.Client.LocalEndPoint).Port);
            lastError = "";
        }
        catch (Exception e) { client?.Close(); lastError = e.GetType().Name; }
    }

    void SendPending()
    {
        var targets = new HashSet<IPEndPoint>(manual);
        foreach (var endpoint in learned.Values) targets.Add(endpoint);
        foreach (var address in Volatile.Read(ref rankingPeers)) targets.Add(new IPEndPoint(address, peerPort));
        if (broadcast) foreach (var address in broadcasts) targets.Add(new IPEndPoint(address, peerPort));
        // 宛先がなくてもキューを排出し、古い診断が際限なく残らないようにする。
        var lines = new List<string>();
        lock (gate) while (pending.Count > 0) lines.Add(pending.Dequeue());
        int offset = 0;
        do
        {
            var body = new StringBuilder();
            int count = 0, bytes = 0;
            while (offset < lines.Count && count < MaximumBatchLines)
            {
                int length = Utf8.GetByteCount(lines[offset]) + 1;
                if (count > 0 && bytes + length > ChunkBytes) break;
                body.Append(lines[offset++]).Append('\n'); count++; bytes += length;
            }
            foreach (byte[] packet in Encode(station, session, ++sequence, body.ToString(), count))
                foreach (var target in targets)
                {
                    try { socket.Send(packet, packet.Length, target); }
                    catch (Exception e) when (e is SocketException || e is ObjectDisposedException) { lastError = e.GetType().Name; }
                }
        } while (offset < lines.Count);
    }

    // part は UTF-8 のバイト境界で分割する。全断片がそろってから厳密に UTF-8 を復号する。
    public static IReadOnlyList<byte[]> Encode(string station, string session, long seq, string body, int lines)
    {
        byte[] payload = Utf8.GetBytes(body);
        int parts = Math.Max(1, (payload.Length + ChunkBytes - 1) / ChunkBytes);
        if (parts > MaximumParts) throw new ArgumentException("payload too long");
        var result = new List<byte[]>();
        for (int i = 0; i < parts; i++)
        {
            byte[] header = Utf8.GetBytes(Magic + "station=" + station + " session=" + session + " seq=" + seq.ToString(CultureInfo.InvariantCulture) +
                " lines=" + lines.ToString(CultureInfo.InvariantCulture) + " part=" + (i + 1) + "/" + parts + "\n");
            int length = Math.Min(ChunkBytes, payload.Length - i * ChunkBytes);
            var packet = new byte[header.Length + length];
            if (packet.Length > MaximumDatagramBytes) throw new ArgumentException("header too long");
            Buffer.BlockCopy(header, 0, packet, 0, header.Length);
            Buffer.BlockCopy(payload, i * ChunkBytes, packet, header.Length, length);
            result.Add(packet);
        }
        return result;
    }

    void Receive(byte[] packet, int length, IPEndPoint from, double now)
    {
        int newline = Array.IndexOf(packet, (byte)'\n', 0, length);
        if (newline < 0 || newline > 220) return;
        string header = Utf8.GetString(packet, 0, newline);
        if (!header.StartsWith(Magic, StringComparison.Ordinal)) return;
        string[] fields = header.Substring(Magic.Length).Split(' ');
        if (fields.Length != 5 || !fields[0].StartsWith("station=", StringComparison.Ordinal) ||
            !fields[1].StartsWith("session=", StringComparison.Ordinal) || !fields[2].StartsWith("seq=", StringComparison.Ordinal) ||
            !fields[3].StartsWith("lines=", StringComparison.Ordinal) || !fields[4].StartsWith("part=", StringComparison.Ordinal)) return;
        string remoteStation = fields[0].Substring(8), remoteSession = fields[1].Substring(8);
        if (remoteStation.Length == 0 || PhoneSaberStation.Normalize(remoteStation) != remoteStation || remoteStation == station ||
            remoteSession.Length != 32 || !Guid.TryParseExact(remoteSession, "N", out _)) return;
        string[] part = fields[4].Substring(5).Split('/');
        if (!long.TryParse(fields[2].Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out long seq) || seq <= 0 ||
            !int.TryParse(fields[3].Substring(6), out int lines) || lines < 0 || lines > MaximumBatchLines ||
            part.Length != 2 || !int.TryParse(part[0], out int index) || !int.TryParse(part[1], out int parts) ||
            parts < 1 || parts > MaximumParts || index < 1 || index > parts || length - newline - 1 > ChunkBytes) return;
        string identity = remoteStation + ":" + remoteSession;
        seen.TryGetValue(identity, out var history);
        if (history != null && history.Contains(seq)) return;
        string key = identity + ":" + seq;
        if (!assembling.TryGetValue(key, out var assembly))
        {
            if (assembling.Count >= 32) return;
            assembly = new Assembly { Parts = new byte[parts][], Lines = lines, At = now };
            assembling.Add(key, assembly);
        }
        if (assembly.Parts.Length != parts || assembly.Lines != lines) return;
        if (assembly.Parts[index - 1] == null)
        {
            byte[] data = new byte[length - newline - 1];
            Buffer.BlockCopy(packet, newline + 1, data, 0, data.Length);
            assembly.Parts[index - 1] = data;
        }
        int total = 0;
        foreach (var data in assembly.Parts) { if (data == null) return; total += data.Length; }
        assembling.Remove(key);
        byte[] combined = new byte[total];
        int at = 0;
        foreach (var data in assembly.Parts) { Buffer.BlockCopy(data, 0, combined, at, data.Length); at += data.Length; }
        string body = Utf8.GetString(combined);
        string[] rows = body.Length == 0 ? Array.Empty<string>() : body.TrimEnd('\n').Split('\n');
        if (rows.Length != lines) return;
        foreach (string line in rows) if (!ValidLine(line, remoteStation)) return;
        if (history == null)
        {
            if (seen.Count >= 32) return;
            history = new Seen(); seen.Add(identity, history);
        }
        history.Add(seq); history.At = now;
        if (remoteStation == "A") stationAAddress = from.Address;
        string endpointKey = from.ToString();
        if (learned.Count < MaximumPeers || learned.ContainsKey(endpointKey)) learned[endpointKey] = from;
        if (now - rateAt >= 1) { rateAt = now; rateLines = 0; }
        foreach (string line in rows)
        {
            if (rateLines >= MaximumLinesPerSecond) { Interlocked.Increment(ref dropped); continue; }
            rateLines++;
            log.Enqueue(DateTimeOffset.UtcNow, station, "PEER", "peer-log", "from-station=" + remoteStation + " " + line);
            Interlocked.Increment(ref received);
            if (line.Contains(" event=latency-estimate ") || line.Contains(" event=perf "))
                latest = "台" + remoteStation + " " + line.Substring(0, 24) + " " +
                    (line.Contains(" event=perf ") ? "fps=" + Value(line, "fps") + " packet/s R=" + Value(line, "red-pkt-per-s") + " B=" + Value(line, "blue-pkt-per-s") :
                    Value(line, "colour") + " 推定遅延=" + Value(line, "total-ms") + "ms / PC=" + Value(line, "pc-median-ms") + "ms");
        }
    }

    static string Value(string line, string name)
    {
        int start = line.IndexOf(" " + name + "=", StringComparison.Ordinal);
        if (start < 0) return "unknown";
        start += name.Length + 2;
        int end = line.IndexOf(' ', start);
        return end < 0 ? line.Substring(start) : line.Substring(start, end - start);
    }

    static IPAddress[] FindBroadcasts()
    {
        var result = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        (nic.OperationalStatus != OperationalStatus.Up && nic.OperationalStatus != OperationalStatus.Unknown)) continue;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        IPAddress mask = null;
                        try { mask = unicast.IPv4Mask; } catch (Exception) { }
                        var address = RankingSyncLog.DirectedBroadcast(unicast.Address, mask, out _);
                        if (address != null) result.Add(address);
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        var addresses = new IPAddress[result.Count]; result.CopyTo(addresses); return addresses;
    }

    // 受付停止→合図→背景で最終送信→Join。通常のフレームからは待たない。
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.CompareExchange(ref relayDiscovery, null, this);
        log.LineWritten -= Capture;
        stopping = true;
        stop.Set();
        if (worker.Join(2000)) stop.Dispose();
        // OS の送信待ちが残る場合もソケットを閉じて解除する。
        else { try { socket?.Close(); } catch (Exception) { } worker.Join(1000); }
    }
}
