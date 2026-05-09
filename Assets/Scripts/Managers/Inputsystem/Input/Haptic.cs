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

    public static void Vibrate(float seconds)
    {
        Init();
        runner.StartCoroutine(VibrateRoutine(seconds));
    }

    static IEnumerator VibrateRoutine(float seconds)
    {
        On();
        yield return new WaitForSeconds(seconds);
        Off();
    }

    public static void On()
    {
        if (!isConnected) return;
        Send("1\n");
    }

    public static void Off()
    {
        if (!isConnected) return;
        Send("0\n");
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