using NUnit.Framework;

public class JudgmentTierTests
{
    // --- 非対称窓：遅め側は緩い (90/150/210/270ms)、早め側は半分 (45/75/105/135ms) ---

    [Test]
    public void Classify_LatePerfectWindow()
    {
        // 遅め 0 〜 +90ms
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.0));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.09));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.08));
    }

    [Test]
    public void Classify_EarlyPerfectWindow()
    {
        // 早め 0 〜 -45ms（厳しい）
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.045));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.04));
    }

    [Test]
    public void Classify_EarlyTighterThanLate()
    {
        // 同じ -90ms でも、遅め側は Perfect だが早め側では Great まで降格
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.09));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.06)); // 早め 60ms は Great
    }

    [Test]
    public void Classify_LateGreatWindow()
    {
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(0.12));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(0.15));
    }

    [Test]
    public void Classify_EarlyGreatWindow()
    {
        // 早め 45 〜 75ms
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.06));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.075));
    }

    [Test]
    public void Classify_LateGoodWindow()
    {
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(0.18));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(0.21));
    }

    [Test]
    public void Classify_EarlyGoodWindow()
    {
        // 早め 75 〜 105ms
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.09));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.105));
    }

    [Test]
    public void Classify_LateBadWindow()
    {
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(0.25));
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(0.27));
    }

    [Test]
    public void Classify_EarlyBadWindow()
    {
        // 早め 105 〜 135ms
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.12));
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.135));
    }

    [Test]
    public void Classify_BeyondLateBad_IsMiss()
    {
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.30));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.5));
    }

    [Test]
    public void Classify_BeyondEarlyBad_IsMiss()
    {
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.15));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.20));
    }

    [Test]
    public void Classify_LateRatioPreserved()
    {
        // 遅め側 Perfect/Bad = 90/270 = 33.3%
        Assert.AreEqual(0.3333, 0.09 / 0.27, 0.005);
    }

    [Test]
    public void Classify_EarlyRatioPreserved()
    {
        // 早め側 Perfect/Bad = 45/135 = 33.3%（遅めと同じ比率）
        Assert.AreEqual(0.3333, 0.045 / 0.135, 0.005);
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
