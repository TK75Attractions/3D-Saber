using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// 83: 判定通知から向きの進捗へつながることを、EditMode で確かめる。
public class TutorialDirectionRequirementTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestCase("arrowEasy", 2)]
    [TestCase("arrowNormal", 3)]
    public void RepeatingUpDoesNotPassAndMissDoesNotFillDown(string id, int goal)
    {
        var root = new GameObject("TutorialDirections");
        try
        {
            var controller = root.AddComponent<TutorialController>();
            var step = TutorialProgram.Get(id);
            typeof(TutorialController).GetField("steps", Private).SetValue(controller, new[] { step });
            typeof(TutorialController).GetField("stepIndex", Private).SetValue(controller, 0);
            var completed = (HashSet<CutDirection>)typeof(TutorialController)
                .GetField("completedDirections", Private).GetValue(controller);

            Assert.AreEqual(goal, step.Goal);
            Assert.AreEqual("あと 2 方向", TutorialProgram.RemainingText(step, 0, controller.RemainingDirections, false));
            for (int i = 0; i < goal; i++) Judge(controller, root, CutDirection.Up, JudgmentTier.Great);
            Assert.AreEqual(goal, controller.Successes);
            Assert.AreEqual(1, controller.RemainingDirections);
            Assert.IsFalse(TutorialProgram.PracticeGoalReached(step, controller.Successes, completed));
            Assert.AreEqual("あと 1 方向", TutorialProgram.RemainingText(step, controller.Successes, controller.RemainingDirections, false));

            Judge(controller, root, CutDirection.Down, JudgmentTier.Miss);
            Assert.AreEqual(goal, controller.Successes);
            Assert.AreEqual(1, controller.RemainingDirections);
            Judge(controller, root, CutDirection.Down, JudgmentTier.Good);
            Assert.AreEqual(0, controller.RemainingDirections);
            Assert.IsTrue(TutorialProgram.PracticeGoalReached(step, controller.Successes, completed));
        }
        finally { Object.DestroyImmediate(root); }
    }

    [Test]
    public void NormalStillNeedsThreeSuccessesAfterUpAndDown()
    {
        var step = TutorialProgram.Get("arrowNormal");
        var completed = new HashSet<CutDirection> { CutDirection.Up, CutDirection.Down };
        Assert.IsFalse(TutorialProgram.PracticeGoalReached(step, 2, completed));
        Assert.AreEqual("あと 1 回", TutorialProgram.RemainingText(step, 2, 0, false));
        Assert.IsTrue(TutorialProgram.PracticeGoalReached(step, 3, completed));
        Assert.AreEqual("", TutorialProgram.RemainingText(step, 3, 0, true));
        completed.Clear();
        Assert.AreEqual(2, TutorialProgram.RemainingDirections(step, completed));
    }

    static void Judge(TutorialController controller, GameObject root, CutDirection direction, JudgmentTier tier)
    {
        var go = new GameObject("Arrow");
        go.transform.SetParent(root.transform);
        var note = go.AddComponent<CuttableNote>();
        note.RequiredDirection = direction;
        note.RequiredHand = direction == CutDirection.Up ? SaberHand.Right : SaberHand.Left;
        typeof(TutorialController).GetMethod("WatchNote", Private).Invoke(controller, new object[] { note });
        typeof(TutorialController).GetMethod("Judged", Private)
            .Invoke(controller, new object[] { note, tier, Vector3.zero, Vector3.up * 8 });
    }
}
