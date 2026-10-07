using System.Globalization;
using NUnit.Framework;

// ソケットや Unity の時計を使わず、運営表示の時間窓と未受信の境界を検証する。
public class PhoneSaberInputStatsTests
{
    [Test]
    public void TimingUsesAveragedMedianAndNearestRankP95()
    {
        var values = new double[20];
        for (int i = 0; i < values.Length; i++) values[i] = 20 - i;
        var stats = new PhoneSaberTimingStats(values);
        Assert.AreEqual(20, stats.Count);
        Assert.AreEqual(10.5, stats.MedianMs);
        Assert.AreEqual(19, stats.P95Ms);
        Assert.AreEqual(20, stats.MaxMs);
        Assert.AreEqual(1, stats.MinMs);
        var odd = new PhoneSaberTimingStats(new[] { 40.0, 10.0, 20.0 });
        Assert.AreEqual(20, odd.MedianMs);
        Assert.AreEqual(40, odd.P95Ms);
        Assert.AreEqual(0, new PhoneSaberTimingStats(new double[0]).Count);
    }

    [Test]
    public void GapsIncludeUntimestampedAndInvalidDatagramsButDelayNeedsParsedTimestamp()
    {
        var statistics = new PhoneSaberPacketStatistics();
        statistics.Reset(0);
        statistics.Record(0, "phone", true, 1000.01, 1000);
        Assert.AreEqual(0, statistics.Read(0).ArrivalGaps.Count);
        statistics.Record(0.02, "phone", false, 1000.02, 1000);
        statistics.Record(0.06, "phone", true);
        statistics.Record(0.09, "phone", true, 1000.02, 1000.03);
        var snapshot = statistics.Read(0.09);
        Assert.AreEqual(3, snapshot.ArrivalGaps.Count);
        Assert.AreEqual(30, snapshot.ArrivalGaps.MedianMs, 1e-8);
        Assert.AreEqual(40, snapshot.ArrivalGaps.P95Ms, 1e-8);
        Assert.AreEqual(40, snapshot.ArrivalGaps.MaxMs, 1e-8);
        Assert.AreEqual(2, snapshot.OneWayDelay.Count);
        Assert.AreEqual(0, snapshot.OneWayDelay.MedianMs, 1e-8);
        Assert.AreEqual(-10, snapshot.OneWayDelay.MinMs, 1e-8);
        Assert.AreEqual(10, snapshot.OneWayDelay.MaxMs, 1e-8);
    }

    [Test]
    public void FiveSecondWindowExpiresAtBoundaryIncludingWhileSilent()
    {
        var statistics = new PhoneSaberPacketStatistics();
        statistics.Reset(0);
        statistics.Record(0, "phone", true, 1000.01, 1000);
        statistics.Record(1, "phone", true, 1001.02, 1001);
        var before = statistics.Read(4.99);
        Assert.AreEqual(2, before.OneWayDelay.Count);
        Assert.AreEqual(1, before.ArrivalGaps.Count);
        Assert.AreEqual(1, statistics.Read(5).OneWayDelay.Count);
        var silent = statistics.Read(6);
        Assert.AreEqual(0, silent.OneWayDelay.Count);
        Assert.AreEqual(0, silent.ArrivalGaps.Count);
        Assert.AreEqual("不良", PhoneSaberStatsDisplay.Verdict(silent));
        Assert.AreEqual(2, before.OneWayDelay.Count);
        // 復帰した datagram の時刻で、長い空白も新しい窓へ入る。
        statistics.Record(6.1, "phone", true);
        Assert.AreEqual(5100, statistics.Read(6.1).ArrivalGaps.MaxMs, 1e-8);
    }

    [Test]
    public void TimingIsBoundedAndResetAndSenderChangeDiscardPreviousTiming()
    {
        var statistics = new PhoneSaberPacketStatistics();
        statistics.Reset(0);
        for (int i = 0; i < 3000; i++) statistics.Record(i / 1000.0, "red", true, 1000.01, 1000);
        var red = statistics.Read(3);
        Assert.AreEqual(PhoneSaberPacketStatistics.MaximumTimingSamples, red.ArrivalGaps.Count);
        Assert.AreEqual(PhoneSaberPacketStatistics.MaximumTimingSamples, red.OneWayDelay.Count);
        statistics.Record(3.1, "anotherPhone", true);
        Assert.AreEqual(0, statistics.Read(3.1).ArrivalGaps.Count);
        Assert.AreEqual(0, statistics.Read(3.1).OneWayDelay.Count);
        statistics.Reset(4);
        statistics.Record(4.1, "red", true);
        Assert.AreEqual(0, statistics.Read(4.1).ArrivalGaps.Count);
        Assert.AreEqual(0, statistics.Read(4.1).OneWayDelay.Count);
    }

