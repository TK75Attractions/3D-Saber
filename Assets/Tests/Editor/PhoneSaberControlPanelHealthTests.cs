using System;
using System.Linq;
using NUnit.Framework;

public class PhoneSaberControlPanelHealthTests
{
    static PhoneSaberControlPanelHealth.GameWindow Observe(PhoneSaberControlPanelHealth.GameWindow previous,
        double now, int frame, int scene, bool inGame, double frameMs, double redAge, double blueAge)
    {
        var next = PhoneSaberControlPanelHealth.ObserveGame(previous, now, frame, scene, inGame, frameMs);
        return inGame ? PhoneSaberControlPanelHealth.ObserveGaps(next, now, redAge, blueAge) : next;
    }
    static PhoneSaberInputStats Sending(double age = .1, string sender = "192.168.1.20") => new PhoneSaberInputStats(true, 60, age, sender, true);
    static PhoneSaberLatencyEstimate Estimate(double at = 100, string thermal = "nominal", string sender = "192.168.1.20",
        string controls = "1235", string platform = "ios", string station = "A") =>
        new PhoneSaberLatencyEstimate(new PhoneSaberMetrics { ReceivedAt = at, Controls = controls, Platform = platform, Station = station,
            CaptureFps = 60, C2sMedian = 35,
            Rtt = 4, RttAge = 0, Thermal = thermal, Route = "LAN" },
            new PhoneSaberTimingStats(new double[] { 3 }), sender, .1, at, 60, 0);
    static PhoneSaberAcknowledgement Ack(int id = 1, string result = "defaults", string detail = "tracklock-on-ev-2",
        string sender = "192.168.1.20", string station = "A") =>
        new PhoneSaberAcknowledgement(id, station, "ios", result, detail, sender);
    static PhoneSaberControlPanelHealth.Check[] Checks(double now = 100, PhoneSaberLatencyEstimate estimate = null,
        PhoneSaberAcknowledgement[] acks = null, int gaps = 0, int frames = 1, bool whole = true, double age = .1) =>
        PhoneSaberControlPanelHealth.Evaluate(now, Sending(age), Sending(age), estimate ?? Estimate(), estimate ?? Estimate(),
            50, 50, acks ?? Array.Empty<PhoneSaberAcknowledgement>(), true, gaps, frames, whole).ToArray();
    static PhoneSaberControlPanelHealth.Check Settings(PhoneSaberControlPanelHealth.Check[] checks, string colour = "赤") =>
        // ホストで修正前との比較も行えるよう、旧表示名でも同じ項目を探す。
        checks.Single(c => c.Text.Contains(colour + " スマホ設定（metrics報告）") || c.Text.Contains(colour + " 最終スマホACK"));

    [TestCase(58, 58, true, "OK")]
    [TestCase(57.999, 58, true, "WARN")]
    [TestCase(50, 50, true, "OK")]
    [TestCase(49.999, 50, true, "WARN")]
    [TestCase(40, 40, false, "OK")]
    [TestCase(40.001, 40, false, "WARN")]
    [TestCase(65, 65, false, "OK")]
    [TestCase(65.001, 65, false, "WARN")]
    [TestCase(double.NaN, 40, false, "WARN")]
    [TestCase(double.PositiveInfinity, 58, true, "WARN")]
    [TestCase(-1, 58, true, "WARN")]
    public void ExactThresholdsAndInvalidValues(double value, double limit, bool minimum, string grade) =>
        Assert.AreEqual(grade, PhoneSaberControlPanelHealth.NumericGrade(value, limit, minimum));

