using NUnit.Framework;

public class CompletionRatioTests
{
    [Test]
    public void FullCompletion_KeepsBaseTier()
    {
        Assert.AreEqual(JudgmentTier.Perfect, ScoreManager.ScaleTierByCompletionRatio(JudgmentTier.Perfect, 1f));
        Assert.AreEqual(JudgmentTier.Good, ScoreManager.ScaleTierByCompletionRatio(JudgmentTier.Good, 1f));
    }

    [Test]
    public void Ratio75_CapsAtGreat()
    {
        Assert.AreEqual(JudgmentTier.Great, ScoreManager.ScaleTierByCompletionRatio(JudgmentTier.Perfect, 0.8f));
        // 元から Good なら据え置き（Greatより悪い）
        Assert.AreEqual(JudgmentTier.Good, ScoreManager.ScaleTierByCompletionRatio(JudgmentTier.Good, 0.8f));
    }

    [Test]
    public void Ratio50_CapsAtGood()
    {
        Assert.AreEqual(JudgmentTier.Good, ScoreManager.ScaleTierByCompletionRatio(JudgmentTier.Perfect, 0.55f));
        Assert.AreEqual(JudgmentTier.Bad, ScoreManager.ScaleTierByCompletionRatio(JudgmentTier.Bad, 0.55f));
    }

    [Test]
    public void Ratio25_IsBad()
    {
        Assert.AreEqual(JudgmentTier.Bad, ScoreManager.ScaleTierByCompletionRatio(JudgmentTier.Perfect, 0.3f));
    }

    [Test]
    public void RatioBelow25_IsMiss()
    {
        Assert.AreEqual(JudgmentTier.Miss, ScoreManager.ScaleTierByCompletionRatio(JudgmentTier.Perfect, 0.2f));
        Assert.AreEqual(JudgmentTier.Miss, ScoreManager.ScaleTierByCompletionRatio(JudgmentTier.Perfect, 0f));
    }
}
