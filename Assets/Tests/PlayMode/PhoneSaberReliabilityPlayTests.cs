using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class PhoneSaberReliabilityPlayTests
{
    InputPoint input;
    GameObject inputObject;
    int redPort;
    int bluePort;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        Time.timeScale = 1f;
        foreach (var old in Object.FindObjectsByType<InputPoint>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            Object.DestroyImmediate(old.gameObject);
        SetInputInstance(null);

        redPort = FreePort();
        do { bluePort = FreePort(); } while (bluePort == redPort);
        inputObject = new GameObject("PhoneSaberReliabilityReceiver");
        inputObject.SetActive(false);
        input = inputObject.AddComponent<InputPoint>();
        input.port = redPort;
        input.port2 = bluePort;
        input.useDirectWorldMapping = true;
        input.sensitivity = 1f;
        inputObject.SetActive(true);

        yield return WaitUntil(() => input.ReceiverAlive && input.ReceiverAlive2, 2.0);
        Assert.IsTrue(input.ReceiverAlive, "RED receiverがbind済み");
        Assert.IsTrue(input.ReceiverAlive2, "BLUE receiverがbind済み");
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        Time.timeScale = 1f;
        if (inputObject != null) Object.Destroy(inputObject);
        yield return null;
        SetInputInstance(null);
    }

    [UnityTest]
    public IEnumerator Freshness_ExpiresWhileTimeScaleIsZero()
    {
        Send(redPort, "0,0,1,0");
        yield return WaitUntil(() => input.ReceivedPacketCount == 1 && input.IsRecentlyActive(), 1.0);
        Assert.IsTrue(input.IsRecentlyActive(.1));

        Time.timeScale = 0f;
        yield return new WaitForSecondsRealtime(.15f);

        Assert.IsFalse(input.IsRecentlyActive(.1),
            "stale判定はscaled game timeではなくreal timeで進む");
    }

    [UnityTest]
    public IEnumerator FourThenTwoElements_InvalidatesOldBladeEndpoints()
    {
        var saberObject = new GameObject("EndpointValiditySaber");
        var saber = saberObject.AddComponent<SaberInputBridge>();
        saber.useInputPoint = true;
        saber.useBladeMode = true;
        saber.fallbackToMouse = false;
        saber.stickIndex = 1;

        Send(redPort, "-1,0,1,0");
        yield return WaitUntil(() => input.ReceivedPacketCount == 1 &&
                                     input.HasValidStickEndpoints && saber.HasBlade, 1.0);
        Assert.IsTrue(input.HasValidStickEndpoints, "4要素packetは端点を有効化する");
        Assert.IsTrue(saber.HasBlade, "4要素packetは従来通りblade表示・判定へ届く");
        Assert.That(saber.WorldEndA.x, Is.EqualTo(-5.5f).Within(.001f));
        Assert.That(saber.WorldEndB.x, Is.EqualTo(5.5f).Within(.001f));

        Send(redPort, ".25,-.25");
        yield return WaitUntil(() => input.ReceivedPacketCount == 2 &&
                                     !input.HasValidStickEndpoints && !saber.HasBlade, 1.0);
        Assert.IsFalse(input.HasValidStickEndpoints, "2要素packetは古い端点を無効化する");
        Assert.IsFalse(saber.HasBlade, "無効な古い端点をbladeへ再利用しない");

        Object.Destroy(saberObject);
    }

    [UnityTest]
    public IEnumerator AbnormalReceiverExit_RebindsAndReceivesAgain()
    {
        int initialRestartCount = input.ReceiverRestartCount;
        CloseReceiverSocket("udpClient1");

        yield return WaitUntil(() => input.ReceiverRestartCount > initialRestartCount &&
                                     input.ReceiverAlive, 2.0);
        Assert.Greater(input.ReceiverRestartCount, initialRestartCount);
        Assert.IsTrue(input.ReceiverAlive);
        Assert.That(input.LastReceiverExitReason, Does.Contain("Exception"));

        Send(redPort, "0,0,1,0");
        yield return WaitUntil(() => input.ReceivedPacketCount == 1, 1.0);
        Assert.AreEqual(1, input.ReceivedPacketCount, "rebind後にRED packetを受信できる");
    }

    [UnityTest]
    public IEnumerator IntentionalShutdown_DoesNotRestart()
    {
        int redRestarts = input.ReceiverRestartCount;
        int blueRestarts = input.ReceiverRestartCount2;

        inputObject.SetActive(false);
        yield return new WaitForSecondsRealtime(.35f);

        Assert.IsFalse(input.ReceiverAlive);
        Assert.IsFalse(input.ReceiverAlive2);
        Assert.AreEqual(redRestarts, input.ReceiverRestartCount);
        Assert.AreEqual(blueRestarts, input.ReceiverRestartCount2);
        Assert.AreEqual("intentional shutdown", input.LastReceiverExitReason);
        Assert.AreEqual("intentional shutdown", input.LastReceiverExitReason2);
    }

    [UnityTest]
    public IEnumerator Destroy_DoesNotRestartReceiver()
    {
        int redRestarts = input.ReceiverRestartCount;
        int blueRestarts = input.ReceiverRestartCount2;
        var destroyedInput = input;

        Object.Destroy(inputObject);
        inputObject = null;
        yield return new WaitForSecondsRealtime(.35f);

        Assert.AreEqual(redRestarts, destroyedInput.ReceiverRestartCount);
        Assert.AreEqual(blueRestarts, destroyedInput.ReceiverRestartCount2);
        Assert.AreEqual("intentional shutdown", destroyedInput.LastReceiverExitReason);
        Assert.AreEqual("intentional shutdown", destroyedInput.LastReceiverExitReason2);
    }

    [UnityTest]
    public IEnumerator QuickDisableEnable_JoinsOldThreadsBeforeStartingNewOnes()
    {
        var oldRed = GetReceiverThread("receiveThread1");
        var oldBlue = GetReceiverThread("receiveThread2");
        Assert.IsTrue(oldRed.IsAlive);
        Assert.IsTrue(oldBlue.IsAlive);

        inputObject.SetActive(false);
        Assert.IsFalse(oldRed.IsAlive, "Disable後に旧RED threadが終了している");
        Assert.IsFalse(oldBlue.IsAlive, "Disable後に旧BLUE threadが終了している");

        inputObject.SetActive(true);
        yield return WaitUntil(() => input.ReceiverAlive && input.ReceiverAlive2, 2.0);

        var newRed = GetReceiverThread("receiveThread1");
        var newBlue = GetReceiverThread("receiveThread2");
        Assert.AreNotSame(oldRed, newRed);
        Assert.AreNotSame(oldBlue, newBlue);
        Assert.IsFalse(oldRed.IsAlive, "再Enable後も旧RED threadは終了済み");
        Assert.IsFalse(oldBlue.IsAlive, "再Enable後も旧BLUE threadは終了済み");
        Assert.IsTrue(newRed.IsAlive);
        Assert.IsTrue(newBlue.IsAlive);
    }

    [UnityTest]
    public IEnumerator RedFailure_DoesNotStopBlueReceiver()
    {
        int initialBlueRestarts = input.ReceiverRestartCount2;
        CloseReceiverSocket("udpClient1");

        Assert.IsTrue(input.ReceiverAlive2, "RED socket close直後もBLUE receiverはalive");
        Send(bluePort, "0,0,1,0");
        yield return WaitUntil(() => input.ReceivedPacketCount2 == 1, 1.0);

        Assert.AreEqual(1, input.ReceivedPacketCount2);
        Assert.IsTrue(input.ReceiverAlive2);
        Assert.AreEqual(initialBlueRestarts, input.ReceiverRestartCount2,
            "RED異常でBLUEをrestartしない");
    }

    void CloseReceiverSocket(string fieldName)
    {
        var field = typeof(InputPoint).GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var socket = field.GetValue(input) as UdpClient;
        Assert.NotNull(socket, $"{fieldName}がbind済み");
        socket.Close();
    }

    Thread GetReceiverThread(string fieldName)
    {
        var field = typeof(InputPoint).GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var thread = field.GetValue(input) as Thread;
        Assert.NotNull(thread);
        return thread;
    }

    static IEnumerator WaitUntil(System.Func<bool> predicate, double timeoutSeconds)
    {
        double deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while (!predicate() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
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
