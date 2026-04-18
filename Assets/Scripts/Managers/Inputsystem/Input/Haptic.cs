using UnityEngine;
using System.Collections;

public static class Haptic
{
    static bool isInitialized = false;
    static bool isConnected = true; // 仮

    static HapticRunner runner;

    static void Init()
    {
        if (isInitialized) return;
        isInitialized = true;

        // ランナー生成
        GameObject obj = new GameObject("HapticRunner");
        Object.DontDestroyOnLoad(obj);
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
        Debug.Log("Send: " + msg);
    }
}