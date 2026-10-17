using NUnit.Framework;
using UnityEngine;

public class PhoneSaberJudgeLatencyTests
{
    [Test]
    public void AutomaticUsesTheMeasuredDelayPlusTheStationOffset()
    {
        Assert.AreEqual(.085, PhoneSaberJudgeLatencySettings.SecondsToApply(PhoneSaberJudgeLatencySettings.Automatic, 85, 0, .12), 1e-6);
        Assert.AreEqual(.105, PhoneSaberJudgeLatencySettings.SecondsToApply(PhoneSaberJudgeLatencySettings.Automatic, 85, 20, .12), 1e-6);
        // 予測で先読みした分は差し引く（二重補正しない）。
        Assert.AreEqual(.045, PhoneSaberJudgeLatencySettings.SecondsToApply(PhoneSaberJudgeLatencySettings.Automatic, 85, 0, .12, 40), 1e-6);
        // 自動予測は同じ推定から先読みを作る。引いた後は 0 近くまで下がってよい（30 ms に張り付かない）。
        Assert.AreEqual(.005, PhoneSaberJudgeLatencySettings.SecondsToApply(PhoneSaberJudgeLatencySettings.Automatic, 85, 0, .12, 80), 1e-6);
        Assert.AreEqual(0, PhoneSaberJudgeLatencySettings.SecondsToApply(PhoneSaberJudgeLatencySettings.Automatic, 85, -20, .12, 85), 1e-6);
        Assert.AreEqual(.025, PhoneSaberJudgeLatencySettings.SecondsToApply(PhoneSaberJudgeLatencySettings.Automatic, 85, 20, .12, 80), 1e-6);
        Assert.AreEqual(60, PhoneSaberJudgeLatencySettings.PredictionLead(60, 0), 1e-9);
        Assert.AreEqual(50, PhoneSaberJudgeLatencySettings.PredictionLead(60, 40), 1e-9);
        Assert.AreEqual(0, PhoneSaberJudgeLatencySettings.PredictionLead(0, 0), 1e-9);
        Assert.AreEqual(.085, PhoneSaberJudgeLatencySettings.SecondsToApply(PhoneSaberJudgeLatencySettings.Automatic, 85, 0, .12, double.NaN), 1e-6);
        // 測定値が無い・ゲームの設定を選んだときはゲームの値のまま。
        Assert.AreEqual(.12, PhoneSaberJudgeLatencySettings.SecondsToApply(PhoneSaberJudgeLatencySettings.Automatic, null, 30, .12), 1e-6);
        Assert.AreEqual(.12, PhoneSaberJudgeLatencySettings.SecondsToApply(PhoneSaberJudgeLatencySettings.GameDefault, 85, 30, .12), 1e-6);
        // 異常な推定でも判定を壊さない範囲に収める。
        Assert.AreEqual(PhoneSaberJudgeLatencySettings.MaximumMs / 1000.0,
            PhoneSaberJudgeLatencySettings.SecondsToApply(PhoneSaberJudgeLatencySettings.Automatic, 900, 0, .12), 1e-6);
        Assert.AreEqual(PhoneSaberJudgeLatencySettings.MinimumMs / 1000.0,
            PhoneSaberJudgeLatencySettings.SecondsToApply(PhoneSaberJudgeLatencySettings.Automatic, 5, -100, .12), 1e-6);
    }

    [Test]
    public void CombineAveragesUsableColoursAndSmoothingFollowsSlowly()
    {
        Assert.AreEqual(90, PhoneSaberJudgeLatencySettings.Combine(80, 100).Value, 1e-9);
        Assert.AreEqual(80, PhoneSaberJudgeLatencySettings.Combine(80, null).Value, 1e-9);
        Assert.AreEqual(100, PhoneSaberJudgeLatencySettings.Combine(double.NaN, 100).Value, 1e-9);
        Assert.IsNull(PhoneSaberJudgeLatencySettings.Combine(null, double.PositiveInfinity));
        Assert.AreEqual(85, PhoneSaberJudgeLatencySettings.Smooth(null, 85), 1e-9);
        Assert.AreEqual(91, PhoneSaberJudgeLatencySettings.Smooth(85, 105), 1e-9);
    }

