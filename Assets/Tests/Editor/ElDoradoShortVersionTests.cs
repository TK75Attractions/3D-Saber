using System.IO;
using System.Linq;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;

// 短縮版への置換で音源終端外や編集点をまたぐロングを残さない。
public class ElDoradoShortVersionTests
{
    [TestCase("easy")]
    [TestCase("normal")]
    [TestCase("hard")]
    public void NotesAndLongEndsStayInsideShortAudioAndDoNotCrossEdits(string difficulty)
    {
        var chart = ChartLoader.LoadFromStreamingAssets("ElDorado", difficulty);
        Assert.AreEqual(100, chart.bpm);
        Assert.AreEqual(0, chart.offsetMs);
        Assert.Less(chart.notes.Last().time / 1000.0, 142);
        foreach (var note in chart.notes)
        {
            double start = note.time / 1000.0;
            double length = note.count > 1 ? (note.lengthMs > 0 ? note.lengthMs / 1000.0 : (note.count - 1) * .7) : 0;
            Assert.That(start, Is.InRange(0, 144.065306));
            Assert.Less(start + length, 144.065306);
            foreach (double join in new[] { 11.403, 119.403 })
                Assert.False(start < join && start + length > join, difficulty + ": 編集点をまたぐロング " + start);
        }
    }

    [TestCase("easy", 7199f, 4200f, 5)]
    [TestCase("easy", 117599f, 1800f, 2)]
    [TestCase("hard", 2399f, 9000f, 9)]
    [TestCase("hard", 7199f, 4200f, 8)]
    [TestCase("hard", 117599f, 1800f, 3)]
    public void CutOffLongsRequireFewerCutsInTheirRemainingWindow(string difficulty, float time, float length, int count)
    {
        var note = ChartLoader.LoadFromStreamingAssets("ElDorado", difficulty).notes.Single(n => n.time == time && n.IsLong);
        Assert.AreEqual(length, note.lengthMs);
        Assert.AreEqual(count, note.count);
    }

    [TestCase("easy")]
    [TestCase("normal")]
    [TestCase("hard")]
    public void EditorRoundTripKeepsReplacementTiming(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "ElDorado", "chart_" + difficulty + ".json");
        string json = File.ReadAllText(path);
        var before = ChartLoader.Parse(json).notes.Select(n => n.time).OrderBy(t => t).ToArray();
        var document = SaberChartUtility.FromJson(json);
        var after = ChartLoader.Parse(SaberChartUtility.ToJson(document)).notes.Select(n => n.time).OrderBy(t => t).ToArray();
        CollectionAssert.AreEqual(before, after);
    }
}
