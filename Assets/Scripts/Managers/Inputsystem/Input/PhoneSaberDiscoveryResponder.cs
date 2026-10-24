using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Globalization;

// Android 版 PhoneSaberSender が、同じ Wi-Fi 上で Unity の PC(Windows / Mac)を見つけるための応答役。
// Windows には Mac の dns-sd に当たる標準コマンドがないので、全 OS 共通の UDP broadcast で答える。
//   要求: "PHONESABER_DISCOVER 1"(UDP 5007 へ broadcast)
//   応答: "PHONESABER_UNITY 1 red=5005 blue=5006 name=<PC名>"(送信元へ unicast)
// 座標は従来どおり InputPoint が UDP 5005 / 5006 で受ける。iPhone は従来の Bonjour を使う。
public sealed class PhoneSaberDiscoveryResponder : IDisposable
{
    public const int Port = 5007;
    public const string Request = "PHONESABER_DISCOVER 1";
    public const string ReplyPrefix = "PHONESABER_UNITY 1";
    // F8 から iPhone への指示（2026-10-18: 会場のコーンを iPhone に触らず無視させる）。応答の形式は変えず、
    // 応答の直後に別の datagram として同じ相手へ送る。期限内は探索要求が来るたびに送り直し、
    // iPhone は同じ id を一度だけ実行する。古いアプリや Android は知らない datagram として捨てる。
    public const string CommandPrefix = "PHONESABER_COMMAND 1";
    public const double CommandSeconds = 10;
    // PC起動・新規接続時の自動指示もF8も、保存済みの選択を1/2000秒・ISO160・色付き発光体ONへ戻す。
    public const string DefaultsCommand = "defaults=1";
    const double NewSenderSeconds = 60;
    static readonly object commandGate = new object();
    static int automaticDefaultsRequested;
    static string commandStation = "";
    static string pendingCommand;
    static double pendingUntil;
    static int commandId = (int)(DateTime.UtcNow.Ticks % 1000000) * 1000;
    public static readonly PhoneSaberAcknowledgementStore Acknowledgements = new PhoneSaberAcknowledgementStore();

    public static string BuildCommand(int id, string fields) => CommandPrefix + " id=" + id + " " + fields;

    // プレイ範囲（2026-10-18、赤い服の人など動く物の対策）: スマホが LearnAreaSeconds 秒間の winner の範囲を覚え、
    // 以後その外（両端の中点）の候補を選ばない。完了後も同じ id を送り、iPhone の保存済み ACK を回収する。
    public const int LearnAreaSeconds = 10;
    public static readonly string LearnAreaCommand = "learn-area=" + LearnAreaSeconds;
    public const string ClearAreaCommand = "clear-area=1";
    // iPhone のキャッシュは受信から学習秒数＋10秒。再送はそれより短く、通常指示は従来の10秒。
    public static double CommandRetrySeconds(string fields) =>
        fields != null && fields.StartsWith("learn-area=", StringComparison.Ordinal) &&
        int.TryParse(fields.Substring(11), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) &&
        seconds >= 1 && seconds <= 60 ? seconds + 5 : CommandSeconds;
    // iPhone専用の固定短時間露出。OFFは既存の自動露出＋補正へ戻す。
    public static readonly int[] ShutterISOChoices = { 2000, 250, 200, 160, 125, 100, 80, 64 };
    public static string ShutterISOCommand(int value) => "shutter-iso=" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static readonly int[] ShutterChoices = { 0, 500, 1000, 2000 };
    public static string ShutterCommand(int value) => "shutter=" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    // 露出補正（EV、0 = OFF、-1、-2 = 既定）。スマホが保存する。
    public static readonly int[] ExposureBiasChoices = { 0, -1, -2 };
    public static string ExposureBiasCommand(int ev) => "exposure-bias=" + ev.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // fields: "ignore=red" / "ignore=blue" / "clear-ignore=1" / LearnAreaCommand / ClearAreaCommand / ExposureBiasCommand(ev)
    public static int QueueCommand(string fields, bool automatic = false)
    {
        lock (commandGate)
        {
            int id = Interlocked.Increment(ref commandId);
            double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
            Acknowledgements.Queue(id, fields, now, automatic);
            Volatile.Write(ref pendingCommand, BuildCommand(id, fields));
            Volatile.Write(ref pendingUntil, now + CommandRetrySeconds(fields));
            if (fields == DefaultsCommand)
            {
                PhoneSaberOperatorKeys.ResetDefaultsState();
                // 受信 thread からも安全な値だけを渡す。PlayerPrefs は読まない。
                PhoneSaberEventLog.Record(Volatile.Read(ref commandStation), "SYSTEM", "phone-defaults", "id=" + id);
            }
            return id;
        }
    }

