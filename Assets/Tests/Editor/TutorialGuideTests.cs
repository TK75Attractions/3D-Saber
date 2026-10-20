using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

public class TutorialGuideTests
{
    [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(8)]
    public void LongSuccessRequiresEveryCut(int count)
    {
        var step = TutorialProgram.Get("long");
        for (int cut = 0; cut < count; cut++)
            Assert.False(TutorialProgram.CountsAsSuccess(step, JudgmentTier.Perfect, true, count, cut));
        Assert.True(TutorialProgram.CountsAsSuccess(step, JudgmentTier.Good, true, count, count));
        Assert.False(TutorialProgram.CountsAsSuccess(step, JudgmentTier.Miss, true, count, count));
    }

    [Test]
    public void LongPracticeTeachesMultipleCountsOnBothHandsAndFitsItsDeadline()
    {
        var step = TutorialProgram.Get("long");
        CollectionAssert.AreEquivalent(new[] { 2, 3, 4 }, step.Pattern.Select(n => n.count));
        Assert.That(step.Pattern.Select(n => n.color).Distinct().Count(), Is.EqualTo(2));
        StringAssert.Contains("数字", step.Title);
        Assert.AreEqual(step.Pattern.Select(n => n.count).Distinct().Count(), step.Goal);
        double lastEnd = step.Pattern.Max(n => n.dt + n.lengthMs / 1000.0);
        Assert.Less(lastEnd + 5 + step.PeriodSeconds, step.TimeoutSeconds, "接近待ちと再挑戦1周が入る");
        Assert.Greater(step.PeriodSeconds, lastEnd + TutorialGuideMotion.RecoverSeconds);
    }

    [Test]
    public void DemonstrationUsesTheActualChartTimeAndIndependentHands()
    {
        var notes = new List<NoteData> { new NoteData { time = 12500, color = "red", direction = "up" } };
        Assert.AreEqual(.5f, TutorialGuideMotion.Sample(notes, 12, SaberHand.Right));
        Assert.Less(TutorialGuideMotion.Sample(notes, 12.46, SaberHand.Right), .5f);
        Assert.Greater(TutorialGuideMotion.Sample(notes, 12.54, SaberHand.Right), .5f);
        Assert.AreEqual(.5f, TutorialGuideMotion.Sample(notes, 12.54, SaberHand.Left));
        Assert.AreEqual(.5f, TutorialGuideMotion.Sample(notes, 12.8, SaberHand.Right));
        Assert.AreEqual(.5f, TutorialGuideMotion.Sample(notes, 16.54, SaberHand.Right), "4秒音源が周回しても勝手に再演しない");
        notes.Add(new NoteData { time = 16500, color = "red", direction = "up" });
        Assert.Greater(TutorialGuideMotion.Sample(notes, 16.54, SaberHand.Right), .5f, "実際に次のノーツがある場合だけ振る");
    }

    [Test]
    public void EveryLongStrokeMatchesTheCountAndReleaseDoesNotAddAnotherStroke()
    {
        foreach (var pattern in TutorialProgram.Get("long").Pattern)
        {
            var note = pattern.ToNote(20);
            var notes = new[] { note };
            var hand = note.color == "blue" ? SaberHand.Left : SaberHand.Right;
            for (int cut = 0; cut < note.count; cut++)
            {
                double hit = note.TimeSeconds + cut * .5;
                float before = TutorialGuideMotion.Sample(notes, hit - .04, hand);
                float after = TutorialGuideMotion.Sample(notes, hit + .04, hand);
                Assert.AreEqual(.5f, TutorialGuideMotion.Sample(notes, hit, hand), .0001);
                Assert.That((after - before) * (cut % 2 == 1 ? 1 : -1), Is.GreaterThan(0));
            }
            Assert.AreEqual(.5f, TutorialGuideMotion.Sample(notes, note.TimeSeconds + note.count * .5, hand));
        }
    }

    [Test]
    public void SimultaneousAndGoldDemonstrateTheRightHandsWithoutInput()
    {
        var notes = TutorialProgram.Get("simul").Pattern.Select(n => n.ToNote(8)).ToArray();
        Assert.Less(TutorialGuideMotion.Sample(notes, 8.04, SaberHand.Left), .5f);
        Assert.AreEqual(TutorialGuideMotion.Sample(notes, 8.04, SaberHand.Left), TutorialGuideMotion.Sample(notes, 8.04, SaberHand.Right));
        notes = TutorialProgram.Get("gold").Pattern.Select(n => n.ToNote(8)).ToArray();
        Assert.AreEqual(.5f, TutorialGuideMotion.Sample(notes, 8.04, SaberHand.Left));
        Assert.Less(TutorialGuideMotion.Sample(notes, 8.04, SaberHand.Right), .5f);
    }
}
