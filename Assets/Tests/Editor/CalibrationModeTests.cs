using NUnit.Framework;
using UnityEngine;

public class CalibrationModeTests
{
    [TearDown]
    public void ResetState()
    {
        GameSession.IsCalibrationMode = false;
    }

    [Test]
    public void IsCalibrationMode_DefaultFalse()
    {
        Assert.IsFalse(GameSession.IsCalibrationMode);
    }

    [Test]
    public void IsCalibrationMode_CanBeSet()
    {
        GameSession.IsCalibrationMode = true;
        Assert.IsTrue(GameSession.IsCalibrationMode);
    }

    // --- 合成譜面 ---

    [Test]
    public void SynthesizeCalibrationChart_GeneratesNotesAtBeatInterval()
    {
        var chart = GamePlayManager.SynthesizeCalibrationChart(120f, 10f, 2.0);
        Assert.AreEqual(120f, chart.bpm, 0.001f);
        Assert.AreEqual(0f, chart.offsetMs, 0.001f);
        // 2.0s から 10s まで 0.5s 間隔 → 16 ノーツ（t=2.0, 2.5, ..., 9.5）
        Assert.AreEqual(16, chart.notes.Count);
        Assert.AreEqual(2000f, chart.notes[0].time, 0.001f);
        Assert.AreEqual(2500f, chart.notes[1].time, 0.001f);
        Assert.AreEqual(9500f, chart.notes[15].time, 0.001f);
    }

    [Test]
    public void SynthesizeCalibrationChart_NotesAreCenterTaps()
    {
        var chart = GamePlayManager.SynthesizeCalibrationChart(120f, 5f, 2.0);
        foreach (var nd in chart.notes)
        {
            Assert.AreEqual(0f, nd.x);
            Assert.AreEqual(0.5f, nd.y);
            Assert.AreEqual("tap", nd.type);
            Assert.AreEqual(1, nd.count);
            Assert.AreEqual("none", nd.direction);
        }
    }

    [Test]
    public void SynthesizeCalibrationChart_LongDurationBigChart()
    {
        var chart = GamePlayManager.SynthesizeCalibrationChart(120f, 600f, 2.0);
        // 0.5s 間隔で 598s 分 → 約 1196 ノーツ
        Assert.That(chart.notes.Count, Is.InRange(1190, 1200));
    }

    [Test]
    public void SynthesizeCalibrationChart_RespectsBpm()
    {
        // BPM 60 = 1s/beat
        var chart = GamePlayManager.SynthesizeCalibrationChart(60f, 10f, 2.0);
        // 2.0 〜 10.0 まで 1.0s 間隔 → 8 ノーツ
        Assert.AreEqual(8, chart.notes.Count);
        Assert.AreEqual(2000f, chart.notes[0].time, 0.001f);
        Assert.AreEqual(3000f, chart.notes[1].time, 0.001f);
    }
}
