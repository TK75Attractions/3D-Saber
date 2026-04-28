using System;
using UnityEngine;
using System.Collections;

public static class Haptic
{
    static bool isInitialized = false;
    static bool isConnected = true; // 仮

    static HapticRunner runner;
    static Action<string> sender;

    public static void SetTransport(Action<string> transport)
    {
        sender = transport;
        isConnected = transport != null;
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