    [TestCase("ts=1791234567.123456;1,2,3,4", 1791234567.123456)]
    [TestCase("ts=1000;1,2", 1000)]
    [TestCase("ts=NaN;1,2", null)]
    [TestCase("ts=Infinity;1,2", null)]
    [TestCase("ts=1e999;1,2", null)]
    [TestCase("ts=-1;1,2", null)]
    [TestCase("ts=0;1,2", null)]
    [TestCase("ts=;1,2", null)]
    [TestCase("ts=oops;1,2", null)]
    [TestCase("ts=1,25;1,2", null)]
    [TestCase("ts=1000", null)]
    [TestCase("timestamp=1000;1,2", null)]
    [TestCase("1,2,3,4", null)]
    [TestCase(null, null)]
    public void TimestampReaderAcceptsFinitePositiveEpochOnly(string payload, double? expected)
    {
        Assert.AreEqual(expected, PhoneSaberPacketStatistics.ReadSendEpoch(payload));
    }

    [Test]
    public void InvalidEpochsAreExcludedAndClockJumpsCannotChangeGaps()
    {
        var statistics = new PhoneSaberPacketStatistics();
        statistics.Reset(0);
        statistics.Record(0, "phone", true, double.NaN, 1000);
        statistics.Record(0.02, "phone", true, 1000, double.PositiveInfinity);
        statistics.Record(0.04, "phone", true, 1000, 0);
        Assert.AreEqual(0, statistics.Read(0.04).OneWayDelay.Count);
        statistics.Record(0.06, "phone", true, 1100, 1000);
        Assert.AreEqual(20, statistics.Read(0.06).ArrivalGaps.MedianMs, 1e-8);
        Assert.AreEqual(100000, statistics.Read(0.06).OneWayDelay.MaxMs);
    }

    static PhoneSaberInputStats Quality(double p95Gap, double maxGap, double age = 0,
        double? delay = null, int count = 20)
    {
        var gaps = new double[count];
        for (int i = 0; i < count; i++) gaps[i] = p95Gap;
        if (count > 0) gaps[count - 1] = maxGap;
        var delays = delay.HasValue ? new double[count] : new double[0];
        for (int i = 0; i < delays.Length; i++) delays[i] = delay.Value;
        return new PhoneSaberInputStats(true, 30, age, "phone", true,
            new PhoneSaberTimingStats(gaps), new PhoneSaberTimingStats(delays));
    }

    [TestCase(49.9, 149.9, "良好")]
    [TestCase(50, 149, "注意")]
    [TestCase(99.9, 499.9, "注意")]
    [TestCase(100, 100, "不良")]
    [TestCase(30, 150, "注意")]
    [TestCase(30, 500, "不良")]
    public void VerdictUsesDocumentedGapThresholds(double p95, double max, string expected)
    {
        Assert.AreEqual(expected, PhoneSaberStatsDisplay.Verdict(Quality(p95, max)));
    }

    [Test]
    public void VerdictIncludesCurrentSilenceAndWarmupAndOnlyUsesDelayWithClockConfirmation()
    {
        Assert.AreEqual("注意（計測中）", PhoneSaberStatsDisplay.Verdict(Quality(30, 30, count: 19)));
        Assert.AreEqual("注意", PhoneSaberStatsDisplay.Verdict(Quality(30, 30, age: 0.15)));
        Assert.AreEqual("不良", PhoneSaberStatsDisplay.Verdict(Quality(30, 30, age: 0.5)));
        Assert.AreEqual("良好", PhoneSaberStatsDisplay.Verdict(Quality(30, 30, delay: 5000)));
        Assert.AreEqual("良好", PhoneSaberStatsDisplay.Verdict(Quality(30, 30), true));
        Assert.AreEqual("良好", PhoneSaberStatsDisplay.Verdict(Quality(30, 30, delay: 49.9), true));
        Assert.AreEqual("注意", PhoneSaberStatsDisplay.Verdict(Quality(30, 30, delay: 50), true));
        Assert.AreEqual("不良", PhoneSaberStatsDisplay.Verdict(Quality(30, 30, delay: 100), true));
        Assert.AreEqual("注意（時計差を確認）", PhoneSaberStatsDisplay.Verdict(Quality(30, 30, delay: -1), true));
        Assert.AreEqual("不良", PhoneSaberStatsDisplay.Verdict(new PhoneSaberInputStats(true, 30, 0, "phone", false)));
    }

