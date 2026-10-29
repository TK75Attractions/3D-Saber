using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// 実機・ソケット不要で射影、台別保存、採取と既存入力変換との接続を検証する。
public class PhoneSaberPositionCalibrationTests
{
    static readonly Vector2[] Rectangle = {
        new Vector2(480, 270), new Vector2(1440, 270), new Vector2(1440, 810), new Vector2(480, 810) };
    static readonly Vector2[] Target = {
        Vector2.zero, new Vector2(1920, 0), new Vector2(1920, 1080), new Vector2(0, 1080) };
    string stationA;
    string stationB;

    [SetUp]
    public void Setup()
    {
        string id = Guid.NewGuid().ToString("N").Substring(0, 10);
        stationA = "testA_" + id;
        stationB = "testB_" + id;
    }

    [TearDown]
    public void Cleanup()
    {
        PhoneSaberPositionCalibrationStore.Reset(stationA);
        PhoneSaberPositionCalibrationStore.Reset(stationB);
    }

    static void Near(Vector2 expected, Vector2 actual)
    {
        Assert.AreEqual(expected.x, actual.x, 0.01f);
        Assert.AreEqual(expected.y, actual.y, 0.01f);
    }

    // 実際の自動指示を検証する間だけ共有キューを隔離し、他のテストの指示・32件の上限に影響させない。
    public sealed class PhoneCommandsScope : IDisposable
    {
        readonly System.Collections.IList commands = (System.Collections.IList)typeof(PhoneSaberAcknowledgementStore)
            .GetField("commands", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(PhoneSaberDiscoveryResponder.Acknowledgements);
        readonly object[] saved;
        public PhoneCommandsScope()
        {
            saved = new object[commands.Count]; commands.CopyTo(saved, 0); commands.Clear();
        }
        public void Dispose()
        {
            commands.Clear(); foreach (var command in saved) commands.Add(command);
        }
    }

    static PhoneSaberPositionCalibration Create(Vector2[] corners)
    {
        Assert.IsTrue(PhoneSaberPositionCalibration.TryCreate(corners, out var result, out var error), error);
        return result;
    }

    [Test]
    public void MissingOrDisabledCalibrationPreservesExactInputIncludingOutsideAndNormalized()
    {
        var calibration = Create(Rectangle);
        foreach (Vector2 input in new[] { new Vector2(-500, 2500), new Vector2(0.2f, -0.7f), Vector2.zero })
        {
            Assert.AreEqual(input, PhoneSaberPositionCalibration.Apply(null, true, input));
            Assert.AreEqual(input, PhoneSaberPositionCalibration.Apply(calibration, false, input));
        }
        Assert.IsNull(PhoneSaberPositionCalibrationStore.Load(stationA, out bool enabled));
        Assert.IsFalse(enabled);
    }

    [Test]
    public void AffineRectangleMapsCornersAndCenter()
    {
        var calibration = Create(Rectangle);
        for (int i = 0; i < 4; i++) Near(Target[i], calibration.Map(Rectangle[i]));
        Near(new Vector2(960, 540), calibration.Map(new Vector2(960, 540)));
        Near(new Vector2(480, 270), calibration.Map(new Vector2(720, 405)));
    }

