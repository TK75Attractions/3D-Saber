using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

public class PhoneSaberReceiverLifecycleTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void MissingFallbackCameraClearsPreviousBladeAndCameraSample(bool wasMouseFallback, bool destroyedCamera)
    {
        var cameras = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
        var tags = new string[cameras.Length];
        var go = new GameObject("カメラ喪失の刃テスト");
        go.SetActive(false);
        var b = go.AddComponent<SaberInputBridge>();
        var cameraObject = new GameObject("破棄されるカメラ");
        var line = b.gameObject.AddComponent<LineRenderer>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        for (int i = 0; i < cameras.Length; i++) tags[i] = cameras[i].tag;
        try
        {
            // シーン切替で対象カメラも MainCamera も失った状態を再現する。
            for (int i = 0; i < cameras.Length; i++)
            {
                if (tags[i] == "MainCamera") cameras[i].tag = "Untagged";
            }
            b.useInputPoint = false;
            b.fallbackToMouse = true;
            b.targetCamera = destroyedCamera ? cameraObject.AddComponent<Camera>() : null;
            Object.DestroyImmediate(cameraObject);
            typeof(SaberInputBridge).GetField("bladeLine", flags).SetValue(b, line);
            typeof(SaberInputBridge).GetField("hasCameraSample", flags).SetValue(b, true);
            b.OverrideBlade(Vector3.left, Vector3.right);
            b.OverrideMouseFallback(wasMouseFallback);
            line.enabled = true;
            Assert.IsNull(Camera.main);

            typeof(SaberInputBridge).GetMethod("Update", flags).Invoke(b, null);

            Assert.IsFalse(b.HasBlade, "カメラを失った後に古い刃を有効なまま残さない");
            Assert.IsFalse(line.enabled);
            Assert.IsFalse(b.UsingMouseFallback);
            Assert.IsFalse((bool)typeof(SaberInputBridge).GetField("hasCameraSample", flags).GetValue(b));

            // MainCamera が再生成されたら、次のフレームで通常のマウス入力へ戻る。
            cameraObject = new GameObject("復帰したカメラ");
            var camera = cameraObject.AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.transform.position = new Vector3(0, 0, -10);
            camera.orthographic = true;
            camera.pixelRect = new Rect(0, 0, 640, 480);
            typeof(SaberInputBridge).GetMethod("Update", flags).Invoke(b, null);
            Assert.AreSame(camera, b.targetCamera);
            Assert.IsTrue(b.HasBlade);
            Assert.IsTrue(line.enabled);
            Assert.IsTrue(b.UsingMouseFallback);
        }
        finally
        {
            for (int i = 0; i < cameras.Length; i++)
                if (cameras[i] != null) cameras[i].tag = tags[i];
            Object.DestroyImmediate(cameraObject);
            Object.DestroyImmediate(go);
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void ReceiverRestartDoesNotReusePendingOrPublishedCoordinates(bool blue, bool publishBeforeStop)
    {
        var go = new GameObject("受信再開の座標テスト");
        go.SetActive(false);
        var input = go.AddComponent<InputPoint>();
        input.useDirectWorldMapping = true;
        input.sensitivity = 1f;
        try
        {
            StartReceiver(input, blue);
            Send(input, blue, "-.5,0,.5,0");
            WaitForPacket(input, blue, 1);
            if (publishBeforeStop)
            {
                ApplyInput(input);
                Assert.IsTrue(blue ? input.HasValidStickEndpoints2 : input.HasValidStickEndpoints);
                Assert.IsTrue(blue ? input.IsRecentlyActive2() : input.IsRecentlyActive());
            }

            Invoke(input, "OnDisable");
            Assert.IsFalse(blue ? input.HasValidStickEndpoints2 : input.HasValidStickEndpoints,
                "停止した受信機の端点を使わない");
            Assert.IsFalse(blue ? input.IsRecentlyActive2() : input.IsRecentlyActive(),
                "停止した受信機を新着入力扱いしない");
            Assert.IsTrue(double.IsNegativeInfinity(blue ? input.LastReceivedMonotonicTime2 : input.LastReceivedMonotonicTime));
            Assert.IsNull(blue ? input.BlueCoordinateSender : input.RedCoordinateSender);
            var timing = blue ? input.BlueCoordinateTiming : input.RedCoordinateTiming;
            Assert.IsFalse(timing.NeedsFirstRead);
            Assert.AreEqual(0, timing.Apply.Count, "前の受信セッションの PC 遅延を持ち越さない");

            // OnDisable の二重通知と再開の間にも、未反映の旧パケットを公開しない。
            Invoke(input, "OnDisable");
            StartReceiver(input, blue);
            ApplyInput(input);
            Assert.IsFalse(blue ? input.HasValidStickEndpoints2 : input.HasValidStickEndpoints);
            Assert.IsFalse(blue ? input.IsRecentlyActive2() : input.IsRecentlyActive(),
                "送信が再開するまで古い座標を復活させない");

            Send(input, blue, "0,.5,1,.5");
            WaitForPacket(input, blue, 2);
            ApplyInput(input);
            Assert.IsTrue(blue ? input.HasValidStickEndpoints2 : input.HasValidStickEndpoints);
            Assert.IsTrue(blue ? input.IsRecentlyActive2() : input.IsRecentlyActive());
            Assert.AreEqual(new Vector2(.5f, .5f), blue ? input.LastRaw2 : input.LastRaw);
            Assert.AreEqual("127.0.0.1", blue ? input.BlueCoordinateSender : input.RedCoordinateSender);
        }
        finally { Invoke(input, "OnDisable"); Object.DestroyImmediate(go); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ShutdownWhilePacketWaitsForMainThreadDoesNotPublishIt(bool blue)
    {
        var go = new GameObject("受信停止の競合テスト");
        go.SetActive(false);
        var input = go.AddComponent<InputPoint>();
        try
        {
            StartReceiver(input, blue);
            lock (Field<object>(input, blue ? "lockObj2" : "lockObj"))
            {
                Send(input, blue, "-.5,0,.5,0");
                Assert.IsTrue(SpinWait.SpinUntil(() => input.ReadInputStats(blue).PayloadParsed, 2000),
                    "受信と解析を終え、座標の lock を待っている");
                // StopNetworkServices が受信後・公開前に停止を通知する順序を再現する。
                Set(input, "networkShutdown", true);
            }
            Assert.IsTrue(Field<Thread>(input, blue ? "receiveThread2" : "receiveThread1").Join(2000));
            Assert.AreEqual(0, blue ? input.ReceivedPacketCount2 : input.ReceivedPacketCount,
                "停止通知後に座標を新着扱いしない");
            Assert.IsFalse(Field<bool>(input, blue ? "hasNewData2" : "hasNewData"));
        }
        finally { Invoke(input, "OnDisable"); Object.DestroyImmediate(go); }
    }

    static void StartReceiver(InputPoint input, bool blue)
    {
        // 片色だけ一時 port で実 supervisor を動かし、Bonjour・5007・外部 process は起動しない。
        input.port = input.port2 = 0;
        Set(input, "networkShutdown", false);
        Set(input, blue ? "receiverStopSignal2" : "receiverStopSignal1", new ManualResetEvent(false));
        var supervisor = typeof(InputPoint).GetMethod("ReceiverSupervisor", BindingFlags.Instance | BindingFlags.NonPublic);
        var thread = new Thread(() => supervisor.Invoke(input, new object[] { blue })) { IsBackground = true };
        Set(input, blue ? "receiveThread2" : "receiveThread1", thread);
        thread.Start();
        Assert.IsTrue(SpinWait.SpinUntil(() => blue ? input.ReceiverAlive2 : input.ReceiverAlive, 2000),
            "一時 port の受信開始が完了する");
    }

    static void Send(InputPoint input, bool blue, string payload)
    {
        var client = Field<UdpClient>(input, blue ? "udpClient2" : "udpClient1");
        int port = ((IPEndPoint)client.Client.LocalEndPoint).Port;
        byte[] data = Encoding.ASCII.GetBytes(payload);
        using (var sender = new UdpClient())
            sender.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    static void WaitForPacket(InputPoint input, bool blue, int count)
        => Assert.IsTrue(SpinWait.SpinUntil(() =>
            (blue ? input.ReceivedPacketCount2 : input.ReceivedPacketCount) >= count, 2000),
            "UDP パケットが受理される");

    static void ApplyInput(InputPoint input)
        => typeof(InputPoint).GetMethod("ApplyLatestInput", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(input, new object[] { false });

    [Test]
    public void DisablingOverlayCancelsCaptureAndClearsProgressBeforeReenable()
    {
        var go = new GameObject("採取キャンセルテスト");
        var input = go.AddComponent<InputPoint>();
        var overlay = go.AddComponent<PhoneSaberOperatorOverlay>();
        try
        {
            Set(overlay, "calibrationInput", input);
            Set(overlay, "calibrationCorner", 2);
            Set(overlay, "captureStarted", 0.0);
            input.PositionCapture.Begin(false, 0);
            for (int i = 0; i < 30; i++)
                input.PositionCapture.Add(false, Vector2.zero, Vector2.right * 100, i / 30.0);
            Invoke(overlay, "OnDisable");
            Assert.AreEqual(-1, Field<int>(overlay, "calibrationCorner"));
            Assert.AreEqual(-1, Field<double>(overlay, "captureStarted"));
            Assert.IsNull(Field<InputPoint>(overlay, "calibrationInput"));
            Invoke(overlay, "OnEnable");
            input.PositionCapture.Add(false, Vector2.zero, Vector2.right * 100, 1.0);
            Assert.IsFalse(input.PositionCapture.Finish(out _, out _), "再有効化で古い採取結果を処理しない");
        }
        finally { Object.DestroyImmediate(go); }
    }

    // 2026-10-13: F9 は maxQueuedFrames を切り替えなくなった（画面条件を1ブロック内で固定し、
    // 画面の遅延を条件ごとに保存するため）。終了・破棄で値を書き換えないことと、状態が閉じることを確かめる。
    [TestCase("Finish", 2)]
    [TestCase("OnDisable", 3)]
    [TestCase("OnDestroy", 4)]
    public void ProbeCompletionLeavesQueuedFramesAloneAndClosesTheProbe(string completion, int queued)
    {
        int saved = QualitySettings.maxQueuedFrames;
        var go = new GameObject("キュー設定テスト");
        var probe = go.AddComponent<PhoneSaberLatencyProbe>();
        try
        {
            QualitySettings.maxQueuedFrames = queued;
            int expected = QualitySettings.maxQueuedFrames;
            Set(probe, "active", true);
            Invoke(probe, completion);
            Assert.AreEqual(expected, QualitySettings.maxQueuedFrames);
            Assert.IsFalse(Field<bool>(probe, "active"));
            Invoke(probe, "OnDisable");
            Assert.AreEqual(expected, QualitySettings.maxQueuedFrames, "二重の終了通知でも変えない");
        }
        finally { Object.DestroyImmediate(go); QualitySettings.maxQueuedFrames = saved; }
    }

    static void Invoke(object owner, string name)
    {
        // 旧コードに OnDestroy がない場合も、Unity と同じく何もしない。
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(owner, null);
    }
    static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
    static void Set(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, value);
}
