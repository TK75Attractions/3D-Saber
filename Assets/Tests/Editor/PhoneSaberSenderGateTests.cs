using System;
using System.Collections.Generic;
using NUnit.Framework;

public class PhoneSaberSenderGateTests
{
    // 本番の既定は「検出して知らせるだけ」。弾く動作の仕様はここで固定しておく。
    [SetUp] public void EnforceForTheseTests() { PhoneSaberSenderGate.Enforce = true; }
    [TearDown] public void RestoreDefault() { PhoneSaberSenderGate.Enforce = false; }

    [Test]
    public void DefaultOnlyDetectsAndNeverRejects()
    {
        PhoneSaberSenderGate.Enforce = false;
        var events = new List<string>();
        var gate = new PhoneSaberSenderGate((kind, detail) => events.Add(kind));
        for (int i = 0; i < 120; i++)
            Assert.IsTrue(gate.Accept(i % 2 == 0 ? "192.168.1.10" : "192.168.1.11", i / 60.0));
        Assert.AreEqual("192.168.1.10", gate.LockedSender, "検出して F8 とログには出す");
        Assert.AreEqual(0, gate.RejectedPackets, "座標は弾かない");
        CollectionAssert.AreEqual(new[] { "sender-conflict" }, events);
    }

    [Test]
    public void LanAndP2PBridgePingPongOfOnePhoneIsNotAConflict()
    {
        // 1台の iPhone が 5007 応答の途絶と復帰で LAN と P2P を往復しても、2台とは判定しない。
        var gate = new PhoneSaberSenderGate();
        string[] order = { "192.168.1.10", "127.0.0.1", "192.168.1.10", "127.0.0.1", "127.0.0.1" };
        for (int i = 0; i < order.Length; i++) Assert.IsTrue(gate.Accept(order[i], i * 0.1));
        Assert.AreEqual("", gate.LockedSender);
        Assert.AreEqual(0, gate.RejectedPackets);
    }

    [Test]
    public void SingleSenderAndOneWayLanToP2PSwitchLoseNothing()
    {
        var gate = new PhoneSaberSenderGate();
        for (int i = 0; i < 120; i++) Assert.IsTrue(gate.Accept("192.168.1.10", i / 60.0));
        for (int i = 120; i < 240; i++) Assert.IsTrue(gate.Accept("127.0.0.1", i / 60.0));
        Assert.AreEqual("", gate.LockedSender);
        Assert.AreEqual(0, gate.RejectedPackets);
    }

    [Test]
    public void InterleavingAt60HzLocksFirstSenderWithinOneRoundTrip()
    {
        var events = new List<string>();
        var gate = new PhoneSaberSenderGate((kind, detail) => events.Add(kind + " " + detail));
        Assert.IsTrue(gate.Accept("A", 0));
        Assert.IsTrue(gate.Accept("B", 1.0 / 120));
        for (int i = 1; i <= 120; i++)
        {
            Assert.IsTrue(gate.Accept("A", i / 60.0));
            Assert.IsFalse(gate.Accept("B", i / 60.0 + 1.0 / 120));
        }
        Assert.AreEqual("A", gate.LockedSender);
        Assert.AreEqual(120, gate.RejectedPackets);
        CollectionAssert.AreEqual(new[] { "sender-conflict kept=A rejected=B" }, events);
    }

    [Test]
    public void RejectedTrafficDoesNotExtendLockAndBAloneTakesOverAtOneSecond()
    {
        var events = new List<string>();
        var gate = new PhoneSaberSenderGate((kind, detail) => events.Add(kind));
        gate.Accept("A", 0); gate.Accept("B", 0.1); gate.Accept("A", 0.2);
        Assert.IsFalse(gate.Accept("B", 1.199));
        Assert.IsTrue(gate.Accept("B", 1.2));
        Assert.IsTrue(gate.Accept("B", 1.21));
        Assert.AreEqual("", gate.LockedSender);
        CollectionAssert.AreEqual(new[] { "sender-conflict", "sender-conflict-end" }, events);
    }

    [Test]
    public void PollReleasesWithoutAnyNewPacketAndResetReleasesOnlyOnce()
    {
        var events = new List<string>();
        var gate = new PhoneSaberSenderGate((kind, detail) => events.Add(kind));
        gate.Accept("A", 0); gate.Accept("B", 0); gate.Accept("A", 0);
        gate.Poll(0.999);
        Assert.AreEqual("A", gate.LockedSender);
        gate.Poll(1); gate.Poll(2);
        Assert.AreEqual("", gate.LockedSender);
        gate.Accept("B", 2); gate.Accept("A", 2); gate.Accept("B", 2);
        gate.Reset(); gate.Reset();
        Assert.IsTrue(gate.Accept("A", 2));
        CollectionAssert.AreEqual(new[] { "sender-conflict", "sender-conflict-end",
            "sender-conflict", "sender-conflict-end" }, events);
        Assert.AreEqual(0, gate.RejectedPackets);
    }

    [TestCase("A")]
    [TestCase("B")]
    public void ThreeSendersDetectReturningSenderAndRejectBothOthers(string returning)
    {
        var gate = new PhoneSaberSenderGate();
        gate.Accept("A", 0); gate.Accept("B", 0.1); gate.Accept("C", 0.2);
        Assert.IsTrue(gate.Accept(returning, 0.3));
        Assert.AreEqual(returning, gate.LockedSender);
        foreach (string sender in new[] { "A", "B", "C" })
            Assert.AreEqual(sender == returning, gate.Accept(sender, 0.4));
        Assert.AreEqual(2, gate.RejectedPackets);
    }

