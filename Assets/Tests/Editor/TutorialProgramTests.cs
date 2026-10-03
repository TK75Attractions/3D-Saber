using System.Linq;
using NUnit.Framework;

// チュートリアルの進行表(純粋データ)と、タイトルの問いかけ・Game シーンのフラグの約束事。
public class TutorialProgramTests
{
    [TearDown]
    public void ResetState()
    {
        GameSession.TutorialPending = false;
    }

    [Test]
    public void TutorialPending_DefaultFalseAndSettable()
    {
        Assert.IsFalse(GameSession.TutorialPending);
        GameSession.TutorialPending = true;
        Assert.IsTrue(GameSession.TutorialPending);
    }

    [Test]
    public void CommonFlow_StartsWithHandsAndEndsWithReady()
    {
        var steps = TutorialProgram.Build(TutorialProgram.CommonFlow, true);
        Assert.AreEqual(TutorialProgram.CommonFlow.Length, steps.Length, "全部の id が定義されている");
        Assert.AreEqual("hands", steps[0].Id);
        Assert.AreEqual(TutorialStepKind.Explain, steps[0].Kind);
        Assert.AreEqual("ready", steps[steps.Length - 1].Id);
        Assert.AreEqual(TutorialStepKind.Ready, steps[steps.Length - 1].Kind);
        var ids = steps.Select(s => s.Id).ToList();
        foreach (var required in new[] { "tapRed", "tapBlue", "tapMix", "arrowEasy", "long", "gold", "simul" })
            CollectionAssert.Contains(ids, required);
    }

    [Test]
    public void Build_WithoutHands_SkipsTheExplainStep()
    {
        var steps = TutorialProgram.Build(TutorialProgram.CommonFlow, false);
        Assert.IsFalse(steps.Any(s => s.Kind == TutorialStepKind.Explain));
        Assert.AreEqual("tapRed", steps[0].Id);
        Assert.AreEqual(TutorialProgram.CommonFlow.Length - 1, steps.Length);
    }

    [Test]
    public void Get_UnknownId_ReturnsNull()
    {
        Assert.IsNull(TutorialProgram.Get("nothing"));
        Assert.IsNull(TutorialProgram.Get(""));
    }

    [Test]
    public void PracticeSteps_AreAchievableInOneLoopAndOrderedInTime()
    {
        foreach (var step in TutorialProgram.Build(TutorialProgram.CommonFlow, true).Where(s => s.Kind == TutorialStepKind.Practice))
        {
            Assert.Greater(step.Pattern.Length, 0, step.Id);
            Assert.Greater(step.Goal, 0, step.Id);
            Assert.LessOrEqual(step.Goal, step.Pattern.Length, step.Id + ": 1 周で合格できる");
            Assert.Greater(step.PeriodSeconds, step.PatternLengthSeconds + 1.0, step.Id + ": 次の周と重ならない");
            for (int i = 1; i < step.Pattern.Length; i++)
                Assert.GreaterOrEqual(step.Pattern[i].dt, step.Pattern[i - 1].dt, step.Id + ": 時刻順");
            Assert.IsTrue(string.IsNullOrEmpty(step.Title) == false, step.Id + ": 文がある");
        }
    }

    [Test]
    public void MirrorRule_BlueOnTheLeftRedOnTheRightInsideEasyRange()
    {
        foreach (var step in TutorialProgram.Build(TutorialProgram.CommonFlow, true))
        foreach (var n in step.Pattern)
        {
            if (n.color == "red") Assert.Greater(n.x, 0f, step.Id + ": 赤は画面の右");
            else if (n.color == "blue") Assert.Less(n.x, 0f, step.Id + ": 青は画面の左");
            else Assert.AreEqual("gold", n.color, step.Id);
            Assert.That(n.y, Is.InRange(-0.65f, 1.1f), step.Id + ": Easy 譜面の y の範囲");
        }
    }

