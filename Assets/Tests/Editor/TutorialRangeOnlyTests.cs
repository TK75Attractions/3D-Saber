using NUnit.Framework;

// チュートリアルで「いいえ」を選んだ人の、振る範囲を合わせる段だけの回(2026-10-26)。
public class TutorialRangeOnlyTests
{
    [Test]
    public void WithThePhoneOnlyTheRangeStepRuns()
    {
        var steps = TutorialProgram.Build(TutorialProgram.RangeOnlyFlow(true), true);
        Assert.AreEqual(1, steps.Length);
        Assert.AreEqual(TutorialProgram.RangeStepId, steps[0].Id);
        Assert.AreEqual(TutorialStepKind.Range, steps[0].Kind);
    }

    [Test]
    public void WithoutThePhoneThereIsNothingToRun()
    {
        Assert.IsEmpty(TutorialProgram.RangeOnlyFlow(false));
        Assert.IsEmpty(TutorialProgram.Build(TutorialProgram.RangeOnlyFlow(false), true));
    }

    [Test]
    public void TheFullTutorialStillStartsWithTheRangeStepOnlyWithThePhone()
    {
        Assert.AreEqual(TutorialProgram.RangeStepId, TutorialProgram.FlowFor(true)[0]);
        CollectionAssert.AreEqual(TutorialProgram.CommonFlow, TutorialProgram.FlowFor(false));
    }
}
