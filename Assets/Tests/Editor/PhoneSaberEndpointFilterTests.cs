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
    public void SettingsDefaultOffAndAreIndependentPerStation()
    {
        string first = "filter-" + Guid.NewGuid().ToString("N").Substring(0, 8), second = "filter-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            Assert.AreEqual(0, PhoneSaberFilterSettings.Load(first));
            PhoneSaberFilterSettings.Save(first, 1);
            Assert.AreEqual(1, PhoneSaberFilterSettings.Load(first)); Assert.AreEqual(0, PhoneSaberFilterSettings.Load(second));
            PhoneSaberFilterSettings.Save(second, 99);
            Assert.AreEqual(2, PhoneSaberFilterSettings.Load(second));
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
        var predictor = (PhoneSaberEndpointPredictor)bridgeType.GetField("endpointPredictor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bridge);
        try
        {
            Assert.AreEqual(0, resolve.Invoke(bridge, new object[] { input }));
            predictor.AddSample(0, Vector2.zero, Vector2.one);
            predictor.AddSample(.03, Vector2.right, Vector2.one + Vector2.right);
            var count = typeof(PhoneSaberEndpointPredictor).GetField("sampleCount", BindingFlags.Instance | BindingFlags.NonPublic);
            inputType.GetProperty("PredictionRevision").SetValue(input, 1);
            resolve.Invoke(bridge, new object[] { input });
            Assert.AreEqual(2, count.GetValue(predictor), "OFF中は従来の予測側だけが履歴を管理する");
            PhoneSaberFilterSettings.Save(station, 1);
            Assert.AreEqual(1, resolve.Invoke(bridge, new object[] { input }));
            Assert.AreEqual(0, count.GetValue(predictor));
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
