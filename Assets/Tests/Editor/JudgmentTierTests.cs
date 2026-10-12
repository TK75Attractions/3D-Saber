using NUnit.Framework;

public class JudgmentTierTests
{
    // --- 窓：±85/130/170/202.5ms(早め・遅めとも同じ幅) ---
    // 2026-09「全体的に厳しく(1/2くらい)、代わりに PERFECT の比率を少し上げて」の要望。
    // 全体(Bad)は旧 405ms の半分、PERFECT は全体の 1/3 → 約 42% へ。
    // 2026-10 カメラの遅れを判定の時計で差し引くのに合わせて、早め側(以前は半分)も遅め側と同じ幅にした。

    [Test]
    public void Classify_LatePerfectWindow()
    {
        // 遅め 0 〜 +85ms
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.0));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.085));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.07));
    }

    [Test]
    public void Classify_EarlyPerfectWindow()
    {
        // 早め 0 〜 -85ms
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.085));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.03));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.0851));
    }

    [TestCase(0.03)] [TestCase(0.085)] [TestCase(0.10)] [TestCase(0.13)] [TestCase(0.15)]
    [TestCase(0.17)] [TestCase(0.19)] [TestCase(0.2025)] [TestCase(0.21)]
    public void Classify_EarlyAndLateAreSymmetric(double error)
    {
        // 同じずれなら、早めでも遅めでも同じ判定
        Assert.AreEqual(JudgmentTierHelper.Classify(error), JudgmentTierHelper.Classify(-error));
    }

    [Test]
    public void Classify_LateGreatWindow()
    {
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(0.10));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(0.130));
    }

    [Test]
    public void Classify_EarlyGreatWindow()
    {
        // 早め 85 〜 130ms
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.10));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.130));
    }

    [Test]
    public void Classify_LateGoodWindow()
    {
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(0.15));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(0.170));
    }

    [Test]
    public void Classify_EarlyGoodWindow()
    {
        // 早め 130 〜 170ms
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.15));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.170));
    }

    [Test]
    public void Classify_LateBadWindow()
    {
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(0.19));
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(0.2025));
    }

    [Test]
    public void Classify_EarlyBadWindow()
    {
        // 早め 170 〜 202.5ms
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.19));
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.2025));
    }

    [Test]
    public void Classify_BeyondLateBad_IsMiss()
    {
        // 旧 Bad 窓(405ms)以内でも今は Miss
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.21));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.30));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(0.6));
    }

    [Test]
    public void Classify_BeyondEarlyBad_IsMiss()
    {
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.21));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.30));
    }

    [Test]
    public void Windows_HalvedOverall()
    {
        // 全体(Bad)は旧 405ms のちょうど半分。早め側も同じ
        Assert.AreEqual(0.2025, JudgmentTierHelper.LateBadSeconds, 1e-9);
        Assert.AreEqual(0.2025, JudgmentTierHelper.EarlyBadSeconds, 1e-9);
    }

    [Test]
    public void Windows_PerfectShareRaised()
    {
        // 遅め側 Perfect/Bad = 85/202.5 ≒ 42%(旧 33%)。早め側も同じ比率。
        double lateShare = JudgmentTierHelper.LatePerfectSeconds / JudgmentTierHelper.LateBadSeconds;
        Assert.Greater(lateShare, 0.40);
        Assert.Less(lateShare, 0.45);
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
