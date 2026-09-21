using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// Task 2の既存payloadが、Unity受信時刻付きCamera sampleまで届くことの回帰テスト。
public class CameraTaskIntegrationPlayTests
{
    [UnityTest]
    public IEnumerator RedEndpointPayload_ReachesTask1ClockDomain()
    {
        foreach (var old in Object.FindObjectsByType<InputPoint>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            Object.DestroyImmediate(old.gameObject);
        SetInputInstance(null);

        int redPort = FreePort();
        int bluePort = FreePort();
        var inputObject = new GameObject("Task2CameraReceiver");
        inputObject.SetActive(false);
        var input = inputObject.AddComponent<InputPoint>();
        input.port = redPort;
        input.port2 = bluePort;
        input.useDirectWorldMapping = true;
        input.sensitivity = 1f;

        var saberObject = new GameObject("Task1RedSaber");
        var saber = saberObject.AddComponent<SaberInputBridge>();
        saber.stickIndex = 1;
        saber.useInputPoint = true;
        saber.fallbackToMouse = false;

        inputObject.SetActive(true);
        yield return null;

        double before = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        Send(redPort, "960,540,1200,540");
        float deadline = Time.realtimeSinceStartup + 1f;
        CameraSaberSample sample = default;
        while ((!saber.TryGetCameraSample(out sample) || input.ReceivedPacketCount == 0)
               && Time.realtimeSinceStartup < deadline)
            yield return null;
        double after = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);

        Assert.That(input.ReceivedPacketCount, Is.EqualTo(1));
        Assert.That(sample.Color, Is.EqualTo(CameraSaberColor.Red));
        Assert.That(sample.ReceiveTime, Is.InRange(before, after),
            "Camera sampleはUnity Updateの推定値ではなくUDP receive時のmonotonic clockを使う");
        Assert.That(sample.EndA.x, Is.EqualTo(0f).Within(.001f));
        Assert.That(sample.EndB.x, Is.EqualTo(1.375f).Within(.001f));

        Object.Destroy(inputObject);
        Object.Destroy(saberObject);
        yield return null;
        SetInputInstance(null);
    }

    static int FreePort()
    {
        using var socket = new UdpClient(0);
        return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
    }

    static void Send(int port, string payload)
    {
        using var sender = new UdpClient();
        byte[] bytes = Encoding.ASCII.GetBytes(payload);
        sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    static void SetInputInstance(InputPoint value)
    {
        typeof(InputPoint).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
            ?.SetValue(null, value);
    }
}
