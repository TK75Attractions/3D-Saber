using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class UdpSwingEventPlayTests
{
    [UnityTest]
    public IEnumerator PersistentBridge_IsSingletonAndReusesExistingTransport()
    {
        foreach (var existing in Object.FindObjectsByType<UdpImuBridge>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            Object.DestroyImmediate(existing.gameObject);

        int port = FindFreeUdpPort();
        var go = new GameObject("PersistentUdpImuBridgeTest");
        go.SetActive(false);
        var bridge = go.AddComponent<UdpImuBridge>();
        bridge.ConfigureForTests(port);
        go.SetActive(true);
        yield return null;

        Assert.AreSame(bridge, UdpImuBridge.EnsurePersistent(false));
        Assert.AreSame(bridge, UdpImuBridge.EnsurePersistent(false));
        Assert.IsFalse(bridge.OwnsBleBridgeProcess,
            "Auto Start OFFではUnityがbridge processを起動しない");

        var duplicate = new GameObject("DuplicateUdpImuBridgeTest");
        duplicate.AddComponent<UdpImuBridge>();
        yield return null;
        Assert.AreEqual(1, Object.FindObjectsByType<UdpImuBridge>(
            FindObjectsInactive.Include, FindObjectsSortMode.None).Length);

        Object.Destroy(bridge.gameObject);
        yield return null;
    }

    [UnityTest]
    public IEnumerator BridgeLauncher_RepeatedStartStopOwnsOnlyItsChild()
    {
        if (Application.platform != RuntimePlatform.OSXEditor)
        {
            Assert.Ignore("Project-local Bleak environment is prepared for the macOS festival host.");
        }

        int commandPort;
        int dataPort;
        using (var first = new UdpClient(0))
        using (var second = new UdpClient(0))
        {
            commandPort = ((IPEndPoint)first.Client.LocalEndPoint).Port;
            dataPort = ((IPEndPoint)second.Client.LocalEndPoint).Port;
        }
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;

        // Play -> Stop -> Playを繰り返す条件に合わせ、3回とも子processを再利用/終了できることを確認する。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var receiver = new UdpClient(dataPort);
            var launcher = new ImuBleBridgeLauncher();
            try
            {
                Assert.IsTrue(launcher.TryStart("python3", projectRoot, commandPort, dataPort), launcher.LastError);
                Assert.IsTrue(launcher.OwnsRunningProcess);
                // 同じlauncherへの再起動要求は既存の子を再利用する。
                Assert.IsTrue(launcher.TryStart("python3", projectRoot, commandPort, dataPort));

                bool ready = false;
                float timeout = Time.realtimeSinceStartup + 5f;
                float nextPing = 0f;
                using var command = new UdpClient();
                while (!ready && Time.realtimeSinceStartup < timeout)
                {
                    if (Time.realtimeSinceStartup >= nextPing)
                    {
                        byte[] ping = Encoding.ASCII.GetBytes("PING");
                        command.Send(ping, ping.Length, new IPEndPoint(IPAddress.Loopback, commandPort));
                        nextPing = Time.realtimeSinceStartup + 0.1f;
                    }
                    while (receiver.Available > 0)
                    {
                        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        string state = Encoding.UTF8.GetString(receiver.Receive(ref remote));
                        if (state == "STATE:BRIDGE_READY") ready = true;
                    }
                    if (!ready) yield return null;
                }
                Assert.IsTrue(ready, "Unityが起動したbridgeがPING応答すること");
            }
            finally
            {
                launcher.Dispose();
            }
            Assert.IsFalse(launcher.OwnsRunningProcess);
            yield return null;
        }
    }

    [UnityTest]
    public IEnumerator UdpReceive_TracksBleConnectionAndReconnectStates()
    {
        foreach (var existing in Object.FindObjectsByType<UdpImuBridge>(
                     FindObjectsInactive.Include,
                     FindObjectsSortMode.None))
        {
            Object.DestroyImmediate(existing.gameObject);
        }

        int port = FindFreeUdpPort();
        var go = new GameObject("UdpSwingBleStatusTest");
        var bridge = go.AddComponent<UdpImuBridge>();
        bridge.ConfigureForTests(port, 0.15f);
        yield return null;

        SendPacket(port, "STATE:BRIDGE_READY");
        SendPacket(port, "STATE:BLE:CONNECTED:XIAO-LSM6DSV16X");
        SendPacket(port, "STATE:BLE:NOTIFICATIONS_ACTIVE:XIAO-LSM6DSV16X");
        float stateTimeout = Time.realtimeSinceStartup + 1f;
        while (bridge.BleState != BleBridgeConnectionState.NotificationsActive &&
               Time.realtimeSinceStartup < stateTimeout) yield return null;
        Assert.IsTrue(bridge.IsBridgeProcessReady);
        Assert.AreEqual(BleBridgeConnectionState.NotificationsActive, bridge.BleState);
        Assert.AreEqual("XIAO-LSM6DSV16X", bridge.BleDevice);
        Assert.IsTrue(bridge.IsBridgeConnected);

        SendPacket(port, "STATE:BLE:DISCONNECTED:XIAO-LSM6DSV16X");
        stateTimeout = Time.realtimeSinceStartup + 1f;
        while (bridge.BleState != BleBridgeConnectionState.Disconnected &&
               Time.realtimeSinceStartup < stateTimeout) yield return null;
        Assert.AreEqual(BleBridgeConnectionState.Disconnected, bridge.BleState);
        Assert.IsFalse(bridge.IsBridgeConnected);

        Object.Destroy(go);
        yield return null;
    }

    [UnityTest]
    public IEnumerator UdpReceive_TracksLeftAndRightConnectionStatesIndependently()
    {
        foreach (var existing in Object.FindObjectsByType<UdpImuBridge>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            Object.DestroyImmediate(existing.gameObject);

        int port = FindFreeUdpPort();
        var go = new GameObject("UdpSwingSideStatusTest");
        var bridge = go.AddComponent<UdpImuBridge>();
        bridge.ConfigureForTests(port);
        yield return null;

        SendPacket(port, "STATE:BLE:LEFT:NOTIFICATIONS_ACTIVE:XIAO-SABER-L");
        SendPacket(port, "STATE:BLE:RIGHT:CONNECTED:XIAO-SABER-R");
        float timeout = Time.realtimeSinceStartup + 1f;
        while ((bridge.LeftBleState != BleBridgeConnectionState.NotificationsActive ||
                bridge.RightBleState != BleBridgeConnectionState.Connected) &&
               Time.realtimeSinceStartup < timeout) yield return null;
        Assert.AreEqual(BleBridgeConnectionState.NotificationsActive, bridge.LeftBleState);
        Assert.AreEqual(BleBridgeConnectionState.Connected, bridge.RightBleState);
        Assert.AreEqual("XIAO-SABER-L", bridge.LeftBleDevice);
        Assert.AreEqual("XIAO-SABER-R", bridge.RightBleDevice);
        Assert.IsTrue(bridge.IsBridgeConnected);

        SendPacket(port, "STATE:BLE:RIGHT:DISCONNECTED:XIAO-SABER-R");
        timeout = Time.realtimeSinceStartup + 1f;
        while (bridge.RightBleState != BleBridgeConnectionState.Disconnected &&
               Time.realtimeSinceStartup < timeout) yield return null;
        Assert.AreEqual(BleBridgeConnectionState.Disconnected, bridge.RightBleState);
        Assert.AreEqual(BleBridgeConnectionState.NotificationsActive, bridge.LeftBleState);
        Assert.IsTrue(bridge.IsBridgeConnected, "Right切断中もLeft接続を維持する");

        Object.Destroy(go);
        yield return null;
    }

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

    [UnityTest]
    public IEnumerator UdpReceive_AcceptsIndependentLeftAndRightSequences()
    {
        foreach (var existing in Object.FindObjectsByType<UdpImuBridge>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            Object.DestroyImmediate(existing.gameObject);

        int port = FindFreeUdpPort();
        var go = new GameObject("UdpSwingSideTest");
        var bridge = go.AddComponent<UdpImuBridge>();
        bridge.ConfigureForTests(port);
        int left = 0;
        int right = 0;
        bridge.OnLeftSwingReceived += _ => left++;
        bridge.OnRightSwingReceived += _ => right++;
        yield return null;
        SendPacket(port, "SWING:LEFT,0,unknown,0.8,1");
        SendPacket(port, "SWING:RIGHT,0,unknown,0.8,1");
        float timeout = Time.realtimeSinceStartup + 1f;
        while ((left == 0 || right == 0) && Time.realtimeSinceStartup < timeout) yield return null;
        Assert.AreEqual(1, left);
        Assert.AreEqual(1, right);
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
