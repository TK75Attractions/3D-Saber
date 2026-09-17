using System.IO;
using System.Linq;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;

// 変拍子の未確定部分を4拍固定へ戻さず、提供音源の秒時刻を保持する。
public class AndalusiaChartTests
{
    const string SongId = "Andalusia";
    static string DirectoryPath => Path.Combine(Application.streamingAssetsPath, "Songs", SongId);

    [TestCase("Andalusia")]
    [TestCase("andalusia")]
    public void JapaneseDisplayTitleDoesNotReplaceTheStorageId(string id)
    {
        Assert.AreEqual("アンダルシア", SongSelectController.DisplaySongTitle(id));
        Assert.AreEqual("アンダルシア", ResultSkin.SongIdToDisplayTitle(id));
        CollectionAssert.Contains(SongSelectController.EnumerateSongIds(), SongId);
    }

    [TestCase("easy", 3)]
    [TestCase("normal", 5)]
    [TestCase("hard", 7)]
    public void DedicatedChartsHaveAuthoredLevelsAndUseTheProvidedAudioTimeline(string difficulty, int level)
    {
        Assert.IsTrue(File.Exists(Path.Combine(DirectoryPath, "chart_" + difficulty + ".json")));
        var chart = ChartLoader.LoadFromStreamingAssets(SongId, difficulty);
        Assert.AreEqual(level, chart.displayLevel);
        Assert.AreEqual(0f, chart.offsetMs);
        Assert.AreEqual(1f, chart.coordScale);
        Assert.Greater(chart.notes.Count, 0);
        Assert.Less(chart.notes[0].time, 8000f, "冒頭の有音区間から遊べる");
        Assert.Greater(chart.notes.Last().time, 98000f, "短い試作だけでなく終盤まで収録する");
        float previous = -1;
        foreach (var note in chart.notes)
        {
            Assert.IsFalse(float.IsNaN(note.time) || float.IsInfinity(note.time));
            Assert.That(note.time, Is.InRange(0f, 112220.6f));
            Assert.GreaterOrEqual(note.time, previous);
            Assert.AreEqual(1, note.count);
            Assert.IsFalse(note.IsLong, "初稿で未確認の持続音をロングへ変換しない");
            previous = note.time;
        }
    }

    [Test]
    public void CompatibilityChartIsTheNormalChart()
    {
        Assert.AreEqual(File.ReadAllText(Path.Combine(DirectoryPath, "chart_normal.json")),
            File.ReadAllText(Path.Combine(DirectoryPath, "chart.json")));
    }

    [TestCase("easy", 3)]
    [TestCase("normal", 5)]
    [TestCase("hard", 7)]
    public void EditorRoundTripAndGridTempoChangePreserveEveryAudioTimestamp(string difficulty, int level)
    {
        var document = SaberChartUtility.FromJson(File.ReadAllText(Path.Combine(DirectoryPath, "chart_" + difficulty + ".json")));
        var expected = document.notes.Select(n => n.time).ToArray();
        var restored = SaberChartUtility.Clone(document);
        // 編集用のグリッドを変えても実音に置いた時刻は量子化しない。
        restored.bpm = 115.34375f;
        SaberChartUtility.RecalculateBeatsFromTimes(restored, 0f);
        var chart = ChartLoader.Parse(SaberChartUtility.ToJson(restored));
        Assert.AreEqual(level, chart.displayLevel);
        Assert.AreEqual(0f, chart.offsetMs);
        Assert.AreEqual(expected.Length, chart.notes.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i], chart.notes[i].time, .01f, "保存で音の時刻を動かさない: " + i);
            Assert.AreEqual(document.notes[i].color, chart.notes[i].color);
            Assert.AreEqual(document.notes[i].direction, chart.notes[i].direction);
            Assert.AreEqual(document.notes[i].x, chart.notes[i].x, .0001f);
            Assert.AreEqual(document.notes[i].y, chart.notes[i].y, .0001f);
        }
    }

    [Test]
    public void EasyKeepsSingleHandNotesAndRecoverySpace()
    {
        var chart = ChartLoader.LoadFromStreamingAssets(SongId, "easy");
        foreach (var note in chart.notes)
        {
            Assert.That(note.color, Is.EqualTo("blue").Or.EqualTo("red"));
            Assert.IsFalse(note.IsDirection);
            Assert.AreEqual("tap", note.type);
        }
        foreach (string hand in new[] { "blue", "red" })
        {
            var times = chart.notes.Where(n => n.color == hand).Select(n => n.TimeSeconds).ToArray();
            for (int i = 1; i < times.Length; i++)
                Assert.GreaterOrEqual(times[i] - times[i - 1], .79, "同じ手をすぐに振り直させない");
        }
        for (int i = 1; i < chart.notes.Count; i++)
            Assert.Greater(chart.notes[i].time - chart.notes[i - 1].time, 10f, "Easyに同時斬りを入れない");
        // 表拍かどうかは整数beatで判定できない。ここでは別途レビューした配置の局所密度だけを監査する。
        int start = 0;
        for (int end = 0; end < chart.notes.Count; end++)
        {
            while (chart.notes[end].time - chart.notes[start].time > 2000f) start++;
            Assert.LessOrEqual(end - start + 1, 4);
        }
    }

    [Test]
    public void StageAvoidsInventedFourBeatBarsAndUsesAnAudioTimePreview()
    {
        var stage = StagePerformanceTimeline.Load(SongId);
        Assert.IsTrue(stage.hideBarLines);
        Assert.AreEqual(0, stage.sections.Length, "未確認のサビ区間を断定しない");
        var window = SongPreviewWindow.Resolve(stage, 112.22059, 10);
        Assert.AreEqual(53.44, window.Start, .001);
        Assert.AreEqual(10, window.Duration, .001);
        foreach (string difficulty in new[] { "easy", "normal", "hard" })
        {
            var chart = ChartLoader.LoadFromStreamingAssets(SongId, difficulty);
            Assert.IsTrue(chart.notes.Any(n => SongPreviewWindow.Intersects(chart, n, window, 1)));
        }
        Assert.IsFalse(JsonUtility.FromJson<StagePerformanceTimeline>("{\"sections\":[]}").hideBarLines,
            "設定のない既存データは小節線を隠さない");
        Assert.IsFalse(StagePerformanceTimeline.Load("Morning").hideBarLines);
    }

    [Test]
    public void ResultHistoryKeysUseTheIndependentSongIdAndDifficulty()
    {
        var keys = new[] { "Easy", "Normal", "Hard" }.Select(d => HighScoreStore.Key(SongId, d)).ToArray();
        Assert.AreEqual(3, keys.Distinct().Count());
        foreach (string difficulty in new[] { "Easy", "Normal", "Hard" })
        {
            string key = HighScoreStore.Key(SongId, difficulty);
            Assert.AreEqual("hiscore_Andalusia_" + difficulty.ToLowerInvariant(), key);
            Assert.AreNotEqual(HighScoreStore.Key("アンダルシア", difficulty), key);
            Assert.AreNotEqual(HighScoreStore.Key("ElDorado", difficulty), key);
        }
    }
}