    [Test]
    public void FullCameraRectangleIsIdentity()
    {
        var calibration = Create(Target);
        Near(new Vector2(123, 456), calibration.Map(new Vector2(123, 456)));
        for (int i = 0; i < 4; i++) Near(Target[i], calibration.Map(Target[i]));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PerspectiveAndMirroredQuadsRoundTripCornersAndInterior(bool mirrored)
    {
        var points = new[] { new Vector2(530, 190), new Vector2(1350, 290),
            new Vector2(1530, 870), new Vector2(350, 760) };
        if (mirrored) for (int i = 0; i < 4; i++) points[i].x = 1920 - points[i].x;
        var calibration = Create(points);
        for (int i = 0; i < 4; i++)
        {
            Near(Target[i], calibration.Map(points[i]));
            Near(points[i], calibration.Unmap(Target[i]));
        }
        for (int x = 0; x <= 4; x++) for (int y = 0; y <= 4; y++)
        {
            var destination = new Vector2(x * 480, y * 270);
            Near(destination, calibration.Map(calibration.Unmap(destination)));
        }
    }

    [Test]
    public void OutsidePointsAllowTenPercentMarginAndStayFiniteNearProjectionPole()
    {
        var calibration = Create(Rectangle);
        Near(new Vector2(-96, 540), calibration.Map(new Vector2(432, 540)));
        Near(new Vector2(-192, 1188), calibration.Map(new Vector2(-10000, 10000)));
        calibration = Create(new[] { new Vector2(700, 200), new Vector2(1200, 200),
            new Vector2(1700, 900), new Vector2(200, 900) });
        foreach (Vector2 point in new[] { new Vector2(960, -150), new Vector2(960, -151), new Vector2(-100000, -100000) })
        {
            Vector2 mapped = calibration.Map(point);
            Assert.That(mapped.x, Is.InRange(-192f, 2112f));
            Assert.That(mapped.y, Is.InRange(-108f, 1188f));
        }
    }

    [Test]
    public void InvalidQuadsAreRejected()
    {
        foreach (Vector2[] points in new[] {
            null, new Vector2[3], new Vector2[4],
            new[] { Rectangle[0], Rectangle[2], Rectangle[1], Rectangle[3] },
            new[] { Rectangle[0], Rectangle[1], new Vector2(960, 540), Rectangle[3] },
            new[] { new Vector2(0, 0), new Vector2(1000, 0), new Vector2(1000, 1), new Vector2(0, 1) },
            new[] { new Vector2(float.NaN, 0), Rectangle[1], Rectangle[2], Rectangle[3] },
            new[] { new Vector2(float.PositiveInfinity, 0), Rectangle[1], Rectangle[2], Rectangle[3] } })
        {
            Assert.IsFalse(PhoneSaberPositionCalibration.TryCreate(points, out var calibration, out var error));
            Assert.IsNull(calibration);
            Assert.IsNotEmpty(error);
        }
    }

    [Test]
    public void SourceCornersAreCopied()
    {
        var points = (Vector2[])Rectangle.Clone();
        var calibration = Create(points);
        points[0] = Vector2.zero;
        calibration.CopyCorners()[1] = Vector2.zero;
        Near(Target[0], calibration.Map(Rectangle[0]));
        Near(Rectangle[1], calibration.CopyCorners()[1]);
    }

    [Test]
    public void KeysUseStationLabelAndKeepUnspecifiedSeparate()
    {
        StringAssert.Contains(".A.", PhoneSaberPositionCalibrationStore.DataKey(" A "));
        Assert.AreNotEqual(PhoneSaberPositionCalibrationStore.DataKey("A"), PhoneSaberPositionCalibrationStore.DataKey("B"));
        Assert.AreNotEqual(PhoneSaberPositionCalibrationStore.EnabledKey("A"), PhoneSaberPositionCalibrationStore.EnabledKey("B"));
        Assert.AreNotEqual(PhoneSaberPositionCalibrationStore.DataKey(""), PhoneSaberPositionCalibrationStore.DataKey("None"));
        Assert.AreEqual(PhoneSaberPositionCalibrationStore.DataKey(null), PhoneSaberPositionCalibrationStore.DataKey(""));
    }

    [Test]
    public void SaveReloadDisableAndResetAreIndependentPerStation()
    {
        PhoneSaberPositionCalibrationStore.Save(stationA, Create(Rectangle));
        PhoneSaberPositionCalibrationStore.Save(stationB, Create(Target));
        var a = PhoneSaberPositionCalibrationStore.Load(stationA, out bool enabledA);
        var b = PhoneSaberPositionCalibrationStore.Load(stationB, out bool enabledB);
        Assert.IsTrue(enabledA); Assert.IsTrue(enabledB);
        Near(Target[0], a.Map(Rectangle[0]));
        Near(Rectangle[0], b.Map(Rectangle[0]));
        PhoneSaberPositionCalibrationStore.SetEnabled(stationA, false);
        Assert.IsNotNull(PhoneSaberPositionCalibrationStore.Load(stationA, out enabledA));
        Assert.IsFalse(enabledA);
        PhoneSaberPositionCalibrationStore.Reset(stationA);
        Assert.IsNull(PhoneSaberPositionCalibrationStore.Load(stationA, out enabledA));
        Assert.IsNotNull(PhoneSaberPositionCalibrationStore.Load(stationB, out enabledB));
        Assert.IsTrue(enabledB);
    }

    [TestCase("not json")]
    [TestCase("{\"version\":2,\"corners\":[]}")]
    [TestCase("{\"version\":1,\"corners\":[]}")]
    public void CorruptOrUnsupportedSavedDataFallsBackToDisabledIdentity(string data)
    {
        PlayerPrefs.SetString(PhoneSaberPositionCalibrationStore.DataKey(stationA), data);
        PlayerPrefs.SetInt(PhoneSaberPositionCalibrationStore.EnabledKey(stationA), 1);
        Assert.IsNull(PhoneSaberPositionCalibrationStore.Load(stationA, out bool enabled));
        Assert.IsFalse(enabled);
    }

    [Test]
    public void MedianCaptureUsesSelectedColorAndFreshWindowRejectingOutliers()
    {
        var capture = new PhoneSaberPositionCapture();
        capture.Begin(false, 10);
        capture.Add(false, Vector2.zero, new Vector2(2, 2), 9.9);
        capture.Add(false, Vector2.zero, new Vector2(2, 2), 11.1);
        for (int i = 0; i < 30; i++)
        {
            double time = 10 + i / 30.0;
            capture.Add(true, Vector2.zero, new Vector2(2, 2), time);
            var midpoint = i < 3 ? new Vector2(1900, 1000) : new Vector2(500 + i % 3 - 1, 300);
            capture.Add(false, midpoint - Vector2.right * 30, midpoint + Vector2.right * 30, time);
        }
        Assert.IsTrue(capture.Finish(out var result, out var error), error);
        Near(new Vector2(500, 300), result);
        capture.Begin(true, 20);
        for (int i = 0; i < 30; i++) capture.Add(true, new Vector2(100, 200), new Vector2(200, 200), 20 + i / 30.0);
        Assert.IsTrue(capture.Finish(out result, out error), error);
        Near(new Vector2(150, 200), result);
    }

    [Test]
    public void CaptureRejectsSilenceBurstMotionAndCanceledSamples()
    {
        var capture = new PhoneSaberPositionCapture();
        capture.Begin(false, 0);
        Assert.IsFalse(capture.Finish(out _, out _));
        capture.Begin(false, 0);
        for (int i = 0; i < 30; i++) capture.Add(false, Vector2.zero, Vector2.right * 100, i / 1000.0);
        Assert.IsFalse(capture.Finish(out _, out _));
        capture.Begin(false, 0);
        for (int i = 0; i < 30; i++) capture.Add(false, new Vector2(i * 20, 0), new Vector2(i * 20 + 100, 0), i / 30.0);
        Assert.IsFalse(capture.Finish(out _, out _));
        capture.Begin(false, 0);
        for (int i = 0; i < 30; i++) capture.Add(false, Vector2.zero, Vector2.right * 100, i / 30.0);
        capture.Cancel();
        Assert.IsFalse(capture.Finish(out _, out _));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InputPointMapsBothEndpointsAndMidpointsBeforeExistingSensitivity(bool enabled)
    {
        var go = new GameObject("位置補正テスト");
        var input = go.AddComponent<InputPoint>();
        input.enabled = false; // ソケットや外部プロセスは開始しない。
        try
        {
            input.useDirectWorldMapping = true;
            input.sensitivity = 1;
            Set(input, "positionCalibration", Create(Rectangle));
            Set(input, "positionCalibrationEnabled", enabled);
            Set(input, "hasNewData", true); Set(input, "hasNewData2", true);
            Set(input, "hasStickData", true); Set(input, "hasStickData2", true);
            Set(input, "rawX", 960f); Set(input, "rawY", 540f);
            Set(input, "rawX1a", 480f); Set(input, "rawY1a", 270f);
            Set(input, "rawX1b", 1440f); Set(input, "rawY1b", 810f);
            Set(input, "rawX2", 960f); Set(input, "rawY2", 270f);
            Set(input, "rawX2a", 480f); Set(input, "rawY2a", 270f);
            Set(input, "rawX2b", 1440f); Set(input, "rawY2b", 270f);
            typeof(InputPoint).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, null);
            Near(new Vector2(960, 540), input.LastRaw);
            Near(new Vector2(960, 270), input.LastRaw2);
            Near(enabled ? new Vector2(-1, -1) : new Vector2(-0.5f, -0.5f), input.LocalStickA);
            Near(enabled ? Vector2.one : new Vector2(0.5f, 0.5f), input.LocalStickB);
            Near(enabled ? new Vector2(-1, -1) : new Vector2(-0.5f, -0.5f), input.LocalStickA2);
            Near(enabled ? new Vector2(1, -1) : new Vector2(0.5f, -0.5f), input.LocalStickB2);
            Near(Vector2.zero, input.LocalPosition);
            Near(new Vector2(0, enabled ? -3 : -1.5f), input.LocalPosition2);
            Near(new Vector2(0.5f, enabled ? 0 : 0.25f), input.NormalizedPosition2);
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    [Test]
    public void CalibratedOriginUsesPixelsAndSensitivityStillActsAfterMapping()
    {
        var calibration = Create(Rectangle);
        Vector2 corner = calibration.Map(Rectangle[0]);
        Near(new Vector2(-1, -1), InputPoint.CanonicalizePoint(corner.x, corner.y, 1920, 1080, true, true));
        Near(Vector2.zero, InputPoint.Normalized01(corner.x, corner.y, 1920, 1080, true));
        Near(new Vector2(0, 0), InputPoint.CanonicalizePoint(0, 0, 1920, 1080, true));

        Vector2 mapped = calibration.Map(new Vector2(840, 472.5f));
        Vector2 canonical = InputPoint.CanonicalizePoint(mapped.x, mapped.y, 1920, 1080, true, true);
        Near(new Vector2(-0.5f, -0.5f), InputPoint.ApplySensitivity(canonical, 2, true, 1920, 1080));
        Near(new Vector2(0.25f, 0.25f), InputPoint.ApplySensitivity01(
            InputPoint.Normalized01(mapped.x, mapped.y, 1920, 1080, true), 2));
    }

    [Test]
    public void LearnedRectangleMapsEasySwingToCentralTwoThirds()
    {
        var bounds = Rect.MinMaxRect(.2f, .1f, .8f, .9f);
        Assert.IsTrue(PhoneSaberPositionCalibration.TryFromLearnedArea(bounds, 1920, 1080, out var mapping, out var error), error);
        var corners = mapping.CopyCorners();
        Near(new Vector2(.05f * 1919, -.1f * 1079), corners[0]);
        Near(new Vector2(.95f * 1919, 1.1f * 1079), corners[2]);
        for (int i = 0; i < 4; i++) Near(Target[i], mapping.Map(corners[i]));
        Near(new Vector2(960, 540), mapping.Map(new Vector2(.5f * 1919, .5f * 1079)));
        Near(new Vector2(1920f / 6, 1080f / 6), mapping.Map(new Vector2(.2f * 1919, .1f * 1079)));
        Near(new Vector2(1920f * 5 / 6, 1080f * 5 / 6), mapping.Map(new Vector2(.8f * 1919, .9f * 1079)));
        Near(new Vector2(1920f / 3, 1080f / 3), mapping.Map(new Vector2(.35f * 1919, .3f * 1079)));
        // iPhoneの左右・上下反転済み矩形と、送信解像度も同じ導出を通す。
        Assert.IsTrue(PhoneSaberPositionCalibration.TryFromLearnedArea(Rect.MinMaxRect(.1f, .3f, .7f, .8f),
            1280, 720, out var other, out _));
        Near(new Vector2(-.05f * 1279, .175f * 719), other.CopyCorners()[0]);
    }

    [TestCase(0f, 0f, 1f, 1f)]
    [TestCase(0f, 0f, .6f, .8f)]
    [TestCase(.4f, .2f, 1f, 1f)]
    public void LearnedRectangleAtCameraEdgesKeepsVirtualExpandedCornersAndGain(float x0, float y0, float x1, float y1)
    {
        var bounds = Rect.MinMaxRect(x0, y0, x1, y1);
        Assert.IsTrue(PhoneSaberPositionCalibration.TryFromLearnedArea(bounds, 1920, 1080, out var mapping, out var error), error);
        var corners = mapping.CopyCorners();
        // カメラ外の仮想隅も保持し、端で拡張が切られて感度や中心が変わらないことを確認する。
        Near(new Vector2((x0 - bounds.width * .25f) * 1919, (y0 - bounds.height * .25f) * 1079), corners[0]);
        Near(new Vector2((x1 + bounds.width * .25f) * 1919, (y1 + bounds.height * .25f) * 1079), corners[2]);
        Near(new Vector2(1920f / 6, 1080f / 6), mapping.Map(new Vector2(x0 * 1919, y0 * 1079)));
        Near(new Vector2(1920f * 5 / 6, 1080f * 5 / 6), mapping.Map(new Vector2(x1 * 1919, y1 * 1079)));
        Near(new Vector2(960, 540), mapping.Map(new Vector2(bounds.center.x * 1919, bounds.center.y * 1079)));
        for (int i = 0; i < 4; i++)
        {
            Near(Target[i], mapping.Map(corners[i]));
            Near(corners[i], mapping.Unmap(Target[i]));
        }
    }

    [Test]
    public void LearnedRectangleExtrapolatesLinearlyUntilExistingOutsideMargin()
    {
        Assert.IsTrue(PhoneSaberPositionCalibration.TryFromLearnedArea(Rect.MinMaxRect(.2f, .1f, .8f, .9f),
            1920, 1080, out var mapping, out var error), error);
        Near(new Vector2(-1920f / 12, -1080f / 12), mapping.Map(new Vector2(-.025f * 1919, -.2f * 1079)));
        Near(new Vector2(1920f * 13 / 12, 1080f * 13 / 12), mapping.Map(new Vector2(1.025f * 1919, 1.2f * 1079)));
        Near(new Vector2(-192, -108), mapping.Map(new Vector2(-10000, -10000)));
        Near(new Vector2(2112, 1188), mapping.Map(new Vector2(10000, 10000)));
    }

    [Test]
    public void LearnedAreaNeedsEnoughSamples()
    {
        // 10-29: 44サンプルの学習を拒否し、当日最少の正常学習（1028）は通す。
        Assert.IsFalse(PhoneSaberPositionCalibration.EnoughLearnedSamples("44", out var error));
        StringAssert.Contains("44", error);
        Assert.IsFalse(PhoneSaberPositionCalibration.EnoughLearnedSamples("299", out _));
        Assert.IsFalse(PhoneSaberPositionCalibration.EnoughLearnedSamples("", out _));
        Assert.IsFalse(PhoneSaberPositionCalibration.EnoughLearnedSamples("-5", out _));
        Assert.IsTrue(PhoneSaberPositionCalibration.EnoughLearnedSamples("300", out _));
        Assert.IsTrue(PhoneSaberPositionCalibration.EnoughLearnedSamples("1028", out _));
    }

    [Test]
    public void LearnedRectangleRejectsInvalidOrTinyBounds()
    {
        foreach (var bounds in new[] { new Rect(), Rect.MinMaxRect(-.1f, 0, 1, 1),
            Rect.MinMaxRect(0, 0, 1.1f, 1), new Rect(0, 0, float.NaN, 1),
            new Rect(0, 0, float.PositiveInfinity, 1), new Rect(.5f, .5f, .001f, .001f), new Rect(.5f, .5f, -.1f, .5f) })
            Assert.IsFalse(PhoneSaberPositionCalibration.TryFromLearnedArea(bounds, 1920, 1080, out _, out _));
        Assert.IsFalse(PhoneSaberPositionCalibration.TryFromLearnedArea(new Rect(0, 0, 1, 1), float.NaN, 1080, out _, out _));
    }

    [TestCase("A", false)]
    [TestCase("B", false)]
    [TestCase("A", true)]
    [TestCase("B", true)]
    public void LearnedAckAndTutorialSendTheSameWidenedPhoneAreaOnce(string station, bool tutorialFirst)
    {
        using var commandScope = new PhoneCommandsScope();
        string key = PhoneSaberPositionCalibrationStore.DataKey(station);
        string enabledKey = PhoneSaberPositionCalibrationStore.EnabledKey(station);
        bool hadData = PlayerPrefs.HasKey(key), hadEnabled = PlayerPrefs.HasKey(enabledKey);
        string original = PlayerPrefs.GetString(key); int enabled = PlayerPrefs.GetInt(enabledKey);
        bool autoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        var go = new GameObject("学習後のスマホ範囲テスト");
        try
        {
            var input = go.AddComponent<InputPoint>(); input.enabled = false;
            Set(input, "phoneSaberStation", station);
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
            var commands = PhoneSaberDiscoveryResponder.Acknowledgements;
            foreach (string bounds in new[] { "0.2_0.1_0.8_0.9", "0.2_0.1_0.8_0.9", "0.4_0.5_0.6_0.7" })
            {
                var store = commands;
                int before = PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.LearnAreaCommand, automatic: true);
                var ack = new PhoneSaberAcknowledgement(before, station, "ios", "learned", "samples300-area" + bounds, "phone");
                store.Receive(ack, "192.0.2.1");
                var learned = bounds.StartsWith("0.2") ? Rect.MinMaxRect(.2f, .1f, .8f, .9f) : Rect.MinMaxRect(.4f, .5f, .6f, .7f);
                int revision = input.PredictionRevision;
                if (tutorialFirst) Assert.IsTrue(TutorialController.ApplyLearnedRange(input, learned));
                else Assert.IsTrue(input.ApplyLearnedPositionCalibration(store, "F8"));
                string firstSave = PlayerPrefs.GetString(key);
                commands.Latest(out int sent, out string fields, out _, out bool automatic);
                Assert.AreEqual(before + 1, sent, "F8 成功時に1回だけ送る");
                Assert.IsTrue(automatic);
                // 既知の1.5倍矩形＋余白。大きい範囲はカメラ端、小さい再学習は新しい範囲に置換する。
                var expected = bounds.StartsWith("0.2") ? new Rect(0, 0, 1, 1) : Rect.MinMaxRect(.27f, .37f, .73f, .83f);
                StringAssert.StartsWith("play-area=", fields);
                var coordinates = Array.ConvertAll(fields.Substring("play-area=".Length).Split(','),
                    value => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
                Assert.That(coordinates, Is.EqualTo(new[] { expected.xMin, expected.yMin, expected.xMax, expected.yMax }).Within(1e-5f));
                store.Receive(ack, "192.0.2.1");
                Assert.IsFalse(input.ApplyLearnedPositionCalibration(store, "duplicate"));
                Assert.IsFalse(input.ApplyLearnedPositionCalibration(new Rect(.5f, .5f, .001f, .001f), "failed", out _));
                commands.Latest(out int unchanged, out _, out _);
                Assert.AreEqual(sent, unchanged, "重複ACK・適用失敗は送らない");
                if (tutorialFirst) Assert.IsFalse(input.ApplyLearnedPositionCalibration(store, "F8"));
                else Assert.IsTrue(TutorialController.ApplyLearnedRange(input, learned), "自動適用済みでもチュートリアルは成功");
                commands.Latest(out int tutorial, out string tutorialFields, out _);
                Assert.AreEqual(sent, tutorial, "両入口を通っても送信は1回");
                Assert.AreEqual(revision + 1, input.PredictionRevision, "予測リセットは1回");
                Assert.AreEqual(firstSave, PlayerPrefs.GetString(key), "2番目の入口は保存日時も上書きしない");
                Assert.AreEqual(fields, tutorialFields);
            }
        }
        finally
        {
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = autoStart;
            UnityEngine.Object.DestroyImmediate(go);
            if (hadData) PlayerPrefs.SetString(key, original); else PlayerPrefs.DeleteKey(key);
            if (hadEnabled) PlayerPrefs.SetInt(enabledKey, enabled); else PlayerPrefs.DeleteKey(enabledKey);
            PlayerPrefs.Save();
        }
    }

    [TestCase("A", "A")]
    [TestCase("B", "B")]
    [TestCase("A", "none")]
    [TestCase("B", "none")]
    public void LearnedAckAppliesOnceToItsStationAndResetOnlyClearsMapping(string station, string phoneStation)
    {
        // 実運用のA/B設定をテスト後に復元する。
        string key = PhoneSaberPositionCalibrationStore.DataKey(station);
        string enabledKey = PhoneSaberPositionCalibrationStore.EnabledKey(station);
        bool hadData = PlayerPrefs.HasKey(key), hadEnabled = PlayerPrefs.HasKey(enabledKey);
        string original = PlayerPrefs.GetString(key); int enabled = PlayerPrefs.GetInt(enabledKey);
        var go = new GameObject("10秒学習テスト");
        try
        {
            var input = go.AddComponent<InputPoint>(); input.enabled = false;
            Set(input, "phoneSaberStation", station);
            var store = new PhoneSaberAcknowledgementStore();
            store.Queue(71, "learn-area=10", 0);
            string line = $"PHONESABER_ACK 1 id=71 station={phoneStation} platform=ios result=learned detail=samples300-area0.2_0.1_0.8_0.9";
            Assert.IsTrue(PhoneSaberAcknowledgement.TryParse(line, out var ack));
            store.Receive(ack, "192.0.2.1");
            if (phoneStation != "none") Assert.IsFalse(store.TakeLearnedArea(station == "A" ? "B" : "A", out _));
            Assert.IsTrue(input.ApplyLearnedPositionCalibration(store, "2026-10-23 12:34:56"));
            Assert.IsTrue(input.PositionCalibrationEnabled);
            Assert.AreEqual("2026-10-23 12:34:56", PhoneSaberPositionCalibrationStore.LearnedAt(station));
            var saved = PhoneSaberPositionCalibrationStore.Load(station, out bool loaded);
            Assert.IsNotNull(saved); Assert.IsTrue(loaded);
            Near(new Vector2(1920f / 6, 1080f / 6), saved.Map(new Vector2(.2f * 1919, .1f * 1079)));
            // チュートリアル本番の適用も、同じ矩形なら F8 と同じ4隅・写像・保存結果になる。
            bool autoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
            try
            {
                PhoneSaberP2PBridgeProcess.AutoStartEnabled = false;
                Assert.IsTrue(TutorialController.ApplyLearnedRange(input, Rect.MinMaxRect(.2f, .1f, .8f, .9f)));
            }
            finally { PhoneSaberP2PBridgeProcess.AutoStartEnabled = autoStart; }
            var tutorial = PhoneSaberPositionCalibrationStore.Load(station, out bool tutorialEnabled);
            Assert.IsTrue(tutorialEnabled);
            CollectionAssert.AreEqual(saved.CopyCorners(), tutorial.CopyCorners());
            for (int i = 0; i < 4; i++) Near(saved.Map(Rectangle[i]), tutorial.Map(Rectangle[i]));
            StringAssert.Contains("倍率: 1.5", input.LearnedPositionCalibrationResult);
            StringAssert.Contains("PC適用: 成功", PhoneSaberPositionCalibrationStore.LearnedResult(station));
            int revision = input.PredictionRevision;
            for (int i = 0; i < 3; i++)
            {
                store.Receive(ack, "192.0.2.1");
                Assert.IsFalse(input.ApplyLearnedPositionCalibration(store, "duplicate"));
            }
            Assert.AreEqual(revision, input.PredictionRevision);
            // リセットなしで新しい小さい可動域へ完全に置換する。古い範囲へ届く必要はない。
            store.Queue(73, "learn-area=10", 15);
            string smaller = line.Replace("id=71", "id=73").Replace("area0.2_0.1_0.8_0.9", "area0.4_0.5_0.6_0.7");
            Assert.IsTrue(PhoneSaberAcknowledgement.TryParse(smaller, out var smallAck));
            store.Receive(smallAck, "192.0.2.1");
            Assert.IsTrue(input.ApplyLearnedPositionCalibration(store, "small"));
            Assert.AreEqual(revision + 1, input.PredictionRevision);
            var replacement = PhoneSaberPositionCalibrationStore.Load(station, out bool replacementEnabled);
            Assert.IsTrue(replacementEnabled);
            Near(Vector2.zero, replacement.Map(new Vector2(.35f * 1919, .45f * 1079)));
            Near(new Vector2(1920, 1080), replacement.Map(new Vector2(.65f * 1919, .75f * 1079)));
            store.Receive(smallAck, "192.0.2.1");
            store.Receive(ack, "192.0.2.1");
            Assert.IsFalse(input.ApplyLearnedPositionCalibration(store, "duplicate"));
            Assert.AreEqual("small", PhoneSaberPositionCalibrationStore.LearnedAt(station));
            PhoneSaberDiscoveryResponder.Acknowledgements.Latest(out int beforeReset, out _, out _);
            input.ResetPositionCalibration();
            PhoneSaberDiscoveryResponder.Acknowledgements.Latest(out int afterReset, out _, out _);
            Assert.AreEqual(beforeReset, afterReset, "リセットはiPhoneへclear-areaを送らない");
            Assert.IsFalse(input.HasPositionCalibration);
            Assert.AreEqual("", PhoneSaberPositionCalibrationStore.LearnedAt(station));
            store.Receive(ack, "192.0.2.1");
            Assert.IsFalse(input.ApplyLearnedPositionCalibration(store, "duplicate"));
            store.Queue(72, "learn-area=10", 20);
            Assert.IsTrue(PhoneSaberAcknowledgement.TryParse(line.Replace("id=71", "id=72"), out var next));
            store.Receive(next, "192.0.2.1");
            Assert.IsTrue(input.ApplyLearnedPositionCalibration(store, "next"));
            Assert.IsTrue(input.SavePositionCalibration(Rectangle, out _));
            Assert.AreEqual("", PhoneSaberPositionCalibrationStore.LearnedAt(station), "詳細の4隅で上書きすると出典も変わる");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
            if (hadData) PlayerPrefs.SetString(key, original); else PlayerPrefs.DeleteKey(key);
            if (hadEnabled) PlayerPrefs.SetInt(enabledKey, enabled); else PlayerPrefs.DeleteKey(enabledKey);
            PlayerPrefs.Save();
        }
    }

    [Test]
    public void FailedLearnedMappingIsShownAndLoggedWithoutReplacingSavedMapping()
    {
        using var commands = new PhoneCommandsScope();
        using var preferences = new CalibrationPrefsScope("A");
        var go = new GameObject("学習適用診断テスト");
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "phonesaber-range-" + Guid.NewGuid().ToString("N"), "events.log");
        var previousLog = PhoneSaberEventLog.Current;
        try
        {
            var input = go.AddComponent<InputPoint>(); input.enabled = false;
            Set(input, "phoneSaberStation", "A");
            var store = PhoneSaberDiscoveryResponder.Acknowledgements;
            store.Queue(9001, "learn-area=10", 0);
            store.Receive(new PhoneSaberAcknowledgement(9001, "A", "ios", "learned", "samples300-area0.2_0.1_0.8_0.9"), "phone");
            PhoneSaberEventLog.Current = new PhoneSaberEventLog(path);
            var bounds = Rect.MinMaxRect(.2f, .1f, .8f, .9f);
            Assert.IsTrue(input.ApplyLearnedPositionCalibration(bounds, "ok", out _));
            string saved = PlayerPrefs.GetString(PhoneSaberPositionCalibrationStore.DataKey("A"));
            store.Queue(9002, "learn-area=10", 20);
            store.Receive(new PhoneSaberAcknowledgement(9002, "A", "ios", "learned", "samples300-area0.2_0.1_0.8_0.9"), "phone");
            input.camWidth = 1; // PC 側の変換条件だけを壊す。iPhone の学習ACKとは区別する。
            Assert.IsFalse(input.ApplyLearnedPositionCalibration(bounds, "failed", out _));
            Assert.AreEqual(saved, PlayerPrefs.GetString(PhoneSaberPositionCalibrationStore.DataKey("A")));
            Assert.IsTrue(input.PositionCalibrationEnabled);
            input.camWidth = 1920;
            Assert.IsFalse(input.ApplyLearnedPositionCalibration(bounds, "retry", out _), "同じ指示の失敗を再試行しない");
            Assert.IsFalse(input.ApplyLearnedPositionCalibration(store, "retry"));
            StringAssert.Contains("PC適用: 失敗", input.LearnedPositionCalibrationResult);
            StringAssert.Contains("学習矩形(0..1): (0.200000, 0.100000)–(0.800000, 0.900000)", input.LearnedPositionCalibrationResult);
            StringAssert.Contains("適用矩形: (0.050000, -0.100000)–(0.950000, 1.100000)", input.LearnedPositionCalibrationResult);
            Assert.IsTrue(PhoneSaberEventLog.Current.FlushAndWait(5000));
            string log = System.IO.File.ReadAllText(path);
            Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(log, "event=learned-area-apply ").Count,
                "成功・失敗は各1回だけ記録し、再試行では増やさない");
            StringAssert.Contains("倍率: 1.5", log);
            StringAssert.Contains("PC適用: 成功", log);
            StringAssert.Contains("PC適用: 失敗", log);
            input.ResetPositionCalibration();
            Assert.AreEqual("", input.LearnedPositionCalibrationResult);
        }
        finally
        {
            PhoneSaberEventLog.Current?.StopAndDrain(5000);
            PhoneSaberEventLog.Current = previousLog;
            UnityEngine.Object.DestroyImmediate(go);
            if (System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(path)))
                System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(path), true);
        }
    }

