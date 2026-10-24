using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 5007 を占有せず、bind 失敗後の socket 解放を確認する。
public class PhoneSaberDiscoveryResponderTests
{
    [TestCase(1)]
    [TestCase(10)]
    [TestCase(60)]
    public void LearningRetriesPastCompletionWithTheSameIdAndStopsWithinIPhoneCache(int seconds)
    {
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(71, "learn-area=" + seconds, 100);
        Assert.AreEqual(seconds + 5, store.CommandSecondsLeft(100));
        Assert.Greater(store.CommandSecondsLeft(100 + seconds + .001), 0);
        Assert.AreEqual(0, store.CommandSecondsLeft(100 + seconds + 5));
        Assert.AreEqual(0, store.CommandSecondsLeft(100 + seconds + 10));
        CollectionAssert.AreEqual(new[] { PhoneSaberDiscoveryResponder.BuildCommand(71, "learn-area=" + seconds) },
            Commands(store, 100 + seconds + .001));
        Assert.IsEmpty(Commands(store, 100 + seconds + 5));
    }

    static string[] Commands(PhoneSaberAcknowledgementStore store, double now, string sender = "phone") =>
        store.PendingCommands(now, sender).Select(Encoding.ASCII.GetString).ToArray();

    [TestCase("A")]
    [TestCase("B")]
    public void RapidDifferentOperationsRetryIndependentlyAndCollectOutOfOrderAcks(string station)
    {
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(71, "shutter-iso=160", 100);
        store.Queue(72, "shutter-emitter=1", 100.01);
        var expected = new[] { PhoneSaberDiscoveryResponder.BuildCommand(71, "shutter-iso=160"),
            PhoneSaberDiscoveryResponder.BuildCommand(72, "shutter-emitter=1") };
        CollectionAssert.AreEqual(expected, Commands(store, 101));
        CollectionAssert.AreEqual(expected, Commands(store, 102), "ACK紛失時は両方を同じIDで再送する");
        Assert.IsFalse(store.Receive(new PhoneSaberAcknowledgement(99, station, "ios", "shutter-iso", "160"), "phone"));
        Assert.IsTrue(store.Receive(new PhoneSaberAcknowledgement(72, station, "ios", "shutter-emitter", "on"), "phone"));
        CollectionAssert.AreEqual(new[] { expected[0] }, Commands(store, 103));
        Assert.IsTrue(store.Receive(new PhoneSaberAcknowledgement(71, station, "ios", "shutter-iso", "160"), "phone"));
        Assert.IsEmpty(Commands(store, 104));
        Assert.AreEqual("shutter-iso", store.Read(71).Single().Result);
        Assert.AreEqual("shutter-emitter", store.Read(72).Single().Result);
        CollectionAssert.AreEqual(expected, Commands(store, 104, "other-phone"), "別端末のACKで再送を止めない");
        Assert.IsEmpty(Commands(store, 110.01, "other-phone"));
    }

    [TestCase("learned", "samples300-area0.2_0.1_0.8_0.9")]
    [TestCase("failed", "samples0")]
    public void LearningAndAnotherCommandKeepTheirOwnDeadlineAndFinalAck(string result, string detail)
    {
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(71, "learn-area=10", 100);
        store.Receive(new PhoneSaberAcknowledgement(71, "A", "ios", "learning", "10s"), "phone");
        store.Queue(72, "shutter-iso=160", 100.01);
        Assert.AreEqual(2, Commands(store, 101).Length);
        store.Receive(new PhoneSaberAcknowledgement(72, "A", "ios", "shutter-iso", "160"), "phone");
        var learning = new[] { PhoneSaberDiscoveryResponder.BuildCommand(71, "learn-area=10") };
        CollectionAssert.AreEqual(learning, Commands(store, 110.5), "別操作の期限後も学習完了ACKを回収する");
        CollectionAssert.AreEqual(learning, Commands(store, 114.999));
        store.Receive(new PhoneSaberAcknowledgement(71, "A", "ios", result, detail), "phone");
        store.Receive(new PhoneSaberAcknowledgement(71, "A", "ios", "learning", "10s"), "phone");
        Assert.IsEmpty(Commands(store, 114.999));
        Assert.AreEqual(result, store.Read(71).Single().Result);
        Assert.IsEmpty(Commands(store, 115, "other-phone"));
        Assert.AreEqual(0, store.CommandSecondsLeft(115));
    }

    [Test]
    public void FullQueuePreservesUnacknowledgedCommandsAndReusesExpiredSlots()
    {
        var store = new PhoneSaberAcknowledgementStore();
        // 先頭の長い学習を残し、後続の短い指示が期限切れになる場合も確認する。
        store.Queue(1, "learn-area=60", 100);
        for (int id = 2; id <= PhoneSaberAcknowledgementStore.MaximumCommands; id++)
            store.Queue(id, "shutter-iso=160", 100);
        var full = Commands(store, 101);
        Assert.AreEqual(PhoneSaberAcknowledgementStore.MaximumCommands, full.Length);
        Assert.Throws<InvalidOperationException>(() => store.Queue(33, "shutter-emitter=1", 101));
        CollectionAssert.AreEqual(full, Commands(store, 101));
        store.Queue(33, "shutter-emitter=1", 110);
        CollectionAssert.AreEqual(new[] { PhoneSaberDiscoveryResponder.BuildCommand(1, "learn-area=60"),
            PhoneSaberDiscoveryResponder.BuildCommand(33, "shutter-emitter=1") }, Commands(store, 110));
        Assert.AreEqual(55, store.CommandSecondsLeft(110));
        Assert.IsTrue(store.Receive(new PhoneSaberAcknowledgement(1, "A", "ios", "learned", "samples300"), "phone"));
        Assert.AreEqual(1, Commands(store, 110).Length);
    }

