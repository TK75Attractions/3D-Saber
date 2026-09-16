using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class UdpSwingEventPlayTests
{
    [UnityTest]
    public IEnumerator UdpReceive_InvokesEventOnUnityMainThread()
    {
        foreach (var existing in Object.FindObjectsByType<UdpImuBridge>(
                     FindObjectsInactive.Include,
                     FindObjectsSortMode.None))
        {
            Object.DestroyImmediate(existing.gameObject);
        }

        int port = FindFreeUdpPort();
        int mainThreadId = Thread.CurrentThread.ManagedThreadId;
        var go = new GameObject("UdpSwingEventPlayTest");
        var bridge = go.AddComponent<UdpImuBridge>();
        bridge.ConfigureForTests(port, 0.15f);
        go.AddComponent<Swing8DirectionLogger>();
        bool callbackReceived = false;
        SwingEvent callbackEvent = default;
        int callbackThreadId = -1;
        bridge.OnSwingReceived += swing =>
        {
            callbackEvent = swing;
            callbackThreadId = Thread.CurrentThread.ManagedThreadId;
            callbackReceived = true;
        };

        yield return null; // Start opens the socket.

        using (var udp = new UdpClient())
        {
            long senderNs = (long)SwingMonotonicClock.ToNanoseconds(SwingMonotonicClock.Timestamp);
            byte[] packet = Encoding.ASCII.GetBytes($"SWING:7,right,0.8,1234,{senderNs}");
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
        }

        float timeout = Time.realtimeSinceStartup + 1f;
        while (!callbackReceived && Time.realtimeSinceStartup < timeout)
        {
            yield return null;
        }

        Assert.IsTrue(callbackReceived, "UDP Swing event callback timed out");
        Assert.AreEqual(mainThreadId, callbackThreadId);
        Assert.AreEqual((ushort)7, callbackEvent.Sequence);
        Assert.AreEqual(SwingDirection.Right, callbackEvent.Direction);
        Assert.GreaterOrEqual(callbackEvent.LocalhostTransportLatencyMs, 0.0);
        Assert.GreaterOrEqual(callbackEvent.MainThreadHandoffLatencyMs, 0.0);
        Assert.IsTrue(Swing8DirectionLogger.TryGetRecent(0.30, out CutDirection direction));
        Assert.AreEqual(CutDirection.Right, direction);
        Debug.Log(
            $"[SwingTest] localhost UDP={callbackEvent.LocalhostTransportLatencyMs:F3}ms " +
            $"main-handoff={callbackEvent.MainThreadHandoffLatencyMs:F3}ms");

        int resets = 0;
        bridge.OnSwingSessionReset += _ => resets++;
        SendPacket(port, "STATE:DISCONNECTED");
        timeout = Time.realtimeSinceStartup + 1f;
        while (resets == 0 && Time.realtimeSinceStartup < timeout) yield return null;
        Assert.Greater(resets, 0, "切断通知がmain threadへ届くこと");
        Assert.IsFalse(Swing8DirectionLogger.TryGetRecent(0.30, out _));

        callbackReceived = false;
        SendPacket(port, "STATE:CONNECTED");
        SendPacket(port, "SWING:0,up,0.8,1235");
        timeout = Time.realtimeSinceStartup + 1f;
        while (!callbackReceived && Time.realtimeSinceStartup < timeout) yield return null;
        Assert.IsTrue(callbackReceived, "再接続後の新しい振りを受理すること");
        Assert.IsTrue(Swing8DirectionLogger.TryGetRecent(0.30, out direction));
        Assert.AreEqual(CutDirection.Up, direction);

        // 実際のUnityライフサイクルでも前のヒントを持ち越さない。
        bridge.enabled = false;
        Assert.IsFalse(Swing8DirectionLogger.TryGetRecent(0.30, out _));
        bridge.enabled = true;
        Assert.IsFalse(Swing8DirectionLogger.TryGetRecent(0.30, out _));

        Object.Destroy(go);
        yield return null;
    }

    private static int FindFreeUdpPort()
    {
        using var socket = new UdpClient(0);
        return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
    }

    private static void SendPacket(int port, string message)
    {
        using var sender = new UdpClient();
        byte[] packet = Encoding.ASCII.GetBytes(message);
        sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }
}
