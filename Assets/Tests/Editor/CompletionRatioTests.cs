using NUnit.Framework;

public class CompletionRatioTests
{
    // ロングノーツは完了率のみで tier を決める仕様（時間誤差は使わない）。
    // 100%=Perfect、85%+=Great、60%+=Good、30%+=Bad、それ未満=Miss。

    [Test]
    public void TierByCompletionRatio_FullCompletion_IsPerfect()
    {
        Assert.AreEqual(JudgmentTier.Perfect, ScoreManager.TierByCompletionRatio(1.0f));
        Assert.AreEqual(JudgmentTier.Perfect, ScoreManager.TierByCompletionRatio(1.2f));
    }

    [Test]
    public void TierByCompletionRatio_GreatRange()
    {
        Assert.AreEqual(JudgmentTier.Great, ScoreManager.TierByCompletionRatio(0.85f));
        Assert.AreEqual(JudgmentTier.Great, ScoreManager.TierByCompletionRatio(0.95f));
    }

    [Test]
    public void TierByCompletionRatio_GoodRange()
    {
        Assert.AreEqual(JudgmentTier.Good, ScoreManager.TierByCompletionRatio(0.60f));
        Assert.AreEqual(JudgmentTier.Good, ScoreManager.TierByCompletionRatio(0.84f));
    }

    [Test]
    public void TierByCompletionRatio_BadRange()
    {
        Assert.AreEqual(JudgmentTier.Bad, ScoreManager.TierByCompletionRatio(0.30f));
        Assert.AreEqual(JudgmentTier.Bad, ScoreManager.TierByCompletionRatio(0.59f));
    }

    [Test]
    public void TierByCompletionRatio_MissRange()
    {
        Assert.AreEqual(JudgmentTier.Miss, ScoreManager.TierByCompletionRatio(0.0f));
        Assert.AreEqual(JudgmentTier.Miss, ScoreManager.TierByCompletionRatio(0.29f));
    }

    // 旧 API ScaleTierByCompletionRatio は後方互換用。
    // 「byRatio と baseTier の悪い方を返す」セマンティクス。
    [Test]
    public void ScaleTier_ReturnsWorseOfBaseAndRatio()
    {
        // ratio=1.0 → byRatio=Perfect、baseが Good なら Good を返す
        Assert.AreEqual(JudgmentTier.Good, ScoreManager.ScaleTierByCompletionRatio(JudgmentTier.Good, 1.0f));
        // ratio=0.5 → byRatio=Miss、baseが Perfect なら Miss を返す
        Assert.AreEqual(JudgmentTier.Miss, ScoreManager.ScaleTierByCompletionRatio(JudgmentTier.Perfect, 0.20f));
    }
}
