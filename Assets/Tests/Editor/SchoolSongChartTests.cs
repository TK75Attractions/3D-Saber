using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 校歌の正式名・指定難易度・音源との時間基準を実データで検証する。
public class SchoolSongChartTests
{
    [TestCase("Epilogue")]
    [TestCase("epilogue")]
    public void SchoolSongUsesJapaneseTitleWithoutChangingStorageId(string id)
    {
        Assert.AreEqual("校歌", ResultSkin.SongIdToDisplayTitle(id));
        Assert.AreEqual("校歌", SongSelectController.DisplaySongTitle(id));
        CollectionAssert.Contains(SongSelectController.EnumerateSongIds(), "Epilogue");
    }

    [Test]
    public void OtherSongTitlesRetainTheirExistingPresentation()
    {
        Assert.AreEqual("ElDorado", SongSelectController.DisplaySongTitle("ElDorado"));
        Assert.AreEqual("EL DORADO", ResultSkin.SongIdToDisplayTitle("ElDorado"));
        Assert.AreEqual("揺籠", SongSelectController.DisplaySongTitle("揺籠"));
    }

    [Test]
    public void TitleFontCanRenderSchoolSongKanji()
    {
        var font = UISkinKit.FontAsset("Oxanium-ExtraBold");
        Assert.IsNotNull(font);
        Assert.IsTrue(font.HasCharacter('校', searchFallbacks: true, tryAddCharacter: true));
        Assert.IsTrue(font.HasCharacter('歌', searchFallbacks: true, tryAddCharacter: true));
    }

    [TestCase("easy", 2)]
    [TestCase("normal", 5)]
    [TestCase("hard", 10)]
    public void DedicatedChartLoadsWithRequestedLevel(string difficulty, int level)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "Epilogue", "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path));
        var chart = ChartLoader.LoadFromStreamingAssets("Epilogue", difficulty);
        Assert.IsNotNull(chart);
        Assert.AreEqual(level, chart.displayLevel);
        Assert.Greater(chart.notes.Count, 0);
        Assert.AreEqual(0f, chart.notes[0].beat, 0.001f, "PDFの4小節目を曲の0拍にする");
        Assert.AreEqual(0f, chart.notes[0].time, 0.01f);
        Assert.That(chart.offsetMs, Is.InRange(1250f, 1350f));
        foreach (var n in chart.notes)
        {
            Assert.That(n.beat, Is.InRange(0f, 303.999f), "別曲3小節の12拍を加えない");
            Assert.Less((n.time + chart.offsetMs + n.lengthMs) / 1000f, 164.2f);
            if (n.IsDirection) Assert.AreNotEqual(CutDirection.None, CutDirectionHelper.Parse(n.direction));
        }
    }

    [Test]
    public void NormalIsTheFallbackChart()
    {
        string root = Path.Combine(Application.streamingAssetsPath, "Songs", "Epilogue");
        Assert.AreEqual(File.ReadAllText(Path.Combine(root, "chart_normal.json")), File.ReadAllText(Path.Combine(root, "chart.json")));
    }

    [Test]
    public void VariableTempoIsResolvedInMilliseconds()
    {
        var chart = ChartLoader.LoadFromStreamingAssets("Epilogue", "hard");
        var early = chart.notes.First(n => Mathf.Abs(n.beat - 4f) < 0.001f);
        var late = chart.notes.First(n => n.beat >= 200f);
        Assert.AreEqual(4f * 60000f / 92f, early.time, 2f);
        Assert.Greater(late.beat * 60000f / 92f - late.time, 15000f, "加速後を92 BPM固定に戻さない");
    }

    [Test]
    public void SongSelectionDisplaysAllThreeAuthoredLevels()
    {
        var go = new GameObject("SchoolSongLevelTest");
        try
        {
            var controller = go.AddComponent<SongSelectController>();
            controller.Populate();
            int index = Enumerable.Range(0, controller.SongCount).First(i => controller.SongIdAt(i) == "Epilogue");
            Assert.AreEqual(2, controller.DisplayLevelFor(index, 0));
            Assert.AreEqual(5, controller.DisplayLevelFor(index, 1));
            Assert.AreEqual(10, controller.DisplayLevelFor(index, 2));
        }
        finally { Object.DestroyImmediate(go); }
    }

    [TestCase("easy", 2)]
    [TestCase("normal", 5)]
    [TestCase("hard", 10)]
    public void EditorSavePreservesAuthoredLevelAndTiming(string difficulty, int level)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "Epilogue", "chart_" + difficulty + ".json");
        var document = Saber.ChartEditor.SaberChartUtility.FromJson(File.ReadAllText(path));
        var roundTrip = ChartLoader.Parse(Saber.ChartEditor.SaberChartUtility.ToJson(Saber.ChartEditor.SaberChartUtility.Clone(document)));
        Assert.AreEqual(level, roundTrip.displayLevel);
        Assert.AreEqual(document.notes.Count, roundTrip.notes.Count);
        Assert.AreEqual(1296f, roundTrip.offsetMs, 0.01f);
    }
}