    // 受信側は要求フラグだけを立て、実際の自動指示は運営キーの main thread で送る。
    public static void ProcessAutomaticDefaults()
    {
        if (Interlocked.Exchange(ref automaticDefaultsRequested, 0) == 0 ||
            !PhoneSaberP2PBridgeProcess.AutoStartEnabled) return;
        lock (commandGate)
            if (CommandSecondsLeft(SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp)) == 0)
                QueueCommand(DefaultsCommand, automatic: true);
    }

    // 判定は時刻と値だけ。送信元 port が変わっても同じ IP は同じ端末として扱う。
    public static bool ShouldQueueDefaults(IReadOnlyDictionary<string, double> lastSeen, string sender,
        double now, double pendingSeconds) => pendingSeconds == 0 &&
        (!lastSeen.TryGetValue(sender, out double seen) || now - seen >= NewSenderSeconds);

    public static double CommandSecondsLeft(double now) =>
        Volatile.Read(ref pendingCommand) == null ? 0 : Math.Max(0, Volatile.Read(ref pendingUntil) - now);

    static byte[] CurrentCommand()
    {
        string command = Volatile.Read(ref pendingCommand);
        if (command == null) return null;
        if (SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp) > Volatile.Read(ref pendingUntil)) return null;
        return Encoding.ASCII.GetBytes(command);
    }

    public PhoneSaberMetricsStore Metrics { get; } = new PhoneSaberMetricsStore();

    readonly object gate = new object();
    UdpClient socket;
    Thread thread;
    volatile bool running;
    bool disposed;

    public bool IsRunning
    {
        get { lock (gate) return running; }
    }

    public static string BuildReply(int redPort, int bluePort, string machineName, string station = "")
    {
        string name = string.IsNullOrWhiteSpace(machineName) ? "Unity" : machineName.Replace(' ', '_');
        string label = PhoneSaberStation.Normalize(station);
        return $"{ReplyPrefix} red={redPort} blue={bluePort} name={name}" +
               (label.Length == 0 ? "" : $" station={label}");
    }

    public static bool IsRequest(byte[] data, int length)
    {
        if (data == null || length <= 0 || length > 64) return false;
        return Encoding.ASCII.GetString(data, 0, length).Trim() == Request;
    }

    public bool Start(int redPort, int bluePort, string station = "")
        => Start(redPort, bluePort, () => new UdpClient(AddressFamily.InterNetwork), station);

    // テストでは既に bind 済みの socket を渡し、5007 を占有せず起動失敗を再現する。
    internal bool Start(int redPort, int bluePort, Func<UdpClient> createClient, string station = "", int listenPort = Port)
    {
        // テスト中は他の自動起動と同じく何も開かない(port 5007 を占有しない)。
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return false;
        lock (gate)
        {
            if (disposed) return false;
            if (running) return true;
            try
            {
                var client = createClient();
                // bind より前に所有し、設定・bind の失敗でも catch で確実に閉じる。
                socket = client;
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
                // Windows の SO_REUSEADDR は他の受信機との共有を許すため、探索も排他で bind する。
                client.ExclusiveAddressUse = true;
#else
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
#endif
                client.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
                client.Client.ReceiveTimeout = 500;
                Volatile.Write(ref commandStation, station);
                QueueCommand(DefaultsCommand, automatic: true);
                running = true;
                byte[] reply = Encoding.ASCII.GetBytes(BuildReply(redPort, bluePort, Environment.MachineName, station));
                thread = new Thread(() => Loop(client, reply)) { IsBackground = true, Name = "PhoneSaberDiscovery" };
                thread.Start();
                UnityEngine.Debug.Log($"[PhoneSaber] Android discovery responder on UDP {listenPort} station={station}");
                return true;
            }
            catch (Exception exception)
            {
                running = false;
                socket?.Close();
                socket = null;
                UnityEngine.Debug.LogWarning($"[PhoneSaber] Android discovery responder failed: {exception.Message}." +
                                             " Android では PC の IP を手入力してください");
                return false;
            }
        }
    }

    void Loop(UdpClient client, byte[] reply)
    {
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        var data = new byte[PhoneSaberMetrics.MaximumBytes + 1];
        var lastSeen = new Dictionary<string, double>();
        // Stop の Join 時間切れ後に再起動しても、閉じた旧 socket で例外を繰り返さない。
        while (running && ReferenceEquals(Volatile.Read(ref socket), client))
        {
            try
            {
                int length = client.Client.ReceiveFrom(data, ref remote);
                if (IsRequest(data, length))
                {
                    string sender = ((IPEndPoint)remote).Address.ToString();
                    double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
                    if (ShouldQueueDefaults(lastSeen, sender, now, CommandSecondsLeft(now)))
                        Interlocked.Exchange(ref automaticDefaultsRequested, 1);
                    lastSeen[sender] = now;
                    client.Send(reply, reply.Length, (IPEndPoint)remote);
                    byte[] command = CurrentCommand();
                    if (command != null) client.Send(command, command.Length, (IPEndPoint)remote);
                }
                else if (PhoneSaberAcknowledgement.TryParse(data, length, out var ack))
                {
                    string from = ((IPEndPoint)remote).Address.ToString();
                    if (Acknowledgements.Receive(ack, from))
                        PhoneSaberEventLog.Record(ack.Station, "SYSTEM", "phone-ack",
                            $"id={ack.Id} result={ack.Result} detail={ack.Detail} from={from}");
                }
                else if (length <= PhoneSaberMetrics.MaximumBytes)
                    Metrics.Receive(data, length, ((IPEndPoint)remote).Address,
                        SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut) { }
            catch (Exception)
            {
                if (!running) return;
            }
        }
    }

    public void Stop()
    {
        Thread active;
        lock (gate)
        {
            running = false;
            Interlocked.Exchange(ref automaticDefaultsRequested, 0);
            socket?.Close();
            socket = null;
            active = thread;
            thread = null;
        }
        active?.Join(1000);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        Stop();
    }
}