    [Test] public void GoodBaselineIsAllGreenAndTextIsExplicit()
    {
        Assert.That(Checks().All(c => c.Grade == "OK"));
        Assert.That(Checks().All(c => c.Text.StartsWith("OK · ")));
        // ctl=1235 のISO100を警告せず、defaults ACKなしでも保存設定の再接続を正常とする。
        StringAssert.Contains("スマホ設定（metrics報告）", Settings(Checks()).Text);
        StringAssert.Contains("追跡ロック ON / 露出 -2", Settings(Checks()).Text);
        var state = new PhoneSaberControlPanelState();
        state.BeginSection("状態チェック");
        state.HealthLabel(Checks()[0]);
        StringAssert.Contains("\"grade\":\"OK\"", state.ToJson());
    }
    [TestCase("serious", "BAD")]
    [TestCase("critical", "BAD")]
    [TestCase("fair", "WARN")]
    [TestCase("unknown", "WARN")]
    public void ThermalGrades(string thermal, string expected) =>
        Assert.AreEqual(expected, Checks(estimate: Estimate(thermal: thermal)).First(c => c.Text.Contains("発熱")).Grade);

    [Test] public void StopGapsPcAndPartialSongNeverLookHealthy()
    {
        Assert.AreEqual("BAD", Checks(age: 1.001)[0].Grade);
        Assert.AreEqual("OK", Checks(age: 1)[0].Grade);
        Assert.AreEqual("BAD", Checks(gaps: 1).Single(c => c.Text.Contains("同時途切れ")).Grade);
        Assert.AreEqual("BAD", Checks(frames: 2).Single(c => c.Text.Contains("PCフレーム")).Grade);
        Assert.AreEqual("WARN", Checks(whole: false).Single(c => c.Text.Contains("PCフレーム")).Grade);
    }
    [Test] public void StaleMissingAndP2pMetricsNeverBecomeGreen()
    {
        foreach (var estimate in new[] { Estimate(at: 84.99), Estimate(sender: "127.0.0.1"), Estimate(sender: "::1") })
            Assert.AreEqual("WARN", Checks(estimate: estimate).First(c => c.Text.Contains("撮影fps")).Grade);
        Assert.AreEqual("WARN", Checks(now: 111).First(c => c.Text.Contains("合計推定")).Grade);
        var checks = PhoneSaberControlPanelHealth.Evaluate(100, default, default, null, null, null, null, null, false, 0, 0, false);
        Assert.AreEqual("BAD", checks[0].Grade);
        Assert.AreEqual("WARN", checks.First(c => c.Text.Contains("撮影fps")).Grade);
        Assert.AreEqual("WARN", checks.First(c => c.Text.Contains("pkt/s")).Grade);
    }
    [TestCase("A")]
    [TestCase("B")]
    public void CurrentMetricsOverrideDefaultsAndIndividualAckHistory(string station)
    {
        var histories = new[] {
            Array.Empty<PhoneSaberAcknowledgement>(), new[] { Ack(station: station) },
            new[] { Ack(station: station), Ack(2, "clip-saved", "saved", station: station) },
            new[] { Ack(station: station), Ack(3, "tracklock", "on", station: station), Ack(4, "exposure", "ev-2", station: station) },
            new[] { Ack(station: station), Ack(3, "tracklock", "off", station: station), Ack(4, "exposure", "ev-1", station: station) },
            new[] { Ack(sender: "192.168.1.99", station: station) }
        };
        foreach (var acks in histories)
        foreach (string colour in new[] { "赤", "青" })
        {
            var good = Settings(Checks(estimate: Estimate(station: station), acks: acks), colour);
            Assert.AreEqual("OK", good.Grade);
            StringAssert.Contains("追跡ロック ON / 露出 -2", good.Text);
            var bad = Settings(Checks(estimate: Estimate(controls: "0035", station: station), acks: acks), colour);
            Assert.AreEqual("WARN", bad.Grade);
            StringAssert.Contains("追跡ロック OFF / 露出 0", bad.Text);
            StringAssert.DoesNotContain("defaults", bad.Text);
        }
    }
    [TestCase("0235", "OFF", "-2", "WARN")]
    [TestCase("1035", "ON", "0", "WARN")]
    [TestCase("1135", "ON", "-1", "WARN")]
    [TestCase("1230", "ON", "-2", "OK")]
    [TestCase("1235", "ON", "-2", "OK")]
    public void SettingsRequireBothBaselineValuesButDoNotJudgeIso(string ctl, string locked, string ev, string grade)
    {
        var check = Settings(Checks(estimate: Estimate(controls: ctl), acks: new[] { Ack() }));
        Assert.AreEqual(grade, check.Grade);
        StringAssert.Contains("追跡ロック " + locked + " / 露出 " + ev, check.Text);
    }
    [TestCase(85, "OK")]
    [TestCase(85.001, "OK")]
    [TestCase(84.999, "WARN")]
    [TestCase(100, "OK")]
    [TestCase(100.001, "WARN")]
    [TestCase(double.NaN, "WARN")]
    [TestCase(double.NegativeInfinity, "WARN")]
    [TestCase(double.PositiveInfinity, "WARN")]
    public void SettingsRespectMetricsFreshnessIncludingTheExactBoundary(double at, string grade)
    {
        foreach (string colour in new[] { "赤", "青" })
        {
            var check = Settings(Checks(estimate: Estimate(at: at), acks: new[] { Ack() }), colour);
            Assert.AreEqual(grade, check.Grade);
            if (grade == "WARN") StringAssert.Contains("追跡ロック 未確認 / 露出 未確認", check.Text);
        }
    }
    [TestCase(null)]
    [TestCase("")]
    [TestCase("123")]
    [TestCase("12350")]
    [TestCase("2235")]
    [TestCase("1335")]
    [TestCase("1245")]
    [TestCase("1238")]
    public void MissingOrInvalidControlsStayUnconfirmedDespiteDefaultsAck(string ctl)
    {
        var check = Settings(Checks(estimate: Estimate(controls: ctl), acks: new[] { Ack() }));
        Assert.AreEqual("WARN", check.Grade);
        StringAssert.Contains("追跡ロック 未確認 / 露出 未確認", check.Text);
    }
    [TestCase("127.0.0.1", "ios")]
    [TestCase("::1", "ios")]
    [TestCase("192.168.1.20", "android")]
    [TestCase("192.168.1.20", null)]
    public void LoopbackAndNonIosReportsCannotConfirmSettings(string sender, string platform)
    {
        var check = Settings(Checks(estimate: Estimate(sender: sender, platform: platform), acks: new[] { Ack(sender: sender) }));
        Assert.AreEqual("WARN", check.Grade);
        StringAssert.Contains("追跡ロック 未確認 / 露出 未確認", check.Text);
    }
    [Test] public void MissingEstimateOrPhoneCannotBeReplacedByAckHistory()
    {
        foreach (var estimate in new[] { null, new PhoneSaberLatencyEstimate(null, default, "192.168.1.20", .1, 100, 60, 0) })
        {
            var checks = PhoneSaberControlPanelHealth.Evaluate(100, Sending(), Sending(), estimate, estimate,
                50, 50, new[] { Ack() }, true, 0, 1, true).ToArray();
            foreach (string colour in new[] { "赤", "青" })
            {
                Assert.AreEqual("WARN", Settings(checks, colour).Grade);
                StringAssert.Contains("追跡ロック 未確認 / 露出 未確認", Settings(checks, colour).Text);
            }
        }
    }
    [TestCase("A", false)]
    [TestCase("B", false)]
    [TestCase("A", true)]
    [TestCase("B", true)]
    public void SeparateColourSendersNeverBorrowEachOthersSettings(string station, bool redGood)
    {
        foreach (string other in new[] { "0035", null })
        {
            var red = Estimate(sender: "192.168.1.20", controls: redGood ? "1235" : other, station: station);
            var blue = Estimate(sender: "192.168.1.21", controls: redGood ? other : "1235", station: station);
            var checks = PhoneSaberControlPanelHealth.Evaluate(100, Sending(sender: red.Sender), Sending(sender: blue.Sender), red, blue,
                50, 50, new[] { Ack(sender: red.Sender, station: station), Ack(sender: blue.Sender, station: station) }, true, 0, 1, true).ToArray();
            Assert.AreEqual(redGood ? "OK" : "WARN", Settings(checks, "赤").Grade);
            Assert.AreEqual(redGood ? "WARN" : "OK", Settings(checks, "青").Grade);
            StringAssert.Contains("追跡ロック ON / 露出 -2", Settings(checks, redGood ? "赤" : "青").Text);
            StringAssert.Contains(other == null ? "追跡ロック 未確認 / 露出 未確認" : "追跡ロック OFF / 露出 0",
                Settings(checks, redGood ? "青" : "赤").Text);
        }
    }
    [Test] public void RatesUseTenSecondsAndResetOnPauseCounterResetOrSenderChange()
    {
        var rates = new PhoneSaberControlPanelRates();
        for (int i = 0; i < 100; i++) rates.Sample(i * .1, i * 5, i * 6, "one");
        Assert.IsNull(rates.Red);
        rates.Sample(10, 500, 600, "one");
        Assert.AreEqual(50, rates.Red.Value, 1e-9);
        Assert.AreEqual(60, rates.Blue.Value, 1e-9);
        rates.Sample(10.7, 535, 642, "one");
        Assert.AreEqual(50, rates.Red.Value, 1e-9);
        rates.Sample(11, 550, 660, "two");
        Assert.IsNull(rates.Red);
        rates.Sample(11.2, 0, 0, "two");
        Assert.IsNull(rates.Blue);
        rates.Sample(20, 500, 600, "two");
        Assert.IsNull(rates.Red);
    }

