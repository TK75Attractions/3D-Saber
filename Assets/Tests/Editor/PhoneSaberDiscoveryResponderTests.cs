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
    public void LaterIsoSuccessStopsLostOlderCommandWithoutInventingItsAck(string station)
    {
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(71, "shutter-iso=160", 100);
        store.Queue(72, "shutter-iso=100", 100.01);
        Assert.AreEqual(2, Commands(store, 101).Length, "後発が未ACKなら先行指示も再送する");
        store.Receive(new PhoneSaberAcknowledgement(72, station, "ios", "shutter-iso", "100"), "phone");
        Assert.IsEmpty(Commands(store, 102), "未着のID71でISO160へ巻き戻さない");
        Assert.IsEmpty(store.Read(71), "再送停止を架空の成功ACKとして履歴に保存しない");
        Assert.AreEqual(2, Commands(store, 102, "other-phone").Length);
        store.Receive(new PhoneSaberAcknowledgement(71, station, "ios", "learning", "3s"), "phone");
        Assert.IsEmpty(Commands(store, 103), "遅延ACKで旧指示の再送を復活させない");
        store.Receive(new PhoneSaberAcknowledgement(71, station, "ios", "shutter-iso", "160"), "phone");
        Assert.AreEqual("160", store.Read(71).Single().Detail, "旧指示の実際のACKは保持する");
        Assert.IsEmpty(Commands(store, 104));
        store.Receive(new PhoneSaberAcknowledgement(71, station, "ios", "shutter-iso", "160"), "other-phone");
        CollectionAssert.AreEqual(new[] { PhoneSaberDiscoveryResponder.BuildCommand(72, "shutter-iso=100") },
            Commands(store, 104, "other-phone"), "先行成功では後発指示を抑止しない");
        store.Receive(new PhoneSaberAcknowledgement(72, station, "ios", "shutter-iso", "100"), "other-phone");
        Assert.IsEmpty(Commands(store, 105, "other-phone"));
        Assert.IsTrue(store.Latest(out int id, out _, out _));
        Assert.AreEqual(72, id);
        Assert.AreEqual(1, store.Read(71).Count(a => a.Sender == "phone"));
    }

    [TestCase("A")]
    [TestCase("B")]
    public void DefaultsSuccessStopsLostOlderIsoWithoutInventingItsAck(string station)
    {
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(71, "shutter-iso=100", 100);
        store.Queue(72, "defaults=1", 100.01);
        Assert.AreEqual(2, Commands(store, 101).Length, "defaultsの受付だけでは再送を止めない");
        Assert.IsTrue(PhoneSaberAcknowledgement.TryParse("PHONESABER_ACK 1 id=72 station=" + station +
            " platform=ios result=defaults detail=tracklock-on-ev-2-shutter-2000", out var ack));
        Assert.IsTrue(store.Receive(ack, "phone"));
        Assert.IsEmpty(Commands(store, 102), "明示リセット後に未着のISO100を再送しない");
        Assert.IsEmpty(store.Read(71), "旧指示の成功ACKを捏造しない");
        Assert.AreEqual(ack.Detail, store.Read(72).Single().Detail);
        Assert.AreEqual(2, Commands(store, 102, "other-phone").Length);
        store.Receive(new PhoneSaberAcknowledgement(71, station, "ios", "learning", "3s"), "phone");
        Assert.IsEmpty(Commands(store, 103), "遅延した開始ACKでも再送を復活させない");
        store.Receive(new PhoneSaberAcknowledgement(71, station, "ios", "shutter-iso", "100"), "phone");
        Assert.AreEqual("100", store.Read(71).Single().Detail, "実際の旧ACKは履歴に残す");
        Assert.IsEmpty(Commands(store, 104));
        Assert.IsTrue(store.Latest(out int id, out string fields, out _));
        Assert.AreEqual(72, id);
        Assert.AreEqual("defaults=1", fields);
    }

    [TestCase("shutter-iso=100")]
    [TestCase("shutter=500")]
    [TestCase("shutter-emitter=0")]
    [TestCase("track-lock=0")]
    [TestCase("exposure-bias=0")]
    public void DefaultsSupersedesEachSettingInAcceptanceOrderOnly(string fields)
    {
        foreach (string station in new[] { "A", "B" })
        {
            var store = new PhoneSaberAcknowledgementStore();
            // IDの大小・時計の逆転ではなく、defaultsより前の受付だけを抑止する。
            store.Queue(900, fields, 100);
            store.Queue(1, "defaults=1", 99);
            store.Queue(800, fields, 98);
            store.Receive(new PhoneSaberAcknowledgement(1, station, "ios", "defaults", "tracklock-on-ev-2-shutter-2000"), "phone");
            CollectionAssert.AreEqual(new[] { PhoneSaberDiscoveryResponder.BuildCommand(800, fields) }, Commands(store, 101));
            Assert.AreEqual(3, Commands(store, 101, "other-phone").Length);
            Assert.IsEmpty(store.Read(900));
        }
    }

    [TestCase("ignore=both")]
    [TestCase("clear-ignore=1")]
    [TestCase("learn-area=10")]
    [TestCase("clear-area=1")]
    [TestCase("clip=1")]
    [TestCase("auto-clip=1")]
    [TestCase("defaults=1")]
    [TestCase("track-lock=toggle")]
    [TestCase("shutter-iso=100 shutter-emitter=0")]
    public void DefaultsKeepsUnrelatedAndCompoundRetries(string fields)
    {
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(71, fields, 100);
        store.Queue(72, "defaults=1", 100.01);
        store.Receive(new PhoneSaberAcknowledgement(72, "A", "ios", "defaults", "tracklock-on-ev-2-shutter-2000"), "phone");
        CollectionAssert.AreEqual(new[] { PhoneSaberDiscoveryResponder.BuildCommand(71, fields) }, Commands(store, 101));
    }

    [TestCase("android", "defaults", "tracklock-on-ev-2-shutter-2000")]
    [TestCase("android", "defaults", "tracklock-on-ev-2")]
    [TestCase("ios", "learning", "3s")]
    [TestCase("ios", "failed", "samples0")]
    [TestCase("ios", "shutter-iso", "160")]
    [TestCase("ios", "shutter", "2000")]
    [TestCase("ios", "defaults", "tracklock-on-ev-2-shutter-500")]
    [TestCase("ios", "defaults", "tracklock-on-ev-2-shutter-2000-extra")]
    [TestCase("ios", "defaults", "")]
    public void DefaultsRequiresMatchingIPhoneCompletionBeforeSuppressing(string platform, string result, string detail)
    {
        var store = new PhoneSaberAcknowledgementStore();
        var settings = new[] { "shutter-iso=100", "shutter=500", "shutter-emitter=0", "track-lock=0", "exposure-bias=0" };
        for (int i = 0; i < settings.Length; i++) store.Queue(i + 1, settings[i], 100);
        store.Queue(71, "defaults=1", 100);
        Assert.AreEqual(6, Commands(store, 101).Length, "ACK未着ならすべて再送する");
        store.Receive(new PhoneSaberAcknowledgement(71, "B", platform, result, detail), "phone");
        foreach (string fields in settings)
            Assert.IsTrue(Commands(store, 102).Any(c => c.EndsWith(" " + fields)), fields);
    }

    [TestCase("A")]
    [TestCase("B")]
    public void LegacyDefaultsOnlySupersedesTrackLockAndExposure(string station)
    {
        var store = new PhoneSaberAcknowledgementStore();
        var settings = new[] { "shutter-iso=100", "shutter-emitter=0", "shutter=500", "track-lock=0", "exposure-bias=0" };
        for (int i = 0; i < settings.Length; i++) store.Queue(i + 1, settings[i], 100);
        store.Queue(71, "defaults=1", 100);
        Assert.IsTrue(PhoneSaberAcknowledgement.TryParse("PHONESABER_ACK 1 id=71 station=" + station +
            " platform=ios result=defaults detail=tracklock-on-ev-2", out var ack));
        store.Receive(ack, "phone");
        // このdetailはshutter未対応版も返すため、ISO・emitter・shutterは推定しない。
        CollectionAssert.AreEqual(new[] { PhoneSaberDiscoveryResponder.BuildCommand(1, settings[0]),
            PhoneSaberDiscoveryResponder.BuildCommand(2, settings[1]),
            PhoneSaberDiscoveryResponder.BuildCommand(3, settings[2]) }, Commands(store, 101));
        Assert.AreEqual("tracklock-on-ev-2", store.Read(71).Single().Detail, "ISO160・emitter ONの情報を補わない");
        for (int id = 1; id <= settings.Length; id++) Assert.IsEmpty(store.Read(id));
        Assert.AreEqual(6, Commands(store, 101, "other-phone").Length);
    }

    [Test]
    public void DefaultsSuppressionKeepsHistoryCapacityAndDeadlines()
    {
        var store = new PhoneSaberAcknowledgementStore();
        for (int id = 1; id < PhoneSaberAcknowledgementStore.MaximumCommands; id++) store.Queue(id, "shutter-iso=100", 100);
        store.Queue(32, "defaults=1", 100);
        store.Receive(new PhoneSaberAcknowledgement(32, "A", "ios", "defaults", "tracklock-on-ev-2-shutter-2000"), "phone");
        Assert.IsEmpty(Commands(store, 101));
        Assert.Throws<InvalidOperationException>(() => store.Queue(33, "shutter-iso=100", 101));
        Assert.AreEqual(9, store.CommandSecondsLeft(101));
        Assert.AreEqual(32, Commands(store, 109.999, "other-phone").Length);
        Assert.IsEmpty(Commands(store, 110, "other-phone"));
        store.Queue(33, "shutter-iso=100", 110);
        CollectionAssert.AreEqual(new[] { PhoneSaberDiscoveryResponder.BuildCommand(33, "shutter-iso=100") }, Commands(store, 111));
        Assert.AreEqual("defaults", store.Read(32).Single().Result);
    }

    [TestCase("shutter-iso=160", "shutter-iso=100", "shutter-iso", "125.5")]
    [TestCase("shutter-emitter=1", "shutter-emitter=0", "shutter-emitter", "off")]
    [TestCase("shutter-emitter=0", "shutter-emitter=1", "shutter-emitter", "on")]
    [TestCase("shutter=2000", "shutter=0", "shutter", "off")]
    [TestCase("shutter=0", "shutter=500", "shutter", "500")]
    [TestCase("shutter=500", "shutter=1000", "shutter", "1000")]
    [TestCase("exposure-bias=-2", "exposure-bias=0", "exposure", "ev0")]
    [TestCase("exposure-bias=0", "exposure-bias=-1", "exposure", "ev-1")]
    [TestCase("track-lock=1", "track-lock=0", "tracklock", "off")]
    [TestCase("track-lock=0", "track-lock=1", "tracklock", "on")]
    public void AbsoluteSettingsUseAcceptanceOrderRatherThanIdOrTimestamp(string older, string newer, string result, string detail)
    {
        var store = new PhoneSaberAcknowledgementStore();
        // IDの折り返し・時計の逆転でも、Queueの受付順だけを見る。
        store.Queue(900, older, 100);
        store.Queue(1, newer, 99);
        store.Receive(new PhoneSaberAcknowledgement(1, "B", "ios", result, detail), "phone");
        Assert.IsEmpty(Commands(store, 101));
        Assert.AreEqual(2, Commands(store, 101, "other-phone").Length);
    }

    [TestCase("shutter-iso=100", "learning", "3s")]
    [TestCase("shutter-iso=100", "failed", "samples0")]
    [TestCase("shutter-iso=100", "shutter-emitter", "on")]
    [TestCase("shutter-iso=100", "shutter-iso", "0")]
    [TestCase("shutter-emitter=0", "shutter-emitter", "on")]
    [TestCase("shutter=0", "shutter", "2000")]
    [TestCase("exposure-bias=0", "exposure", "ev-2")]
    [TestCase("track-lock=0", "tracklock", "on")]
    public void NonMatchingSuccessDoesNotSuppressOlderCommands(string fields, string result, string detail)
    {
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(71, fields, 100);
        store.Queue(72, fields, 100.01);
        store.Receive(new PhoneSaberAcknowledgement(72, "A", "ios", result, detail), "phone");
        CollectionAssert.Contains(Commands(store, 101), PhoneSaberDiscoveryResponder.BuildCommand(71, fields));
    }

    [TestCase("learn-area=10", "learned", "samples300")]
    [TestCase("ignore=both", "ignored", "red1-blue1")]
    [TestCase("clear-ignore=1", "cleared", "ignore")]
    [TestCase("clear-area=1", "cleared", "area")]
    [TestCase("clip=1", "clip-saved", "saved")]
    [TestCase("auto-clip=1", "autoclip", "on")]
    [TestCase("defaults=1", "defaults", "tracklock-on-ev-2-shutter-2000")]
    [TestCase("track-lock=toggle", "tracklock", "on")]
    [TestCase("shutter-iso=160 shutter-emitter=1", "shutter-iso", "160")]
    public void OtherOperationsAndCompoundCommandsKeepTheirOwnRetries(string fields, string result, string detail)
    {
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(71, fields, 100);
        store.Queue(72, fields, 100.01);
        store.Receive(new PhoneSaberAcknowledgement(72, "A", "ios", result, detail), "phone");
        CollectionAssert.AreEqual(new[] { PhoneSaberDiscoveryResponder.BuildCommand(71, fields) }, Commands(store, 101));
    }

    [Test]
    public void SuppressedHistoryKeepsQueueCapacityAndRetryDeadline()
    {
        var store = new PhoneSaberAcknowledgementStore();
        for (int id = 1; id <= PhoneSaberAcknowledgementStore.MaximumCommands; id++)
            store.Queue(id, "shutter-iso=160", 100);
        store.Receive(new PhoneSaberAcknowledgement(32, "A", "ios", "shutter-iso", "160"), "phone");
        Assert.IsEmpty(Commands(store, 101));
        Assert.Throws<InvalidOperationException>(() => store.Queue(33, "shutter-iso=100", 101));
        Assert.AreEqual(9, store.CommandSecondsLeft(101));
        Assert.AreEqual(32, Commands(store, 109.999, "other-phone").Length);
        Assert.IsEmpty(Commands(store, 110, "other-phone"));
        store.Queue(33, "shutter-iso=100", 110);
        CollectionAssert.AreEqual(new[] { PhoneSaberDiscoveryResponder.BuildCommand(33, "shutter-iso=100") }, Commands(store, 111));
        Assert.AreEqual("160", store.Read(32).Single().Detail);
    }

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
    public void DefaultsCommandIsAnExplicitReset()
    {
        Assert.AreEqual("PHONESABER_COMMAND 1 id=81 defaults=1",
            PhoneSaberDiscoveryResponder.BuildCommand(81, PhoneSaberDiscoveryResponder.DefaultsCommand));
    }

    [TestCase("A")]
    [TestCase("B")]
    public void StartDiscoveryAndRestartNeverQueueDefaults(string station)
    {
        bool savedAutoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        var store = PhoneSaberDiscoveryResponder.Acknowledgements;
        int revision = store.Revision;
        using (var responder = new PhoneSaberDiscoveryResponder())
        {
            try
            {
                PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
                // PC起動、探索、新しい送信元port、受信機の再起動を通して操作を増やさない。
                for (int restart = 0; restart < 2; restart++)
                {
                    UdpClient server = null;
                    Assert.IsTrue(responder.Start(5005, 5006,
                        () => server = new UdpClient(AddressFamily.InterNetwork), station, 0));
                    Assert.AreEqual(revision, store.Revision, "PC起動時にdefaultsをキューへ入れない");
                    for (int connection = 0; connection < 2; connection++)
                    using (var phone = new UdpClient(0))
                    {
                        phone.Client.ReceiveTimeout = 2000;
                        var destination = (System.Net.IPEndPoint)server.Client.LocalEndPoint;
                        destination.Address = System.Net.IPAddress.Loopback;
                        byte[] request = Encoding.ASCII.GetBytes(PhoneSaberDiscoveryResponder.Request);
                        for (int probe = 0; probe < 2; probe++)
                        {
                            phone.Send(request, request.Length, destination);
                            var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                            string reply;
                            do { reply = Encoding.ASCII.GetString(phone.Receive(ref remote)); }
                            while (reply.StartsWith(PhoneSaberDiscoveryResponder.CommandPrefix));
                            Assert.AreEqual(PhoneSaberDiscoveryResponder.BuildReply(5005, 5006, Environment.MachineName, station), reply);
                            Assert.AreEqual(revision, store.Revision, "探索・再接続で保存値をリセットしない");
                        }
                    }
                    responder.Stop();
                }
            }
            finally { PhoneSaberP2PBridgeProcess.AutoStartEnabled = savedAutoStart; }
        }
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
                LogAssert.Expect(LogType.Warning, new Regex("iPhone discovery responder failed:"));
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
                        LogAssert.Expect(LogType.Warning, new Regex("iPhone discovery responder failed:"));
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
