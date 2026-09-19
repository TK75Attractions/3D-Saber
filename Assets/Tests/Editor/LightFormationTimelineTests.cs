using System;
using NUnit.Framework;
using UnityEngine;

public class LightFormationTimelineTests
{
    private static StagePerformanceTimeline.Section Section(double start, double end, float intensity = 1) =>
        new StagePerformanceTimeline.Section { startSeconds = start, endSeconds = end, intensity = intensity };

    private static StagePerformanceTimeline Timeline(params StagePerformanceTimeline.Section[] sections) =>
        new StagePerformanceTimeline { sections = sections };

    [TestCase(0,0)] [TestCase(10,0)] [TestCase(11.999,0)] [TestCase(12,0)]
    [TestCase(13.5,.5)] [TestCase(15,1)] [TestCase(22,1)] [TestCase(27,1)]
    [TestCase(28.5,.5)] [TestCase(30,0)] [TestCase(31,0)]
    public void WaitsTwoSecondsThenRisesHoldsAndReturnsWithinTheSection(double time, double expected)
    {
        Assert.That(Timeline(Section(10,30)).EvaluateLightFormation(time),Is.EqualTo((float)expected).Within(.00001f));
    }

    [Test]
    public void TwelveSecondMinimumKeepsFourSecondsAtTheFullFormation()
    {
        var timeline = Timeline(Section(10,22,.65f));
        Assert.AreEqual(0,timeline.EvaluateLightFormation(12));
        Assert.AreEqual(.5f,timeline.EvaluateLightFormation(13.5));
        foreach (double time in new[]{15d,17,19}) Assert.AreEqual(1,timeline.EvaluateLightFormation(time));
        Assert.AreEqual(.5f,timeline.EvaluateLightFormation(20.5));
        Assert.AreEqual(0,timeline.EvaluateLightFormation(22));
    }

    [Test]
    public void ChoosesTheStrongestEligibleSectionRatherThanRejectingEverythingForAShortPeak()
    {
        var timeline = Timeline(Section(10,21.999,1), Section(30,60,.6499f), Section(70,82,.65f));
        Assert.AreEqual(0,timeline.EvaluateLightFormation(16));
        Assert.AreEqual(0,timeline.EvaluateLightFormation(45));
        Assert.AreEqual(1,timeline.EvaluateLightFormation(76));
    }

    [Test]
    public void StrengthThenLaterStartThenLaterEndBreakTiesRegardlessOfArrayOrder()
    {
        var timeline = Timeline(Section(100,130,.9f), Section(10,40,1), Section(50,70,1),
            Section(50,90,1), Section(150,180,.7f), Section(50,90,1));
        foreach (bool reverse in new[]{false,true})
        {
            if (reverse) Array.Reverse(timeline.sections);
            Assert.AreEqual(0,timeline.EvaluateLightFormation(20));
            Assert.AreEqual(1,timeline.EvaluateLightFormation(55));
            Assert.AreEqual(1,timeline.EvaluateLightFormation(80));
            Assert.AreEqual(0,timeline.EvaluateLightFormation(110));
            Assert.AreEqual(0,timeline.EvaluateLightFormation(160));
        }
    }

    [Test]
    public void ClampedStrengthSelectsTheSectionButDoesNotReduceTheTargetPosition()
    {
        var clamped = Timeline(Section(10,30,3),Section(50,80,2));
        Assert.AreEqual(0,clamped.EvaluateLightFormation(20));
        Assert.AreEqual(1,clamped.EvaluateLightFormation(60));
        var threshold = Timeline(Section(10,30,.65f));
        Assert.AreEqual(1,threshold.EvaluateLightFormation(20));
    }

    [Test]
    public void OverlapsDoNotSwitchTheSelectedSectionOrReplayItsEntrance()
    {
        var timeline = Timeline(Section(10,80,.7f),Section(20,50,.9f),Section(30,41,1));
        Assert.AreEqual(0,timeline.EvaluateLightFormation(15));
        Assert.AreEqual(1,timeline.EvaluateLightFormation(25));
        Assert.AreEqual(1,timeline.EvaluateLightFormation(30));
        Assert.AreEqual(1,timeline.EvaluateLightFormation(40));
        Assert.AreEqual(0,timeline.EvaluateLightFormation(55));
        // 選ばれなかった短い区間の既存入口は消さず、灯具の保持だけを独立させる。
        Assert.Greater(timeline.EvaluatePresentation(30).impact,0);
    }

    [Test]
    public void PauseAndNonSequentialSeeksReproduceTheSamePositionsWithoutState()
    {
        var timeline = Timeline(Section(10,30));
        double[] times = {28.5,13.5,80,22,0,13.5,28.5,9,15,15};
        float[] expected = {.5f,.5f,0,1,0,.5f,.5f,0,1,1};
        for (int i = 0; i < times.Length; i++)
            Assert.AreEqual(expected[i],timeline.EvaluateLightFormation(times[i]),"time="+times[i]);
    }