    [Test]
    public void DelayMaximumAndSampleWarmupAreAlsoUsedOnlyWhenSynchronized()
    {
        var gap = Quality(30, 30).ArrivalGaps;
        var values = new double[20];
        values[19] = 150;
        var stats = new PhoneSaberInputStats(true, 30, 0, "phone", true, gap, new PhoneSaberTimingStats(values));
        Assert.AreEqual("注意", PhoneSaberStatsDisplay.Verdict(stats, true));
        values[19] = 500;
        stats = new PhoneSaberInputStats(true, 30, 0, "phone", true, gap, new PhoneSaberTimingStats(values));
        Assert.AreEqual("不良", PhoneSaberStatsDisplay.Verdict(stats, true));
        stats = new PhoneSaberInputStats(true, 30, 0, "phone", true, gap, new PhoneSaberTimingStats(new[] { 1.0 }));
        Assert.AreEqual("注意（計測中）", PhoneSaberStatsDisplay.Verdict(stats, true));
        stats = new PhoneSaberInputStats(true, 30, 0, "phone", true, gap,
            new PhoneSaberTimingStats(new[] { -1.0, 5000.0 }));
        Assert.AreEqual("注意（時計差を確認）", PhoneSaberStatsDisplay.Verdict(stats, true));
    }

    [Test]
    public void ConcurrentReadsAndReceivesProduceConsistentBoundedSnapshots()
    {
        var statistics = new PhoneSaberPacketStatistics();
        statistics.Reset(0);
        var writer = System.Threading.Tasks.Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++) statistics.Record(i / 1000.0, "phone", true, 1000.02, 1000);
        });
        var reader = System.Threading.Tasks.Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                // 読み取りで窓を進めず、受信とコピーの競合だけを検証する。
                var snapshot = statistics.Read(0);
                Assert.LessOrEqual(snapshot.ArrivalGaps.Count, PhoneSaberPacketStatistics.MaximumTimingSamples);
                Assert.LessOrEqual(snapshot.OneWayDelay.Count, PhoneSaberPacketStatistics.MaximumTimingSamples);
                if (snapshot.OneWayDelay.Count > 0) Assert.AreEqual(20, snapshot.OneWayDelay.MedianMs, 1e-8);
            }
        });
        System.Threading.Tasks.Task.WaitAll(writer, reader);
        Assert.AreEqual(999, statistics.Read(1).ArrivalGaps.Count);
        Assert.AreEqual(1000, statistics.Read(1).OneWayDelay.Count);
    }

    [Test]
    public void FormattingReportsMissingTimestampAndWhichMetricsDriveVerdict()
    {
        string text = PhoneSaberStatsDisplay.Format("RED", 5005, Quality(30, 30), false, true);
        StringAssert.Contains("受信間隔（直近5秒）: 中央値 30.0 / p95 30.0 / 最大 30.0 ms", text);
        StringAssert.Contains("片道時計差（tsあり）: --（サンプルなし）", text);
        StringAssert.Contains("判定: 良好（間隔のみ）", text);
        StringAssert.Contains("判定: 注意（間隔＋片道）",
            PhoneSaberStatsDisplay.Format("BLUE", 5006, Quality(30, 30, delay: 50), false, true));
    }

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
        red.Record(10.25, "127.0.0.1", true, 1000.01, 1000);
        red.Record(10.3, "127.0.0.1", true, 1000.01, 1000);
        Assert.IsFalse(blue.Read(10.5).HasPacket);
        Assert.AreEqual(0.5, blue.Read(10.5).SecondsSinceLastPacket);
        Assert.IsTrue(red.Read(10.5).HasPacket);
        Assert.AreEqual(1, red.Read(10.5).ArrivalGaps.Count);
        Assert.AreEqual(2, red.Read(10.5).OneWayDelay.Count);
        Assert.AreEqual(0, blue.Read(10.5).ArrivalGaps.Count);
        Assert.AreEqual(0, blue.Read(10.5).OneWayDelay.Count);
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
