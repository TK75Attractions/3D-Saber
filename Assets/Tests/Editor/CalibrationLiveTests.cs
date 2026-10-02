using NUnit.Framework;
using UnityEngine;

public class CalibrationLiveTests
{
    [Test] public void RecentHistoryRollsOverWithoutTurningMissesIntoCenteredHits()
    {
        var h = new CalibrationHistory();
        for (int i = 0; i < 24; i++) h.Add(-30, i % 2 == 0 ? SaberHand.Left : SaberHand.Right);
        for (int i = 0; i < 24; i++) h.Add(35, i % 2 == 0 ? SaberHand.Left : SaberHand.Right);
        h.RecordMiss(); h.Add(double.NaN, SaberHand.Any);
        Assert.AreEqual(24, h.Count); Assert.AreEqual(1, h.Misses);
        Assert.AreEqual(35, h.Summary.MedianMs); Assert.AreEqual(24, h.Summary.Late);
        StringAssert.Contains("＋", h.Hint); Assert.False(h.Summary.CanRecommend);
        h.Clear(); Assert.AreEqual(0, h.Count); Assert.AreEqual(0, h.Misses); Assert.IsNull(h.Summary);
    }
    [Test] public void RecentHistoryRequiresEnoughConsistentHitsBeforeSuggestingADirection()
    {
        var h = new CalibrationHistory();
        for (int i = 0; i < 7; i++) h.Add(-30, SaberHand.Left);
        StringAssert.Contains("あと 1", h.Hint);
        h.Add(-30, SaberHand.Right); StringAssert.Contains("−", h.Hint);
        h.Clear();
        for (int i = 0; i < 16; i++) h.Add(i % 2 == 0 ? -25 : 25, i % 2 == 0 ? SaberHand.Left : SaberHand.Right);
        Assert.False(h.Hint.Contains("側へ"), "左右差・ばらつきを全体のずれと取り違えない");
    }
    [TestCase(-30,"早い")]
    [TestCase(-8.01,"早い")]
    [TestCase(-8,"ぴったり")]
    [TestCase(0,"ぴったり")]
    [TestCase(8,"ぴったり")]
    [TestCase(8.01,"遅い")]
    [TestCase(30,"遅い")]
    public void LiveFeedbackClassifiesCenterAndBothSides(double error,string expected)
    {Assert.AreEqual(expected,CalibrationProtocol.LiveFeedback(error));}
    [Test] public void LiveClickLoopIsFourEqualBeatsWithSilentSeam()
    {
        const int sr=48000;var pcm=CalibrationProtocol.LiveClickSamples(sr);
        Assert.AreEqual(115200,pcm.Length);Assert.AreEqual(0,pcm[0]);Assert.AreEqual(0,pcm[pcm.Length-1]);
        for(int beat=0;beat<4;beat++)
        {
            int start=beat*28800;double sum=0;
            for(int i=0;i<1680;i++)sum+=pcm[start+i]*pcm[start+i];
            Assert.Greater(sum,1);Assert.AreEqual(pcm[100],pcm[start+100]);
        }
    }
    [Test] public void LiveNotesKeepRegularTimingAndAlternateHands()
    {
        var left=CalibrationProtocol.LiveNote(100);var right=CalibrationProtocol.LiveNote(101);
        Assert.AreEqual("blue",left.color);Assert.AreEqual("red",right.color);
        Assert.AreEqual(600,right.time-left.time,.001);Assert.AreEqual(1,left.count);
        Assert.AreEqual(-1.65f,left.x);Assert.AreEqual(1.65f,right.x);
    }
    [TestCase(960,540,false)]
    [TestCase(960,100,true)]
    [TestCase(1921,100,false)]
    [TestCase(100,-1,false)]
    public void LivePointerIgnoresTheNoteCuttingArea(float x,float y,bool allowed)
    {Assert.AreEqual(allowed,SaberUIPointer.IsInsideBottomControls(new Vector2(x,y),1920,1080));}
}
