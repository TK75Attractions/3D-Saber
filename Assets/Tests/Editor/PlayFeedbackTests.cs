using NUnit.Framework;

public class PlayFeedbackTests
{
    [TestCase(0,0,0,false)] [TestCase(1,0,0,true)] [TestCase(50,1,0,false)] [TestCase(50,0,1,false)]
    public void FullCombo_FollowsBadAndMissRules(int hits,int bad,int miss,bool expected)
        => Assert.AreEqual(expected,GameplayFeedbackPresenter.FullComboEligible(hits,bad,miss));
    [TestCase(0,false)] [TestCase(49,false)] [TestCase(50,true)] [TestCase(100,true)] [TestCase(200,true)]
    public void Milestones(int combo,bool expected) => Assert.AreEqual(expected,GameplayFeedbackPresenter.IsMilestone(combo));
    // 赤青を同じコマで切ると 29→31 のように節目を飛び越える。HUD の節目の演出はそれでも出す(10/23 点検)。
    [TestCase(29,31,true)] [TestCase(49,51,true)] [TestCase(99,101,true)] [TestCase(0,30,true)]
    [TestCase(100,101,false)] [TestCase(31,49,false)] [TestCase(30,30,false)] [TestCase(120,0,false)]
    public void MilestoneIsCrossedEvenWhenTwoNotesLandInOneFrame(int previous,int current,bool expected)
        => Assert.AreEqual(expected,GameplayFeedbackPresenter.CrossedMilestone(previous,current));
    [TestCase(9,10,8,10,10,10,false)] [TestCase(10,10,8,10,10,10,true)]
    [TestCase(10,10,12,10,10,10,false)] [TestCase(12,10,12,10,9,10,false)]
    [TestCase(12,10,12,10,10,9,false)] [TestCase(12,10,12,10,10,10,true)]
    [TestCase(12,10,12,0,0,0,false)]
    public void Outro_WaitsForMusicAndEveryJudgment(double time,double duration,double last,int total,int judged,int spawned,bool expected)
        => Assert.AreEqual(expected,GameplayFeedbackPresenter.CanEnd(time,duration,last,total,judged,spawned));
}