// ACK は短い ASCII token だけ。Unity API を使わずテストできる解析・保存部分。
public sealed class PhoneSaberAcknowledgement
{
    public int Id { get; }
    public string Station { get; }
    public string Platform { get; }
    public string Result { get; }
    public string Detail { get; }
    public string Sender { get; }

    public PhoneSaberAcknowledgement(int id, string station, string platform, string result, string detail, string sender = "")
    { Id = id; Station = station; Platform = platform; Result = result; Detail = detail; Sender = sender; }

    static bool Atom(string value) => !string.IsNullOrEmpty(value) && value.All(c =>
        c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '.' || c == '-');
    static bool Number(string value) => !string.IsNullOrEmpty(value) && value.All(c => c >= '0' && c <= '9') && int.TryParse(value, out _);
    public string SampleCount => Detail.Split('-')[0].Substring(7);
    // 解析・受信スレッドは Unity の型や保存 API に依存させない。
    public readonly struct LearnedBounds
    {
        public readonly float xMin, yMin, xMax, yMax;
        public LearnedBounds(float x0, float y0, float x1, float y1)
        { xMin = x0; yMin = y0; xMax = x1; yMax = y1; }
    }
    public bool TryLearnedBounds(out LearnedBounds bounds) => TryLearnedBounds(Detail, out bounds);