    [Test]
    public void DefaultsCommandAndNewSenderRule()
    {
        Assert.AreEqual("PHONESABER_COMMAND 1 id=81 defaults=1",
            PhoneSaberDiscoveryResponder.BuildCommand(81, PhoneSaberDiscoveryResponder.DefaultsCommand));
        var seen = new Dictionary<string, double> { ["10.0.0.1"] = 100 };
        Assert.IsTrue(PhoneSaberDiscoveryResponder.ShouldQueueDefaults(seen, "10.0.0.2", 101, 0));
        Assert.IsFalse(PhoneSaberDiscoveryResponder.ShouldQueueDefaults(seen, "10.0.0.1", 159.999, 0));
        Assert.IsTrue(PhoneSaberDiscoveryResponder.ShouldQueueDefaults(seen, "10.0.0.1", 160, 0));
        Assert.IsFalse(PhoneSaberDiscoveryResponder.ShouldQueueDefaults(seen, "10.0.0.2", 101, 0.001));
        Assert.IsFalse(PhoneSaberDiscoveryResponder.ShouldQueueDefaults(seen, "10.0.0.1", 200, 10));
        Assert.AreEqual(1, seen.Count, "判定だけでは最終探索時刻を更新しない");
    }

    [Test]
    public void RetiredLoopExitsWhileReplacementIsRunning()
    {
        bool savedAutoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        using (var responder = new PhoneSaberDiscoveryResponder())
        using (var retired = new UdpClient(0))
        using (var phone = new UdpClient(0))
        {
            Thread oldThread = null;
            try
            {
                PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
                UdpClient replacement = null;
                Assert.IsTrue(responder.Start(5005, 5006,
                    () => replacement = new UdpClient(AddressFamily.InterNetwork), "A", 0));
                retired.Close();
                // Stop の Join が時間切れになり、旧 Loop の再開前に Start が running を true に戻した状態。
                var loop = typeof(PhoneSaberDiscoveryResponder).GetMethod("Loop", BindingFlags.NonPublic | BindingFlags.Instance);
                byte[] reply = Encoding.ASCII.GetBytes("replacement");
                oldThread = new Thread(() => loop.Invoke(responder, new object[] { retired, reply })) { IsBackground = true };
                oldThread.Start();
                Assert.IsTrue(oldThread.Join(1000), "閉じた旧 socket の例外を繰り返して CPU を占有しない");
                Assert.IsTrue(responder.IsRunning, "旧 Loop の終了で新しい受信機を止めない");
                phone.Client.ReceiveTimeout = 2000;
                byte[] request = Encoding.ASCII.GetBytes(PhoneSaberDiscoveryResponder.Request);
                phone.Send(request, request.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,
                    ((System.Net.IPEndPoint)replacement.Client.LocalEndPoint).Port));
                var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                Assert.AreEqual(PhoneSaberDiscoveryResponder.BuildReply(5005, 5006, Environment.MachineName, "A"),
                    Encoding.ASCII.GetString(phone.Receive(ref remote)));
            }
            finally
            {
                responder.Stop();
                oldThread?.Join(2000);
                PhoneSaberP2PBridgeProcess.AutoStartEnabled = savedAutoStart;
            }
        }
    }

    // OS の socket を開かず、初期化途中の失敗と Dispose の呼び出しを確認する。
    sealed class TrackingClient : UdpClient
    {
        public bool Closed;
        protected override void Dispose(bool disposing) { Closed = true; }
    }

    [Test]
    public void FailedStartupClosesAcquiredClient()
    {
        bool savedAutoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        // コンストラクタを通さず Client=null とし、socket 設定の段階で失敗させる。
        var client = (TrackingClient)FormatterServices.GetUninitializedObject(typeof(TrackingClient));
        try
        {
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
            using (var responder = new PhoneSaberDiscoveryResponder())
            {
                LogAssert.Expect(LogType.Warning, new Regex("Android discovery responder failed:"));
                Assert.IsFalse(responder.Start(5005, 5006, () => client, "A"));
                Assert.IsFalse(responder.IsRunning);
                Assert.IsTrue(client.Closed, "初期化途中で失敗しても、取得済み client を解放する");
            }
        }
        finally
        {
            client.Close();
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = savedAutoStart;
        }
    }

    [Test]
    public void FailedBindClosesSocketOnEveryAttempt()
    {
        bool savedAutoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        try
        {
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
            using (var responder = new PhoneSaberDiscoveryResponder())
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    using (var client = new UdpClient(0))
                    {
                        Socket socket = client.Client;
                        LogAssert.Expect(LogType.Warning, new Regex("Android discovery responder failed:"));
                        Assert.IsFalse(responder.Start(5005, 5006, () => client, "A"));
                        Assert.IsFalse(responder.IsRunning);
                        Assert.Throws<ObjectDisposedException>(() => socket.Poll(0, SelectMode.SelectRead),
                            "bind に失敗した socket も、その場で閉じて次の起動へ残さない");
                    }
                }
            }
        }
        finally { PhoneSaberP2PBridgeProcess.AutoStartEnabled = savedAutoStart; }
    }
}
