using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;

// 2026-10-23 の採用分。A5 と、107.9秒の連続なぞり2組は保留。
public class Review1023Tests
{
    const double TimeToleranceMs = .03;

    static string ChartPath(string song, string difficulty) =>
        Path.Combine(Application.streamingAssetsPath, "Songs", song,
            "chart_" + difficulty.ToLowerInvariant() + ".json");

    static ChartData Load(string song, string difficulty)
    {
        Assert.True(File.Exists(ChartPath(song, difficulty)),
            "別難易度へのフォールバックを許さない");
        var chart = ChartLoader.LoadFromStreamingAssets(song, difficulty);
        Assert.IsNotNull(chart);
        Assert.IsNotEmpty(chart.notes);
        return chart;
    }

    static bool At(ChartData chart, NoteData note, double audioMs) =>
        Math.Abs((double)note.time + chart.offsetMs - audioMs) < TimeToleranceMs;

    [TestCase(38, 21329.724609375, 21359.533, 30.0)]
    [TestCase(47, 26667.123046875, 26692.867, 38.000001)]
    public void HardRedNotesUseTheExistingEasySounds(
        int index, double from, double to, double beat)
    {
        var hard = Load("2_23_AM", "hard");
        var easy = Load("2_23_AM", "easy");
        Assert.False(hard.notes.Any(n => At(hard, n, from)),
            "移動前には残さない");
        Assert.True(At(hard, hard.notes[index], to));
        Assert.AreEqual("red", hard.notes[index].color);
        Assert.AreEqual(beat, hard.notes[index].beat, .00005);
        Assert.True(easy.notes.Any(n => At(easy, n, to)),
            "下位にも同じ音がある");
        if (index == 38)
        {
            var normal = Load("2_23_AM", "normal");
            Assert.True(normal.notes.Any(n => At(normal, n, to)));
        }
    }

    // 既存18組の同期を維持する。62Aで追加した赤2個より後ろはindexを2つ繰り下げる。
    // 2026-10-31 に曲を 1:51 に縮め(元の 24〜47・56〜63 小節を抜いた)、残ったのはアウトロの5組。32 小節(85.333 秒)前へ寄った。
    [TestCase(183, 88715.823, 88692.869792, 131)]
    [TestCase(196, 94047.073, 94026.197917, 139)]
    [TestCase(210, 99383.12, 99359.526042, 147)]
    [TestCase(216, 102045.339, 102026.197917, 151)]
    [TestCase(218, 103346.557, 103359.526042, 153)]
    public void AcceptedGoldChordsHaveOneTiming(
        int index, double from, double to, double beat)
    {
        var chart = Load("2_23_AM", "hard");
        var moved = chart.notes[index];
        Assert.AreEqual("gold", moved.color);
        Assert.False(chart.notes.Any(n => At(chart, n, from)),
            "移動前には残さない");
        Assert.True(At(chart, moved, to), "移動した金が移動先にある");
        Assert.AreEqual(beat, moved.beat, .00005);
        Assert.True(chart.notes.Where((n, i) => i != index)
            .Any(n => At(chart, n, to)), "同じ時刻の相手を消していない");
    }

    // 金を相手の横へ出した4組(元の 99〜106 秒、ビルドの区間)と、未確定の連続なぞり(元の 107.9 秒)は、
    // 2026-10-31 に曲を 1:51 に縮めたとき抜いた区間と一緒に無くなった。
    [Test]
    public void AcceptedChordsFitTenMilliseconds()
    {
        string json = File.ReadAllText(ChartPath("2_23_AM", "hard"));
        var document = JsonUtility.FromJson<SaberChartDocument>(json);
        Assert.AreEqual(0, SaberChartLint.Check(document, "hard").Count(i => i.rule == "chord-offset"));
        foreach (var group in SaberChartLint.GroupSimultaneous(document.notes))
        {
            if (group.Count < 2) continue;
            Assert.LessOrEqual(group.Last().time - group.First().time,
                NoteSpawner.SimultaneousEpsilonSeconds * 1000.0,
                group[0].time + "ms");
        }
    }

    [Test]
    public void HardKeepsItsNotesInOrder()
    {
        var raw = JsonUtility.FromJson<ChartData>(
            File.ReadAllText(ChartPath("2_23_AM", "hard")));
        // 449 個から、抜いた区間の 216 個と、1つ目のつなぎ目の直前の青 1 個(同じ手が 0.3 秒になる)を除いた。
        Assert.AreEqual(232, raw.notes.Count);
        for (int i = 1; i < raw.notes.Count; i++)
            Assert.GreaterOrEqual(raw.notes[i].time, raw.notes[i - 1].time,
                "ローダーの整列で保存順の不具合を隠さない");
    }

    [Test]
    public void ReleasedChartsHaveNoOverlapBelowPointFour()
    {
        foreach (string song in BeginnerChartTests.Songs)
        foreach (string diff in song == "RushE"
            ? new[] { "easy", "normal", "hard", "e" }
            : new[] { "easy", "normal", "hard" })
        {
            string path = ChartPath(song, diff);
            Assert.True(File.Exists(path), path);
            var document = JsonUtility.FromJson<SaberChartDocument>(
                File.ReadAllText(path));
            foreach (var issue in SaberChartLint.Check(document, diff)
                .Where(i => i.rule == "overlap" || i.rule == "duplicate"))
            {
                var a = document.notes[issue.notes[0]];
                var b = document.notes[issue.notes[1]];
                float scale = document.coordScale > 0
                    ? document.coordScale : 1;
                float distance = Vector2.Distance(
                    new Vector2(a.x, a.y),
                    new Vector2(b.x, b.y)) * scale;
                Assert.GreaterOrEqual(distance, .4f,
                    song + "/" + diff + " " + issue.timeMs + "ms");
            }
        }
    }

    [TestCase("easy", 500.0)]
    [TestCase("normal", 300.0)]
    public void TwoTwentyThreeLowerChartsKeepTheSameHandIntervals(
        string difficulty, double minimumMs)
    {
        var chart = Load("2_23_AM", difficulty);
        foreach (string hand in new[] { "blue", "red" })
        {
            var times = new List<double>();
            foreach (var note in chart.notes)
            {
                bool belongs = note.color == hand ||
                    ((note.color == "gold" || note.color == "default") &&
                     (note.x < 0) == (hand == "blue"));
                if (!belongs) continue;
                for (int k = 0; k < note.count; k++)
                    times.Add((double)note.time + chart.offsetMs +
                        (note.count > 1
                            ? note.lengthMs * (double)k / (note.count - 1)
                            : 0));
            }
            times.Sort();
            for (int i = 1; i < times.Count; i++)
                Assert.GreaterOrEqual(
                    times[i] - times[i - 1], minimumMs - .03,
                    hand + ": " + times[i - 1] + " -> " + times[i]);
        }
    }
}
