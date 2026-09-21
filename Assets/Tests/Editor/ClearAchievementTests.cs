using NUnit.Framework;
using UnityEngine;

public class ClearAchievementTests
{
    [Test]
    public void EmptyAndResetRunsHaveNoAchievement()
    {
        var score = new GameObject("AchievementScore").AddComponent<ScoreManager>();
        try
        {
            Assert.False(score.IsFullCombo);
            Assert.False(score.IsAllPerfect);
            score.RegisterHit(JudgmentTier.Perfect);
            Assert.True(score.IsAllPerfect);
            score.FinalizeScore();
            Assert.True(score.IsAllPerfect, "精算で達成条件を変えない");
            score.Reset();
            Assert.False(score.IsFullCombo);
            Assert.False(score.IsAllPerfect);
        }
        finally { Object.DestroyImmediate(score.gameObject); }
    }

    [TestCase(JudgmentTier.Perfect, true, true)]
    [TestCase(JudgmentTier.Great, true, false)]
    [TestCase(JudgmentTier.Good, true, false)]
    [TestCase(JudgmentTier.Bad, false, false)]
    [TestCase(JudgmentTier.Miss, false, false)]
    public void AchievementFollowsTheGamesComboJudgments(JudgmentTier tier, bool fullCombo, bool allPerfect)
    {
        var score = new GameObject("AchievementScore").AddComponent<ScoreManager>();
        try
        {
            score.RegisterHit(JudgmentTier.Perfect);
            score.RegisterHit(tier);
            for (int i = 0; i < 8; i++) score.RegisterHit(JudgmentTier.Perfect);
            Assert.AreEqual(fullCombo, score.IsFullCombo, "途中のBad・Missを最後の連続Perfectで取り消さない");
            Assert.AreEqual(allPerfect, score.IsAllPerfect);
        }
        finally { Object.DestroyImmediate(score.gameObject); }
    }
}
