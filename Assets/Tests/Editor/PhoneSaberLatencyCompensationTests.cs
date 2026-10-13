using System;
using NUnit.Framework;

public class PhoneSaberLatencyCompensationTests
{
    static PhoneSaberLatencyEstimate Estimate(double phoneMs = 34.4, double network = .7, double? display = null)
    {
        var phone = new PhoneSaberMetrics { C2sMedian = phoneMs, C2sCount = 300, Rtt = network * 2,
            RttCount = 8, RttAge = 0, ReceivedAt = 0, CaptureFps = 60, Route = "LAN" };
        var pc = new PhoneSaberTimingStats(new double[] { .3 });
#if COMPENSATION_BEFORE
        return new PhoneSaberLatencyEstimate(phone, pc, "phone", 0, 0, 60, 0);
#else
        return new PhoneSaberLatencyEstimate(phone, pc, "phone", 0, 0, 60, 0, display, 2);
#endif
    }

    [Test] public void NormalCoordinatesWithoutTsUseMeasuredTotal()
    {
#if COMPENSATION_BEFORE
        int lead = PhoneSaberPredictionSettings.AutomaticLead(default, false, out _);
#else
        var policy = new PhoneSaberAutomaticLeadPolicy();
        policy.Update(0, "phone", PhoneSaberPredictionSettings.AutomaticEstimate(Estimate()), null);
        int lead = policy.Lead;
        Assert.AreEqual("automatic-estimate", policy.Source);
#endif
        Assert.AreEqual(44, lead, "通常payloadにもスマホ34.4 + 通信0.7 + PC0.3 + 画面8.3を適用する");
    }

