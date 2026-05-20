using NUnit.Framework;

public class JudgmentTierTests
{
    [Test]
    public void Classify_WithinPerfectWindow()
    {
        // ±90ms（Bad=270ms の 33.3%）
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.0));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.09));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.08));
    }

    [Test]
    public void Classify_GreatWindow()
    {
        // 90ms < |e| <= 150ms
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(0.12));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.15));
    }

    [Test]
    public void Classify_GoodWindow()
    {
        // 150ms < |e| <= 210ms
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(0.18));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.21));
    }

    [Test]
    public void Classify_BadWindow()
    {
        // 210ms < |e| <= 270ms
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(0.25));
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.27));
    }

    [Test]
    public void Classify_BeyondBad_IsMiss()
    {
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.30));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.5));
    }

    [Test]
    public void Classify_RatioBetweenTiersPreserved()
    {
        // Perfect の割合は約 33.3%（Bad の上限に対する比率）
        const double bad = 0.27;
        const double perfect = 0.09;
        double ratio = perfect / bad;
        Assert.AreEqual(0.3333, ratio, 0.005);
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