    [Test]
    public void ShapeRemainsBoundedAndContinuousAtAllFourTransitions()
    {
        var timeline = Timeline(Section(10,22));
        foreach (double boundary in new[]{12d,15,19,22})
        {
            float before = timeline.EvaluateLightFormation(boundary-.001);
            float at = timeline.EvaluateLightFormation(boundary);
            float after = timeline.EvaluateLightFormation(boundary+.001);
            Assert.Less(Mathf.Abs(at-before),.00001f);
            Assert.Less(Mathf.Abs(after-at),.00001f);
        }
        for (double time = 9; time < 23; time += .05)
            Assert.That(timeline.EvaluateLightFormation(time),Is.InRange(0f,1f));
    }

    [Test]
    public void EmptyOrMalformedSectionsReturnZeroAndDoNotHideAValidCandidate()
    {
        Assert.AreEqual(0,Timeline().EvaluateLightFormation(15));
        Assert.AreEqual(0,Timeline(null).EvaluateLightFormation(15));
        var badFadeIn = Section(0,100); badFadeIn.fadeInSeconds = float.NaN;
        var badFadeOut = Section(0,100); badFadeOut.fadeOutSeconds = float.PositiveInfinity;
        var negativeInfiniteFade = Section(0,100); negativeInfiniteFade.fadeOutSeconds = float.NegativeInfinity;
        var invalid = new[]{null,Section(-1,100),Section(4,4),Section(9,2),
            Section(double.NaN,100),Section(double.NegativeInfinity,100),
            Section(0,double.PositiveInfinity),Section(0,double.NaN),
            Section(0,100,float.NaN),Section(0,100,float.PositiveInfinity),
            Section(0,100,float.NegativeInfinity),badFadeIn,badFadeOut,negativeInfiniteFade};
        foreach (var section in invalid)
        {
            Assert.AreEqual(0,Timeline(section).EvaluateLightFormation(15));
            var withValid = Timeline(section,Section(50,80,.65f));
            Assert.AreEqual(0,withValid.EvaluateLightFormation(15));
            Assert.AreEqual(1,withValid.EvaluateLightFormation(60));
        }
    }

    [Test]
    public void InvalidSongTimesNeverReturnAnOldNonzeroValue()
    {
        var timeline = Timeline(Section(0,12));
        foreach (double invalid in new[]{-1d,0,double.NaN,double.NegativeInfinity,double.PositiveInfinity})
        {
            Assert.AreEqual(1,timeline.EvaluateLightFormation(6));
            Assert.AreEqual(0,timeline.EvaluateLightFormation(invalid));
        }
    }

    [Test]
    public void FixedMotionTimingDoesNotUseFiniteFadeLengthsOrEntranceOverrides()
    {
        var section = Section(10,30);
        section.fadeInSeconds = -4;
        section.fadeOutSeconds = 100;
        section.anticipationSeconds = float.NaN;
        section.impactSeconds = float.PositiveInfinity;
        var timeline = Timeline(section);
        Assert.AreEqual(.5f,timeline.EvaluateLightFormation(13.5));
        Assert.AreEqual(1,timeline.EvaluateLightFormation(20));
        Assert.AreEqual(.5f,timeline.EvaluateLightFormation(28.5));
    }

    [Test]
    public void LargeFiniteTimesAreClampedBeforeTheyAreConvertedToFloat()
    {
        var timeline = Timeline(Section(0,double.MaxValue));
        Assert.AreEqual(1,timeline.EvaluateLightFormation(double.MaxValue*.5));
        Assert.AreEqual(0,timeline.EvaluateLightFormation(double.MaxValue));
        var later = Timeline(Section(1e100,2e100));
        Assert.AreEqual(0,later.EvaluateLightFormation(1));
        Assert.AreEqual(1,later.EvaluateLightFormation(1.5e100));
    }

    [TestCase("2_23_AM",129.333,150.667)] [TestCase("ElDorado",167.4,186.6)]
    [TestCase("Epilogue",111.208,145.401)] [TestCase("Morning",113.898,130.169)]
    [TestCase("揺籠",140.245,165.361)]
    public void ExistingSongsUseOneAuthoredLongSection(string song, double start, double end)
    {
        var timeline = StagePerformanceTimeline.Load(song);
        Assert.AreEqual(0,timeline.EvaluateLightFormation(start+2));
        Assert.That(timeline.EvaluateLightFormation(start+3.5),Is.EqualTo(.5f).Within(.00001f));
        Assert.AreEqual(1,timeline.EvaluateLightFormation(start+5));
        Assert.AreEqual(1,timeline.EvaluateLightFormation(end-3));
        Assert.That(timeline.EvaluateLightFormation(end-1.5),Is.EqualTo(.5f).Within(.00001f));
        Assert.AreEqual(0,timeline.EvaluateLightFormation(end));
    }

    [Test]
    public void AndalusiaWithoutAuthoredSectionsNeverMovesTheFormation()
    {
        var timeline = StagePerformanceTimeline.Load("Andalusia");
        Assert.IsEmpty(timeline.sections);
        foreach (double time in new[]{0d,1,50,100,150,200,300})
            Assert.AreEqual(0,timeline.EvaluateLightFormation(time));
    }
}