    [Test]
    public void UnsetStationsDefaultToAutomaticAndChoicesPersistPerStation()
    {
        string a = PhoneSaberJudgeLatencySettings.Key("jlat-a"), b = PhoneSaberJudgeLatencySettings.Key("jlat-b");
        PlayerPrefs.DeleteKey(a); PlayerPrefs.DeleteKey(b);
        PlayerPrefs.DeleteKey(PhoneSaberJudgeLatencySettings.OffsetKey("jlat-a"));
        try
        {
            Assert.AreEqual(PhoneSaberJudgeLatencySettings.Automatic, PhoneSaberJudgeLatencySettings.Load("jlat-a"));
            Assert.AreEqual(0, PhoneSaberJudgeLatencySettings.LoadOffset("jlat-a"));
            PhoneSaberJudgeLatencySettings.Save("jlat-a", PhoneSaberJudgeLatencySettings.GameDefault, 400);
            Assert.AreEqual(PhoneSaberJudgeLatencySettings.GameDefault, PhoneSaberJudgeLatencySettings.Load("jlat-a"));
            Assert.AreEqual(PhoneSaberJudgeLatencySettings.MaximumOffsetMs, PhoneSaberJudgeLatencySettings.LoadOffset("jlat-a"));
            Assert.AreEqual(PhoneSaberJudgeLatencySettings.Automatic, PhoneSaberJudgeLatencySettings.Load("jlat-b"));
        }
        finally
        {
            PlayerPrefs.DeleteKey(a); PlayerPrefs.DeleteKey(b);
            PlayerPrefs.DeleteKey(PhoneSaberJudgeLatencySettings.OffsetKey("jlat-a"));
        }
    }

    [Test]
    public void CentringAddsTheLateMedianInTensAndStaysInRange()
    {
        Assert.AreEqual(30, PhoneSaberJudgeLatencySettings.CentredOffset(0, 31));
        Assert.AreEqual(-20, PhoneSaberJudgeLatencySettings.CentredOffset(10, -26));
        Assert.AreEqual(PhoneSaberJudgeLatencySettings.MaximumOffsetMs, PhoneSaberJudgeLatencySettings.CentredOffset(140, 300));
    }

    [Test]
    public void RecentHitMedianKeepsTheLastFortyHits()
    {
        PhoneSaberJudgeTimingStats.Clear();
        try
        {
            Assert.IsNull(PhoneSaberJudgeTimingStats.Median());
            for (int i = 0; i < 40; i++) PhoneSaberJudgeTimingStats.AddHit(-100);
            for (int i = 0; i < 40; i++) PhoneSaberJudgeTimingStats.AddHit(i < 39 ? 20 : double.NaN);
            Assert.AreEqual(40, PhoneSaberJudgeTimingStats.Count);
            // 古い −100 は1件だけ残り、中央は新しい値。
            Assert.AreEqual(20, PhoneSaberJudgeTimingStats.Median().Value, 1e-9);
        }
        finally { PhoneSaberJudgeTimingStats.Clear(); }
    }

    [Test]
    public void TrialMarkRecordsTheSettingsInEffect()
    {
        Assert.AreEqual("n=3 judge=auto offset-ms=20 applied-ms=105 prediction-red-ms=40 prediction-blue-ms=0 frame-cap=2",
            PhoneSaberTrialMark.Detail(3, PhoneSaberJudgeLatencySettings.Automatic, 20, 104.6, 40, 0, 2));
        StringAssert.Contains("judge=game", PhoneSaberTrialMark.Detail(1, PhoneSaberJudgeLatencySettings.GameDefault, 0, null, 0, 0, 0));
        StringAssert.Contains("applied-ms=unknown", PhoneSaberTrialMark.Detail(1, PhoneSaberJudgeLatencySettings.GameDefault, 0, null, 0, 0, 0));
    }
}
