using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class PhoneSaberEndpointFilterTests
{
    static void Near(Vector2 expected, Vector2 actual) => Assert.Less(Vector2.Distance(expected, actual), 1e-6f);

    [Test]
    public void OffPreservesBitsAndOrderIncludingSignedZeroAndInvalidInput()
    {
        var filter = new PhoneSaberEndpointFilter();
        filter.Apply(0, Vector2.zero, Vector2.one, 2, out _, out _);
        float negativeZero = BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0);
        var a = new Vector2(negativeZero, float.NaN);
        var b = new Vector2(float.PositiveInfinity, -2.75f);
        filter.Apply(double.NaN, a, b, 0, out var resultA, out var resultB);
        float[] expected = { a.x, a.y, b.x, b.y }, actual = { resultA.x, resultA.y, resultB.x, resultB.y };
        for (int i = 0; i < 4; i++)
            CollectionAssert.AreEqual(BitConverter.GetBytes(expected[i]), BitConverter.GetBytes(actual[i]));
        filter.Apply(.1, Vector2.one, Vector2.zero, 2, out resultA, out resultB);
        Assert.AreEqual(Vector2.one, resultA); Assert.AreEqual(Vector2.zero, resultB);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void ConstantInputAndSwapsRemainConstant(int mode)
    {
        var filter = new PhoneSaberEndpointFilter();
        var a = new Vector2(-1, .25f); var b = new Vector2(1, .5f);
        for (int i = 0; i < 120; i++)
        {
            filter.Apply(i / 30.0, i % 2 == 0 ? a : b, i % 2 == 0 ? b : a, mode, out var x, out var y);
            Assert.AreEqual(a, x); Assert.AreEqual(b, y);
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    public void StepResponseIsMonotonicAndBounded(int mode)
    {
        var filter = new PhoneSaberEndpointFilter();
        filter.Apply(0, new Vector2(-1, .25f), new Vector2(1, .5f), mode, out var previousA, out var previousB);
        for (int i = 1; i < 90; i++)
        {
            filter.Apply(i / 30.0, new Vector2(0, 1), new Vector2(2, 1.5f), mode, out var a, out var b);
            Assert.That(a.x, Is.InRange(previousA.x, 0f)); Assert.That(a.y, Is.InRange(previousA.y, 1f));
            Assert.That(b.x, Is.InRange(previousB.x, 2f)); Assert.That(b.y, Is.InRange(previousB.y, 1.5f));
            previousA = a; previousB = b;
        }
        Assert.Less(Vector2.Distance(new Vector2(0, 1), previousA), 1e-3f);
        Assert.Less(Vector2.Distance(new Vector2(2, 1.5f), previousB), 1e-3f);
    }

    [Test]
    public void MatchingUsesDistanceSumAndKeepsTies()
    {
        var filter = new PhoneSaberEndpointFilter();
        filter.Apply(0, Vector2.zero, new Vector2(2, 0), 1, out _, out _);
        filter.Apply(.03, new Vector2(-5, -5), new Vector2(-6, -6), 1, out var a, out var b);
        // 二乗距離和で入れ替える実装とは、次の共有ベクトルでも区別する。
        Assert.Greater(a.x, b.x);
        filter.Reset();
        filter.Apply(0, new Vector2(-1, 0), new Vector2(1, 0), 1, out _, out _);
        filter.Apply(.03, new Vector2(0, 1), new Vector2(0, -1), 1, out a, out b);
        Assert.Greater(a.y, 0); Assert.Less(b.y, 0);
    }

    [Test]
    public void DuplicateOutOfOrderGapModeChangeAndInvalidInputResetAsExpected()
    {
        var filter = new PhoneSaberEndpointFilter();
        filter.Apply(0, Vector2.zero, Vector2.one, 1, out _, out _);
        filter.Apply(.03, Vector2.right, Vector2.one + Vector2.right, 1, out var a, out var b);
        filter.Apply(.03, Vector2.zero, Vector2.zero, 1, out var duplicateA, out var duplicateB);
        Assert.AreEqual(a, duplicateA); Assert.AreEqual(b, duplicateB);
        filter.Apply(.02, Vector2.zero, Vector2.zero, 1, out duplicateA, out duplicateB);
        Assert.AreEqual(a, duplicateA); Assert.AreEqual(b, duplicateB);
        filter.Apply(.131, Vector2.one, Vector2.zero, 1, out a, out b);
        Assert.AreEqual(Vector2.one, a); Assert.AreEqual(Vector2.zero, b);
        filter.Apply(.15, Vector2.zero, Vector2.one, 2, out a, out b);
        Assert.AreEqual(Vector2.zero, a); Assert.AreEqual(Vector2.one, b);
        filter.Apply(.17, new Vector2(float.NaN, 0), Vector2.one, 2, out _, out _);
        filter.Apply(.18, Vector2.one, Vector2.zero, 2, out a, out b);
        Assert.AreEqual(Vector2.one, a); Assert.AreEqual(Vector2.zero, b);
    }

    [Test]
    public void SettingsDefaultMediumAndAreIndependentPerStation()
    {
        string first = "filter-" + Guid.NewGuid().ToString("N").Substring(0, 8), second = "filter-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            Assert.AreEqual(PhoneSaberEndpointFilter.Medium, PhoneSaberFilterSettings.Load(first));
            PhoneSaberFilterSettings.Save(first, 1);
            Assert.AreEqual(1, PhoneSaberFilterSettings.Load(first)); Assert.AreEqual(PhoneSaberEndpointFilter.Medium, PhoneSaberFilterSettings.Load(second));
            PhoneSaberFilterSettings.Save(second, 99);
            Assert.AreEqual(2, PhoneSaberFilterSettings.Load(second));
            PhoneSaberFilterSettings.Save(second, 3);
            Assert.AreEqual(3, PhoneSaberFilterSettings.Load(second));
            PhoneSaberFilterSettings.Save(first, -1);
            Assert.AreEqual(0, PhoneSaberFilterSettings.Load(first));
        }
        finally
        {
            PlayerPrefs.DeleteKey(PhoneSaberFilterSettings.Key(first)); PlayerPrefs.DeleteKey(PhoneSaberFilterSettings.Key(second));
            PlayerPrefs.Save();
        }
    }

    [Test]
    public void SteadyRejectsManualPredictionAndRestoresMediumWhenSwitching()
    {
        string station = "shape-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            PhoneSaberPredictionSettings.Save(station, 40);
            PhoneSaberPredictionSettings.SaveAutomatic(station, false);
            Assert.IsFalse(PhoneSaberFilterSettings.CanUseSteady(station));
            PhoneSaberFilterSettings.Save(station, 3);
            Assert.AreEqual(2, PhoneSaberFilterSettings.Load(station));
            // 以前保存された併用設定でも、起動時は「中」を使う。
            PlayerPrefs.SetInt(PhoneSaberFilterSettings.Key(station), 3);
            Assert.AreEqual(2, PhoneSaberFilterSettings.Load(station));
            PhoneSaberPredictionSettings.SaveAutomatic(station, true);
            PhoneSaberFilterSettings.Save(station, 3);
            Assert.AreEqual(3, PhoneSaberFilterSettings.Load(station));
            int revision = PhoneSaberFilterSettings.Revision;
            PhoneSaberPredictionSettings.SaveAutomatic(station, false);
            Assert.AreEqual(2, PhoneSaberFilterSettings.Load(station));
            Assert.Greater(PhoneSaberFilterSettings.Revision, revision, "稼働中のフィルタ履歴も再開する");
            PhoneSaberPredictionSettings.Save(station, 0);
            Assert.AreEqual(3, PhoneSaberFilterSettings.Load(station), "自動からOFFへ移る保存順でも形状安定を維持する");
            PhoneSaberFilterSettings.Save(station, 3);
            Assert.AreEqual(3, PhoneSaberFilterSettings.Load(station));
            PhoneSaberPredictionSettings.Save(station, 20);
            Assert.AreEqual(2, PhoneSaberFilterSettings.Load(station));
        }
        finally
        {
            PlayerPrefs.DeleteKey(PhoneSaberFilterSettings.Key(station));
            PlayerPrefs.DeleteKey(PhoneSaberPredictionSettings.Key(station));
            PlayerPrefs.DeleteKey(PhoneSaberPredictionSettings.AutomaticKey(station));
            PlayerPrefs.Save();
        }
    }

    [Test]
    public void SteadyKeepsMediumMidpointAndConvergesAfterShapeChange()
    {
        var medium = new PhoneSaberEndpointFilter();
        var steady = new PhoneSaberEndpointFilter();
        Vector2 a = Vector2.zero, b = Vector2.zero;
        for (int i = 0; i < 240; i++)
        {
            var rawA = i == 0 ? new Vector2(-1, -.125f) : new Vector2(0, .75f);
            var rawB = i == 0 ? new Vector2(1, .125f) : new Vector2(2, 1.25f);
            medium.Apply(i / 60.0, rawA, rawB, 2, out var ma, out var mb);
            steady.Apply(i / 60.0, rawA, rawB, 3, out a, out b);
            Assert.Less(Vector2.Distance((ma + mb) * .5f, (a + b) * .5f), 1e-6f);
            Assert.That((b.y - a.y) * .5f, Is.InRange(.124999f, .250001f));
        }
        Assert.Less(Vector2.Distance(new Vector2(0, .75f), a), 1e-3f);
        Assert.Less(Vector2.Distance(new Vector2(2, 1.25f), b), 1e-3f);
    }

    [Test]
    public void SteadyIsExplicitAndSwitchingRestartsHistory()
    {
        Assert.IsTrue(PhoneSaberControlPanelActions.IsValid("filter", "3"));
        Assert.IsFalse(PhoneSaberControlPanelActions.IsValid("filter", "4"));
        Assert.AreEqual("形状安定", PhoneSaberFilterSettings.Label(3));
        var filter = new PhoneSaberEndpointFilter();
        filter.Apply(0, Vector2.zero, Vector2.one, 2, out _, out _);
        filter.Apply(.016, Vector2.one, Vector2.zero, 3, out var a, out var b);
        Assert.AreEqual(Vector2.one, a); Assert.AreEqual(Vector2.zero, b);
        filter.Apply(.032, Vector2.zero, Vector2.one, 3, out a, out b);
        filter.Apply(.032, Vector2.right, Vector2.right, 3, out var duplicateA, out var duplicateB);
        Assert.AreEqual(a, duplicateA); Assert.AreEqual(b, duplicateB);
        filter.Apply(.2, Vector2.right, Vector2.left, 3, out a, out b);
        Assert.AreEqual(Vector2.right, a); Assert.AreEqual(Vector2.left, b);
    }

    [Test]
    public void SharedOfflineVectorsMatchActualFilterBits()
    {
        // オフラインPythonと.NETでも使用する固定入力。テスト中に生成しない。
        string path = Path.Combine(Application.dataPath, "..", "PhoneSaber", "tools", "fixtures", "endpoint_filter_vectors.csv");
        var filter = new PhoneSaberEndpointFilter();
        int count = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            string[] r = line.Split(',');
            if (r[0] == "time") continue;
            Func<int, double> number = i => double.Parse(r[i], CultureInfo.InvariantCulture);
            filter.Apply(number(0), new Vector2((float)number(1), (float)number(2)),
                new Vector2((float)number(3), (float)number(4)), (int)number(5), out var a, out var b);
            float[] actual = { a.x, a.y, b.x, b.y };
            for (int axis = 0; axis < 4; axis++)
            {
                uint expectedBits = Convert.ToUInt32(r[axis + 6], 16);
                uint actualBits = BitConverter.ToUInt32(BitConverter.GetBytes(actual[axis]), 0);
                // ±0 は位置として同じ。Unity の Mono と .NET で 0 の符号だけ異なる場合がある。
                bool bothZero = (expectedBits & 0x7fffffffu) == 0 && (actualBits & 0x7fffffffu) == 0;
                if (!bothZero) Assert.AreEqual(expectedBits, actualBits, $"フィルタ {count} 成分 {axis}");
            }
            count++;
        }
        Assert.Greater(count, 200);
    }

    [TestCase(1, 0)]
    [TestCase(2, 0)]
    [TestCase(1, 1)]
    [TestCase(2, 2)]
    public void BladeUpdateKeepsRawObservationAndOffEndpointBits(int stick, int mode)
    {
        var go = new GameObject("ブレード端点と観測履歴テスト");
        go.SetActive(false);
        var input = go.AddComponent<InputPoint>(); input.enabled = false;
        var bridge = go.AddComponent<SaberInputBridge>();
        bridge.stickIndex = stick; bridge.clampToBounds = false;
        string station = "filter-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var type = typeof(InputPoint);
        var instance = type.GetProperty("Instance");
        var previousInput = InputPoint.Instance;
        float previousScale = Time.timeScale;
        string suffix = stick == 2 ? "2" : "";
        Action<string, object> set = (name, value) => type.GetProperty(name).SetValue(input, value);
        var update = typeof(SaberInputBridge).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
        try
        {
            Time.timeScale = 1;
            instance.SetValue(null, input);
            type.GetField("phoneSaberStation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, station);
            PhoneSaberFilterSettings.Save(station, mode);
            set("HasValidStickEndpoints" + suffix, true);
            set("LastReceivedTime" + suffix, Time.realtimeSinceStartupAsDouble);
            set("LastReceivedMonotonicTime" + suffix, 1.0);
            set("LocalStickRawA" + suffix, new Vector2(-1, 0));
            set("LocalStickRawB" + suffix, new Vector2(1, 0));
            update.Invoke(bridge, null);
            float negativeZero = BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0);
            var rawA = new Vector2(negativeZero, .1f); var rawB = new Vector2(2, .1f);
            set("LastReceivedMonotonicTime" + suffix, 1.03);
            set("LocalStickRawA" + suffix, rawA); set("LocalStickRawB" + suffix, rawB);
            update.Invoke(bridge, null);
            var sample = (CameraSaberSample)typeof(SaberInputBridge)
                .GetField("cameraSample", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bridge);
            Assert.AreEqual(new Vector3(rawA.x, rawA.y, bridge.fixedZ), sample.EndA);
            Assert.AreEqual(new Vector3(rawB.x, rawB.y, bridge.fixedZ), sample.EndB);
            if (mode == 0)
            {
                float[] expected = { rawA.x, rawA.y, rawB.x, rawB.y };
                float[] actual = { bridge.WorldEndA.x, bridge.WorldEndA.y, bridge.WorldEndB.x, bridge.WorldEndB.y };
                for (int i = 0; i < 4; i++) CollectionAssert.AreEqual(BitConverter.GetBytes(expected[i]), BitConverter.GetBytes(actual[i]));
            }
            else Assert.Less(bridge.WorldEndA.x, rawA.x);
            var beforeA = bridge.WorldEndA; var beforeB = bridge.WorldEndB;
            update.Invoke(bridge, null);
            Assert.AreEqual(beforeA, bridge.WorldEndA); Assert.AreEqual(beforeB, bridge.WorldEndB);
        }
        finally
        {
            instance.SetValue(null, previousInput); Time.timeScale = previousScale;
            PlayerPrefs.DeleteKey(PhoneSaberFilterSettings.Key(station)); PlayerPrefs.Save();
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [TestCase(1, 1, 0)]
    [TestCase(2, 2, 0)]
    [TestCase(1, 0, 40)]
    [TestCase(2, 0, 40)]
    [TestCase(1, 1, 40)]
    [TestCase(2, 2, 40)]
    public void SenderChangesRestartOnlyTheSelectedSticksFilterAndPrediction(int stick, int mode, int horizon)
    {
        var go = new GameObject("送信元切替の端点履歴テスト");
        go.SetActive(false);
        var input = go.AddComponent<InputPoint>(); input.enabled = false;
        var bridge = go.AddComponent<SaberInputBridge>();
        bridge.stickIndex = stick; bridge.clampToBounds = false;
        string station = "source-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var type = typeof(InputPoint);
        var instance = type.GetProperty("Instance");
        var previousInput = InputPoint.Instance;
        string suffix = stick == 2 ? "2" : "";
        string senderProperty = stick == 2 ? "BlueCoordinateSender" : "RedCoordinateSender";
        Action<string, object> set = (name, value) => type.GetProperty(name).SetValue(input, value);
        var update = typeof(SaberInputBridge).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
        // 実時計の減衰と無関係に、100ms未満の送信元切替を再現する。ソケットは開かない。
        double at = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp) + 1;
        Action<string, double, float> sample = (sender, time, x) =>
        {
            set(senderProperty, sender);
            set("LastReceivedTime" + suffix, Time.realtimeSinceStartupAsDouble);
            set("LastReceivedMonotonicTime" + suffix, time);
            set("LocalStickRawA" + suffix, new Vector2(x, 0));
            set("LocalStickRawB" + suffix, new Vector2(x + 1, 0));
            update.Invoke(bridge, null);
        };
        try
        {
            instance.SetValue(null, input);
            type.GetField("phoneSaberStation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, station);
            PhoneSaberFilterSettings.Save(station, mode);
            set("PredictionHorizonMilliseconds", horizon);
            set("HasValidStickEndpoints" + suffix, true);
            for (int i = 0; i < 4; i++) sample("10.0.0.1", at + i * .02, i * .1f);
            at += .06;

            var beforeA = bridge.WorldEndA; var beforeB = bridge.WorldEndB;
            set(stick == 2 ? "RedCoordinateSender" : "BlueCoordinateSender", "10.0.0.99");
            update.Invoke(bridge, null);
            Assert.AreEqual(beforeA, bridge.WorldEndA, "反対色の送信元変更では履歴を捨てない");
            Assert.AreEqual(beforeB, bridge.WorldEndB);

            // LAN→P2P→別のLAN端末。切替後の各サンプルは、新しい履歴だけの結果と一致する。
            foreach (string sender in new[] { "127.0.0.1", "10.0.0.2" })
            {
                var filter = new PhoneSaberEndpointFilter();
                PhoneSaberEndpointPredictor predictor = default;
                for (int i = 0; i < 4; i++)
                {
                    at += .02;
                    float x = 1 + i * .1f;
                    var a = new Vector2(x, 0); var b = new Vector2(x + 1, 0);
                    if (mode != 0) filter.Apply(at, a, b, mode, out a, out b);
                    if (horizon > 0) { predictor.AddSample(at, a, b); predictor.Predict(at, horizon, out a, out b); }
                    sample(sender, at, x);
                    Near(a, new Vector2(bridge.WorldEndA.x, bridge.WorldEndA.y));
                    Near(b, new Vector2(bridge.WorldEndB.x, bridge.WorldEndB.y));
                }
            }
        }
        finally
        {
            instance.SetValue(null, previousInput);
            PlayerPrefs.DeleteKey(PhoneSaberFilterSettings.Key(station)); PlayerPrefs.Save();
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void BridgeModeChangesClearPredictionAndCalibrationChangesRestartFilter()
    {
        var go = new GameObject("フィルタ切替テスト");
        go.SetActive(false);
        var input = go.AddComponent<InputPoint>(); input.enabled = false;
        var bridge = go.AddComponent<SaberInputBridge>();
        string station = "filter-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var inputType = typeof(InputPoint);
        var bridgeType = typeof(SaberInputBridge);
        inputType.GetField("phoneSaberStation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, station);
        var resolve = bridgeType.GetMethod("ResolveFilterMode", BindingFlags.Instance | BindingFlags.NonPublic);
        var filter = (PhoneSaberEndpointFilter)bridgeType.GetField("endpointFilter", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bridge);
        // 予測器は struct。取り出した値は複製なので、書き戻してから bridge 側の実体を読み直す。
        var predictorField = bridgeType.GetField("endpointPredictor", BindingFlags.Instance | BindingFlags.NonPublic);
        var predictor = (PhoneSaberEndpointPredictor)predictorField.GetValue(bridge);
        try
        {
            // 未設定の台の既定は「中」（10-18）。このテストは OFF から切り替える動きを見るので、OFF を保存してから始める。
            PhoneSaberFilterSettings.Save(station, PhoneSaberEndpointFilter.Off);
            Assert.AreEqual(0, resolve.Invoke(bridge, new object[] { input }));
            predictor.AddSample(0, Vector2.zero, Vector2.one);
            predictor.AddSample(.03, Vector2.right, Vector2.one + Vector2.right);
            predictorField.SetValue(bridge, predictor);
            var count = typeof(PhoneSaberEndpointPredictor).GetField("sampleCount", BindingFlags.Instance | BindingFlags.NonPublic);
            inputType.GetProperty("PredictionRevision").SetValue(input, 1);
            resolve.Invoke(bridge, new object[] { input });
            Assert.AreEqual(2, count.GetValue(predictorField.GetValue(bridge)), "OFF中は従来の予測側だけが履歴を管理する");
            PhoneSaberFilterSettings.Save(station, 1);
            Assert.AreEqual(1, resolve.Invoke(bridge, new object[] { input }));
            Assert.AreEqual(0, count.GetValue(predictorField.GetValue(bridge)));
            filter.Apply(0, Vector2.zero, Vector2.one, 1, out _, out _);
            filter.Apply(.03, Vector2.right, Vector2.one + Vector2.right, 1, out _, out _);
            inputType.GetProperty("PredictionRevision").SetValue(input, 99);
            resolve.Invoke(bridge, new object[] { input });
            filter.Apply(.04, Vector2.one, Vector2.zero, 1, out var a, out var b);
            Assert.AreEqual(Vector2.one, a); Assert.AreEqual(Vector2.zero, b);
        }
        finally
        {
            PlayerPrefs.DeleteKey(PhoneSaberFilterSettings.Key(station)); PlayerPrefs.Save();
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
