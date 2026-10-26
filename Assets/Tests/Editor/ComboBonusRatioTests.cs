using NUnit.Framework;

// 曲終わりのコンボ演出(コインが出るほう)の段は、最大コンボ ÷ 満コンボ の割合で決まる(2026-10-26)。
// プレイ中のコンボ表示の色は、続いた数の節目のまま。
public class ComboBonusRatioTests
{
    [TestCase(0, 300, 0)]
    [TestCase(29, 300, 0)]
    [TestCase(30, 300, 1)]
    [TestCase(89, 300, 1)]
    [TestCase(90, 300, 2)]
    [TestCase(179, 300, 2)]
    [TestCase(180, 300, 3)]
    [TestCase(269, 300, 3)]
    [TestCase(270, 300, 4)]
    [TestCase(300, 300, 4)]
    public void TheTierFollowsTheShareOfTheFullCombo(int combo, int possible, int tier)
    {
        Assert.AreEqual(tier, ComboBonusPresentation.RatioTier(combo, possible));
    }

    [Test]
    public void ShortAndLongChartsReachTheSameTierAtTheSameShare()
    {
        // 前はノーツ数の節目(300 で最上段)だったので、短い曲では最上段に届かなかった。
        Assert.AreEqual(ComboBonusPresentation.RatioTier(180, 200), ComboBonusPresentation.RatioTier(900, 1000));
        Assert.AreEqual(4, ComboBonusPresentation.RatioTier(150, 150), "短い曲でも満コンボは最上段");
        Assert.AreEqual(0, ComboBonusPresentation.RatioTier(10, 1158), "長い曲で 10 コンボは最下段");
    }

    [Test]
    public void MissingFullComboCountsAsFullAndPlayingColoursStayOnCounts()
    {
        Assert.AreEqual(4, ComboBonusPresentation.RatioTier(5, 0), "満コンボが渡されないとき(演出の確認)は満コンボ扱い");
        Assert.AreEqual(ComboBonusPresentation.TierAccent(3), ComboBonusPresentation.Accent(100), "プレイ中は続いた数の節目");
        Assert.AreEqual(ComboBonusPresentation.TierAccent(1), ComboBonusPresentation.Accent(10));
    }
}
