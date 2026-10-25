using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// logic!!! の裏譜面 S(緩急、2026-10-26)。ノーツは Hard そのまま、流れる速さの表(scroll)だけが足してある。
// 譜面は C:\Users\shike\dev\tools\ChartLogicSpeed\gen_speed.py --write で作る。
public class LogicSpeedChartTests
{
    const double Approach = 1.0;   // GameSession.NoteApproachTimeDefault

    static ChartData Read(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "logic", "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path), "必須の譜面がありません: " + path);
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    [Test]
    public void NotesAreTheHardChartWithASpeedTable()
    {
        var speed = Read("s");
        var hard = Read("hard");
        Assert.AreEqual(9, speed.displayLevel);
        Assert.AreEqual(hard.bpm, speed.bpm);
        Assert.AreEqual(hard.editorTempoMap.Count, speed.editorTempoMap.Count);
        Assert.AreEqual(hard.notes.Count, speed.notes.Count);
        for (int i = 0; i < hard.notes.Count; i++)
        {
            NoteData a = hard.notes[i], b = speed.notes[i];
            string label = b.beat + "拍";
            Assert.AreEqual(a.time, b.time, label);
            Assert.AreEqual(a.x, b.x, label);
            Assert.AreEqual(a.y, b.y, label);
            Assert.AreEqual(a.color, b.color, label);
            Assert.AreEqual(a.direction, b.direction, label);
            Assert.AreEqual(a.count, b.count, label);
        }
        Assert.AreEqual(0, hard.scroll.Count, "Hard はいつもの速さ");
        Assert.Greater(speed.scroll.Count, 10);
        Assert.AreSame(RushESecretState.LogicSpeed, RushESecretState.ForDifficulty("S"));
    }

    [Test]
    public void ItStopsAFewTimesButNeverWhileANoteArrives()
    {
        var chart = Read("s");
        var map = ScrollSpeedMap.Build(chart.scroll);
        Assert.NotNull(map);
        int stops = chart.scroll.Count(p => p.speed == 0);
        Assert.That(stops, Is.InRange(2, 4), "止まるのは数回だけ(処理落ちに見せない)");
        foreach (var note in chart.notes)
        {
            double hit = note.TimeSeconds;
            Assert.GreaterOrEqual(map.SpeedAt(hit - .001), .3, hit + " 秒のノーツは動きながら届く(止まって待たない)");
        }
        // 止まっている間、空中に少なくとも1つのノーツが見えている(止まったことが分かる)。
        for (int i = 0; i < chart.scroll.Count; i++)
        {
            if (chart.scroll[i].speed != 0) continue;
            double from = chart.scroll[i].time / 1000.0;
            bool frozen = chart.notes.Any(n => n.TimeSeconds > from && map.Remaining(from, n.TimeSeconds) <= Approach);
            Assert.IsTrue(frozen, from + " 秒の止まりに、空中のノーツがある");
        }
    }

    [Test]
    public void EveryNoteStaysOnScreenLongEnoughToRead()
    {
        var chart = Read("s");
        var map = ScrollSpeedMap.Build(chart.scroll);
        foreach (var note in chart.notes)
        {
            double hit = note.TimeSeconds;
            double seen = hit - map.TimeAtPosition(map.PositionAt(hit) - Approach);
            Assert.GreaterOrEqual(seen, .5, hit + " 秒のノーツは 0.5 秒以上見える");
        }
        Assert.LessOrEqual(chart.scroll.Max(p => p.speed), 2.0f, "新しく出るノーツが速すぎない");
    }
}