    [Test] public void MatchingDisplayMeasurementReplacesHalfRefresh()
    {
        var estimate = Estimate(display: 80);
        Assert.AreEqual(80, estimate.DisplayMs, "半周期8.3msではなく保存済みの画面残差を使う");
#if !COMPENSATION_BEFORE
        StringAssert.Contains("display-source=measured", estimate.Log());
        StringAssert.Contains("画面は実測（2分前）", estimate.Line("赤", 0));
#endif
    }

#if !COMPENSATION_BEFORE
    [Test] public void ThirtySecondMedianAndStrictFiveMsHysteresis()
    {
        var policy = new PhoneSaberAutomaticLeadPolicy();
        Assert.IsTrue(policy.Update(0, "phone", 40, null));
        for (int i = 1; i <= 30; i++) Assert.IsFalse(policy.Update(i, "phone", 45, null));
        Assert.AreEqual(40, policy.Lead, "5msちょうどでは更新しない");
        for (int i = 31; i <= 50; i++) policy.Update(i, "phone", 46, null);
        Assert.AreEqual(46, policy.Lead);
    }
    [Test] public void ChangesAreAtLeastTenSecondsApartAndClampIsExplicit()
    {
        var policy = new PhoneSaberAutomaticLeadPolicy();
        policy.Update(0, "phone", 20, null);
        for (int i = 1; i < 10; i++) { policy.Update(i, "phone", 200, null); Assert.AreEqual(20, policy.Lead); }
        Assert.IsTrue(policy.Update(10, "phone", 200, null));
        Assert.AreEqual(60, policy.Lead); Assert.IsTrue(policy.Clamped);
        for (int i = 11; i < 20; i++) { policy.Update(i, "phone", 0, null); Assert.AreEqual(60, policy.Lead); }
    }
    [Test] public void PerColourSourcesDisagreeAndSenderChangeClearsHistory()
    {
        var red = new PhoneSaberAutomaticLeadPolicy(); var blue = new PhoneSaberAutomaticLeadPolicy();
        red.Update(0, "red", 44, 2); blue.Update(0, "blue", null, 22);
        Assert.AreEqual(44, red.Lead); Assert.AreEqual(22, blue.Lead);
        Assert.AreEqual("one-way-ts", blue.Source);
        red.Update(1, "replacement", 55, null);
        Assert.AreEqual(55, red.Lead); Assert.AreEqual(22, blue.Lead);
        red.Update(17, "replacement", null, null);
        Assert.AreEqual(0, red.Lead); Assert.AreEqual("fallback", red.Source);
    }
    [Test] public void InvalidOrExpiredEstimateFallsBackWithoutUsingOtherColour()
    {
        var red = new PhoneSaberAutomaticLeadPolicy(); var blue = new PhoneSaberAutomaticLeadPolicy();
        red.Update(0, "phone", 44, null); blue.Update(0, "blue", 60, null);
        red.Update(15, "phone", double.NaN, double.PositiveInfinity);
        Assert.AreEqual(0, red.Lead); Assert.AreEqual(60, blue.Lead);
        var old = Estimate();
        var expired = new PhoneSaberLatencyEstimate(old.Phone, old.Pc, "phone", 0, 16, 60, 0);
        Assert.IsNull(PhoneSaberPredictionSettings.AutomaticEstimate(expired));
        Assert.IsNull(PhoneSaberPredictionSettings.AutomaticEstimate(
            new PhoneSaberLatencyEstimate(old.Phone, old.Pc, "127.0.0.1", 0, 0, 60, 0)));
        Assert.IsNull(PhoneSaberPredictionSettings.AutomaticEstimate(
            new PhoneSaberLatencyEstimate(old.Phone, old.Pc, "phone", 2, 0, 60, 0)));
    }
    [Test] public void NtpIsSecondSourceAndSourceChangeObeysRateLimit()
    {
        var p = new PhoneSaberAutomaticLeadPolicy();
        p.Update(0, "phone", 44, 3); Assert.AreEqual(44, p.Lead);
        p.Update(1, "phone", null, 3); Assert.AreEqual(44, p.Lead);
        p.Update(10, "phone", null, 3); Assert.AreEqual(3, p.Lead); Assert.AreEqual("one-way-ts", p.Source);
        p.Update(11, "phone", 55, 3); Assert.AreEqual(3, p.Lead);
        p.Update(20, "phone", 55, 3); Assert.AreEqual(55, p.Lead);
    }
    [Test] public void BriefPcWindowGapDoesNotFlapLeadButMissingExpiresWithinFifteenSeconds()
    {
        var p = new PhoneSaberAutomaticLeadPolicy();
        p.Update(0, "phone", 44, null);
        Assert.IsFalse(p.Update(1, "phone", null, null));
        Assert.AreEqual(44, p.Lead);
        Assert.IsFalse(p.Update(2, "phone", 44, null));
        Assert.IsFalse(p.NeedsExpiry(16.9));
        Assert.IsFalse(p.Update(16.9, "phone", null, null));
        Assert.IsTrue(p.NeedsExpiry(17), "秒1回の通常更新を待たず期限を処理できる");
        Assert.IsTrue(p.Update(17, "phone", null, null));
        Assert.AreEqual(0, p.Lead);
    }

    [Test] public void CachedMetricExpiryAndDeviceChangeNeverExtendOldLead()
    {
        var p = new PhoneSaberAutomaticLeadPolicy();
        p.Update(0, "phone", 44, null, 10, "ios:42");
        Assert.IsFalse(p.Update(9, "phone", 44, null, 10, "ios:42"));
        Assert.IsTrue(p.Update(10, "phone", 44, null, 10, "ios:42"));
        Assert.AreEqual(0, p.Lead, "古い計測を毎秒読んでも寿命を延長しない");
        Assert.IsFalse(p.NeedsExpiry(10), "fallback中は毎frameの統計再読を避ける");
        p.Update(11, "phone", 44, null, 20, "ios:42");
        Assert.IsFalse(p.Update(12, "phone", null, null, device: null));
        Assert.IsTrue(p.Update(13, "phone", 20, null, 25, "android:50"));
        Assert.AreEqual(20, p.Lead, "同じIPでも別アプリ/端末の履歴を混ぜない");
    }

    [Test] public void RevokingNtpConfirmationStopsUsingTheOldClockDifferenceImmediately()
    {
        var p = new PhoneSaberAutomaticLeadPolicy();
        p.Update(0, "phone", null, 22);
        Assert.IsTrue(p.Update(1, "phone", null, null, 1));
        Assert.AreEqual(0, p.Lead);
        Assert.AreEqual("fallback", p.Source);
    }

