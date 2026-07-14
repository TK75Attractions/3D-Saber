using NUnit.Framework;

public class JudgmentTierTests
{
    // --- 非対称窓：遅め側は緩い (135/225/315/405ms)、早め側は半分 (67.5/112.5/157.5/202.5ms) ---
    // 2026-07「だいぶ甘く」要望で従来値(90/150/210/270ms)の1.5倍へ拡大。比率は維持。

    [Test]
    public void Classify_LatePerfectWindow()
    {
        // 遅め 0 〜 +135ms
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.0));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.135));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.12));
    }

    [Test]
    public void Classify_EarlyPerfectWindow()
    {
        // 早め 0 〜 -67.5ms（遅めより厳しい）
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.0675));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.06));
    }

    [Test]
    public void Classify_EarlyTighterThanLate()
    {
        // 同じ 135ms のずれでも、遅め側は Perfect だが早め側では Great へ降格
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.135));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.09));
    }

    [Test]
    public void Classify_LateGreatWindow()
    {
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(0.18));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(0.225));
    }

    [Test]
    public void Classify_EarlyGreatWindow()
    {
        // 早め 67.5 〜 112.5ms
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.09));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.1125));
    }

    [Test]
    public void Classify_LateGoodWindow()
    {
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(0.27));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(0.315));
    }

    [Test]
    public void Classify_EarlyGoodWindow()
    {
        // 早め 112.5 〜 157.5ms
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.135));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.1575));
    }

    [Test]
    public void Classify_LateBadWindow()
    {
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(0.375));
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(0.405));
    }

    [Test]
    public void Classify_EarlyBadWindow()
    {
        // 早め 157.5 〜 202.5ms
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.18));
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.2025));
    }

    [Test]
    public void Classify_BeyondLateBad_IsMiss()
    {
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.45));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.6));
    }

    [Test]
    public void Classify_BeyondEarlyBad_IsMiss()
    {
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.21));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.30));
    }

    [Test]
    public void Classify_LateRatioPreserved()
    {
        // 遅め側 Perfect/Bad = 135/405 = 33.3%（拡大前と同じ比率）
        Assert.AreEqual(0.3333,
            JudgmentTierHelper.LatePerfectSeconds / JudgmentTierHelper.LateBadSeconds, 0.005);
    }

    [Test]
    public void Classify_EarlyRatioPreserved()
    {
        // 早め側も同じ比率、かつ早め窓 = 遅め窓の 1/2
        Assert.AreEqual(0.3333,
            JudgmentTierHelper.EarlyPerfectSeconds / JudgmentTierHelper.EarlyBadSeconds, 0.005);
        Assert.AreEqual(JudgmentTierHelper.LateBadSeconds * 0.5,
            JudgmentTierHelper.EarlyBadSeconds, 1e-9);
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
