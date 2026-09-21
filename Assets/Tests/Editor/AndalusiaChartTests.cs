using System.IO;
using System.Linq;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;

// 音源から推定した小節マップを保存し、提供音源のノーツ時刻を保持する。
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
            if (note.IsLong)
            {
                Assert.That(note.count, Is.InRange(2, 3));
                Assert.That(note.lengthMs, Is.InRange(450f, 2400f));
                Assert.AreEqual("none", note.direction, "連続切りに方向拘束を重ねない");
                Assert.Less(note.time + note.lengthMs, 104000f);
            }
            else
            {
                Assert.AreEqual(1, note.count);
                Assert.AreEqual(0f, note.lengthMs);
            }
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
        // 同時刻の左右2個はローダーのソートで順番が入れ替わり得るため、位置で対応付ける。
        var loaded = chart.notes.OrderBy(n => n.time).ThenBy(n => n.x).ThenBy(n => n.y).ToArray();
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i], loaded[i].time, .01f, "保存で音の時刻を動かさない: " + i);
            Assert.AreEqual(document.notes[i].color, loaded[i].color);
            Assert.AreEqual(document.notes[i].direction, loaded[i].direction);
            Assert.AreEqual(document.notes[i].x, loaded[i].x, .0001f);
            Assert.AreEqual(document.notes[i].y, loaded[i].y, .0001f);
            Assert.AreEqual(document.notes[i].count, loaded[i].count);
            Assert.AreEqual(document.notes[i].lengthMs, loaded[i].lengthMs, .01f);
            Assert.AreEqual(document.notes[i].type, loaded[i].type);
        }
    }

    [Test]
    public void EasyIntroducesSpecialNotesWithRecoverySpace()
    {
        var chart = ChartLoader.LoadFromStreamingAssets(SongId, "easy");
        foreach (var note in chart.notes)
        {
            Assert.Contains(note.color, new[] { "blue", "red", "gold" });
            if (note.IsLong) Assert.AreEqual(2, note.count);
            if (note.IsDirection) Assert.Contains(note.direction, new[] { "up", "down" });
        }
        foreach (string hand in new[] { "blue", "red" })
        {
            var notes = chart.notes.Where(n => IntendedHand(n) == hand).ToArray();
            for (int i = 1; i < notes.Length; i++)
                Assert.GreaterOrEqual(notes[i].TimeSeconds - EndSeconds(notes[i - 1]), .63,
                    "ロング終端と金ノーツも含めて同じ手の回復を残す");
        }
        // 表拍かどうかは整数beatで判定できない。ここでは別途レビューした配置の局所密度だけを監査する。
        int start = 0;
        for (int end = 0; end < chart.notes.Count; end++)
        {
            while (chart.notes[end].time - chart.notes[start].time > 2000f) start++;
            Assert.LessOrEqual(end - start + 1, 5, "少数の同時切り以外は主打の密度を維持する");
        }
    }

    static string IntendedHand(NoteData note) => note.color == "gold" ? (note.x < 0 ? "blue" : "red") : note.color;
    static double EndSeconds(NoteData note) => note.TimeSeconds + (note.IsLong ? note.lengthMs / 1000.0 : 0);

    [TestCase("easy", .63)]
    [TestCase("normal", .45)]
    [TestCase("hard", .29)]
    public void ArrangementHasVarietyWithoutImpossibleHandOccupancy(string difficulty, double recovery)
    {
        var chart = ChartLoader.LoadFromStreamingAssets(SongId, difficulty);
        Assert.IsTrue(chart.notes.Any(n => n.IsLong));
        Assert.IsTrue(chart.notes.Any(n => n.IsDirection));
        Assert.IsTrue(chart.notes.Any(n => n.color == "gold"));
        Assert.IsTrue(chart.notes.GroupBy(n => n.time).Any(g => g.Count() == 2));
        foreach (var group in chart.notes.GroupBy(n => n.time))
        {
            Assert.LessOrEqual(group.Count(), 2, "三本目の手を要求しない");
            if (group.Count() != 2) continue;
            var pair = group.OrderBy(n => n.x).ToArray();
            Assert.AreNotEqual(IntendedHand(pair[0]), IntendedHand(pair[1]));
            Assert.GreaterOrEqual(pair[1].x - pair[0].x, 1.4f, "同時切りを重ねない");
        }
        foreach (string hand in new[] { "blue", "red" })
        {
            var notes = chart.notes.Where(n => IntendedHand(n) == hand).ToArray();
            for (int i = 1; i < notes.Length; i++)
            {
                double gap = notes[i].TimeSeconds - EndSeconds(notes[i - 1]);
                Assert.GreaterOrEqual(gap, recovery, "同手ロングの拘束中に別ノーツを要求しない: " + notes[i].time);
                float travel = Vector2.Distance(new Vector2(notes[i].x, notes[i].y),
                    new Vector2(notes[i - 1].x, notes[i - 1].y));
                Assert.LessOrEqual(travel, (float)(gap * 1.55 + .003), "短い間隔の大移動を避ける");
            }
        }
    }

    [Test]
    public void StageShowsAuthoredMixedMeterBarsAndUsesAnAudioTimePreview()
    {
        var stage = StagePerformanceTimeline.Load(SongId);
        Assert.IsFalse(stage.hideBarLines);
        Assert.AreEqual(0, stage.sections.Length, "未確認のサビ区間を断定しない");
        var window = SongPreviewWindow.Resolve(stage, 112.22059, 10);
        Assert.AreEqual(53.44, window.Start, .001);
        Assert.AreEqual(10, window.Duration, .001);
        foreach (string difficulty in new[] { "easy", "normal", "hard" })
        {
            var chart = ChartLoader.LoadFromStreamingAssets(SongId, difficulty);
            Assert.Greater(chart.timeSignatures.Count, 1);
            Assert.IsTrue(chart.timeSignatures.Any(item => item.denominator == 8));
            var meter = new ChartMeterMap(chart.timeSignatures);
            Assert.AreEqual(96, meter.At(96).BarStart);
            Assert.AreEqual(6, meter.At(96).Numerator);
            Assert.AreEqual(99, meter.AdjacentBar(96, true));
            Assert.AreEqual(108, meter.At(108).BarStart);
            CollectionAssert.AreEqual(
                ChartLoader.LoadFromStreamingAssets(SongId, "normal").timeSignatures.Select(item => JsonUtility.ToJson(item)),
                chart.timeSignatures.Select(item => JsonUtility.ToJson(item)));
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
