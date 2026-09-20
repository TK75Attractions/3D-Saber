using System;
using NUnit.Framework;

public class MarinePassTimelineTests
{
    private static StagePerformanceTimeline.Section Section(double start, double end, float intensity = 1) =>
        new StagePerformanceTimeline.Section { startSeconds = start, endSeconds = end, intensity = intensity };

    private static StagePerformanceTimeline Timeline(params StagePerformanceTimeline.Section[] sections) =>
        new StagePerformanceTimeline { sections = sections };

    // nextDown for the positive finite boundary values used here; no newer Math.BitDecrement API required.
    private static double PreviousDouble(double value) =>
        BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) - 1);

    [TestCase(0, -1)] [TestCase(1.999, -1)] [TestCase(2, 0)]
    [TestCase(5, 3)] [TestCase(9, 7)] [TestCase(13, 11)]
    [TestCase(15.999, 13.999)] [TestCase(16, -1)] [TestCase(18, -1)]
    public void MinimumSectionLeavesTwoSecondsOnEachSideAndPassesOnce(double time, double expected)
    {
        var timeline = Timeline(Section(0, 18, .65f));
        Assert.That(timeline.EvaluateMarinePassAge(time), Is.EqualTo((float)expected).Within(.00001f));
    }

    [Test]
    public void ValidLengthAndStrengthAreRequiredBeforeSelectingTheStrongest()
    {
        var timeline = Timeline(Section(0, 17.999, 1), Section(30, 90, .6499f), Section(100, 118, .65f));
        Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(8));
        Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(60));
        Assert.AreEqual(0, timeline.EvaluateMarinePassAge(102));
        Assert.AreEqual(7, timeline.EvaluateMarinePassAge(109));
        Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(116));
    }

    [Test]
    public void StrongestThenLaterStartThenLaterEndWinsInEveryArrayOrder()
    {
        var timeline = Timeline(Section(0, 30, 1), Section(50, 80, 1), Section(50, 100, 1),
            Section(110, 150, .9f), Section(50, 100, 1));
        for (int rotation = 0; rotation < timeline.sections.Length; rotation++)
        {
            Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(15));
            Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(60));
            Assert.AreEqual(0, timeline.EvaluateMarinePassAge(68));
            Assert.AreEqual(7, timeline.EvaluateMarinePassAge(75));
            Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(82));
            Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(130));
            var first = timeline.sections[0];
            Array.Copy(timeline.sections, 1, timeline.sections, 0, timeline.sections.Length - 1);
            timeline.sections[timeline.sections.Length - 1] = first;
        }
    }

    [Test]
    public void ClampedStrengthSelectsButDoesNotScaleOrSpeedUpThePass()
    {
        var timeline = Timeline(Section(0, 30, 9), Section(50, 100, 2));
        Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(15));
        Assert.AreEqual(7, timeline.EvaluateMarinePassAge(75));
        Assert.AreEqual(7, Timeline(Section(50, 100, .65f)).EvaluateMarinePassAge(75));
    }

    [Test]
    public void NestedShortImpactKeepsItsOwnAccentWithoutRestartingThePass()
    {
        var timeline = Timeline(Section(10, 50, .7f), Section(32, 44, 1));
        Assert.AreEqual(8, timeline.EvaluateMarinePassAge(31));
        Assert.AreEqual(9, timeline.EvaluateMarinePassAge(32));
        Assert.That(timeline.EvaluateMarinePassAge(32.2), Is.EqualTo(9.2f).Within(.00001f));
        Assert.Greater(timeline.EvaluatePresentation(32).impact, 0);
        Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(37));
        Assert.Greater(timeline.Evaluate(38), 0);
    }

    [Test]
    public void PausedClockAndOutOfOrderSeeksDirectlyReproduceTheAge()
    {
        var timeline = Timeline(Section(0, 18));
        double[] times = { 9d, 9, 15, 1, 5, 16, 9, 2, 2, 0, 18, 13 };
        float[] expected = { 7, 7, 13, -1, 3, -1, 7, 0, 0, -1, -1, 11 };
        for (int i = 0; i < times.Length; i++)
            Assert.AreEqual(expected[i], timeline.EvaluateMarinePassAge(times[i]), "time=" + times[i]);
    }

    [Test]
    public void MissingSectionsAndInvalidClockDoNotCarryAnEarlierPass()
    {
        Assert.AreEqual(-1, Timeline().EvaluateMarinePassAge(9));
        Assert.AreEqual(-1, new StagePerformanceTimeline { sections = null }.EvaluateMarinePassAge(9));
        var timeline = Timeline(Section(0, 18));
        foreach (double time in new[] { -1d, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
        {
            Assert.AreEqual(7, timeline.EvaluateMarinePassAge(9));
            Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(time));
        }
        timeline.sections = Array.Empty<StagePerformanceTimeline.Section>();
        Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(9));
    }

    [Test]
    public void MalformedSectionsCannotHideAValidCandidate()
    {
        var inNaN = Section(0, 100); inNaN.fadeInSeconds = float.NaN;
        var outInfinity = Section(0, 100); outInfinity.fadeOutSeconds = float.PositiveInfinity;
        var inNegativeInfinity = Section(0, 100); inNegativeInfinity.fadeInSeconds = float.NegativeInfinity;
        var invalid = new[] { null, Section(-1, 100), Section(10, 10), Section(10, 5),
            Section(double.NaN, 100), Section(double.NegativeInfinity, 100),
            Section(0, double.PositiveInfinity), Section(0, double.NaN),
            Section(0, 100, float.NaN), Section(0, 100, float.PositiveInfinity),
            Section(0, 100, float.NegativeInfinity), inNaN, outInfinity, inNegativeInfinity };
        foreach (var bad in invalid)
        {
            Assert.AreEqual(-1, Timeline(bad).EvaluateMarinePassAge(50));
            var withValid = Timeline(bad, Section(120, 138, .65f));
            Assert.AreEqual(-1, withValid.EvaluateMarinePassAge(50));
            Assert.AreEqual(7, withValid.EvaluateMarinePassAge(129));
        }
    }

    [Test]
    public void FiniteFadesAndUnusedPresentationFieldsDoNotAlterTheFixedPassWindow()
    {
        var section = Section(0, 18);
        section.fadeInSeconds = -100; section.fadeOutSeconds = float.MaxValue;
        section.anticipationSeconds = float.NaN; section.impactSeconds = float.PositiveInfinity;
        var timeline = Timeline(section);
        Assert.AreEqual(0, timeline.EvaluateMarinePassAge(2));
        Assert.AreEqual(7, timeline.EvaluateMarinePassAge(9));
        Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(16));
    }

    [Test]
    public void LastRepresentableInstantNeverRoundsTheReturnedFloatToFourteen()
    {
        var timeline = Timeline(Section(0, 18));
        Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(PreviousDouble(2)));
        float age = timeline.EvaluateMarinePassAge(PreviousDouble(16));
        Assert.That(age, Is.GreaterThan(13.999f).And.LessThan(14f));
        Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(16));
    }

    [Test]
    public void FiniteHugeTimesWithNoRepresentableFourteenSecondWindowStayInactive()
    {
        var full = Timeline(Section(0, double.MaxValue));
        Assert.AreEqual(-1, full.EvaluateMarinePassAge(double.MaxValue * .5));
        Assert.AreEqual(-1, full.EvaluateMarinePassAge(double.MaxValue));
        var later = Timeline(Section(1e100, 2e100));
        Assert.AreEqual(-1, later.EvaluateMarinePassAge(1.5e100));
        Assert.AreEqual(-1, later.EvaluateMarinePassAge(2e100));
    }

    [TestCase("2_23_AM", 133, 147)] [TestCase("ElDorado", 170, 184)]
    [TestCase("Epilogue", 121.3045, 135.3045)] [TestCase("揺籠", 145.803, 159.803)]
    public void ExistingSongsUseTheAuthoredCentralWindowWithoutDifficultyOrPhraseInference(string song, double start, double end)
    {
        var timeline = StagePerformanceTimeline.Load(song);
        Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(start - .00001));
        Assert.That(timeline.EvaluateMarinePassAge(start + .00001), Is.EqualTo(.00001f).Within(.00005f));
        Assert.That(timeline.EvaluateMarinePassAge(start + 3), Is.EqualTo(3f).Within(.00005f));
        Assert.That(timeline.EvaluateMarinePassAge(start + 7), Is.EqualTo(7f).Within(.00005f));
        Assert.That(timeline.EvaluateMarinePassAge(start + 11), Is.EqualTo(11f).Within(.00005f));
        Assert.That(timeline.EvaluateMarinePassAge(end - .00001), Is.GreaterThan(13.999f).And.LessThan(14f));
        Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(end + .00001));
    }

    [TestCase("Morning")] [TestCase("Andalusia")]
    public void SongsWithOnlyShortOrUnspecifiedSectionsDoNotCreateAPass(string song)
    {
        var timeline = StagePerformanceTimeline.Load(song);
        foreach (double time in new[] { 0d, 2, 40.678, 49, 56.949, 113.898, 122, 130.169, 145, 200, 300 })
            Assert.AreEqual(-1, timeline.EvaluateMarinePassAge(time));
    }
}
