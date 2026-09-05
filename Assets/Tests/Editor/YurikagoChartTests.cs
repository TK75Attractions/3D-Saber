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

    [Test]
    public void HardDisplaysNine_WithoutChangingOtherDifficultiesOrSongs()
    {
        var go = new GameObject("levelDisplayTest");
        try
        {
            var controller = go.AddComponent<SongSelectController>();
            controller.Populate();
            int yurikago = -1, elDorado = -1;
            for (int i = 0; i < controller.SongCount; i++)
            {
                if (controller.SongIdAt(i) == "揺籠") yurikago = i;
                if (controller.SongIdAt(i) == "ElDorado") elDorado = i;
            }
            Assert.GreaterOrEqual(yurikago, 0);
            Assert.GreaterOrEqual(elDorado, 0);
            Assert.AreEqual(9, controller.DisplayLevelFor(yurikago, 2));
            Assert.AreEqual(6, controller.DisplayLevelFor(yurikago, 1));
            Assert.AreEqual(4, controller.DisplayLevelFor(yurikago, 0));
            Assert.AreEqual(8, controller.DisplayLevelFor(elDorado, 2));
            CollectionAssert.AreEqual(new[] { 4, 6, 8 }, controller.difficultyDisplayLevels);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void EditorRoundTrip_PreservesAuthoredDifficultyLevel()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "揺籠", "chart_hard.json");
        var document = Saber.ChartEditor.SaberChartUtility.FromJson(File.ReadAllText(path));
        Assert.AreEqual(9, document.displayLevel);
        var clone = Saber.ChartEditor.SaberChartUtility.Clone(document);
        var runtime = ChartLoader.Parse(Saber.ChartEditor.SaberChartUtility.ToJson(clone));
        Assert.AreEqual(9, runtime.displayLevel, "エディターで開いて保存してもLv.9が消えない");
        Assert.AreEqual(document.notes.Count, runtime.notes.Count);
    }

    [TestCase(-1)]
    [TestCase(11)]
    public void EditorRejectsOutOfRangeDisplayLevels(int level)
    {
        var document = new Saber.ChartEditor.SaberChartDocument { displayLevel = level };
        Saber.ChartEditor.SaberChartUtility.Normalize(document);
        Assert.AreEqual(0, document.displayLevel, "不正な指定は既存の難易度表示へ戻す");
    }
}
