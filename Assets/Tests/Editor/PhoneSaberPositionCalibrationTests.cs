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

    static void Set(InputPoint input, string name, object value) =>
        typeof(InputPoint).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, value);
}
