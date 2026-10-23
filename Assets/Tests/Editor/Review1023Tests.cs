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

    // 金だけを基準ノーツへ合わせた18組。配列の順番とノーツ数も維持する。
    [TestCase(155, 76011.25, 76026.203125, 112)]
    [TestCase(163, 78674.9140625, 78692.8671875, 116)]
    [TestCase(176, 82671.4375, 82692.8671875, 122)]
    [TestCase(178, 84003.234375, 84026.203125, 124)]
    [TestCase(183, 85370.4765625, 85359.53125, 126)]
    [TestCase(198, 99345.7421875, 99359.53125, 147)]
    [TestCase(211, 103369.8203125, 103359.53125, 153)]
    [TestCase(260, 120049.6484375, 120026.203125, 178)]
    [TestCase(263, 121349.0546875, 121359.53125, 180)]
    [TestCase(275, 124703.921875, 124692.8671875, 185)]
    [TestCase(283, 126705.9609375, 126692.8671875, 188)]
    [TestCase(285, 127372.625, 127359.53125, 189)]
    [TestCase(289, 128714.625, 128692.8671875, 191)]
    [TestCase(398, 174049.15625, 174026.203125, 259)]
    [TestCase(411, 179380.40625, 179359.53125, 267)]
    [TestCase(425, 184716.453125, 184692.859375, 275)]
    [TestCase(431, 187378.671875, 187359.53125, 279)]
    [TestCase(433, 188679.890625, 188692.859375, 281)]
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

    [TestCase(198, 199, 99359.53125, 1.79)]
    [TestCase(211, 210, 103359.53125, -.60)]
    [TestCase(216, 215, 104694.0234375, -.60)]
    [TestCase(221, 220, 106031.2265625, -.60)]
    public void GoldNotesAreVisibleBesideTheirPartners(
        int index, int partnerIndex, double time, double x)
    {
        var chart = Load("2_23_AM", "hard");
        var gold = chart.notes[index];
        var partner = chart.notes[partnerIndex];
        Assert.AreEqual("gold", gold.color);
        Assert.True(At(chart, gold, time));
        Assert.AreEqual(x, gold.x, .00001);
        Assert.AreEqual(-.2142857015132904, gold.y, .00001,
            "高さを変えない");
        float distance = Vector2.Distance(
            new Vector2(gold.x, gold.y),
            new Vector2(partner.x, partner.y)) * chart.coordScale;
        Assert.GreaterOrEqual(distance, SaberChartLint.OverlapDistance);
        Assert.LessOrEqual(distance, 2 * SaberChartLint.HitRange);
        Assert.LessOrEqual(Math.Abs(gold.time - partner.time),
            NoteSpawner.SimultaneousEpsilonSeconds * 1000.0);
    }

    [Test]
    public void AcceptedChordsFitTenMillisecondsAndOnlyTwoReviewedGroupsRemain()
    {
        string json = File.ReadAllText(ChartPath("2_23_AM", "hard"));
        var document = JsonUtility.FromJson<SaberChartDocument>(json);
        var issues = SaberChartLint.Check(document, "hard")
            .Where(i => i.rule == "chord-offset").ToArray();

        // 全件0を偽装しない。未確定の連続なぞりだけを明示して残す。
        Assert.AreEqual(2, issues.Length);
        CollectionAssert.AreEqual(new[] { 226, 227 }, issues[0].notes);
        CollectionAssert.AreEqual(new[] { 228, 229 }, issues[1].notes);
        foreach (var group in SaberChartLint.GroupSimultaneous(document.notes))
        {
            if (group.Count < 2) continue;
            double first = group[0].time;
            if (Math.Abs(first - 107884.703125) < TimeToleranceMs ||
                Math.Abs(first - 107929.421875) < TimeToleranceMs)
                continue;
            Assert.LessOrEqual(group.Last().time - group.First().time,
                NoteSpawner.SimultaneousEpsilonSeconds * 1000.0,
                first + "ms");
        }
    }

    [Test]
    public void HardKeepsAllNotesAndTheDeferredSweep()
    {
        var raw = JsonUtility.FromJson<ChartData>(
            File.ReadAllText(ChartPath("2_23_AM", "hard")));
        Assert.AreEqual(447, raw.notes.Count, "今回の採用分では削除しない");
        for (int i = 1; i < raw.notes.Count; i++)
            Assert.GreaterOrEqual(raw.notes[i].time, raw.notes[i - 1].time,
                "ローダーの整列で保存順の不具合を隠さない");
        double[] times = {
            107884.703125, 107909.09375, 107929.421875,
            107945.671875, 107966.0, 107974.125
        };
        for (int i = 0; i < times.Length; i++)
        {
            var note = raw.notes[226 + i];
            Assert.AreEqual(times[i], note.time, TimeToleranceMs);
            Assert.AreEqual("gold", note.color);
            Assert.AreEqual(i % 2 == 0 ? "left" : "right", note.direction);
        }
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
