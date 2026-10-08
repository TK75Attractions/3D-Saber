using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class PhoneSaberEndpointPredictorTests
{
    static void Near(Vector2 expected, Vector2 actual) =>
        Assert.Less(Vector2.Distance(expected, actual), 1e-5f);

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
    public void ConstantVelocityIsExactForIndependentEndpoints(int samples)
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
        Near(va * (float)(time + 0.04), a);
        Near(Vector2.one + vb * (float)(time + 0.04), b);
    }

    [Test]
    public void DisplacementIsClampedByDistanceAndHorizonIsCapped()
    {
        var predictor = new PhoneSaberEndpointPredictor(0.25f);
        predictor.AddSample(0, Vector2.zero, Vector2.zero);
        predictor.AddSample(0.02, new Vector2(3, 4), new Vector2(-4, 3));
        predictor.Predict(0.02, 1000, out var a, out var b);
        Near(new Vector2(3.15f, 4.2f), a);
        Near(new Vector2(-4.2f, 3.15f), b);
        var slow = new PhoneSaberEndpointPredictor();
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
        Near(Vector2.right * 1.04f, a); Near(Vector2.one, b);
    }

    [Test]
    public void StopsDecayToZeroWithoutExtrapolatingSampleAge()
    {
        var predictor = new PhoneSaberEndpointPredictor();
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

    [TestCase("1,2", "1,2")]
    [TestCase("ts=123;1,2,3,4", "1,2,3,4")]
    [TestCase("timestamp=123;1,2", "1,2")]
    [TestCase("ts=;1,2", "ts=;1,2")]
    [TestCase("timestamp=123", "timestamp=123")]
    public void OptionalTimestampStrippingPreservesExistingFormat(string packet, string expected)
    {
        var strip = typeof(InputPoint).GetMethod("StripOptionalTimestamp", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.AreEqual(expected, strip.Invoke(null, new object[] { packet }));
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
