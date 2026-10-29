using System;
using System.Reflection;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEngine;

public class PhoneSaberEndpointPredictorTests
{
    [Test]
    public void SharedOfflineVectorsMatchFloatBits()
    {
        // Python/.NET と同じ固定ベクトル。生成はテスト中に行わない。
        string path = Path.Combine(Application.dataPath, "..", "PhoneSaber", "tools", "fixtures", "endpoint_predictor_vectors.csv");
        PhoneSaberEndpointPredictor predictor = default;
        int predictions = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            string[] row = line.Split(',');
            Func<int, double> number = i => double.Parse(row[i], CultureInfo.InvariantCulture);
            switch (row[0])
            {
                case "op": break;
                case "new": predictor = new PhoneSaberEndpointPredictor((float)number(1)); break;
                case "reset": predictor.Reset(); break;
                case "add":
                    predictor.AddSample(number(1), new Vector2((float)number(2), (float)number(3)),
                        new Vector2((float)number(4), (float)number(5)));
                    break;
                case "predict":
                    predictor.Predict(number(1), (int)number(6), out var a, out var b);
                    float[] actual = { a.x, a.y, b.x, b.y };
                    for (int axis = 0; axis < 4; axis++)
                    {
                        uint expectedBits = Convert.ToUInt32(row[axis + 7], 16);
                        uint actualBits = BitConverter.ToUInt32(BitConverter.GetBytes(actual[axis]), 0);
                        // ±0 は位置として同じ。Unity の Mono と .NET で 0 の符号だけ異なる場合がある（10-09 に確認）。
                        bool bothZero = (expectedBits & 0x7fffffffu) == 0 && (actualBits & 0x7fffffffu) == 0;
                        if (!bothZero) Assert.AreEqual(expectedBits, actualBits, $"予測 {predictions} 成分 {axis}");
                    }
                    predictions++;
                    break;
                default: Assert.Fail("不明な共有ベクトル操作: " + row[0]); break;
            }
        }
        Assert.Greater(predictions, 200);
    }

    static void Near(Vector2 expected, Vector2 actual) =>
        Assert.Less(Vector2.Distance(expected, actual), 1e-5f);

    [TestCase(0.0)]
    [TestCase(1.0)]
    public void FirstIntervalAfterStartOrGapDoesNotExtendJump(double start)
    {
        PhoneSaberEndpointPredictor predictor = default;
        if (start > 0)
        {
            predictor.AddSample(0, Vector2.zero, Vector2.zero);
            predictor.AddSample(0.02, Vector2.one * 0.02f, Vector2.one * 0.02f);
        }
        predictor.AddSample(start, Vector2.zero, Vector2.zero);
        var a = new Vector2(3, 4); var b = new Vector2(-4, 3);
        predictor.AddSample(start + 0.02, a, b);
        predictor.Predict(start + 0.02, 60, out var predictedA, out var predictedB);
        Near(a, predictedA); Near(b, predictedB);
        Assert.IsTrue(predictor.LastPredictionSuppressed);
    }

    [Test]
    public void AutomaticLeadRejectsUnsynchronizedNegativeStaleAndMissingMeasurements()
    {
        var values = new double[PhoneSaberStatsDisplay.MinimumTimingSamples];
        for (int i = 0; i < values.Length; i++) values[i] = 32;
        var measured = new PhoneSaberInputStats(true, 60, .01, "", true,
            oneWayDelay: new PhoneSaberTimingStats(values));
        Assert.AreEqual(32, PhoneSaberPredictionSettings.AutomaticLead(measured, true, out bool used));
        Assert.IsTrue(used);
        Assert.AreEqual(0, PhoneSaberPredictionSettings.AutomaticLead(measured, false, out used));
        Assert.IsFalse(used);
        Assert.AreEqual(0, PhoneSaberPredictionSettings.AutomaticLead(default, true, out used));
        var stale = new PhoneSaberInputStats(true, 60, .2, "", true, oneWayDelay: measured.OneWayDelay);
        Assert.AreEqual(0, PhoneSaberPredictionSettings.AutomaticLead(stale, true, out used));
        values[0] = -1;
        var negative = new PhoneSaberInputStats(true, 60, .01, "", true,
            oneWayDelay: new PhoneSaberTimingStats(values));
        Assert.AreEqual(0, PhoneSaberPredictionSettings.AutomaticLead(negative, true, out used));
        for (int i = 0; i < values.Length; i++) values[i] = 135;
        var high = new PhoneSaberInputStats(true, 60, .01, "", true,
            oneWayDelay: new PhoneSaberTimingStats(values));
        Assert.AreEqual(60, PhoneSaberPredictionSettings.AutomaticLead(high, true, out used));
    }

    [Test]
    public void AutomaticModePersistsIndependentlyAndManualSelectionClearsIt()
    {
        string station = "auto-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        var go = new GameObject("自動予測設定テスト");
        go.SetActive(false);
        var input = go.AddComponent<InputPoint>();
        typeof(InputPoint).GetField("phoneSaberStation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, station);
        try
        {
            Assert.IsTrue(PhoneSaberPredictionSettings.LoadAutomatic(station)); // 未保存の台は自動（10-18）
            input.SetPredictionHorizonMilliseconds(0);
            Assert.IsFalse(PhoneSaberPredictionSettings.LoadAutomatic(station)); // OFF を選べば保存される
            input.SetPredictionAutomatic();
            Assert.IsTrue(input.PredictionAutomatic);
            Assert.IsTrue(PhoneSaberPredictionSettings.LoadAutomatic(station));
            Assert.AreEqual(0, input.PredictionHorizonMilliseconds);
            input.RecordPrediction(true); input.RecordPrediction(false);
            Assert.AreEqual(2, input.PredictionDraws);
            Assert.AreEqual(1, input.PredictionSuppressedDraws);
            input.SetPredictionHorizonMilliseconds(0);
            Assert.IsFalse(input.PredictionAutomatic);
            Assert.IsFalse(PhoneSaberPredictionSettings.LoadAutomatic(station));
            Assert.AreEqual(0, input.PredictionHorizonMilliseconds);
        }
        finally
        {
            PlayerPrefs.DeleteKey(PhoneSaberPredictionSettings.Key(station));
            PlayerPrefs.DeleteKey(PhoneSaberPredictionSettings.AutomaticKey(station));
            PlayerPrefs.Save();
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void DecelerationDampsEachAxisButAccelerationKeepsConservativeSpeed()
    {
        var predictor = new PhoneSaberEndpointPredictor();
        predictor.AddSample(0, Vector2.zero, Vector2.zero);
        predictor.AddSample(0.02, new Vector2(0.04f, 0.02f), new Vector2(-0.04f, -0.02f));
        predictor.AddSample(0.04, new Vector2(0.06f, 0.06f), new Vector2(-0.06f, -0.06f));
        predictor.Predict(0.04, 40, out var a, out var b);
        Near(new Vector2(0.08f, 0.10f), a);
        Near(new Vector2(-0.08f, -0.10f), b);
    }

    [Test]
    public void OffReturnsIdenticalFloatBitsIncludingSignedZero()
    {
        var predictor = new PhoneSaberEndpointPredictor();
        var a = new Vector2(BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0), 1.234567f);
        var b = new Vector2(0.125f, -2.75f);
        predictor.AddSample(0, Vector2.one, Vector2.zero);
        predictor.AddSample(0.02, a, b);
        predictor.Predict(0.025, 0, out var predictedA, out var predictedB);
        CollectionAssert.AreEqual(BitConverter.GetBytes(a.x), BitConverter.GetBytes(predictedA.x));
        CollectionAssert.AreEqual(BitConverter.GetBytes(a.y), BitConverter.GetBytes(predictedA.y));
        CollectionAssert.AreEqual(BitConverter.GetBytes(b.x), BitConverter.GetBytes(predictedB.x));
        CollectionAssert.AreEqual(BitConverter.GetBytes(b.y), BitConverter.GetBytes(predictedB.y));
    }

    [TestCase(2)]
    [TestCase(3)]
    public void ConstantVelocityWaitsForThreeSamplesThenIsExactForIndependentEndpoints(int samples)
    {
        var predictor = new PhoneSaberEndpointPredictor();
        var va = new Vector2(2, -1);
        var vb = new Vector2(-1, 3);
        double time = 0;
        for (int i = 0; i < samples; i++)
        {
            time = i * 0.025;
            predictor.AddSample(time, va * (float)time, Vector2.one + vb * (float)time);
        }
        predictor.Predict(time, 40, out var a, out var b);
        double target = samples == 2 ? time : time + 0.04;
        Near(va * (float)target, a);
        Near(Vector2.one + vb * (float)target, b);
    }

    [Test]
    public void DisplacementIsClampedByDistanceAndHorizonIsCapped()
    {
        var predictor = new PhoneSaberEndpointPredictor(0.25f);
        predictor.AddSample(-0.02, new Vector2(-3, -4), new Vector2(4, -3));
        predictor.AddSample(0, Vector2.zero, Vector2.zero);
        predictor.AddSample(0.02, new Vector2(3, 4), new Vector2(-4, 3));
        predictor.Predict(0.02, 1000, out var a, out var b);
        Near(new Vector2(3.15f, 4.2f), a);
        Near(new Vector2(-4.2f, 3.15f), b);
        var slow = new PhoneSaberEndpointPredictor();
        slow.AddSample(-0.02, Vector2.right * -0.02f, Vector2.zero);
        slow.AddSample(0, Vector2.zero, Vector2.zero);
        slow.AddSample(0.02, Vector2.right * 0.02f, Vector2.zero);
        slow.Predict(0.02, 1000, out a, out b);
        Near(Vector2.right * 0.08f, a);
    }

    [Test]
    public void GapRestartsHistoryAndNeedsAnotherSample()
    {
        var predictor = new PhoneSaberEndpointPredictor();
        predictor.AddSample(0, Vector2.zero, Vector2.zero);
        predictor.AddSample(0.02, Vector2.right * 0.02f, Vector2.zero);
        predictor.AddSample(0.121, Vector2.right, Vector2.one);
        predictor.Predict(0.121, 60, out var a, out var b);
        Near(Vector2.right, a); Near(Vector2.one, b);
        predictor.AddSample(0.141, Vector2.right * 1.02f, Vector2.one);
        predictor.Predict(0.141, 20, out a, out b);
        Near(Vector2.right * 1.02f, a); Near(Vector2.one, b);
        predictor.AddSample(0.161, Vector2.right * 1.04f, Vector2.one);
        predictor.Predict(0.161, 20, out a, out b);
        Near(Vector2.right * 1.06f, a); Near(Vector2.one, b);
    }

    [Test]
    public void StopsDecayToZeroWithoutExtrapolatingSampleAge()
    {
        var predictor = new PhoneSaberEndpointPredictor();
        predictor.AddSample(-0.02, Vector2.right * -0.02f, Vector2.one);
        predictor.AddSample(0, Vector2.zero, Vector2.one);
        predictor.AddSample(0.02, Vector2.right * 0.02f, Vector2.one);
        predictor.Predict(0.04, 60, out var a, out _);
        Near(Vector2.right * 0.08f, a);
        double halfway = 0.02 + (PhoneSaberEndpointPredictor.DecayStartSeconds + 0.1) * 0.5;
        predictor.Predict(halfway, 60, out a, out _);
        Near(Vector2.right * 0.05f, a);
        predictor.Predict(0.121, 60, out a, out var b);
        Near(Vector2.right * 0.02f, a); Near(Vector2.one, b);
    }

    [Test]
    public void ReversalAndSingleIntervalSpikeDoNotExtendOldVelocity()
    {
        var predictor = new PhoneSaberEndpointPredictor();
        predictor.AddSample(0, Vector2.zero, Vector2.zero);
        predictor.AddSample(0.02, Vector2.right * 0.02f, Vector2.right * 0.02f);
        predictor.AddSample(0.04, Vector2.right * 0.01f, Vector2.right * 0.2f);
        predictor.Predict(0.04, 40, out var a, out var b);
        Near(Vector2.right * 0.01f, a); Near(Vector2.right * 0.24f, b);
    }

    [Test]
    public void DuplicateOutOfOrderAndInvalidSamplesCannotChangeLatestEndpoints()
    {
        var predictor = new PhoneSaberEndpointPredictor();
        predictor.AddSample(1, Vector2.one, Vector2.right);
        predictor.AddSample(1, Vector2.zero, Vector2.zero);
        predictor.AddSample(0.9, Vector2.zero, Vector2.zero);
        predictor.AddSample(double.NaN, Vector2.zero, Vector2.zero);
        predictor.AddSample(1.02, new Vector2(float.NaN, 0), Vector2.zero);
        predictor.Predict(1.03, 60, out var a, out var b);
        Near(Vector2.one, a); Near(Vector2.right, b);
        predictor.Reset();
        predictor.AddSample(2, Vector2.zero, Vector2.zero);
        predictor.Predict(2, 60, out a, out b);
        Near(Vector2.zero, a); Near(Vector2.zero, b);
    }

    [Test]
    public void StationSettingsDefaultOffAndRemainIndependent()
    {
        string first = "pred-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        string second = "pred-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        try
        {
            Assert.AreEqual(0, PhoneSaberPredictionSettings.Load(first));
            PhoneSaberPredictionSettings.Save(first, 40);
            Assert.AreEqual(40, PhoneSaberPredictionSettings.Load(first));
            Assert.AreEqual(0, PhoneSaberPredictionSettings.Load(second));
            PhoneSaberPredictionSettings.Save(second, 999);
            Assert.AreEqual(60, PhoneSaberPredictionSettings.Load(second));
            PhoneSaberPredictionSettings.Save(first, -1);
            Assert.AreEqual(0, PhoneSaberPredictionSettings.Load(first));
        }
        finally
        {
            PlayerPrefs.DeleteKey(PhoneSaberPredictionSettings.Key(first));
            PlayerPrefs.DeleteKey(PhoneSaberPredictionSettings.Key(second));
            PlayerPrefs.Save();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PublishedEndpointFlagAndTimestampFollowAppliedSnapshot(bool blue)
    {
        var go = new GameObject("受信スナップショットテスト");
        go.SetActive(false); // Editor のテストからソケットや外部プロセスを開始しない。
        var input = go.AddComponent<InputPoint>();
        input.enabled = false;
        try
        {
            input.useDirectWorldMapping = true;
            input.sensitivity = 1;
            input.worldScale = Vector2.one;
            string suffix = blue ? "2" : "";
            Action<string, object> set = (name, value) => typeof(InputPoint)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, value);
            var update = typeof(InputPoint).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            set("hasNewData" + suffix, true);
            set("hasStickData" + suffix, true);
            set("rawReceiveTimestampTicks" + suffix, 123L);
            set(blue ? "rawX2a" : "rawX1a", -0.5f);
            set(blue ? "rawX2b" : "rawX1b", 0.5f);
            update.Invoke(input, null);
            Assert.IsTrue(blue ? input.HasValidStickEndpoints2 : input.HasValidStickEndpoints);
            Near(new Vector2(-0.5f, 0), blue ? input.LocalStickRawA2 : input.LocalStickRawA);
            Assert.AreEqual(SwingMonotonicClock.ToSeconds(123),
                blue ? input.LastReceivedMonotonicTime2 : input.LastReceivedMonotonicTime);

            set("hasNewData" + suffix, true);
            set("hasStickData" + suffix, false);
            set("rawReceiveTimestampTicks" + suffix, 456L);
            set("rawX" + suffix, 0.25f);
            update.Invoke(input, null);
            Assert.IsFalse(blue ? input.HasValidStickEndpoints2 : input.HasValidStickEndpoints);
            Near(new Vector2(0.25f, 0), blue ? input.LocalPosition2 : input.LocalPosition);
            Assert.AreEqual(SwingMonotonicClock.ToSeconds(456),
                blue ? input.LastReceivedMonotonicTime2 : input.LastReceivedMonotonicTime);
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // 旧 InputPoint.StripOptionalTimestamp と同じ規則を、受信で使う PhoneSaberPacketParser で確認する。
    // ts=/timestamp= は ';' までを外し、空の時刻や ';' のない形式は座標として受理しない。
    [TestCase("1,2", true, false, 1f, 2f, 0f, 0f)]
    [TestCase("ts=123;1,2,3,4", true, true, 1f, 2f, 3f, 4f)]
    [TestCase("timestamp=123;1,2", true, false, 1f, 2f, 0f, 0f)]
    [TestCase("ts=;1,2", false, false, 0f, 0f, 0f, 0f)]
    [TestCase("timestamp=123", false, false, 0f, 0f, 0f, 0f)]
    public void OptionalTimestampStrippingPreservesExistingFormat(string packet, bool parsed, bool stick,
        float a, float b, float c, float d)
    {
        var parser = new PhoneSaberPacketParser();
        bool ok = parser.TryParse(System.Text.Encoding.ASCII.GetBytes(packet), out bool isStick,
            out float pa, out float pb, out float pc, out float pd, out double? _);
        Assert.AreEqual(parsed, ok);
        if (!parsed) return;
        Assert.AreEqual(stick, isStick);
        Assert.AreEqual(a, pa); Assert.AreEqual(b, pb);
        if (stick) { Assert.AreEqual(c, pc); Assert.AreEqual(d, pd); }
    }

    [Test]
    public void CameraHistoryKeepsReceivedEndpointsWhileBladeUsesPrediction()
    {
        var go = new GameObject("予測と観測の分離テスト");
        go.SetActive(false);
        var input = go.AddComponent<InputPoint>();
        input.enabled = false;
        var bridge = go.AddComponent<SaberInputBridge>();
        float previousTimeScale = Time.timeScale;
        try
        {
            Time.timeScale = 1;
            typeof(InputPoint).GetProperty("LastReceivedMonotonicTime").SetValue(input, 1.0);
            var observedA = new Vector3(-1, 0, 0);
            var observedB = new Vector3(1, 0, 0);
            var predictedA = observedA + Vector3.right * 0.2f;
            var predictedB = observedB + Vector3.right * 0.2f;
            bridge.OverrideBlade(predictedA, predictedB);
            typeof(SaberInputBridge).GetMethod("RecordCameraSample", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(bridge, new object[] { input, observedA, observedB });
            var sample = (CameraSaberSample)typeof(SaberInputBridge)
                .GetField("cameraSample", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bridge);
            Assert.AreEqual(observedA, sample.EndA); Assert.AreEqual(observedB, sample.EndB);
            Assert.AreEqual(1.0, sample.ReceiveTime);
            Assert.AreEqual(predictedA, bridge.WorldEndA); Assert.AreEqual(predictedB, bridge.WorldEndB);
        }
        finally
        {
            Time.timeScale = previousTimeScale;
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
}

public class PhoneSaberAdaptivePredictionTests
{
    static Vector2 A(float x) => new Vector2(x, -.5f);
    static Vector2 B(float x) => new Vector2(x, .5f);
    static void Predict(ref PhoneSaberEndpointPredictor p, double now, out Vector2 a, out Vector2 b)
    {
        p.PredictAdaptive(now, 60, out a, out b);
    }

    [Test]
    public void BothPacketRatesRecoverTheMeasuredTotalBeyondSixtyMs()
    {
        foreach (int rate in new[] { 60, 30 })
        {
            PhoneSaberEndpointPredictor p = default;
            for (int i = 0; i <= 20; i++) p.AddSample(i / (double)rate, A(i * 10f / rate), B(i * 10f / rate));
            double now = 20.0 / rate + .008;
            p.PredictAdaptive(now, 106, out var a, out var b);
            Assert.AreEqual(200f / rate + 1.14f, a.x, .002f, $"{rate}fps 合計106ms＋受信age8ms");
            Assert.AreEqual(a.x, b.x);
            p.Predict(now, 60, out var manual, out _);
            Assert.AreEqual(200f / rate + .35f, manual.x, .00001f, "手動は従来の60ms/0.35world上限");
        }
    }

    // 2026-10-29 台B: 速い振り（50 world/s）は既定の 2 world で頭打ち。台ごとの上限 4 なら 2 倍まで先回りする。
    [Test]
    public void FastSwingsReachTheStationLimitAndSlowSwingsDoNotChange()
    {
        PhoneSaberEndpointPredictor fast = default;
        for (int i = 0; i <= 20; i++) fast.AddSample(i / 60.0, A(i * 50f / 60), B(i * 50f / 60));
        double now = 20.0 / 60 + .008;
        float last = 20 * 50f / 60;
        fast.PredictAdaptive(now, 106, out var standard, out _);
        Assert.AreEqual(last + PhoneSaberEndpointPredictor.AutomaticMaximumDisplacement, standard.x, .002f, "既定は 2 world で頭打ち");
        fast.PredictAdaptive(now, 106, out var wide, out var wideB, 4);
        Assert.AreEqual(last + 4, wide.x, .002f);
        Assert.AreEqual(wide.x, wideB.x);
        fast.PredictAdaptive(now, 106, out var invalid, out _, float.NaN);
        Assert.AreEqual(standard.x, invalid.x, .00001f, "範囲外の上限は既定の 2");
        PhoneSaberEndpointPredictor slow = default;
        for (int i = 0; i <= 20; i++) slow.AddSample(i / 60.0, A(i * 10f / 60), B(i * 10f / 60));
        slow.PredictAdaptive(now, 106, out var slow2, out _);
        slow.PredictAdaptive(now, 106, out var slow4, out _, 4);
        Assert.AreEqual(slow2.x, slow4.x, .00001f, "上限に届かない振りは変わらない");
        fast.PredictAdaptive(now, 106, out var widest, out _, 6);
        Assert.AreEqual(last + 50 * (.106f + .008f), widest.x, .002f, "6 world なら 50 world/s・106+8 ms をそのまま先回りする");
        // 保存値は 2〜6 の整数だけ（未保存・範囲外は従来の 2）。
        Assert.AreEqual(2f, PhoneSaberPredictionSettings.ClampReach(float.NaN));
        Assert.AreEqual(2f, PhoneSaberPredictionSettings.ClampReach(2.9f));
        Assert.AreEqual(3f, PhoneSaberPredictionSettings.ClampReach(3f));
        Assert.AreEqual(4f, PhoneSaberPredictionSettings.ClampReach(4.5f));
        Assert.AreEqual(5f, PhoneSaberPredictionSettings.ClampReach(5f));
        Assert.AreEqual(6f, PhoneSaberPredictionSettings.ClampReach(99f));
        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5, 6 }, PhoneSaberPredictionSettings.ReachChoices);
    }

    [Test]
    public void SupportedAccelerationCannotLeapBeyondObservedTravelWindow()
    {
        var p = new PhoneSaberEndpointPredictor(100);
        for (int i = 0; i <= 4; i++) p.AddSample(i / 30.0, A(i * i), B(i * i));
        p.PredictAdaptive(4 / 30.0, 106, out var a, out var b);
        Assert.AreEqual(41.44f, a.x, .001f, "観測窓の平均速度120の2倍×106msまで延長する");
        Assert.AreEqual(a.x, b.x);
    }

    [Test]
    public void SupportedDecelerationStopsAtTheVertexWithoutInventingAReversal()
    {
        var p = new PhoneSaberEndpointPredictor(100);
        for (int i = 0; i <= 8; i++)
        {
            double t = (i - 8) / 60.0;
            float x = (float)(40*t - 500*t*t);
            p.AddSample(t, A(x), B(x));
        }
        p.PredictAdaptive(0, 120, out var a, out _);
        Assert.AreEqual(.8f, a.x, .002f, "v=40,a=-1000 の40ms後の停止位置で止まる");
        p.PredictAdaptive(.008, 120, out var later, out _);
        Assert.AreEqual(a.x, later.x, .002f);
        p.AddSample(.02, A(-.2f), B(-.2f));
        p.PredictAdaptive(.02, 120, out var reversed, out _);
        Assert.LessOrEqual(reversed.x, -.2f, "反転受信後に古い外向き速度を延長しない");
    }

    [Test]
    public void MissingSixtyFpsPacketsShrinkLeadBeforeTheOldThirtyFpsDeadline()
    {
        PhoneSaberEndpointPredictor p = default;
        for (int i = 0; i <= 12; i++) p.AddSample(i / 60.0, A(i / 6f), B(i / 6f));
        p.PredictAdaptive(.208, 106, out var fresh, out _);
        p.PredictAdaptive(.25, 106, out var missing, out _);
        Assert.Less(missing.x, fresh.x, "60fpsの欠測を30fpsの期限まで延長しない");
        p.PredictAdaptive(.30, 106, out var stale, out _);
        Assert.AreEqual(2, stale.x, .00001f);
    }

    [Test]
    public void IndependentStructCopiesAndResetDoNotShareFitHistory()
    {
        PhoneSaberEndpointPredictor p = default;
        for (int i = 0; i <= 12; i++) p.AddSample(i / 60.0, A(i / 6f), B(i / 6f));
        var copy = p;
        copy.Reset(); copy.AddSample(.21, A(-4), B(-4));
        p.PredictAdaptive(.208, 106, out var original, out _);
        copy.PredictAdaptive(.218, 106, out var reset, out _);
        Assert.Greater(original.x, 3); Assert.AreEqual(-4, reset.x);
    }

    [Test]
    public void StillNoiseDoesNotAcquireLead()
    {
        PhoneSaberEndpointPredictor p = default;
        for (int i = 0; i < 4; i++) p.AddSample(i / 30.0, A(i * .005f), B(i * .005f));
        Predict(ref p, .1, out var a, out var b);
        Assert.AreEqual(.015f, a.x);
        Assert.AreEqual(.015f, b.x);
        Assert.IsTrue(p.LastPredictionSuppressed);
    }

    [Test]
    public void FourthSampleIsRequiredAfterStartAndLongGap()
    {
        foreach (double start in new[] { 0.0, 1.0 })
        {
            PhoneSaberEndpointPredictor p = default;
            p.AddSample(0, A(-3), B(-3));
            for (int i = 0; i < 3; i++) p.AddSample(start + i / 30.0, A(i * .1f), B(i * .1f));
            Predict(ref p, start + 2 / 30.0, out var a, out var b);
            Assert.AreEqual(.2f, a.x); Assert.AreEqual(.2f, b.x);
        }
    }

    [Test]
    public void SharedTranslationDoesNotAmplifyEndpointLengthNoise()
    {
        PhoneSaberEndpointPredictor p = default;
        for (int i = 0; i < 4; i++) p.AddSample(i / 30.0, A(i * .1f), B(i * .13f));
        Predict(ref p, .1, out var a, out var b);
        Assert.AreEqual(.39f - .3f, b.x - a.x, 1e-6);
        Assert.AreEqual(1f, b.y - a.y);
    }

    [Test]
    public void PacketAgeAdvancesShortHoldButStopsAtHardDeadline()
    {
        PhoneSaberEndpointPredictor p = default;
        for (int i = 0; i < 4; i++) p.AddSample(i / 30.0, A(i * .15f), B(i * .15f));
        Predict(ref p, .1, out var first, out _);
        Predict(ref p, .12, out var advanced, out _);
        Assert.Greater(advanced.x, first.x + .03f);
        Assert.LessOrEqual(Math.Abs(advanced.x - .45f), PhoneSaberEndpointPredictor.AutomaticMaximumDisplacement + .00001f);
        Predict(ref p, .200001, out var stale, out _);
        Assert.AreEqual(3 * .15f, stale.x);
        p.PredictAdaptive(.12, 0, out var off, out _);
        Assert.AreEqual(3 * .15f, off.x);
    }

    [Test]
    public void DuplicateInvalidAndNearlySimultaneousPacketsStayFinite()
    {
        PhoneSaberEndpointPredictor p = default;
        for (int i = 0; i < 4; i++) p.AddSample(i * 1e-10, A(i), B(i));
        p.AddSample(1e-10, A(99), B(99));
        p.AddSample(double.NaN, A(99), B(99));
        p.PredictAdaptive(.01, 60, out var a, out var b);
        Assert.AreEqual(3, a.x); Assert.AreEqual(3, b.x);
        p.Reset(); p.AddSample(1, A(-0f), B(2));
        p.PredictAdaptive(double.NaN, 60, out a, out b);
        Assert.AreEqual(0, a.x); Assert.AreEqual(2, b.x);
    }

    [Test]
    public void SharedVectorsMatchStandaloneBits()
    {
        string path = Path.Combine(Application.dataPath, "..", "PhoneSaber", "tools", "fixtures", "endpoint_catchup_vectors.csv");
        PhoneSaberEndpointPredictor p = default;
        int count = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            string[] row = line.Split(',');
            Func<int, double> number = i => {
                double value = double.Parse(row[i], CultureInfo.InvariantCulture);
                // MonoのParseが落とす負のゼロも、固定fixtureのbitどおりに渡す。
                return value == 0 && row[i].StartsWith("-", StringComparison.Ordinal) ?
                    BitConverter.Int64BitsToDouble(long.MinValue) : value;
            };
            if (row[0] == "new") p = new PhoneSaberEndpointPredictor((float)number(1));
            else if (row[0] == "reset") p.Reset();
            else if (row[0] == "add") p.AddSample(number(1), new Vector2((float)number(2), (float)number(3)), new Vector2((float)number(4), (float)number(5)));
            else if (row[0] == "predict")
            {
                p.PredictAdaptive(number(1), (int)number(6), out var a, out var b);
                float[] actual = { a.x, a.y, b.x, b.y };
                for (int axis = 0; axis < 4; axis++)
                {
                    uint bits = BitConverter.ToUInt32(BitConverter.GetBytes(actual[axis]), 0);
                    uint expected = Convert.ToUInt32(row[axis + 7], 16);
                    Assert.AreEqual(expected, bits, $"予測{count} 成分{axis}");
                }
                count++;
            }
        }
        Assert.Greater(count, 200);
    }
}
