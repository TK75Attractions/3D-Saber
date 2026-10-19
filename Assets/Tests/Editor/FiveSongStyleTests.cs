using NUnit.Framework;

// 表示レベルは 2026-10-19 の見直しの値(物量・山場・片手の速さ・動き・読み・持久の6観点で、全曲を同じ物差しでそろえた)。
// 自動推定(ChartDifficultyRater)とは一致させない。どの曲も Easy < Normal < Hard。
public class FiveSongStyleTests
{
    [TestCase("ManicSonic", 3, 7, 10)]
    [TestCase("escapade", 3, 6, 10)]
    [TestCase("logic", 3, 6, 8)]
    [TestCase("BenkyouRemaster", 3, 5, 9)]
    [TestCase("Aobasou", 3, 5, 9)]
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