    static bool TryLearnedBounds(string detail, out LearnedBounds bounds)
    {
        bounds = default;
        var parts = detail.Split(new[] { "-area" }, StringSplitOptions.None);
        if (parts.Length != 2 || !parts[0].StartsWith("samples") || !Number(parts[0].Substring(7)) ||
            int.Parse(parts[0].Substring(7)) < 30) return false;
        var values = parts[1].Split('_');
        if (values.Length != 4) return false;
        var parsed = new float[4];
        for (int i = 0; i < 4; i++)
            if (!float.TryParse(values[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out parsed[i])) return false;
        if (parsed.Any(v => float.IsNaN(v) || float.IsInfinity(v) || v < 0 || v > 1) ||
            parsed[0] >= parsed[2] || parsed[1] >= parsed[3]) return false;
        bounds = new LearnedBounds(parsed[0], parsed[1], parsed[2], parsed[3]);
        return true;
    }

    static bool DetailValid(string result, string detail)
    {
        switch (result)
        {
            case "ignored":
                var counts = detail.Split('-');
                return counts.Length == 2 && counts[0].StartsWith("red") && counts[1].StartsWith("blue") &&
                    Number(counts[0].Substring(3)) && Number(counts[1].Substring(4)) &&
                    (int.Parse(counts[0].Substring(3)) > 0 || int.Parse(counts[1].Substring(4)) > 0);
            case "nothing": return detail == "red0-blue0";
            case "cleared": return detail == "ignore" || detail == "area";
            case "learning": return detail.EndsWith("s") && Number(detail.Substring(0, detail.Length - 1)) &&
                int.Parse(detail.Substring(0, detail.Length - 1)) >= 1 && int.Parse(detail.Substring(0, detail.Length - 1)) <= 60;
            case "learned": return detail.StartsWith("samples") &&
                (Number(detail.Substring(7)) || TryLearnedBounds(detail, out _));
            case "failed": return detail.StartsWith("samples") && Number(detail.Substring(7));
            // 形式のminISOで制限された実適用値は、選択肢外や小数にもなる。
            case "shutter-iso": return double.TryParse(detail, System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out double iso) && iso > 0 && !double.IsInfinity(iso);
            case "shutter-emitter": return detail == "on" || detail == "off";
            case "shutter": return detail == "off" || detail == "500" || detail == "1000" || detail == "2000";
            case "exposure": return detail == "ev0" || detail == "ev-1" || detail == "ev-2";
            case "defaults": return detail == "tracklock-on-ev-2" || detail == "tracklock-on-ev-2-shutter-2000";
            case "tracklock": case "autoclip": return detail == "on" || detail == "off";
            case "clip-saved": return detail == "saved";
            case "clip-off": return detail == "off";
            default: return false;
        }
    }

    public static bool TryParse(byte[] data, int length, out PhoneSaberAcknowledgement ack)
    {
        ack = null;
        if (data == null || length <= 0 || length >= 200 || length > data.Length) return false;
        for (int i = 0; i < length; i++) if (data[i] < 32 || data[i] > 126) return false;
        return TryParse(Encoding.ASCII.GetString(data, 0, length), out ack);
    }

    public static bool TryParse(string line, out PhoneSaberAcknowledgement ack)
    {
        ack = null;
        if (line == null || line.Length >= 200 || !line.StartsWith("PHONESABER_ACK 1 ")) return false;
        var fields = new Dictionary<string, string>();
        foreach (string token in line.Substring("PHONESABER_ACK 1 ".Length).Split(' '))
        {
            int equal = token.IndexOf('=');
            if (equal <= 0 || !Atom(token.Substring(0, equal)) || !Atom(token.Substring(equal + 1))) return false;
            string key = token.Substring(0, equal);
            if (key != "id" && key != "station" && key != "platform" && key != "result" && key != "detail") continue;
            if (fields.ContainsKey(key)) return false;
            fields.Add(key, token.Substring(equal + 1));
        }
        if (!fields.TryGetValue("id", out string id) || !Number(id) ||
            !fields.TryGetValue("station", out string station) ||
            !fields.TryGetValue("platform", out string platform) || (platform != "ios" && platform != "android") ||
            !fields.TryGetValue("result", out string result) || !fields.TryGetValue("detail", out string detail) ||
            !DetailValid(result, detail)) return false;
        ack = new PhoneSaberAcknowledgement(int.Parse(id), station, platform, result, detail);
        return true;
    }
}

public sealed class PhoneSaberAcknowledgementStore
{
    sealed class Command
    {
        public int Id;
        public string Fields;
        public double QueuedAt;
        public bool Automatic;
        public bool LearnedAreaTaken;
        public readonly HashSet<string> AppliedStations = new HashSet<string>();
        public readonly Dictionary<string, PhoneSaberAcknowledgement> Phones = new Dictionary<string, PhoneSaberAcknowledgement>();
    }
    readonly object gate = new object();
    readonly List<Command> commands = new List<Command>();
    int revision;
    public int Revision => Volatile.Read(ref revision);

    public void Queue(int id, string fields, double now, bool automatic = false)
    {
        lock (gate)
        {
            commands.RemoveAll(c => c.Id == id);
            commands.Add(new Command { Id = id, Fields = fields, QueuedAt = now, Automatic = automatic });
            if (commands.Count > 32) commands.RemoveAt(0);
            Interlocked.Increment(ref revision);
        }
    }

    public bool Receive(PhoneSaberAcknowledgement ack, string sender)
    {
        lock (gate)
        {
            var command = commands.Find(c => c.Id == ack.Id);
            if (command == null) return false;
            if (!command.Phones.TryGetValue(sender, out var previous) && command.Phones.Count >= 8) return false;
            // 開始の再送が完了より遅れて届いても、学習中へ戻さない。
            if (previous != null && (previous.Result == "learned" || previous.Result == "failed" ||
                previous.Result == "ignored" || previous.Result == "nothing") && ack.Result == "learning") return true;
            command.Phones[sender] = new PhoneSaberAcknowledgement(ack.Id, ack.Station, ack.Platform, ack.Result, ack.Detail, sender);
            Interlocked.Increment(ref revision);
            return true;
        }
    }

