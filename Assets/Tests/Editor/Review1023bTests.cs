using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 2026-10-23 採用分。同手間隔はh15の指定1組だけを例外にする。
public class Review1023bTests
{
    const double EpsilonMs = .03;
    [Serializable] sealed class Label { public string label; }
    [Serializable] sealed class Labels { public Label[] sections; }

    static ChartData Load(string song, string diff)
    {
        string path = Path.Combine(Application.streamingAssetsPath,
            "Songs", song, "chart_" + diff + ".json");
        Assert.True(File.Exists(path), path);
        // 保存順も検査するため、ローダーによる自動整列を通さない。
        var chart = JsonUtility.FromJson<ChartData>(
            File.ReadAllText(path).TrimStart('\uFEFF'));
        Assert.IsNotNull(chart);
        Assert.IsNotEmpty(chart.notes);
        return chart;
    }

    static double AudioTime(ChartData c, NoteData n) => (double)n.time + c.offsetMs;
    static bool At(ChartData c, NoteData n, double ms, double tolerance = EpsilonMs) =>
        Math.Abs(AudioTime(c, n) - ms) <= tolerance;
    static IEnumerable<double> Cuts(ChartData c, NoteData n)
    {
        for (int k = 0; k < n.count; k++)
            yield return AudioTime(c, n) +
                (n.count > 1 ? (double)n.lengthMs * k / (n.count - 1) : 0);
    }
    static string Hand(NoteData n) =>
        n.color == "red" || n.color == "blue" ? n.color : n.x < 0 ? "blue" : "red";

    [TestCase("escapade", "easy", 16441.0512)]
    [TestCase("escapade", "normal", 16441.0512)]
    [TestCase("Phobia PARTⅠ", "easy", 52743.2727)]
    [TestCase("Epilogue", "easy", 104630.79)]
    [TestCase("揺籠", "easy", 158035.2558)]
    [TestCase("揺籠", "easy", 159081.7674)]
    [TestCase("Phobia PARTⅠ", "easy", 48197.8182)]
    [TestCase("Phobia PARTⅠ", "easy", 88197.8182)]
    [TestCase("Andalusia", "easy", 6952.1582)]
    [TestCase("Aobasou", "normal", 58714.2857)]
    [TestCase("Aobasou", "normal", 59142.8571)]
    [TestCase("Aobasou", "normal", 106714.2857)]
    [TestCase("Aobasou", "normal", 107142.8571)]
    [TestCase("logic", "normal", 51403.0)]
    [TestCase("logic", "normal", 52153.0)]
    [TestCase("RushE", "normal", 110437.2421)]
    [TestCase("RushE", "normal", 110968.4921)]
    [TestCase("BenkyouRemaster", "normal", 70674.0)]
    [TestCase("BenkyouRemaster", "normal", 71049.0)]
    [TestCase("escapade", "normal", 64441.0508)]
    [TestCase("escapade", "normal", 65388.4219)]
    [TestCase("Phobia PARTⅠ", "normal", 45470.5455)]
    [TestCase("Phobia PARTⅠ", "normal", 47743.2727)]
    [TestCase("Phobia PARTⅠ", "normal", 85470.5455)]
    [TestCase("Phobia PARTⅠ", "normal", 88197.8182)]
    public void SelectedSingleNotesAreAbsent(string song, string diff, double audioMs)
    {
        var c = Load(song, diff);
        Assert.False(c.notes.Any(n => At(c, n, audioMs)), song + "/" + diff);
    }

    [TestCase("Andalusia", "easy", 3815.0288, 3949.193115234375, "blue")]
    [TestCase("logic", "normal", 46528, 46434.25, "blue")]
    [TestCase("logic", "normal", 129653, 129403, "red")]
    [TestCase("logic", "normal", 138653, 138403, "red")]
    public void SelectedHalfBeatNotesMatchHard(
        string song, string diff, double from, double to, string color)
    {
        var c = Load(song, diff);
        var hard = Load(song, "hard");
        Assert.False(c.notes.Any(n => At(c, n, from)));
        var moved = c.notes.Where(n => n.color == color && At(c, n, to)).ToArray();
        Assert.AreEqual(1, moved.Length);
        Assert.True(hard.notes.Any(n => At(hard, n, to)));
        var map = new ChartTempoMap(c.bpm, c.beatZeroMs, c.editorTempoMap);
        Assert.AreEqual(map.BeatAt(moved[0].time), moved[0].beat, .0001);
    }