    [Test] public void GameWindowExcludesMenusCountsGapOnceAndResetsPerSong()
    {
        var window = Observe(default, 0, 1, 10, false, 100, 5, 5);
        window = Observe(window, .1, 2, 11, true, 100, 5, 5);
        Assert.IsTrue(window.WholeSong);
        Assert.AreEqual(0, window.FramesOver50); // メニューから切り替わったフレームは除く。
        window = Observe(window, 1.1, 3, 11, true, 50, 2, 2);
        Assert.AreEqual(0, window.BothGaps); // 曲内の重なりがちょうど1秒。
        window = Observe(window, 1.2, 4, 11, true, 51, 2, 2);
        Assert.AreEqual(1, window.BothGaps);
        Assert.AreEqual(1, window.FramesOver50);
        window = Observe(window, 1.3, 5, 11, true, 51, 3, 3);
        Assert.AreEqual(1, window.BothGaps);
        Assert.AreEqual(2, window.FramesOver50);
        window = Observe(window, 1.4, 6, 11, true, 10, .1, 3);
        window = Observe(window, 3, 7, 11, true, 10, 2, 2);
        Assert.AreEqual(2, window.BothGaps);
        window = Observe(window, 3.1, 8, 12, false, 100, 3, 3);
        Assert.AreEqual(0, window.BothGaps);
        Assert.AreEqual(0, window.FramesOver50);
    }
    [Test] public void StartingOrResumingMidSongIsPartialAndDoesNotCountUnobservedFrames()
    {
        var window = Observe(default, 100, 200, 11, true, 100, 2, 2);
        Assert.IsFalse(window.WholeSong);
        Assert.AreEqual(0, window.FramesOver50);
        window = Observe(window, 105, 500, 11, true, 100, 2, 2);
        Assert.IsFalse(window.WholeSong);
        Assert.AreEqual(0, window.FramesOver50);
        Assert.AreEqual(0, window.BothGaps);
    }
    [Test] public void RatesExcludePacketsBeforeTheTenSecondBoundary()
    {
        var rates = new PhoneSaberControlPanelRates();
        rates.Sample(0, 0, 0, "one");
        for (int i = 1; i <= 10; i++) rates.Sample(i, 100, 100, "one");
        rates.Sample(10.5, 100, 100, "one");
        Assert.AreEqual(5, rates.Red.Value, 1e-9);
        rates.Sample(11, 100, 100, "one");
        Assert.AreEqual(0, rates.Red.Value, 1e-9);
    }
}
