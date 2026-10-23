using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 2026-10-23 採用分21A〜24A。正規化前の保存値を検査する。
public class Review1023cTests
{
    const double EpsilonMs = .03;

    [Serializable] sealed class CutRow
    {
        public int index;
        public double[] timesMs;
        public string[] directions;
        public string hand;
    }

    [Serializable] sealed class CutPlan { public CutRow[] notes; }

    static string Folder(string song) =>
        Path.Combine(Application.streamingAssetsPath, "Songs", song);

    static ChartData Load(string song)
    {
        string path = Path.Combine(Folder(song), "chart_normal.json");
        Assert.True(File.Exists(path), path);
        var chart = JsonUtility.FromJson<ChartData>(
            File.ReadAllText(path).TrimStart('\uFEFF'));
        Assert.IsNotNull(chart);
        Assert.IsNotEmpty(chart.notes);
        return chart;
    }

    static CutPlan Plan(string song) => JsonUtility.FromJson<CutPlan>(
        File.ReadAllText(Path.Combine(Application.dataPath, "..", "Tools",
            "ChartDesign", "Beginner20261021", "plans", song, "normal.json"))
            .TrimStart('\uFEFF'));

    static bool At(ChartData chart, NoteData note, double ms) =>
        Math.Abs((double)note.time + chart.offsetMs - ms) <= EpsilonMs;

    static NoteData One(ChartData chart, double ms, string color)
    {
        var found = chart.notes.Where(n => At(chart, n, ms) && n.color == color).ToArray();
        Assert.AreEqual(1, found.Length, ms + "ms / " + color);
        return found[0];
    }

    static void Tap(NoteData note, double x, double y)
    {
        Assert.AreEqual("tap", note.type);
        Assert.AreEqual("none", note.direction);
        Assert.AreEqual(1, note.count);
        Assert.AreEqual(0, note.lengthMs);
        Assert.AreEqual(x, note.x, .00001);
        Assert.AreEqual(y, note.y, .00001);
    }

    [TestCase("Aobasou", 0.0, "red", .9, -.6, 0.0, "down")]
    [TestCase("BenkyouRemaster", 40224.0, "blue", -1.05, -.45, 97.0, "down")]
    [TestCase("escapade", 71072.6328, "red", 1.9, .3, 225.000023, "down")]
    [TestCase("ManicSonic", 79500.0, "blue", -.9, .2, 235.0, "down")]
    [TestCase("ManicSonic", 108833.3333, "blue", -1.1, -.15, 323.0, "down")]
    public void SelectedNotesAreSingleTapsAtTheOriginalSoundAndPosition(
        string song, double ms, string color, double x, double y,
        double beat, string plannedStroke)
    {
        var chart = Load(song);
        var note = One(chart, ms, color);
        Tap(note, x, y);
        Assert.AreEqual(beat, note.beat, .00005);
        int index = chart.notes.IndexOf(note);
        var row = Plan(song).notes[index];
        Assert.AreEqual(index, row.index);
        Assert.AreEqual(color, row.hand);
        Assert.AreEqual(1, row.timesMs.Length);
        Assert.AreEqual(ms, row.timesMs[0] + chart.offsetMs, EpsilonMs);
        // directionsは必須方向ではなく、試験で実際に振る方向。
        CollectionAssert.AreEqual(new[] { plannedStroke }, row.directions);
    }

    [TestCase("escapade", 7283.1573)]
    [TestCase("ManicSonic", 107500.0)]
    [TestCase("ManicSonic", 107833.3333)]
    [TestCase("ManicSonic", 145500.0)]
    public void SelectedBlueNotesAreAbsent(string song, double ms)
    {
        var chart = Load(song);
        Assert.False(chart.notes.Any(n => At(chart, n, ms) && n.color == "blue"));
    }

    [TestCase("escapade", 6967.3682, "blue", -1.9, -.6)]
    [TestCase("escapade", 7598.9473, "blue", -1.9, -.6)]
    [TestCase("ManicSonic", 107166.6667, "blue", -.55, -.15)]
    [TestCase("ManicSonic", 108166.6667, "blue", -1.1, -.15)]
    [TestCase("ManicSonic", 144500.0, "red", 1.15, -.15)]
    [TestCase("ManicSonic", 145833.3333, "blue", -1.15, -.15)]
    [TestCase("ManicSonic", 146166.6667, "red", 1.15, -.15)]
    public void NeighboursRemainAtTheirOriginalSoundsAndPositions(
        string song, double ms, string color, double x, double y)
    {
        var chart = Load(song);
        Tap(One(chart, ms, color), x, y);
    }

    [TestCase("Aobasou", 176, 177, 1, 6)]
    [TestCase("BenkyouRemaster", 155, 156, 1, 7)]
    [TestCase("escapade", 119, 120, 1, 4)]
    [TestCase("ManicSonic", 207, 208, 1, 8)]
    public void CountsPlansAndFallbackMatchTheReviewedResult(
        string song, int notes, int cuts, int longs, int flicks)
    {
        var chart = Load(song);
        Assert.AreEqual(notes, chart.notes.Count);
        Assert.AreEqual(cuts, chart.notes.Sum(n => n.count));
        Assert.AreEqual(longs, chart.notes.Count(n => n.IsLong));
        Assert.AreEqual(flicks, chart.notes.Count(n => n.IsDirection));
        CollectionAssert.AreEqual(
            File.ReadAllBytes(Path.Combine(Folder(song), "chart_normal.json")),
            File.ReadAllBytes(Path.Combine(Folder(song), "chart.json")));
        var plan = Plan(song);
        Assert.AreEqual(notes, plan.notes.Length);
        for (int i = 0; i < notes; i++)
        {
            var n = chart.notes[i];
            var row = plan.notes[i];
            Assert.AreEqual(i, row.index);
            Assert.AreEqual(n.count, row.timesMs.Length);
            Assert.AreEqual(n.count, row.directions.Length);
            if (n.color == "red" || n.color == "blue")
                Assert.AreEqual(n.color, row.hand);
            if (i > 0)
                Assert.GreaterOrEqual(n.time, chart.notes[i - 1].time);
            for (int k = 0; k < n.count; k++)
            {
                double time = n.time +
                    (n.count > 1 ? (double)n.lengthMs * k / (n.count - 1) : 0);
                Assert.AreEqual(time, row.timesMs[k], EpsilonMs);
                Assert.Contains(row.directions[k], new[] { "up", "down" });
                if (n.IsDirection)
                    Assert.AreEqual(n.direction, row.directions[k]);
            }
        }
    }
}