    // receive thread は値の保存だけ。main thread が台を照合し、最新の学習結果を一度だけ取り出す。
    public bool TakeLearnedArea(string station, out PhoneSaberAcknowledgement.LearnedBounds bounds)
    {
        bounds = default;
        if (station != "A" && station != "B") return false;
        lock (gate)
        {
            var command = commands.LastOrDefault(c => c.Fields.StartsWith("learn-area="));
            if (command == null || command.LearnedAreaTaken || command.AppliedStations.Contains(station)) return false;
            var phones = command.Phones.Values.Where(a => a.Platform == "ios").ToArray();
            // 同じ台の送信元が開始ACKだけでも返していれば、その端末の完了を待つ。
            // 台未設定は送信元を一意に選べる場合だけ代用し、別の台のACKは使わない。
            var matching = phones.Where(a => a.Station == station).ToArray();
            var candidates = matching.Length > 0 ? matching : phones.Where(a => a.Station == "none").ToArray();
            if (matching.Length == 0 && candidates.Length != 1) return false;
            var ack = candidates.OrderBy(a => a.Sender, StringComparer.Ordinal).FirstOrDefault(a =>
                a.Result == "learned" && a.TryLearnedBounds(out _));
            if (ack == null || !ack.TryLearnedBounds(out bounds)) return false;
            // 台名変更や遅延ACKでも、同じ指示から別の矩形を再適用しない。
            command.LearnedAreaTaken = true;
            return true;
        }
    }

    // リセット／手動保存より前の学習は、未着の完了ACKも含めて補正へ再適用しない。
    public void DiscardLearnedAreas(string station)
    {
        lock (gate)
            foreach (var command in commands)
                if (command.Fields.StartsWith("learn-area=")) command.AppliedStations.Add(station);
    }

    public PhoneSaberAcknowledgement[] Read(int id)
    {
        lock (gate) return commands.Find(c => c.Id == id)?.Phones.Values.OrderBy(a => a.Sender, StringComparer.Ordinal).ToArray()
            ?? Array.Empty<PhoneSaberAcknowledgement>();
    }

    public bool Latest(out int id, out string fields, out double queuedAt)
        => Latest(out id, out fields, out queuedAt, out _);

    public bool Latest(out int id, out string fields, out double queuedAt, out bool automatic)
    {
        lock (gate)
        {
            var command = commands.LastOrDefault();
            id = command?.Id ?? 0; fields = command?.Fields ?? ""; queuedAt = command?.QueuedAt ?? 0;
            automatic = command?.Automatic ?? false;
            return command != null;
        }
    }
}

// 台設定は main thread で読み、受信 thread には値だけ渡す(PlayerPrefs は thread 非対応)。
public static class PhoneSaberStation
{
    public const string PlayerPrefsKey = "PhoneSaber.Station";
    public const string EnvironmentVariable = "PHONESABER_STATION";
    public const string Argument = "-phonesaberStation";

    // UDP の field と process 引数に安全な、短い台名のみ使う。
    public static string Normalize(string value)
    {
        string label = value == null ? "" : value.Trim();
        if (label.Length > 16) return "";
        foreach (char c in label)
            if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') &&
                !(c >= '0' && c <= '9') && c != '-' && c != '_') return "";
        return label;
    }

    public static string Resolve(string[] arguments, string environment, string preference)
    {
        if (arguments != null)
            for (int i = 0; i + 1 < arguments.Length; i++)
                if (arguments[i] == Argument && !arguments[i + 1].StartsWith("-"))
                    return Normalize(arguments[i + 1]);
        return Normalize(environment ?? preference);
    }

    public static string Read()
    {
        return Resolve(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable(EnvironmentVariable),
                       UnityEngine.PlayerPrefs.GetString(PlayerPrefsKey, ""));
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("Tools/PhoneSaber/Station/A")]
    static void SetA() => Save("A");
    [UnityEditor.MenuItem("Tools/PhoneSaber/Station/B")]
    static void SetB() => Save("B");
    [UnityEditor.MenuItem("Tools/PhoneSaber/Station/None")]
    static void SetNone() => Save("");

    static void Save(string value)
    {
        UnityEngine.PlayerPrefs.SetString(PlayerPrefsKey, value);
        UnityEngine.PlayerPrefs.Save();
        UnityEngine.Debug.Log($"[PhoneSaber] Station preference={value}; effective station={Read()} (次の Play で適用)");
    }
#endif
}
