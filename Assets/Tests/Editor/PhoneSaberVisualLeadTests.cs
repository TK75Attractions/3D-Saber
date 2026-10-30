using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class PhoneSaberVisualLeadTests
{
    [Test]
    public void SettingChangesWriteExactlyOneEventAndUnchangedValuesWriteNone()
    {
        const string station = "visual-log";
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "visual-lead-" + System.Guid.NewGuid().ToString("N"));
        string path = System.IO.Path.Combine(directory, "events.log");
        var previous = PhoneSaberEventLog.Current;
        var log = new PhoneSaberEventLog(path);
        PlayerPrefs.DeleteKey(PhoneSaberVisualLeadSettings.Key(station));
        try
        {
            PhoneSaberEventLog.Current = log;
            PhoneSaberVisualLeadSettings.Save(station, 10);
            PhoneSaberVisualLeadSettings.Save(station, 10);
            Assert.IsTrue(log.FlushAndWait(5000));
            var lines = System.IO.File.ReadAllLines(path);
            Assert.AreEqual(1, lines.Length);
            StringAssert.Contains("event=visual-lead-setting ms=10", lines[0]);
            PhoneSaberVisualLeadSettings.Save(station, 0);
            Assert.IsTrue(log.FlushAndWait(5000));
            lines = System.IO.File.ReadAllLines(path);
            Assert.AreEqual(2, lines.Length);
            StringAssert.Contains("event=visual-lead-setting ms=0", lines[1]);
        }
        finally
        {
            PhoneSaberEventLog.Current = previous;
            log.StopAndDrain(5000);
            PlayerPrefs.DeleteKey(PhoneSaberVisualLeadSettings.Key(station));
            PlayerPrefs.Save();
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
    }

    [Test]
    public void DefaultRangeStepsAndPersistenceArePerStation()
    {
        const string a = "visual-test-a", b = "visual-test-b";
        PlayerPrefs.DeleteKey(PhoneSaberVisualLeadSettings.Key(a));
        PlayerPrefs.DeleteKey(PhoneSaberVisualLeadSettings.Key(b));
        try
        {
            Assert.AreEqual(0, PhoneSaberVisualLeadSettings.Load(a));
            Assert.AreEqual(PhoneSaberVisualLeadSettings.Key(a), PhoneSaberVisualLeadSettings.Key(" " + a + " "));
            PhoneSaberVisualLeadSettings.Save(a, 34);
            Assert.AreEqual(30, PhoneSaberVisualLeadSettings.Load(a));
            Assert.AreEqual(0, PhoneSaberVisualLeadSettings.Load(b));
            PhoneSaberVisualLeadSettings.Save(a, int.MaxValue);
            Assert.AreEqual(80, PhoneSaberVisualLeadSettings.Load(a));
            PhoneSaberVisualLeadSettings.Save(b, 45);
            Assert.AreEqual(50, PhoneSaberVisualLeadSettings.Load(b));
            PhoneSaberVisualLeadSettings.Save(a, int.MinValue);
            Assert.AreEqual(0, PhoneSaberVisualLeadSettings.Load(a));
            for (int ms = 0; ms <= 80; ms += 10)
                Assert.AreEqual(ms, PhoneSaberVisualLeadSettings.Clamp(ms));
            Assert.AreEqual(200, PhoneSaberVisualLeadSettings.TotalLead(120, 80));
            Assert.AreEqual(200, PhoneSaberVisualLeadSettings.TotalLead(int.MaxValue, 80));
        }
        finally
        {
            PlayerPrefs.DeleteKey(PhoneSaberVisualLeadSettings.Key(a));
            PlayerPrefs.DeleteKey(PhoneSaberVisualLeadSettings.Key(b));
            PlayerPrefs.Save();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ActualPredictedEndpointsAdvanceBeyondTheOldHorizon(bool automatic)
    {
        var predictor = new PhoneSaberEndpointPredictor(6);
        for (int i = 0; i <= 20; i++)
            predictor.AddSample(i / 60.0, new Vector2(i / 6f, -1), new Vector2(i / 6f, 1));
        double now = 20 / 60.0;
        if (automatic)
        {
            predictor.PredictAdaptive(now, 120, out var before, out _);
            predictor.PredictAdaptive(now, 120, out var after, out var afterB, visualExtraMilliseconds: 80);
            Assert.AreEqual(.8f, after.x - before.x, .002f);
            Assert.AreEqual(20 / 6f + 2, after.x, .002f, "120+80=200 ms を実際の描画座標まで通す");
            Assert.AreEqual(after.x, afterB.x);
            predictor.PredictAdaptive(now + .101, 120, out var stale, out _, visualExtraMilliseconds: 80);
            Assert.AreEqual(20 / 6f, stale.x, .00001f, "追加分でも欠測時は止まる");
        }
        else
        {
            predictor.Predict(now, 60, out var before, out _);
            predictor.Predict(now, 60, out var after, out _, 80);
            Assert.AreEqual(.8f, after.x - before.x, .00001f);
        }
        predictor.Predict(now, 0, out var off, out _, 80);
        Assert.AreEqual(20 / 6f, off.x, .00001f);
        predictor.PredictAdaptive(now, 0, out off, out _, visualExtraMilliseconds: 80);
        Assert.AreEqual(20 / 6f, off.x, .00001f);
    }

    [Test]
    public void EffectiveJudgeCorrectionIsUnchangedIncludingBothClampBoundariesAndFallbacks()
    {
        foreach (double? automatic in new double?[] { null, 5, 30, 85, 120, 250, 900 })
        foreach (int mode in new[] { PhoneSaberJudgeLatencySettings.Automatic, PhoneSaberJudgeLatencySettings.GameDefault })
        foreach (int offset in new[] { -250, -50, 0, 150 })
        foreach (int red in new[] { 0, 20, 60, 120 })
        foreach (int blue in new[] { 0, 40, 120 })
        foreach (int extra in new[] { 0, 10, 80 })
        {
            double beforeLead = PhoneSaberJudgeLatencySettings.PredictionLead(red, blue);
            double afterLead = PhoneSaberJudgeLatencySettings.PredictionLead(
                PhoneSaberVisualLeadSettings.TotalLead(red, extra), PhoneSaberVisualLeadSettings.TotalLead(blue, extra));
            double before = PhoneSaberJudgeLatencySettings.SecondsToApply(mode, automatic, offset, .12, beforeLead);
            double after = PhoneSaberJudgeLatencySettings.SecondsToApply(mode, automatic, offset, .12, afterLead, afterLead - beforeLead);
            Assert.AreEqual(beforeLead + before * 1000, afterLead + after * 1000, 1e-6,
                $"auto={automatic} mode={mode} offset={offset} red={red} blue={blue} extra={extra}");
            if (beforeLead == 0) Assert.AreEqual(before, after);
        }
        Assert.AreEqual(-.230, PhoneSaberJudgeLatencySettings.SecondsToApply(
            PhoneSaberJudgeLatencySettings.Automatic, 30, -250, .12, 200, 80), 1e-6);
    }

    [Test]
    public void JudgeClockCancelsTheNewLeadWithoutWaitingForTheNextEstimateRefresh()
    {
        const string station = "visual-clock";
        var go = new GameObject("先読みと判定時計の同時反映テスト");
        go.SetActive(false);
        var input = go.AddComponent<InputPoint>();
        var manager = go.AddComponent<GamePlayManager>();
        var judge = go.AddComponent<PhoneSaberJudgeLatency>();
        var instanceProperty = typeof(InputPoint).GetProperty("Instance");
        object oldInstance = instanceProperty.GetValue(null);
        string[] properties = { "Mode", "OffsetMs", "AutomaticMs", "GameMs", "AppliedMs", "PredictionLeadMs" };
        var oldValues = new object[properties.Length];
        for (int i = 0; i < properties.Length; i++)
            oldValues[i] = typeof(PhoneSaberJudgeLatency).GetProperty(properties[i]).GetValue(null);
        try
        {
            instanceProperty.SetValue(null, input);
            typeof(InputPoint).GetField("phoneSaberStation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, station);
            typeof(PhoneSaberJudgeLatency).GetProperty("Mode").SetValue(null, PhoneSaberJudgeLatencySettings.Automatic);
            typeof(PhoneSaberJudgeLatency).GetProperty("OffsetMs").SetValue(null, 0);
            typeof(PhoneSaberJudgeLatency).GetProperty("AutomaticMs").SetValue(null, (double?)85);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(PhoneSaberJudgeLatency).GetField("manager", flags).SetValue(judge, manager);
            typeof(PhoneSaberJudgeLatency).GetField("gameSeconds", flags).SetValue(judge, .12);
            typeof(PhoneSaberJudgeLatency).GetField("nextTick", flags).SetValue(judge, double.PositiveInfinity);
            var update = typeof(PhoneSaberJudgeLatency).GetMethod("Update", flags);
            input.SetPredictionHorizonMilliseconds(40);
            update.Invoke(judge, null);
            double before = manager.cameraJudgeLatencySeconds * 1000.0 + PhoneSaberJudgeLatency.PredictionLeadMs;
            input.SetVisualExtraMilliseconds(80);
            update.Invoke(judge, null);
            Assert.AreEqual(120, PhoneSaberJudgeLatency.PredictionLeadMs);
            Assert.AreEqual(before, manager.cameraJudgeLatencySeconds * 1000.0 + PhoneSaberJudgeLatency.PredictionLeadMs, .00001);
            Assert.AreEqual(85, PhoneSaberJudgeLatency.AutomaticMs.Value, "測定値は追加分で動かさない");
            int order = typeof(PhoneSaberJudgeLatency).GetCustomAttribute<DefaultExecutionOrder>().order;
            Assert.Greater(order, typeof(SaberInputBridge).GetCustomAttribute<DefaultExecutionOrder>().order);
            Assert.Less(order, 0, "既定順序の判定より前に相殺する");
        }
        finally
        {
            instanceProperty.SetValue(null, oldInstance);
            for (int i = 0; i < properties.Length; i++)
                typeof(PhoneSaberJudgeLatency).GetProperty(properties[i]).SetValue(null, oldValues[i]);
            Object.DestroyImmediate(go);
            PlayerPrefs.DeleteKey(PhoneSaberVisualLeadSettings.Key(station));
            PlayerPrefs.DeleteKey(PhoneSaberPredictionSettings.Key(station));
            PlayerPrefs.DeleteKey(PhoneSaberPredictionSettings.AutomaticKey(station));
            PlayerPrefs.Save();
        }
    }

    [Test]
    public void InputPointReportsTheSameTotalLeadForDrawingAndJudgeAndOffIgnoresExtra()
    {
        const string station = "visual-input";
        var go = new GameObject("画面の先回りテスト");
        go.SetActive(false); // ソケット・外部プロセスは開始しない。
        var input = go.AddComponent<InputPoint>();
        typeof(InputPoint).GetField("phoneSaberStation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, station);
        try
        {
            input.SetPredictionHorizonMilliseconds(40);
            input.SetVisualExtraMilliseconds(80);
            Assert.AreEqual(40, input.BasePredictionLead(false));
            Assert.AreEqual(120, input.PredictionLead(false));
            Assert.AreEqual(120, input.PredictionLead(true));
            input.SetPredictionHorizonMilliseconds(0);
            Assert.AreEqual(0, input.PredictionLead(false));
            Assert.AreEqual(80, input.VisualExtraMilliseconds, "OFFでも保存値は保つ");
            input.SetPredictionAutomatic();
            Assert.AreEqual(0, input.PredictionLead(false), "自動の欠測も追加だけで動かさない");
            var policy = (PhoneSaberAutomaticLeadPolicy)typeof(InputPoint)
                .GetField("redPrediction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(input);
            policy.Update(1, "test", 120, null);
            Assert.AreEqual(200, input.PredictionLead(false));
            Assert.AreEqual(0, input.PredictionLead(true));
        }
        finally
        {
            Object.DestroyImmediate(go);
            PlayerPrefs.DeleteKey(PhoneSaberVisualLeadSettings.Key(station));
            PlayerPrefs.DeleteKey(PhoneSaberPredictionSettings.Key(station));
            PlayerPrefs.DeleteKey(PhoneSaberPredictionSettings.AutomaticKey(station));
            PlayerPrefs.Save();
        }
    }
}
