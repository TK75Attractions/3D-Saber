using NUnit.Framework;

// 表示レベルは 2026-10-19 の見直しの値(物量・山場・片手の速さ・動き・読み・持久の6観点で、全曲を同じ物差しでそろえた)。
// Easy・Normal は 10/21 の作り直しのあと、同じ物差し(両端は 10/19 の譜面のまま)で付け直した。
// そのあと Andalusia Normal 6・BenkyouRemaster Normal 5 はユーザーの指定で付け直す前の値に戻した(10/21 夕方、表記だけ)。
// 自動推定(ChartDifficultyRater)とは一致させない。どの曲も Easy < Normal < Hard。
public class FiveSongStyleTests
{
    [TestCase("ManicSonic", 3, 6, 10)]
    [TestCase("escapade", 2, 4, 10)]
    [TestCase("logic", 2, 4, 8)]
    [TestCase("BenkyouRemaster", 2, 5, 9)]
    [TestCase("Aobasou", 2, 5, 9)]
    public void LevelsFollowTheReviewAndClimbFromEasyToHard(string songId, int easy, int normal, int hard)
    {
        string[] names = { "easy", "normal", "hard" };
        int[] expected = { easy, normal, hard };
        for (int i = 0; i < names.Length; i++)
            Assert.AreEqual(expected[i], ChartLoader.LoadFromStreamingAssets(songId, names[i]).displayLevel, songId + " / " + names[i]);
        Assert.Less(easy, normal, songId);
        Assert.Less(normal, hard, songId);
    }
}
