using NUnit.Framework;

public class JudgmentTierTests
{
    [Test]
    public void Classify_WithinPerfectWindow()
    {
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.0));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.05));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.04));
    }

    [Test]
    public void Classify_GreatWindow()
    {
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(0.08));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.10));
    }

    [Test]
    public void Classify_GoodWindow()
    {
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(0.12));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.15));
    }

    [Test]
    public void Classify_BadWindow()
    {
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(0.18));
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.20));
    }

    [Test]
    public void Classify_BeyondBad_IsMiss()
    {
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.25));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.3));
    }

    [Test]
    public void BasePoints_Monotonic()
    {
        Assert.Greater(JudgmentTierHelper.BasePoints(JudgmentTier.Perfect),
                       JudgmentTierHelper.BasePoints(JudgmentTier.Great));
        Assert.Greater(JudgmentTierHelper.BasePoints(JudgmentTier.Great),
                       JudgmentTierHelper.BasePoints(JudgmentTier.Good));
        Assert.Greater(JudgmentTierHelper.BasePoints(JudgmentTier.Good),
                       JudgmentTierHelper.BasePoints(JudgmentTier.Bad));
        Assert.AreEqual(0, JudgmentTierHelper.BasePoints(JudgmentTier.Miss));
    }

    [Test]
    public void Label_AllDefined()
    {
        Assert.AreEqual("PERFECT", JudgmentTierHelper.Label(JudgmentTier.Perfect));
        Assert.AreEqual("MISS", JudgmentTierHelper.Label(JudgmentTier.Miss));
    }
}