    static PhoneSaberTimingStats Timing(double value, int count = 15)
    {
        var samples = new double[count]; for (int i = 0; i < count; i++) samples[i] = value;
        return new PhoneSaberTimingStats(samples);
    }
    [Test] public void DisplayDecompositionSubtractsOnlyTermsInTheLoop()
    {
        var m = new PhoneSaberDisplayMeasurement(Timing(100), Estimate().Phone, Timing(2), 0);
        Assert.IsTrue(m.Valid); Assert.AreEqual(100 - 34.4 - .7 - 500.0 / 60 - 2, m.DisplayMs, 1e-9);
        StringAssert.Contains("exposure-source=half-camera-frame-estimate", m.Log());
        StringAssert.Contains("この PC とこの画面", m.Result);
    }
    [Test] public void DisplayRejectsMissingPhoneSparseStaleAndImplausibleBlocks()
    {
        Assert.AreEqual("missing-phone-metrics", new PhoneSaberDisplayMeasurement(Timing(100), null, Timing(2), 0).Reason);
        Assert.IsFalse(new PhoneSaberDisplayMeasurement(Timing(100), Estimate().Phone, Timing(2), 16).Valid);
        Assert.AreEqual("too-few-samples", new PhoneSaberDisplayMeasurement(Timing(100, 14), Estimate().Phone, Timing(2, 14), 0).Reason);
        Assert.IsFalse(new PhoneSaberDisplayMeasurement(Timing(100), Estimate().Phone, Timing(2, 14), 0).Valid);
        foreach (double value in new double[] { 20, 1000, double.NaN, double.PositiveInfinity })
            Assert.IsFalse(new PhoneSaberDisplayMeasurement(Timing(value), Estimate().Phone, Timing(2), 0).Valid);
        var changed = new PhoneSaberDisplayMeasurement(Timing(100), Estimate().Phone, Timing(2), 0, true);
        Assert.AreEqual("conditions-changed", changed.Reason);
        Assert.IsFalse(changed.Valid); StringAssert.Contains("変わりました", changed.Result);
        var p = Estimate().Phone; p.RttCount = 1;
        Assert.IsFalse(new PhoneSaberDisplayMeasurement(Timing(100), p, Timing(2), 0).Valid);
        p = Estimate().Phone; p.CaptureFps = null;
        Assert.IsFalse(new PhoneSaberDisplayMeasurement(Timing(100), p, Timing(2), 0).Valid);
    }
    [Test] public void DisplayPersistenceKeepsStationConfigurationAndAge()
    {
        string station = "CMP" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string key = PhoneSaberDisplayMeasurementSettings.Key(station);
        try
        {
            PhoneSaberDisplayMeasurementSettings.Save(station, "display-A", 80, 1000);
            PhoneSaberDisplayMeasurementSettings.Save(station, "display-B", 20, 1100);
            Assert.IsTrue(PhoneSaberDisplayMeasurementSettings.Load(station, "display-A", 1120, out var value, out var age));
            Assert.AreEqual(80, value); Assert.AreEqual(2, age);
            Assert.IsFalse(PhoneSaberDisplayMeasurementSettings.Load(station, "display-C", 1120, out _, out _));
            Assert.IsFalse(PhoneSaberDisplayMeasurementSettings.Load(station + "-x", "display-A", 1120, out _, out _));
            Assert.IsFalse(PhoneSaberDisplayMeasurementSettings.Load(station, "display-A", 999, out _, out _));
            foreach (string bad in new[] { "", "display-A|NaN|1000", "display-A|501|1000", "display-A|20|NaN" })
                Assert.IsFalse(PhoneSaberDisplayMeasurementSettings.Decode(bad, "display-A", 1120, out _, out _));
        }
        finally { UnityEngine.PlayerPrefs.DeleteKey(key + ".display-A"); UnityEngine.PlayerPrefs.DeleteKey(key + ".display-B"); }
    }
#endif
}
