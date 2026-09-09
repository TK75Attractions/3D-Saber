using NUnit.Framework;

public class JudgmentTierTests
{
    // --- 非対称窓：遅め側 (85/130/170/202.5ms)、早め側はその半分 (42.5/65/85/101.25ms) ---
    // 2026-09「全体的に厳しく(1/2くらい)、代わりに PERFECT の比率を少し上げて」の要望。
    // 全体(Bad)は旧 405ms の半分、PERFECT は全体の 1/3 → 約 42% へ。

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
        // 早め 0 〜 -42.5ms（遅めより厳しい）
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.0425));
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(-0.03));
    }

    [Test]
    public void Classify_EarlyTighterThanLate()
    {
        // 同じ 85ms のずれでも、遅め側は Perfect だが早め側では Good へ降格
        Assert.AreEqual(JudgmentTier.Perfect, JudgmentTierHelper.Classify(0.085));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.085));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.05));
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
        // 早め 42.5 〜 65ms
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.05));
        Assert.AreEqual(JudgmentTier.Great, JudgmentTierHelper.Classify(-0.065));
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
        // 早め 65 〜 85ms
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.07));
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(-0.085));
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
        // 早め 85 〜 101.25ms
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.09));
        Assert.AreEqual(JudgmentTier.Bad, JudgmentTierHelper.Classify(-0.10125));
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
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.11));
        Assert.AreEqual(JudgmentTier.Miss, JudgmentTierHelper.Classify(-0.30));
    }

    [Test]
    public void Windows_HalvedOverall()
    {
        // 全体(Bad)は旧 405ms のちょうど半分
        Assert.AreEqual(0.2025, JudgmentTierHelper.LateBadSeconds, 1e-9);
        Assert.AreEqual(0.10125, JudgmentTierHelper.EarlyBadSeconds, 1e-9);
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
    public void Windows_EarlyIsHalfOfLate_AndMonotonic()
    {
        Assert.AreEqual(JudgmentTierHelper.LatePerfectSeconds * 0.5, JudgmentTierHelper.EarlyPerfectSeconds, 1e-9);
        Assert.AreEqual(JudgmentTierHelper.LateBadSeconds * 0.5, JudgmentTierHelper.EarlyBadSeconds, 1e-9);
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
