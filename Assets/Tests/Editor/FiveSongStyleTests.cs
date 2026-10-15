using NUnit.Framework;

// 表示だけを下げず、実際に読み込んだ5曲の密度も要求された難度へ収める。
public class FiveSongStyleTests
{
    [TestCaseSource(typeof(FiveSongPackTests), nameof(FiveSongPackTests.Ids))]
    public void HardLevelsMatchContentAndStayWithinSevenToNine(string songId)
    {
        var chart = ChartLoader.LoadFromStreamingAssets(songId, "hard");
        int calculated = ChartDifficultyRater.Rate(chart);
        Assert.That(chart.displayLevel, Is.InRange(7, 9), songId);
        Assert.That(calculated, Is.InRange(7, 9), songId);
        Assert.AreEqual(calculated, chart.displayLevel, songId + " / 表示と実データの計算");
    }
}