    [Test]
    public void IdenticalTimestampsDetectConflictButExpiredReturnDoesNot()
    {
        var gate = new PhoneSaberSenderGate();
        gate.Accept("A", 10); gate.Accept("B", 10); gate.Accept("A", 10);
        Assert.IsFalse(gate.Accept("B", 10));
        gate.Reset();
        gate.Accept("A", 10); gate.Accept("B", 10);
        Assert.IsTrue(gate.Accept("A", 11));
        Assert.AreEqual("", gate.LockedSender);
    }

    [Test]
    public void SteadySingleSenderAndLockedInterleavingDoNotAllocate()
    {
        var single = new PhoneSaberSenderGate();
        var locked = new PhoneSaberSenderGate();
        var stats = new PhoneSaberPacketStatistics();
        for (int i = 0; i < 1000; i++) Step(i / 60.0, single, locked, stats);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1000; i < 11000; i++) Step(i / 60.0, single, locked, stats);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0, allocated);
    }

    static void Step(double now, PhoneSaberSenderGate single, PhoneSaberSenderGate locked,
        PhoneSaberPacketStatistics stats)
    {
        single.Accept("A", now);
        locked.Accept("A", now); locked.Accept("B", now);
        stats.Record(now, "A", true, 1000.01, 1000);
        stats.Record(now, "B", true, 1000.01, 1000);
        stats.PollSenderGate(now); stats.Read(now);
    }

    [Test]
    public void RejectionsAreVisibleAndCountAsArrivalButNotValidTiming()
    {
        var stats = new PhoneSaberPacketStatistics();
        stats.Reset(0);
        stats.Record(0, "A", true); stats.Record(0.01, "B", true);
        stats.Record(0.02, "A", true, 1000.01, 1000);
        stats.Record(0.03, "B", true, 2000, 1000);
        var rejected = stats.Read(0.04);
        Assert.AreEqual(4, rejected.PacketsPerSecond);
        Assert.AreEqual(0.01, rejected.SecondsSinceLastPacket, 1e-9);
        Assert.IsFalse(rejected.PayloadParsed);
        Assert.AreEqual(1, rejected.SenderRejectedPackets);
        Assert.AreEqual("A", rejected.LockedSender);
        Assert.AreEqual(1, rejected.OneWayDelay.Count);
        Assert.AreEqual(10, rejected.OneWayDelay.MaxMs, 1e-8);
        stats.Record(0.05, "A", true);
        Assert.AreEqual(30, stats.Read(0.05).ArrivalGaps.MedianMs, 1e-8);
        stats.ResetSenderGate();
        Assert.IsTrue(stats.Record(0.06, "B", true));
        Assert.AreEqual(1, stats.Read(0.06).SenderRejectedPackets);
        stats.Reset(1);
        Assert.AreEqual(0, stats.Read(1).SenderRejectedPackets);
    }

    [Test]
    public void ReceiverRestartReleasesOnceAndColourGatesStayIndependent()
    {
        var events = new List<string>();
        var red = new PhoneSaberPacketStatistics();
        var blue = new PhoneSaberPacketStatistics();
        red.ResetSenderGate((kind, detail) => events.Add(kind));
        red.Record(0, "A", true); red.Record(0, "B", true); red.Record(0, "A", true);
        Assert.IsFalse(red.Record(0, "B", true));
        Assert.IsTrue(blue.Record(0, "B", true));
        red.ResetSenderGate(); red.ResetSenderGate();
        Assert.IsTrue(red.Record(0, "B", true));
        Assert.AreEqual(1, red.Read(0).SenderRejectedPackets);
        Assert.AreEqual(0, blue.Read(0).SenderRejectedPackets);
        Assert.AreEqual("", blue.Read(0).LockedSender);
        CollectionAssert.AreEqual(new[] { "sender-conflict", "sender-conflict-end" }, events);
    }

    [Test]
    public void InvalidPacketsNeitherTakeNorExtendOwnership()
    {
        var stats = new PhoneSaberPacketStatistics();
        stats.Record(0, "A", true); stats.Record(0.1, "B", true); stats.Record(0.2, "A", true);
        Assert.AreEqual("A", stats.Read(0.2).LockedSender);
        stats.Record(1.1, "A", false); // 不正 payload は固定を延長しない
        Assert.IsTrue(stats.Record(1.21, "B", true), "A の有効な座標が1秒途絶えたので B を受理する");

        // 不正 payload だけの送信元は、正常なスマホを締め出せない。
        var other = new PhoneSaberPacketStatistics();
        other.Record(0, "X", false); other.Record(0.01, "B", true); other.Record(0.02, "X", false);
        Assert.IsTrue(other.Record(0.03, "B", true));
        Assert.AreEqual("", other.Read(0.03).LockedSender);
    }

    [Test]
    public void OverlayExplainsConflictAndRemovesWarningAfterRelease()
    {
        var stats = new PhoneSaberPacketStatistics();
        stats.Record(0, "A", true); stats.Record(0, "B", true); stats.Record(0, "A", true);
        string warning = PhoneSaberStatsDisplay.Warning("RED", stats.Read(0));
        StringAssert.Contains("別のスマホ", warning);
        StringAssert.Contains("台・送信先を確認", warning);
        stats.Record(0, "B", true);
        StringAssert.Contains("送信元競合による拒否: 1 pkt",
            PhoneSaberStatsDisplay.Format("RED", 5005, stats.Read(0), false));
        stats.PollSenderGate(1);
        Assert.AreEqual("", PhoneSaberStatsDisplay.Warning("RED", stats.Read(1)));
    }
}
