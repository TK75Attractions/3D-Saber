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

    public static string BuildReply(int redPort, int bluePort, string machineName)
    {
        string name = string.IsNullOrWhiteSpace(machineName) ? "Unity" : machineName.Replace(' ', '_');
        return $"{ReplyPrefix} red={redPort} blue={bluePort} name={name}";
    }

    public static bool IsRequest(byte[] data, int length)
    {
        if (data == null || length <= 0 || length > 64) return false;
        return Encoding.ASCII.GetString(data, 0, length).Trim() == Request;
    }

    public bool Start(int redPort, int bluePort)
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
                byte[] reply = Encoding.ASCII.GetBytes(BuildReply(redPort, bluePort, Environment.MachineName));
                thread = new Thread(() => Loop(client, reply)) { IsBackground = true, Name = "PhoneSaberDiscovery" };
                thread.Start();
                UnityEngine.Debug.Log($"[PhoneSaber] Android discovery responder on UDP {Port}");
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
