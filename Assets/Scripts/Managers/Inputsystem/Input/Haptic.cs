using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using System.Collections;

public static class Haptic
{
    static bool isInitialized = false;
    static bool isConnected = true; // 仮

    static HapticRunner runner;
    static Action<string> sender;
    static UdpClient udpClient;
    static IPEndPoint udpEndpoint;

    public static void SetTransport(Action<string> transport)
    {
        sender = transport;
        isConnected = transport != null;
    }

    // Set a simple UDP transport so Haptic can send commands directly to a bridge/port.
    public static void SetUdpTransport(string host = "127.0.0.1", int port = 9001)
    {
        try
        {
            udpClient?.Close();
            udpClient = new UdpClient();
            udpEndpoint = new IPEndPoint(IPAddress.Parse(host), port);
            sender = (string msg) =>
            {
                try
                {
                    var trimmed = (msg ?? "").Trim();
                    var outMsg = "H:" + trimmed;
                    byte[] data = System.Text.Encoding.UTF8.GetBytes(outMsg);
                    udpClient.Send(data, data.Length, udpEndpoint);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("Haptic UDP send failed: " + ex.Message);
                }
            };
            isConnected = true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Failed to set UDP transport: " + ex.Message);
            isConnected = false;
        }
    }

    public static void ClearTransport()
    {
        sender = null;
        isConnected = false;
        try { udpClient?.Close(); } catch { }
        udpClient = null;
        udpEndpoint = null;
    }

    static void Init()
    {
        if (isInitialized) return;
        isInitialized = true;

        // ランナー生成
        GameObject obj = new GameObject("HapticRunner");
        UnityEngine.Object.DontDestroyOnLoad(obj);
        runner = obj.AddComponent<HapticRunner>();

        // BLE初期化
    }

    // 手識別付きコマンド("1,L" 等)を送るか。既定 false = 従来どおり "1"/"0" のみ
    // (トラッカー側が2台構成・手識別に対応したら true にする。オプトインなので後方互換)。
    public static bool handAwareCommands = false;

    public static void Vibrate(float seconds)
    {
        Vibrate(seconds, SaberHand.Any);
    }

    // hand: どの手(デバイス)を振動させるか。handAwareCommands=false か Any なら従来コマンド。
    public static void Vibrate(float seconds, SaberHand hand)
    {
        Init();
        runner.StartCoroutine(VibrateRoutine(seconds, hand));
    }

    static IEnumerator VibrateRoutine(float seconds, SaberHand hand)
    {
        On(hand);
        yield return new WaitForSeconds(seconds);
        Off(hand);
    }

    public static void On() { On(SaberHand.Any); }
    public static void Off() { Off(SaberHand.Any); }

    public static void On(SaberHand hand)
    {
        if (!isConnected) return;
        Send(Command("1", hand));
    }

    public static void Off(SaberHand hand)
    {
        if (!isConnected) return;
        Send(Command("0", hand));
    }

    // コマンド文字列の組み立て(純粋関数・テスト用に公開)。
    // handAwareCommands が有効かつ手が確定しているときだけ ",L"/",R" を付ける。
    public static string Command(string baseCommand, SaberHand hand)
    {
        if (!handAwareCommands || hand == SaberHand.Any) return baseCommand + "\n";
        return baseCommand + (hand == SaberHand.Left ? ",L" : ",R") + "\n";
    }

    static void Send(string msg)
    {
        if (!isConnected || sender == null)
        {
            Debug.LogWarning("Haptic transport is not connected: " + msg.Trim());
            return;
        }

        sender(msg);
    }
}