    [Test]
    public void ToNote_ConvertsSecondsToMillisecondsAndKeepsKind()
    {
        var longNote = TutorialProgram.Get("long").Pattern[0].ToNote(12.5);
        Assert.AreEqual(12500f, longNote.time, .001f);
        Assert.AreEqual(3, longNote.count);
        Assert.AreEqual(1400f, longNote.lengthMs, .001f);
        Assert.AreEqual("red", longNote.color);
        Assert.IsTrue(longNote.IsLong);

        var arrow = TutorialProgram.Get("arrowEasy").Pattern[1].ToNote(0);
        Assert.AreEqual(1800f, arrow.time, .001f);
        Assert.AreEqual("down", arrow.direction);
        Assert.AreEqual("blue", arrow.color);
        Assert.IsTrue(arrow.IsDirection);
        Assert.IsFalse(arrow.IsLong);

        var pair = TutorialProgram.Get("simul").Pattern;
        Assert.AreEqual(pair[0].ToNote(3).time, pair[1].ToNote(3).time, .001f, "同時は同じ時刻");
    }

    [Test]
    public void CountsAsSuccess_FollowsEachStepsRule()
    {
        var tap = TutorialProgram.Get("tapRed");
        var arrow = TutorialProgram.Get("arrowEasy");
        var longStep = TutorialProgram.Get("long");
        var hands = TutorialProgram.Get("hands");
        Assert.IsTrue(TutorialProgram.CountsAsSuccess(tap, JudgmentTier.Good, true, 1, 1));
        Assert.IsTrue(TutorialProgram.CountsAsSuccess(tap, JudgmentTier.Bad, false, 1, 1), "タップは向きを問わない");
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(tap, JudgmentTier.Miss, true, 1, 0));
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(arrow, JudgmentTier.Great, false, 1, 1), "矢印は向きが違えば数えない");
        Assert.IsTrue(TutorialProgram.CountsAsSuccess(arrow, JudgmentTier.Great, true, 1, 1));
        Assert.IsTrue(TutorialProgram.CountsAsSuccess(longStep, JudgmentTier.Good, true, 3, 2), "2/3 は 60% 以上");
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(longStep, JudgmentTier.Bad, true, 3, 1));
        Assert.IsTrue(TutorialProgram.CountsAsSuccess(longStep, JudgmentTier.Miss, true, 3, 3), "ロングは達成率だけを見る");
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(hands, JudgmentTier.Perfect, true, 1, 1));
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(null, JudgmentTier.Perfect, true, 1, 1));
    }

    [Test]
    public void Hints_NameTheHandAndTheDirection()
    {
        StringAssert.Contains("左手", TutorialProgram.MissHint("blue", false));
        StringAssert.Contains("右手", TutorialProgram.MissHint("red", false));
        StringAssert.Contains("どちらの手", TutorialProgram.MissHint("gold", false));
        StringAssert.Contains("矢印", TutorialProgram.MissHint("red", true));
        Assert.AreEqual("左上", TutorialProgram.DirectionJapanese(CutDirection.UpLeft));
        Assert.AreEqual("下", TutorialProgram.DirectionJapanese(CutDirection.Down));
        Assert.AreEqual("", TutorialProgram.DirectionJapanese(CutDirection.None));
    }

    [Test]
    public void Timings_MatchTheWebPrototype()
    {
        Assert.AreEqual(6f, TutorialProgram.HandsTimeoutSeconds, .001f);
        Assert.AreEqual(15f, TutorialProgram.DefaultStepTimeoutSeconds, .001f);
        Assert.AreEqual(.9, TutorialProgram.ReadDelaySeconds, .0001);
        Assert.AreEqual(10f, TitleTutorialPrompt.DefaultTimeoutSeconds, .001f);
        Assert.AreEqual(TitlePresentationMotion.DepartureDuration, TitleTutorialPrompt.RevealDelaySeconds, .001f);
        Assert.Less(TitleTutorialPrompt.YesPosition.x, 0f, "はい は画面の左");
        Assert.Greater(TitleTutorialPrompt.NoPosition.x, 0f, "いいえ は画面の右");
        Assert.AreEqual("Game", TitleSceneSkin.TutorialSceneName);
    }
}