    // E/N/H/隠しEの個数。曲内の同時ノーツを片方だけ動かしても通さない。
    [TestCase("2_23_AM", 8026, 8075, 1, 1, 1, 0)]
    [TestCase("logic", 111403, 111453, 1, 1, 2, 0)]
    [TestCase("logic", 113403, 113454, 1, 1, 2, 0)]
    [TestCase("BenkyouRemaster", 24774, 24805, 1, 0, 1, 0)]
    [TestCase("BenkyouRemaster", 64299, 64325, 0, 1, 1, 0)]
    [TestCase("RushE", 118406, 118382, 1, 2, 2, 2)]
    [TestCase("RushE", 50115, 50138, 1, 1, 1, 2)]
    [TestCase("RushE", 134812, 134770, 1, 1, 1, 1)]
    public void OnsetMovesCoverEveryDifficultyAndEveryPartner(
        string song, double from, double to, int easy, int normal, int hard, int secret)
    {
        var diffs = new[] { "easy", "normal", "hard", "e" };
        var counts = new[] { easy, normal, hard, secret };
        for (int i = 0; i < diffs.Length; i++)
        {
            if (i == 3 && song != "RushE") continue;
            var c = Load(song, diffs[i]);
            Assert.False(c.notes.Any(n => At(c, n, from, 1)));
            var moved = c.notes.Where(n => At(c, n, to)).ToArray();
            Assert.AreEqual(counts[i], moved.Length, song + "/" + diffs[i]);
            var map = new ChartTempoMap(c.bpm, c.beatZeroMs, c.editorTempoMap);
            foreach (var n in moved)
                Assert.AreEqual(map.BeatAt(n.time), n.beat, .0001);
        }
    }

    [TestCase("Aobasou", "normal", 77571)]
    [TestCase("Aobasou", "normal", 79286)]
    [TestCase("Aobasou", "normal", 129000)]
    [TestCase("Aobasou", "normal", 87857)]
    [TestCase("Aobasou", "normal", 115286)]
    [TestCase("Phobia PARTⅠ", "easy", 62743)]
    [TestCase("Phobia PARTⅠ", "easy", 97289)]
    [TestCase("揺籠", "easy,normal", 165361)]
    [TestCase("製作中", "easy,normal", 46551)]
    [TestCase("製作中", "easy,normal", 53285)]
    [TestCase("製作中", "easy,normal", 56653)]
    [TestCase("製作中", "easy,normal", 61857)]
    [TestCase("logic", "easy,normal", 78403)]
    [TestCase("製作中", "easy,normal", 19000)]
    public void UnselectedHalfBeatChoicesAndZeroRemain(string song, string diffs, double ms)
    {
        foreach (var diff in diffs.Split(','))
        {
            var c = Load(song, diff);
            Assert.True(c.notes.Any(n => At(c, n, ms, 1)), song + "/" + diff);
        }
    }

    [TestCase("escapade", "easy", 67)]
    [TestCase("escapade", "normal", 120)]
    [TestCase("Phobia PARTⅠ", "easy", 79)]
    [TestCase("Phobia PARTⅠ", "normal", 138)]
    [TestCase("Andalusia", "easy", 105)]
    [TestCase("Epilogue", "easy", 131)]
    [TestCase("揺籠", "easy", 106)]
    [TestCase("Aobasou", "normal", 193)]
    [TestCase("logic", "normal", 212)]
    [TestCase("RushE", "normal", 186)]
    [TestCase("BenkyouRemaster", "normal", 154)]
    [TestCase("2_23_AM", "hard", 447)]
    [TestCase("logic", "hard", 425)]
    [TestCase("BenkyouRemaster", "hard", 335)]
    [TestCase("RushE", "hard", 353)]
    [TestCase("RushE", "e", 1158)]
    public void CountsChangeOnlyByTheSelectedDeletions(string song, string diff, int count)
    {
        Assert.AreEqual(count, Load(song, diff).notes.Count);
    }

    [Test]
    public void AndalusiaHasTheTwoSelectedSections()
    {
        var t = StagePerformanceTimeline.Load("Andalusia");
        Assert.AreEqual(2, t.sections.Length);
        var starts = new[] { 52.370, 86.358 };
        var ends = new[] { 75.260, 100.578 };
        var levels = new[] { .85, 1.0 };
        var labels = new[] { "主部", "最終展開" };
        var saved = JsonUtility.FromJson<Labels>(File.ReadAllText(Path.Combine(
            Application.streamingAssetsPath, "Songs", "Andalusia", "stage.json")));
        for (int i = 0; i < 2; i++)
        {
            var s = t.sections[i];
            Assert.AreEqual(labels[i], saved.sections[i].label);
            Assert.AreEqual(starts[i], s.startSeconds, .00001);
            Assert.AreEqual(ends[i], s.endSeconds, .00001);
            Assert.AreEqual(levels[i], s.intensity, .00001);
            Assert.AreEqual(.8, s.fadeInSeconds, .00001);
            Assert.AreEqual(1.4, s.fadeOutSeconds, .00001);
            Assert.AreEqual(levels[i], t.Evaluate((starts[i] + ends[i]) / 2), .00001);
        }
        Assert.AreEqual(0, t.Evaluate(80));
        Assert.AreEqual(0, t.Evaluate(103));
    }

