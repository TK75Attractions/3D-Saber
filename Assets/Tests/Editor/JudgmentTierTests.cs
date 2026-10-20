using NUnit.Framework;

public class JudgmentTierTests
{
    // --- 窓：±90/125/155/180ms(早め・遅めとも同じ幅) ---
    // 2026-10-20 全体を約11%圧縮し、PERFECT の割合を約42% → 50%へ。

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
        // 早め 0 〜 -90ms
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.090));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.03));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.0901));
    }

    [TestCase(0.03)] [TestCase(0.090)] [TestCase(0.10)] [TestCase(0.125)] [TestCase(0.15)]
    [TestCase(0.155)] [TestCase(0.165)] [TestCase(0.180)] [TestCase(0.1801)]
    public void Classify_EarlyAndLateAreSymmetric(double error)
    {
        // 同じずれなら、早めでも遅めでも同じ判定
        Assert.AreEqual(JudgmentTierHelper.Classify(error), JudgmentTierHelper.Classify(-error));
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
        // 早め 90 〜 125ms
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.10));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.125));
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
        // 早め 125 〜 155ms
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.15));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.155));
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
        // 早め 155 〜 180ms
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.165));
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.180));
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
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.1801));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.19));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.30));
    }

    [Test]
    public void Windows_CompressedOverall()
    {
        // 全体(Bad)は 202.5ms → 180ms に約11%圧縮。早め側も同じ。
        Assert.AreEqual(0.180, JudgmentTierHelper.LateBadSeconds, 1e-9);
        Assert.AreEqual(0.180, JudgmentTierHelper.EarlyBadSeconds, 1e-9);
    }

    [Test]
    public void Windows_PerfectShareRaised()
    {
        // 遅め側 Perfect/Bad = 90/180 = 50%(旧 約42%)。早め側も同じ比率。
        double lateShare = JudgmentTierHelper.LatePerfectSeconds / JudgmentTierHelper.LateBadSeconds;
        Assert.AreEqual(0.50, lateShare, 1e-9);
        Assert.AreEqual(lateShare,
            JudgmentTierHelper.EarlyPerfectSeconds / JudgmentTierHelper.EarlyBadSeconds, 1e-9);
    }

    [Test]
    public void Windows_EarlyEqualsLate_AndMonotonic()
    {
        Assert.AreEqual(JudgmentTierHelper.LatePerfectSeconds, JudgmentTierHelper.EarlyPerfectSeconds, 1e-9);
        Assert.AreEqual(JudgmentTierHelper.LateGreatSeconds, JudgmentTierHelper.EarlyGreatSeconds, 1e-9);
        Assert.AreEqual(JudgmentTierHelper.LateGoodSeconds, JudgmentTierHelper.EarlyGoodSeconds, 1e-9);
        Assert.AreEqual(JudgmentTierHelper.LateBadSeconds, JudgmentTierHelper.EarlyBadSeconds, 1e-9);
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
