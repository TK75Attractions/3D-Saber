using System;
using NUnit.Framework;

public class VaultCurtainTimelineTests
{
    private static StagePerformanceTimeline.Section Section(double start, double end, float intensity = 1) =>
        new StagePerformanceTimeline.Section { startSeconds = start, endSeconds = end, intensity = intensity };

    private static StagePerformanceTimeline Timeline(params StagePerformanceTimeline.Section[] sections) =>
        new StagePerformanceTimeline { sections = sections };

    [TestCase(9, 0)] [TestCase(10, 0)] [TestCase(12, 0)]
    [TestCase(13, .15625)] [TestCase(14, .5)] [TestCase(16, 1)]
    [TestCase(22, 1)] [TestCase(24, .5)] [TestCase(25, .15625)]
    [TestCase(26, 0)] [TestCase(27, 0)]
    public void MinimumSectionHasTwoSecondWaitFourSecondTravelAndSixSecondHold(double time, double expected)
    {
        var timeline = Timeline(Section(10, 26, .65f));
        Assert.That(timeline.EvaluateVaultCurtain(time), Is.EqualTo((float)expected).Within(.00001f));
    }

    [Test]
    public void EligibilityIsCheckedBeforeChoosingTheStrongestSection()
    {
        var timeline = Timeline(Section(10, 25.999, 1), Section(30, 70, .6499f), Section(80, 96, .65f));
        Assert.AreEqual(0, timeline.EvaluateVaultCurtain(18));
        Assert.AreEqual(0, timeline.EvaluateVaultCurtain(45));
        Assert.AreEqual(1, timeline.EvaluateVaultCurtain(86));
    }

    [Test]
    public void StrengthThenLaterStartThenLaterEndSelectOneSectionIndependentOfArrayOrder()
    {
        var timeline = Timeline(Section(10, 40, 1), Section(50, 70, 1), Section(50, 90, 1),
            Section(100, 140, .9f), Section(50, 90, 1));
        for (int rotation = 0; rotation < timeline.sections.Length; rotation++)
        {
            Assert.AreEqual(0, timeline.EvaluateVaultCurtain(20));
            Assert.AreEqual(1, timeline.EvaluateVaultCurtain(56));
            Assert.AreEqual(1, timeline.EvaluateVaultCurtain(80));
            Assert.AreEqual(0, timeline.EvaluateVaultCurtain(110));
            var first = timeline.sections[0];
            Array.Copy(timeline.sections, 1, timeline.sections, 0, timeline.sections.Length - 1);
            timeline.sections[timeline.sections.Length - 1] = first;
        }
    }

    [Test]
    public void ClampedStrengthSelectsTheSectionButDoesNotScaleTheOpening()
    {
        var timeline = Timeline(Section(10, 40, 9), Section(50, 90, 2));
        Assert.AreEqual(0, timeline.EvaluateVaultCurtain(20));
        Assert.AreEqual(1, timeline.EvaluateVaultCurtain(60));
        Assert.AreEqual(1, Timeline(Section(10, 26, .65f)).EvaluateVaultCurtain(18));
    }

    [Test]
    public void NestedShortImpactDoesNotRestartTheCurtainOrSuppressExistingPresentation()
    {
        var timeline = Timeline(Section(10, 80, .7f), Section(20, 55, .9f), Section(34, 49, 1));
        Assert.AreEqual(0, timeline.EvaluateVaultCurtain(16));
        Assert.AreEqual(1, timeline.EvaluateVaultCurtain(30));
        Assert.AreEqual(1, timeline.EvaluateVaultCurtain(34));
        Assert.AreEqual(1, timeline.EvaluateVaultCurtain(45));
        Assert.AreEqual(0, timeline.EvaluateVaultCurtain(65));
        Assert.Greater(timeline.EvaluatePresentation(34).impact, 0);
    }

    [Test]
    public void PauseAndOutOfOrderSeeksReproduceTheSameOpening()
    {
        var timeline = Timeline(Section(10, 26));
        double[] times = { 24, 14, 100, 22, 0, 14, 24, 16, 16, 9 };
        float[] expected = { .5f, .5f, 0, 1, 0, .5f, .5f, 1, 1, 0 };
        for (int i = 0; i < times.Length; i++)
            Assert.AreEqual(expected[i], timeline.EvaluateVaultCurtain(times[i]), "time=" + times[i]);
    }

    [Test]
    public void OpeningIsMonotonicThenHeldThenMonotonicallyClosedWithoutOvershoot()
    {
        var timeline = Timeline(Section(10, 26));
        float previousOpen = 0, previousClose = 1;
        for (int step = 0; step <= 80; step++)
        {
            float opening = timeline.EvaluateVaultCurtain(12 + step * .05);
            float closing = timeline.EvaluateVaultCurtain(22 + step * .05);
            Assert.That(opening, Is.InRange(previousOpen, 1f));
            Assert.That(closing, Is.InRange(0f, previousClose));
            previousOpen = opening; previousClose = closing;
        }
        foreach (double time in new[] { 16d, 17, 19, 21, 22 })
            Assert.AreEqual(1, timeline.EvaluateVaultCurtain(time));
        foreach (double boundary in new[] { 12d, 16, 22, 26 })
        {
            float at = timeline.EvaluateVaultCurtain(boundary);
            Assert.That(timeline.EvaluateVaultCurtain(boundary - .001), Is.EqualTo(at).Within(.00001f));
            Assert.That(timeline.EvaluateVaultCurtain(boundary + .001), Is.EqualTo(at).Within(.00001f));
        }
    }