    [TestCase("Phobia PARTⅠ", 50925.0909, 54561.4545)]
    [TestCase("Epilogue", 103203.63, 105578.69)]
    [TestCase("揺籠", 156988.7442, 161174.7907)]
    public void SelectedPhrasesHaveThreeBeatRests(string song, double a, double b)
    {
        var c = Load(song, "easy");
        var times = c.notes.SelectMany(n => Cuts(c, n)).ToArray();
        Assert.True(times.Any(t => Math.Abs(t - a) <= EpsilonMs));
        Assert.True(times.Any(t => Math.Abs(t - b) <= EpsilonMs));
        Assert.False(times.Any(t => a + EpsilonMs < t && t < b - EpsilonMs));
        // 報告で使用された曲のBPMでも3拍以上を確保する。
        Assert.GreaterOrEqual(b - a, 3 * 60000 / c.bpm - EpsilonMs);
        Assert.Less(b - a, 5000);
    }

    [TestCase("Phobia PARTⅠ", 49106.9091)]
    [TestCase("Phobia PARTⅠ", 89106.9091)]
    [TestCase("Andalusia", 7641.9561)]
    public void SelectedRedBlueChordsHaveTwoBeatsToPrepare(string song, double ms)
    {
        var c = Load(song, "easy");
        CollectionAssert.AreEquivalent(new[] { "blue", "red" },
            c.notes.Where(n => At(c, n, ms)).Select(n => n.color));
        double previous = c.notes.SelectMany(n => Cuts(c, n))
            .Where(t => t < ms - EpsilonMs).Max();
        Assert.GreaterOrEqual(ms - previous, 2 * 60000 / c.bpm - EpsilonMs);
        var map = new ChartTempoMap(c.bpm, c.beatZeroMs, c.editorTempoMap);
        Assert.GreaterOrEqual(map.BeatAt((float)(ms - c.offsetMs)) -
            map.BeatAt((float)(previous - c.offsetMs)), 2 - .0001);
    }

    // 上限2個では目安1.0に届かない区間は、実際の前/サビ個数を固定する。
    [TestCase("Aobasou", 61.714286, 4, 5, .625)]
    [TestCase("Aobasou", 109.714286, 5, 30, 1.0)]
    [TestCase("logic", 54.403, 4, 7, .73828125)]
    [TestCase("RushE", 112.22494, 5, 26, .86467323)]
    [TestCase("RushE", 40.05941, 7, 33, .78571428)]
    [TestCase("BenkyouRemaster", 73.674, 2, 5, 1.25)]
    [TestCase("escapade", 65.704211, 3, 18, 1.5)]
    [TestCase("Phobia PARTⅠ", 49.106909, 5, 42, 1.2)]
    [TestCase("Phobia PARTⅠ", 89.106909, 6, 21, .875)]
    public void ChorusDensityUsesTheActualPreviousEightBeats(
        string song, double start, int expectedPre, int expectedChorus, double expectedRatio)
    {
        var c = Load(song, "normal");
        var s = StagePerformanceTimeline.Load(song).sections.Single(x =>
            Math.Abs(x.startSeconds - start) < .00001);
        var map = new ChartTempoMap(c.bpm, c.beatZeroMs, c.editorTempoMap);
        double a = start * 1000, b = s.endSeconds * 1000;
        double lo = map.TimeAt(map.BeatAt((float)(a - c.offsetMs)) - 8) + c.offsetMs;
        var times = c.notes.Select(n => AudioTime(c, n)).ToArray();
        int pre = times.Count(t => lo - EpsilonMs <= t && t < a - EpsilonMs);
        int chorus = times.Count(t => a - EpsilonMs <= t && t < b - EpsilonMs);
        Assert.AreEqual(expectedPre, pre);
        Assert.AreEqual(expectedChorus, chorus);
        Assert.AreEqual(expectedRatio, chorus / (b - a) * (a - lo) / pre, .0001);
    }

    [Test]
    public void RushEBuildUpKeepsItsAcceleratingRedPhrase()
    {
        var c = Load("RushE", "normal");
        double[] times = { 36343.4921, 36905.9921, 37437.2421, 37968.4921,
            38468.4921, 38937.2421, 39843.4921 };
        foreach (double t in times)
            Assert.AreEqual(1, c.notes.Count(n => n.color == "red" && At(c, n, t)));
    }

