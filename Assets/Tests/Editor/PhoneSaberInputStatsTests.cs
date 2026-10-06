using System.Globalization;
using NUnit.Framework;

// ソケットや Unity の時計を使わず、運営表示の時間窓と未受信の境界を検証する。
public class PhoneSaberInputStatsTests
{
    [Test]
    public void RateCountsAllDatagramsInTrailingSecondAndFallsToZero()
    {
        var statistics = new PhoneSaberPacketStatistics();
        statistics.Reset(10);
        statistics.Record(10, "192.168.1.10", true);
        statistics.Record(10.5, "192.168.1.10", true);
        statistics.Record(10.75, "192.168.1.20", false);
        var snapshot = statistics.Read(10.9);
        Assert.AreEqual(3, snapshot.PacketsPerSecond);
        Assert.AreEqual("192.168.1.20", snapshot.SenderIP);
        Assert.IsFalse(snapshot.PayloadParsed);
        Assert.AreEqual(0.15, snapshot.SecondsSinceLastPacket, 1e-9);
        Assert.AreEqual(2, statistics.Read(11).PacketsPerSecond);
        var silent = statistics.Read(11.75);
        Assert.AreEqual(0, silent.PacketsPerSecond);
        Assert.AreEqual(1, silent.SecondsSinceLastPacket);
        Assert.AreEqual("192.168.1.20", silent.SenderIP);
        // 戻り値はコピーなので、後の受信や読み取りでは書き換わらない。
        Assert.AreEqual(3, snapshot.PacketsPerSecond);
    }

    [Test]
    public void ResetStartsNewSessionAndColoursStayIndependent()
    {
        var red = new PhoneSaberPacketStatistics();
        var blue = new PhoneSaberPacketStatistics();
        red.Reset(10);
        blue.Reset(10);
        red.Record(10.25, "127.0.0.1", true);
        Assert.IsFalse(blue.Read(10.5).HasPacket);
        Assert.AreEqual(0.5, blue.Read(10.5).SecondsSinceLastPacket);
        Assert.IsTrue(red.Read(10.5).HasPacket);
        red.Reset(20);
        var reset = red.Read(20.5);
        Assert.IsFalse(reset.HasPacket);
        Assert.AreEqual(0, reset.PacketsPerSecond);
        Assert.AreEqual("", reset.SenderIP);
        Assert.AreEqual(0.5, reset.SecondsSinceLastPacket);
    }

    [TestCase(false, 0.0, false)]
    [TestCase(false, 1.0, false)]
    [TestCase(false, 1.001, true)]
    [TestCase(true, 1.0, false)]
    [TestCase(true, 1.001, true)]
    public void StalenessWarnsOnlyAfterOneSecond(bool hasPacket, double age, bool expected)
    {
        var stats = new PhoneSaberInputStats(hasPacket, 0, age, "", false);
        Assert.AreEqual(expected, PhoneSaberStatsDisplay.IsStale(stats));
        string warning = PhoneSaberStatsDisplay.Warning("BLUE", stats);
        if (expected)
        {
            StringAssert.Contains("警告: BLUE", warning);
            StringAssert.Contains("1秒以上届いていません", warning);
        }
        else Assert.AreEqual("", warning);
    }

    [TestCase("127.0.0.1", true, "P2P bridge")]
    [TestCase("127.0.0.1", false, "LAN")]
    [TestCase("192.168.1.25", true, "LAN")]
    [TestCase("192.168.1.25", false, "LAN")]
    [TestCase("127.0.0.2", true, "LAN")]
    [TestCase("", true, "--")]
    [TestCase(null, false, "--")]
    public void RouteUsesMacBridgeLoopbackOnly(string sender, bool mac, string expected)
    {
        Assert.AreEqual(expected, PhoneSaberStatsDisplay.ClassifyRoute(sender, mac));
    }

    [Test]
    public void FormattingUsesInvariantDecimalsAndLatestParseStatus()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var stats = new PhoneSaberInputStats(true, 32, 0.126, "127.0.0.1", false);
            string text = PhoneSaberStatsDisplay.Format("RED", 5005, stats, true);
            StringAssert.Contains("RED UDP 5005", text);
            StringAssert.Contains("32 pkt/s", text);
            StringAssert.Contains("0.13 s", text);
            StringAssert.Contains("送信元: 127.0.0.1", text);
            StringAssert.Contains("経路: P2P bridge", text);
            StringAssert.Contains("payload解析: NG", text);
            var valid = new PhoneSaberInputStats(true, 1, 0, "192.168.1.2", true);
            StringAssert.Contains("payload解析: OK", PhoneSaberStatsDisplay.Format("BLUE", 5006, valid, false));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Test]
    public void FormattingBeforeFirstPacketDoesNotClaimValidPayloadOrRoute()
    {
        var stats = new PhoneSaberInputStats(false, 0, 2.5, "", false);
        string text = PhoneSaberStatsDisplay.Format("BLUE", 5006, stats, false);
        StringAssert.Contains("BLUE UDP 5006", text);
        StringAssert.Contains("未受信 (開始から 2.50 s)", text);
        StringAssert.Contains("送信元: --", text);
        StringAssert.Contains("経路: --", text);
        StringAssert.Contains("payload解析: --", text);
    }
}
