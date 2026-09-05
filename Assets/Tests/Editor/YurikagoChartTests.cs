using System.IO;
using NUnit.Framework;
using UnityEngine;

// 今回制作した揺籠の実データが、ゲーム側のローダーでも同じ意味で読めることを確認。
public class YurikagoChartTests
{
    [TestCase("easy")]
    [TestCase("normal")]
    [TestCase("hard")]
    public void EachDifficulty_HasDedicatedPlayableFile(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "揺籠", "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path), "別難易度へのフォールバックではなく、専用譜面がある");
        var chart = ChartLoader.LoadFromStreamingAssets("揺籠", difficulty);
        Assert.IsNotNull(chart);
        Assert.Greater(chart.notes.Count, 0);
        Assert.Greater(chart.bpm, 30f);
        Assert.Greater(ChartDifficultyRater.Rate(chart), 0);
    }

    [TestCase("easy")]
    [TestCase("normal")]
    [TestCase("hard")]
    public void EachDifficulty_UsesRuntimeCompatibleNoteKinds(string difficulty)
    {
        var chart = ChartLoader.LoadFromStreamingAssets("揺籠", difficulty);
        float previousTime = -1f;
        foreach (var note in chart.notes)
        {
            Assert.GreaterOrEqual(note.time, previousTime);
            previousTime = note.time;
            Assert.That(note.x, Is.InRange(-2.5f, 2.5f));
            Assert.That(note.y, Is.InRange(-1.5f, 1.5f));
            Assert.Contains(note.color, new[] { "red", "blue", "gold" });
            if (note.type == "long")
            {
                Assert.GreaterOrEqual(note.count, 2);
                Assert.Greater(note.lengthMs, 0f);
            }
            else
            {
                Assert.AreEqual(1, note.count);
                Assert.AreEqual(0f, note.lengthMs);
            }
            if (note.type == "direction")
                Assert.AreNotEqual(CutDirection.None, CutDirectionHelper.Parse(note.direction));
        }
    }

    [Test]
    public void SongSelection_RecognizesYurikagoAsPlayable()
    {
        Assert.IsTrue(SongSelectController.HasPlayableChart("揺籠", new[] { "Easy", "Normal", "Hard" }));
        CollectionAssert.Contains(SongSelectController.EnumerateSongIds(), "揺籠");
    }
}