    // h15 / logic / Normal / 青 46153→46434.25ms。
    // 理由: 2026-10-23 ユーザーが音に合わせることを選んだ。
    static bool IsH15Pair(string song, string diff, string side, double a, double b) =>
        song == "logic" && diff == "normal" && side == "blue" &&
        Math.Abs(a - 46153.0) <= EpsilonMs &&
        Math.Abs(b - 46434.25) <= EpsilonMs &&
        Math.Abs(b - a - 281.25) <= EpsilonMs;

    static bool SingleBlueTap(NoteData n) =>
        n.color == "blue" && n.count == 1 && n.lengthMs == 0 &&
        n.type == "tap" && n.direction == "none";

    [TestCase("logic", "normal", "blue", 46153, 46434.25, true)]
    [TestCase("Other", "normal", "blue", 46153, 46434.25, false)]
    [TestCase("logic", "easy", "blue", 46153, 46434.25, false)]
    [TestCase("logic", "normal", "red", 46153, 46434.25, false)]
    [TestCase("logic", "normal", "red", 40153, 40434.25, false)]
    [TestCase("logic", "normal", "blue", 46152, 46434.25, false)]
    [TestCase("logic", "normal", "blue", 46153, 46434.29, false)]
    public void SameHandExceptionMatchesOnlyH15(
        string song, string diff, string side, double a, double b, bool expected)
    {
        Assert.AreEqual(expected, IsH15Pair(song, diff, side, a, b));
    }

    [Test]
    public void ApprovedH15PairRemainsExactly281Point25Milliseconds()
    {
        var c = Load("logic", "normal");
        var blue = c.notes.Where(n => Hand(n) == "blue")
            .SelectMany(n => Cuts(c, n).Select(t => (time: t, note: n)))
            .OrderBy(x => x.time).ToArray();
        int i = Array.FindIndex(blue, x => Math.Abs(x.time - 46434.25) <= EpsilonMs);
        Assert.Greater(i, 0);
        Assert.True(SingleBlueTap(blue[i - 1].note) && SingleBlueTap(blue[i].note));
        Assert.True(IsH15Pair("logic", "normal", "blue", blue[i - 1].time, blue[i].time));
    }

    [Test]
    public void LowerChartsRespectHandIntervalsAndFiveSecondLimit()
    {
        // 免除は h15(ユーザーが選んだ)と、10/16 からある logic 40 秒の赤の組だけ。ほかの違反は失敗する。
        foreach (string song in BeginnerChartTests.Songs)
        foreach (string diff in new[] { "easy", "normal" })
        {
            var c = Load(song, diff);
            for (int i = 1; i < c.notes.Count; i++)
                Assert.GreaterOrEqual(c.notes[i].time, c.notes[i - 1].time);
            var all = c.notes.SelectMany(n => Cuts(c, n)).Distinct()
                .OrderBy(t => t).ToArray();
            for (int i = 1; i < all.Length; i++)
                Assert.Less(all[i] - all[i - 1], 5000, song + "/" + diff);
            foreach (string side in new[] { "blue", "red" })
            {
                var events = c.notes.Where(n => Hand(n) == side)
                    .SelectMany(n => Cuts(c, n).Select(t => (time: t, note: n)))
                    .OrderBy(x => x.time).ToArray();
                for (int i = 1; i < events.Length; i++)
                {
                    var a = events[i - 1];
                    var b = events[i];
                    if (SingleBlueTap(a.note) && SingleBlueTap(b.note) &&
                        IsH15Pair(song, diff, side, a.time, b.time))
                        continue;
                    // 10/16 からある logic Normal 40 秒の赤の組(281.25ms)。今日の直しで増えたものではない(取りまとめ役が確認)。
                    if (song == "logic" && diff == "normal" && side == "red" &&
                        Math.Abs(a.time - 40153.0) <= EpsilonMs && Math.Abs(b.time - 40434.25) <= EpsilonMs)
                        continue;
                    Assert.GreaterOrEqual(b.time - a.time,
                        (diff == "easy" ? 500 : 300) - EpsilonMs,
                        song + "/" + diff + "/" + side + " " + a.time);
                }
            }
            if (diff == "normal")
            {
                string folder = Path.Combine(Application.streamingAssetsPath, "Songs", song);
                CollectionAssert.AreEqual(
                    File.ReadAllBytes(Path.Combine(folder, "chart_normal.json")),
                    File.ReadAllBytes(Path.Combine(folder, "chart.json")));
            }
        }
    }
}