    [Test]
    public void MissingSectionsAndInvalidTimesDoNotReuseAnEarlierOpening()
    {
        Assert.AreEqual(0, Timeline().EvaluateVaultCurtain(18));
        Assert.AreEqual(0, new StagePerformanceTimeline { sections = null }.EvaluateVaultCurtain(18));
        var timeline = Timeline(Section(0, 16));
        foreach (double invalid in new[] { -1d, 0, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
        {
            Assert.AreEqual(1, timeline.EvaluateVaultCurtain(8));
            Assert.AreEqual(0, timeline.EvaluateVaultCurtain(invalid));
        }
    }

    [Test]
    public void MalformedSectionsAreIgnoredWithoutHidingAValidCandidate()
    {
        var fadeInNaN = Section(0, 100); fadeInNaN.fadeInSeconds = float.NaN;
        var fadeOutInfinity = Section(0, 100); fadeOutInfinity.fadeOutSeconds = float.PositiveInfinity;
        var fadeInNegativeInfinity = Section(0, 100); fadeInNegativeInfinity.fadeInSeconds = float.NegativeInfinity;
        var invalid = new[] { null, Section(-1, 100), Section(10, 10), Section(10, 5),
            Section(double.NaN, 100), Section(double.NegativeInfinity, 100),
            Section(0, double.PositiveInfinity), Section(0, double.NaN),
            Section(0, 100, float.NaN), Section(0, 100, float.PositiveInfinity),
            Section(0, 100, float.NegativeInfinity), fadeInNaN, fadeOutInfinity, fadeInNegativeInfinity };
        foreach (var bad in invalid)
        {
            Assert.AreEqual(0, Timeline(bad).EvaluateVaultCurtain(18));
            var withValid = Timeline(bad, Section(50, 80, .65f));
            Assert.AreEqual(0, withValid.EvaluateVaultCurtain(18));
            Assert.AreEqual(1, withValid.EvaluateVaultCurtain(60));
        }
    }

    [Test]
    public void FixedTravelDoesNotFollowFiniteFadeOrPresentationOverrides()
    {
        var section = Section(10, 26);
        section.fadeInSeconds = -100; section.fadeOutSeconds = float.MaxValue;
        section.anticipationSeconds = float.NaN; section.impactSeconds = float.PositiveInfinity;
        var timeline = Timeline(section);
        Assert.AreEqual(.5f, timeline.EvaluateVaultCurtain(14));
        Assert.AreEqual(1, timeline.EvaluateVaultCurtain(19));
        Assert.AreEqual(.5f, timeline.EvaluateVaultCurtain(24));
    }

    [Test]
    public void VeryLargeFiniteSongTimesRemainBoundedAndReturnAtTheEnd()
    {
        var timeline = Timeline(Section(0, double.MaxValue));
        Assert.AreEqual(1, timeline.EvaluateVaultCurtain(double.MaxValue * .5));
        Assert.AreEqual(0, timeline.EvaluateVaultCurtain(double.MaxValue));
        var later = Timeline(Section(1e100, 2e100));
        Assert.AreEqual(0, later.EvaluateVaultCurtain(1));
        Assert.AreEqual(1, later.EvaluateVaultCurtain(1.5e100));
        Assert.AreEqual(0, later.EvaluateVaultCurtain(2e100));
    }

    [TestCase("2_23_AM", 129.333, 150.667)] [TestCase("ElDorado", 167.4, 186.6)]
    [TestCase("Epilogue", 111.208, 145.401)] [TestCase("Morning", 113.898, 130.169)]
    [TestCase("揺籠", 140.245, 165.361)]
    public void ExistingSongsSelectAnAuthoredLongSectionWithoutDifficultyInput(string song, double start, double end)
    {
        var timeline = StagePerformanceTimeline.Load(song);
        Assert.AreEqual(0, timeline.EvaluateVaultCurtain(start + 2));
        Assert.That(timeline.EvaluateVaultCurtain(start + 4), Is.EqualTo(.5f).Within(.00001f));
        Assert.AreEqual(1, timeline.EvaluateVaultCurtain(start + 6));
        Assert.AreEqual(1, timeline.EvaluateVaultCurtain(end - 4));
        Assert.That(timeline.EvaluateVaultCurtain(end - 2), Is.EqualTo(.5f).Within(.00001f));
        Assert.AreEqual(0, timeline.EvaluateVaultCurtain(end));
    }

    [Test]
    public void AndalusiaWithoutAuthoredSectionsLeavesTheCurtainAtRest()
    {
        var timeline = StagePerformanceTimeline.Load("Andalusia");
        Assert.IsEmpty(timeline.sections);
        foreach (double time in new[] { 0d, 1, 50, 100, 150, 200, 300 })
            Assert.AreEqual(0, timeline.EvaluateVaultCurtain(time));
    }
}
