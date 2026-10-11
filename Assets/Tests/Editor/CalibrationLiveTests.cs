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
        Assert.AreEqual(35, h.Summary.MedianMs); Assert.AreEqual(24, h.Summary.Center);
        Assert.AreEqual("音に合っています", h.Hint); Assert.False(h.Summary.CanRecommend);
        h.Clear(); Assert.AreEqual(0, h.Count); Assert.AreEqual(0, h.Misses); Assert.IsNull(h.Summary);
    }
    [Test] public void SixConsistentHitsGuideOnlyOutsideTheGamePerfectWindow()
    {
        var h = new CalibrationHistory();
        for (int i=0;i<5;i++) h.Add(-65,i%2==0?SaberHand.Left:SaberHand.Right);
        Assert.AreEqual("",h.Hint); Assert.Zero(h.SuggestedDirection);
        h.Add(-65,SaberHand.Right);
        Assert.AreEqual(-1,h.SuggestedDirection); Assert.AreEqual(-65,h.RecommendedDelta);
        Assert.AreEqual("少し早め",h.Hint);
        h.Clear();
        for (int i=0;i<6;i++) h.Add(100,i%2==0?SaberHand.Left:SaberHand.Right);
        Assert.AreEqual(1,h.SuggestedDirection); Assert.AreEqual(100,h.RecommendedDelta);
        h.Clear();
        for (int i=0;i<6;i++) h.Add(35,i%2==0?SaberHand.Left:SaberHand.Right);
        Assert.Zero(h.SuggestedDirection); Assert.Zero(h.RecommendedDelta);
        Assert.AreEqual("音に合っています",h.Hint);
    }
    [Test] public void MissingUnbalancedAndScatteredInputsNeverOfferAnAdjustment()
    {
        var h=new CalibrationHistory();
        for(int i=0;i<6;i++) h.Add(100,SaberHand.Left);
        Assert.Zero(h.SuggestedDirection);
        h.Clear();
        for(int i=0;i<6;i++) h.Add(i%2==0?-70:120,i%2==0?SaberHand.Left:SaberHand.Right);
        Assert.Zero(h.SuggestedDirection);
        h.Clear();
        for(int i=0;i<8;i++) h.Add(i%4<2?-70:150,i%2==0?SaberHand.Left:SaberHand.Right);
        Assert.Zero(h.SuggestedDirection);
        h.Clear();
        for(int i=0;i<6;i++) {h.Add(100,i%2==0?SaberHand.Left:SaberHand.Right);h.RecordMiss();}
        Assert.Zero(h.SuggestedDirection);
        for(int i=0;i<12;i++) h.Add(100,i%2==0?SaberHand.Left:SaberHand.Right);
        Assert.AreEqual(1,h.SuggestedDirection,"古い見逃しは回復後まで妨げない");
    }
    [TestCase(-42.51,"少し早め")]
    [TestCase(-42.5,"音に合っています")]
    [TestCase(0,"音に合っています")]
    [TestCase(35,"音に合っています")]
    [TestCase(85,"音に合っています")]
    [TestCase(85.01,"少し遅め")]
    public void LiveFeedbackUsesActualPerfectWindow(double error,string expected)
    {Assert.AreEqual(expected,CalibrationProtocol.LiveFeedback(error));}
    [TestCase(-100)] [TestCase(195)]
    public void EdgeCensoredHitsOnlySuggestSmallManualSteps(double error)
    {
        var h=new CalibrationHistory();
        for(int i=0;i<6;i++)h.Add(error,i%2==0?SaberHand.Left:SaberHand.Right);
        Assert.AreNotEqual(0,h.SuggestedDirection); Assert.Zero(h.RecommendedDelta);
    }
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
