using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

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
    {
        // テスト中は他の自動起動と同じく何も開かない(port 5007 を占有しない)。
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return false;
        lock (gate)
        {
            if (disposed) return false;
            if (running) return true;
            try
            {
                var client = new UdpClient(AddressFamily.InterNetwork);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                client.Client.ReceiveTimeout = 500;
                socket = client;
                running = true;
                byte[] reply = Encoding.ASCII.GetBytes(BuildReply(redPort, bluePort, Environment.MachineName, station));
                thread = new Thread(() => Loop(client, reply)) { IsBackground = true, Name = "PhoneSaberDiscovery" };
                thread.Start();
                UnityEngine.Debug.Log($"[PhoneSaber] Android discovery responder on UDP {Port} station={station}");
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
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                byte[] data = client.Receive(ref remote);
                if (IsRequest(data, data.Length)) client.Send(reply, reply.Length, remote);
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
