using NUnit.Framework;

public class JudgmentTierTests
{
    // --- 早め：50/70/85/100ms、遅め：90/125/155/180ms ---
    // 2026-10-20 追加指定: 早め側を厳しくし、合計280msのうちPERFECTは140ms(50%)。

    [Test]
    public void Classify_LatePerfectWindow()
    {
        // 遅め 0 〜 +90ms
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.0));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.090));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.07));
    }

    [Test]
    public void Classify_EarlyPerfectWindow()
    {
        // 早め 0 〜 -50ms
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.050));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.03));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.0501));
    }

    [TestCase(0.050, JudgmentTier.Perfect, JudgmentTier.Perfect)]
    [TestCase(0.060, JudgmentTier.Great, JudgmentTier.Perfect)]
    [TestCase(0.070, JudgmentTier.Great, JudgmentTier.Perfect)]
    [TestCase(0.0701, JudgmentTier.Good, JudgmentTier.Perfect)]
    [TestCase(0.085, JudgmentTier.Good, JudgmentTier.Perfect)]
    [TestCase(0.090, JudgmentTier.Bad, JudgmentTier.Perfect)]
    [TestCase(0.100, JudgmentTier.Bad, JudgmentTier.Great)]
    [TestCase(0.1001, JudgmentTier.Miss, JudgmentTier.Great)]
    [TestCase(0.150, JudgmentTier.Miss, JudgmentTier.Good)]
    [TestCase(0.180, JudgmentTier.Miss, JudgmentTier.Bad)]
    public void Classify_SameErrorIsStricterWhenEarly(double error, JudgmentTier early, JudgmentTier late)
    {
        Assert.AreEqual(early, JudgmentTierHelper.Classify(-error));
        Assert.AreEqual(late, JudgmentTierHelper.Classify(error));
    }

    [Test]
    public void Classify_LateGreatWindow()
    {
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(0.10));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(0.125));
    }

    [Test]
    public void Classify_EarlyGreatWindow()
    {
        // 早め 50 〜 70ms
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.060));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.070));
    }

    [Test]
    public void Classify_LateGoodWindow()
    {
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(0.15));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(0.155));
    }

    [Test]
    public void Classify_EarlyGoodWindow()
    {
        // 早め 70 〜 85ms
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.080));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.085));
    }

    [Test]
    public void Classify_LateBadWindow()
    {
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(0.165));
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(0.180));
    }

    [Test]
    public void Classify_EarlyBadWindow()
    {
        // 早め 85 〜 100ms
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.0851));
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.100));
    }

    [Test]
    public void Classify_BeyondLateBad_IsMiss()
    {
        // 旧 Bad 窓(202.5ms)以内でも今は Miss
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.1801));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.19));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.30));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.6));
    }

    [Test]
    public void Classify_BeyondEarlyBad_IsMiss()
    {
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.1001));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.19));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.30));
    }

    [Test]
    public void Windows_CompressedOverall()
    {
        // 全体(Bad)は早め100ms + 遅め180ms = 280ms(片側平均140ms)。
        Assert.AreEqual(0.180, JudgmentTierHelper.LateBadSeconds, 1e-9);
        Assert.AreEqual(0.100, JudgmentTierHelper.EarlyBadSeconds, 1e-9);
        Assert.AreEqual(0.280, JudgmentTierHelper.EarlyBadSeconds + JudgmentTierHelper.LateBadSeconds, 1e-9);
    }

    [Test]
    public void Windows_PerfectKeepsHalfTheTotalWidth()
    {
        // PERFECT は早め50ms + 遅め90ms = 140ms(片側平均70ms)。全体の50%を維持。
        Assert.AreEqual(0.140, JudgmentTierHelper.EarlyPerfectSeconds + JudgmentTierHelper.LatePerfectSeconds, 1e-9);
        double lateShare = JudgmentTierHelper.LatePerfectSeconds / JudgmentTierHelper.LateBadSeconds;
        Assert.AreEqual(0.50, lateShare, 1e-9);
        Assert.AreEqual(lateShare,
            JudgmentTierHelper.EarlyPerfectSeconds / JudgmentTierHelper.EarlyBadSeconds, 1e-9);
    }

    [Test]
    public void Windows_EarlyIsStricter_AndBothSidesAreMonotonic()
    {
        Assert.Less(JudgmentTierHelper.EarlyPerfectSeconds, JudgmentTierHelper.LatePerfectSeconds);
        Assert.Less(JudgmentTierHelper.EarlyGreatSeconds, JudgmentTierHelper.LateGreatSeconds);
        Assert.Less(JudgmentTierHelper.EarlyGoodSeconds, JudgmentTierHelper.LateGoodSeconds);
        Assert.Less(JudgmentTierHelper.EarlyBadSeconds, JudgmentTierHelper.LateBadSeconds);
        Assert.Less(JudgmentTierHelper.EarlyPerfectSeconds, JudgmentTierHelper.EarlyGreatSeconds);
        Assert.Less(JudgmentTierHelper.EarlyGreatSeconds, JudgmentTierHelper.EarlyGoodSeconds);
        Assert.Less(JudgmentTierHelper.EarlyGoodSeconds, JudgmentTierHelper.EarlyBadSeconds);
        Assert.Less(JudgmentTierHelper.LatePerfectSeconds, JudgmentTierHelper.LateGreatSeconds);
        Assert.Less(JudgmentTierHelper.LateGreatSeconds, JudgmentTierHelper.LateGoodSeconds);
        Assert.Less(JudgmentTierHelper.LateGoodSeconds, JudgmentTierHelper.LateBadSeconds);
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