    // Rect入口も本番と同じ指示ID・送信元付きACKを使う。
    public static int QueueLearnedRect(string station, Rect rect)
    {
        int id = PhoneSaberDiscoveryResponder.QueueCommand("learn-area=10", automatic: true);
        string detail = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "samples300-area{0}_{1}_{2}_{3}", rect.xMin, rect.yMin, rect.xMax, rect.yMax);
        PhoneSaberDiscoveryResponder.Acknowledgements.Receive(
            new PhoneSaberAcknowledgement(id, station, "ios", "learned", detail), "test-phone");
        return id;
    }

    public sealed class CalibrationPrefsScope : IDisposable
    {
        readonly string key, enabledKey, data;
        readonly bool hadData, hadEnabled;
        readonly int enabled;
        public CalibrationPrefsScope(string station)
        {
            key = PhoneSaberPositionCalibrationStore.DataKey(station);
            enabledKey = PhoneSaberPositionCalibrationStore.EnabledKey(station);
            hadData = PlayerPrefs.HasKey(key); data = PlayerPrefs.GetString(key);
            hadEnabled = PlayerPrefs.HasKey(enabledKey); enabled = PlayerPrefs.GetInt(enabledKey);
        }
        public void Dispose()
        {
            if (hadData) PlayerPrefs.SetString(key, data); else PlayerPrefs.DeleteKey(key);
            if (hadEnabled) PlayerPrefs.SetInt(enabledKey, enabled); else PlayerPrefs.DeleteKey(enabledKey);
            PlayerPrefs.Save();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ResetOrManualSaveRejectsDelayedCompletionAndCachedSuccess(bool manual)
    {
        using var commands = new PhoneCommandsScope();
        using var preferences = new CalibrationPrefsScope("A");
        var go = new GameObject("古い学習は復活させない");
        try
        {
            var input = go.AddComponent<InputPoint>(); input.enabled = false;
            Set(input, "phoneSaberStation", "A");
            var store = PhoneSaberDiscoveryResponder.Acknowledgements;
            var rect = Rect.MinMaxRect(.2f, .1f, .8f, .9f);
            for (int id = 1; id <= 2; id++)
            {
                store.Queue(id, "learn-area=10", id * 20);
                var ack = new PhoneSaberAcknowledgement(id, "A", "ios", "learned", "samples300-area0.2_0.1_0.8_0.9");
                if (id == 1) { store.Receive(ack, "phone"); Assert.IsTrue(input.ApplyLearnedPositionCalibration(store, "ok")); }
                if (manual) Assert.IsTrue(input.SavePositionCalibration(Rectangle, out _));
                else input.ResetPositionCalibration();
                string saved = PlayerPrefs.GetString(PhoneSaberPositionCalibrationStore.DataKey("A"));
                int revision = input.PredictionRevision;
                store.Receive(ack, "phone");
                Assert.IsFalse(input.ApplyLearnedPositionCalibration(store, "late"));
                Assert.IsFalse(input.ApplyLearnedPositionCalibration(rect, "late", out _));
                Assert.AreEqual(revision, input.PredictionRevision);
                Assert.AreEqual(saved, PlayerPrefs.GetString(PhoneSaberPositionCalibrationStore.DataKey("A")));
            }
            store.Queue(3, "learn-area=10", 60);
            store.Receive(new PhoneSaberAcknowledgement(3, "A", "ios", "learned", "samples300-area0.2_0.1_0.8_0.9"), "phone");
            Assert.IsTrue(input.ApplyLearnedPositionCalibration(rect, "new", out _), "同じ矩形でも次指示は新規");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    [Test]
    public void CachedSuccessCannotBeReusedForAnotherSenderEvenWithTheSameRectangle()
    {
        using var commands = new PhoneCommandsScope();
        using var preferences = new CalibrationPrefsScope("A");
        var go = new GameObject("適用結果の送信元照合");
        try
        {
            var input = go.AddComponent<InputPoint>(); input.enabled = false;
            Set(input, "phoneSaberStation", "A");
            var store = PhoneSaberDiscoveryResponder.Acknowledgements;
            store.Queue(1, "learn-area=10", 0);
            store.Receive(new PhoneSaberAcknowledgement(1, "none", "ios", "learned", "samples300-area0.2_0.1_0.8_0.9"), "phone-none");
            Assert.IsTrue(input.ApplyLearnedPositionCalibration(store, "first"));
            int revision = input.PredictionRevision;
            store.Receive(new PhoneSaberAcknowledgement(1, "A", "ios", "learned", "samples300-area0.2_0.1_0.8_0.9"), "phone-A");
            Assert.IsFalse(input.ApplyLearnedPositionCalibration(Rect.MinMaxRect(.2f, .1f, .8f, .9f), "other", out _));
            Assert.IsFalse(input.ApplyLearnedPositionCalibration(store, "other"));
            Assert.AreEqual(revision, input.PredictionRevision);
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    [TestCase("B", null)]
    [TestCase("none", "none")]
    [TestCase("A", "A")]
    public void RectEntryRejectsOtherStationAndAmbiguousSenders(string first, string second)
    {
        using var commands = new PhoneCommandsScope();
        using var preferences = new CalibrationPrefsScope("A");
        var go = new GameObject("Rect入口の送信元照合");
        try
        {
            var input = go.AddComponent<InputPoint>(); input.enabled = false;
            Set(input, "phoneSaberStation", "A");
            var store = PhoneSaberDiscoveryResponder.Acknowledgements;
            store.Queue(1, "learn-area=10", 0);
            store.Receive(new PhoneSaberAcknowledgement(1, first, "ios", "learned", "samples300-area0.2_0.1_0.8_0.9"), "phone1");
            if (second != null) store.Receive(new PhoneSaberAcknowledgement(1, second, "ios", "learned", "samples300-area0.2_0.1_0.8_0.9"), "phone2");
            int revision = input.PredictionRevision;
            Assert.IsFalse(input.ApplyLearnedPositionCalibration(Rect.MinMaxRect(.2f, .1f, .8f, .9f), "ambiguous", out _));
            Assert.AreEqual(revision, input.PredictionRevision);
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static void Set(InputPoint input, string name, object value) =>
        typeof(InputPoint).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, value);
}
