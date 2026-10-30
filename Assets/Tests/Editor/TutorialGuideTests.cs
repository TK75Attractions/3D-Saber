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
        // 切り切ったロングは ScoreManager が PERFECT にする(残すと MISS)。
        Assert.True(TutorialProgram.CountsAsSuccess(step, JudgmentTier.Perfect, true, count, count));
        Assert.False(TutorialProgram.CountsAsSuccess(step, JudgmentTier.Miss, true, count, count));
    }

    [Test]
    public void LongPracticeTeachesMultipleCountsOnBothHandsAndFitsItsDeadline()
    {
        var step = TutorialProgram.Get("long");
        // 2026-10-31「ロングノーツは2種類だけでいいよ」: 2回と3回(前は 2・3・4 回)。
        CollectionAssert.AreEquivalent(new[] { 2, 3 }, step.Pattern.Select(n => n.count));
        Assert.That(step.Pattern.Select(n => n.color).Distinct().Count(), Is.EqualTo(2));
        StringAssert.Contains("数字", step.Title);
        Assert.AreEqual(step.Pattern.Select(n => n.count).Distinct().Count(), step.Goal);
        double lastEnd = step.Pattern.Max(n => n.dt + n.lengthMs / 1000.0);
        // 2026-10-31 から1周だけ流す。接近待ちと1周と、判定が来なくて進むまでの待ちが入る。
        Assert.Less(5 + TutorialProgram.PatternEndAt(step, 0), step.TimeoutSeconds, "接近待ちと1周と判定の待ちが入る");
        Assert.AreEqual(lastEnd, step.PatternEndSeconds, 1e-9);
        Assert.Greater(step.PeriodSeconds, lastEnd + TutorialGuideMotion.RecoverSeconds);
    }

    // 範囲を合わせる段のお手本: 両腕を腰から肩まで、2拍で1往復(2026-10-30 夜、頭の上まで振らない)。
    [Test]
    public void RangeSweepRaisesBothArmsOnTheBeat()
    {
        Assert.AreEqual(1.0, TutorialGuideMotion.RangeSweepSeconds, 1e-9);
        Assert.AreEqual(TutorialGuideMotion.RangeSweepLow, TutorialGuideMotion.RangeSweep(0), 1e-6, "腰");
        Assert.AreEqual(TutorialGuideMotion.RangeSweepHigh, TutorialGuideMotion.RangeSweep(TutorialGuideMotion.RangeSweepSeconds / 2), 1e-6, "半周で肩");
        Assert.AreEqual(TutorialGuideMotion.RangeSweepLow, TutorialGuideMotion.RangeSweep(TutorialGuideMotion.RangeSweepSeconds * 3), 1e-6);
        Assert.AreEqual(.5f, TutorialGuideMotion.RangeSweep(double.NaN));
        for (double t = 0; t < 2; t += .01)
            Assert.That(TutorialGuideMotion.RangeSweep(t), Is.InRange(TutorialGuideMotion.RangeSweepLow - 1e-6, TutorialGuideMotion.RangeSweepHigh + 1e-6), "頭の上(1)まで上げない");
        var go = new UnityEngine.GameObject("RangeGuide", typeof(UnityEngine.RectTransform));
        try
        {
            var guide = go.AddComponent<TutorialGuideModel>();
            guide.ShowPose(0, 1);
            Assert.AreEqual(0, guide.LeftFrame);
            Assert.AreEqual(TutorialGuideModel.FrameCount - 1, guide.RightFrame);
            guide.ShowPose(.5f, .5f);
            Assert.AreEqual(8, guide.LeftFrame); Assert.AreEqual(8, guide.RightFrame);
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
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
            // お手本は長さを回数で割った間隔で振る(2026-10-31 に長さを2倍にして、1回ごとに2拍=1秒)。
            double spacing = note.lengthMs / 1000.0 / (note.count - 1);
            Assert.AreEqual(1.0, spacing, 1e-9, note.count + " 回");
            for (int cut = 0; cut < note.count; cut++)
            {
                double hit = note.TimeSeconds + cut * spacing;
                float before = TutorialGuideMotion.Sample(notes, hit - .04, hand);
                float after = TutorialGuideMotion.Sample(notes, hit + .04, hand);
                Assert.AreEqual(.5f, TutorialGuideMotion.Sample(notes, hit, hand), .0001);
                Assert.That((after - before) * (cut % 2 == 1 ? 1 : -1), Is.GreaterThan(0));
            }
            Assert.AreEqual(.5f, TutorialGuideMotion.Sample(notes, note.TimeSeconds + (note.count - 1) * spacing + .5, hand));
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
