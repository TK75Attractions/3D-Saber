using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Saber.ChartEditor;

// 譜面チェック(SaberChartLint)の規則を、小さな作り物の譜面で確かめる。
// 実際の曲の譜面は人が編集するので、「今の譜面がエラー0件」はテストにしない。
public class ChartLintTests
{
    static SaberChartNote Note(float time, float x, string color, string type = "tap", string direction = "none",
        int count = 1, float lengthMs = 0f, float y = 0f) => new SaberChartNote
        { time = time, x = x, y = y, color = color, type = type, direction = direction, count = count, lengthMs = lengthMs };

    static SaberChartDocument Chart(params SaberChartNote[] notes) =>
        new SaberChartDocument { bpm = 120, notes = notes.ToList() };

    static List<SaberChartIssue> Issues(SaberChartDocument chart, string rule, string difficulty = "normal", float audioMs = 0f) =>
        SaberChartLint.Check(chart, difficulty, audioMs).Where(i => i.rule == rule).ToList();

    [Test]
    public void EmptyChartHasNoIssues()
    {
        Assert.IsEmpty(SaberChartLint.Check(Chart(), "hard"));
        Assert.IsEmpty(SaberChartLint.Check(null, "hard"));
    }

    [Test]
    public void SameHandNoteDuringALongIsAnError()
    {
        var chart = Chart(Note(1000, -1, "blue", "long", count: 3, lengthMs: 1000), Note(1500, -1.5f, "blue"), Note(1500, 1, "red"));
        var issue = Issues(chart, "long-overlap").Single();
        Assert.AreEqual(SaberChartIssueLevel.Error, issue.level);
        CollectionAssert.AreEqual(new[] { 0, 1 }, issue.notes, "ロングと、その途中の同じ手のノーツ");
        Assert.IsEmpty(Issues(Chart(Note(1000, -1, "blue", "long", count: 3, lengthMs: 1000), Note(2100, -1, "blue")), "long-overlap"),
            "ロングが終わったあとの同じ手は問題なし");
    }

    [Test]
    public void InwardSimultaneousSwingsCollide()
    {
        var clap = Chart(Note(1000, -.5f, "blue", "direction", "right"), Note(1000, .5f, "red", "direction", "left"));
        Assert.AreEqual(SaberChartIssueLevel.Error, Issues(clap, "handclap").Single().level);
        var outward = Chart(Note(1000, -.5f, "blue", "direction", "left"), Note(1000, .5f, "red", "direction", "right"));
        Assert.IsEmpty(Issues(outward, "handclap"));
    }

    [TestCase("normal", SaberChartIssueLevel.Warning)]
    [TestCase("hard", SaberChartIssueLevel.Info)]
    public void CrossedHandsDependOnDifficulty(string difficulty, SaberChartIssueLevel expected)
    {
        var chart = Chart(Note(1000, 1, "blue"), Note(1000, -1, "red"));
        Assert.AreEqual(expected, Issues(chart, "crossed", difficulty).Single().level);
    }

    [Test]
    public void SameHandSimultaneousNotesTooFarApartWarn()
    {
        Assert.AreEqual(SaberChartIssueLevel.Warning, Issues(Chart(Note(1000, -2.5f, "red"), Note(1000, 1, "red")), "same-hand-far").Single().level);
        Assert.IsEmpty(Issues(Chart(Note(1000, -.5f, "red"), Note(1000, .5f, "red")), "same-hand-far"), "1振りで届く距離");
    }

    [Test]
    public void FastSameHandIsAWarningUnlessItCanBeSwept()
    {
        var sameSpot = Chart(Note(1000, 1, "red"), Note(1100, 1, "red"));
        Assert.AreEqual(SaberChartIssueLevel.Warning, Issues(sameSpot, "fast-same-hand", "hard").Single().level, "同じ位置の速い連打は振り返せない");
        var adjacent = Chart(Note(1000, 0, "red"), Note(1100, 1, "red"));
        Assert.IsEmpty(Issues(adjacent, "fast-same-hand", "hard"));
        Assert.AreEqual(SaberChartIssueLevel.Info, Issues(adjacent, "sweep", "hard").Single().level, "隣へなぞれば本編で1振りに切れる");
        var againstArrow = Chart(Note(1000, 0, "red", "direction", "left"), Note(1100, 1, "red", "direction", "left"));
        Assert.AreEqual(1, Issues(againstArrow, "fast-same-hand", "hard").Count, "矢印がなぞる向きと逆なら、なぞりにならない");
    }

    [TestCase("easy", 1)]
    [TestCase("normal", 1)]
    [TestCase("hard", 0)]
    public void ModeratelyFastSameHandOnlyWarnsBelowHard(string difficulty, int expected)
    {
        var chart = Chart(Note(1000, 1, "red"), Note(1200, 1.5f, "red"));
        Assert.AreEqual(expected, Issues(chart, "fast-same-hand", difficulty).Count);
    }

    [Test]
    public void SameDirectionArrowsNeedTimeToReset()
    {
        var reset = Chart(Note(1000, 1, "red", "direction", "down"), Note(1300, 1.5f, "red", "direction", "down"));
        Assert.AreEqual(SaberChartIssueLevel.Warning, Issues(reset, "reset", "hard").Single().level);
        Assert.IsEmpty(Issues(Chart(Note(1000, 1, "red", "direction", "down"), Note(1300, 1, "red", "direction", "up")), "reset", "hard"),
            "上下の往復は振り直しが要らない");
        Assert.IsEmpty(Issues(Chart(Note(1000, 1, "red", "direction", "down"), Note(1500, 1, "red", "direction", "down")), "reset", "hard"),
            "0.4秒あれば戻せる");
    }

    [Test]
    public void LongArrowRunsAreReported()
    {
        var notes = Enumerable.Range(0, 4).Select(k => Note(1000 + k * 500, 1, "red", "direction", k % 2 == 0 ? "down" : "up")).ToArray();
        var issue = Issues(Chart(notes), "arrow-run", "hard").Single();
        Assert.AreEqual(SaberChartIssueLevel.Info, issue.level);
        Assert.AreEqual(4, issue.notes.Length);
        Assert.IsEmpty(Issues(Chart(notes.Take(3).ToArray()), "arrow-run", "hard"));
    }

    [Test]
    public void GoldNotesAreNotTiedToOneHand()
    {
        var chart = Chart(Note(1000, -1, "gold", "long", count: 3, lengthMs: 1000), Note(1200, -1, "gold"), Note(1250, -1, "gold"));
        Assert.IsEmpty(Issues(chart, "long-overlap"));
        Assert.IsEmpty(Issues(chart, "fast-same-hand", "easy"));
    }

    [Test]
    public void FormatProblemsAreReportedPerNote()
    {
        var chart = Chart(Note(-50, 0, "red"), Note(500, 0, "blue", "direction"), Note(500, 0, "blue", "direction"),
            Note(900, 4, "red"), Note(9000, 0, "red"));
        Assert.AreEqual(1, Issues(chart, "before-start").Count);
        Assert.AreEqual(2, Issues(chart, "no-arrow").Count);
        CollectionAssert.AreEqual(new[] { 1, 2 }, Issues(chart, "duplicate").Single().notes);
        Assert.AreEqual(SaberChartIssueLevel.Warning, Issues(chart, "outside").Single().level);
        Assert.AreEqual(1, Issues(chart, "after-end", "normal", 5000).Count);
        Assert.IsEmpty(Issues(chart, "after-end"), "音源の長さが分からないときは調べない");
    }

    [Test]
    public void NearlyOverlappingNotesAndMisalignedChordsAreReported()
    {
        var chart = Chart(Note(1000, 0, "red"), Note(1015, .3f, "blue"), Note(2000, -1, "blue"), Note(2018, 1, "red"));
        Assert.AreEqual(SaberChartIssueLevel.Warning, Issues(chart, "overlap", "hard").Single().level, "15ms差・距離0.3は重なって見える");
        Assert.AreEqual(2, Issues(chart, "chord-offset", "hard").Count, "15ms と 18ms ずれた同時押しは、本編で同時線が出ない");
        Assert.IsEmpty(Issues(Chart(Note(1000, -1, "blue"), Note(1005, 1, "red")), "chord-offset", "hard"), "10ms 以内なら同時線が出る");
        Assert.IsEmpty(Issues(chart, "duplicate", "hard"), "完全に同じ時刻・位置のものだけがエラー");
    }

    [Test]
    public void LongNotesThatMustBeCutTooFastAreReported()
    {
        Assert.AreEqual(SaberChartIssueLevel.Warning, Issues(Chart(Note(1000, 0, "red", "long", count: 10, lengthMs: 500)), "long-rate").Single().level);
        Assert.IsEmpty(Issues(Chart(Note(1000, 0, "red", "long", count: 7, lengthMs: 2000)), "long-rate"), "1秒に3回(今の譜面の最大)は問題なし");
    }

    [Test]
    public void SimultaneousGroupingMatchesTheGame()
    {
        var groups = SaberChartLint.GroupSimultaneous(new[] { Note(1000, 0, "red"), Note(1024, 1, "blue"), Note(1030, 0, "red"), null });
        Assert.AreEqual(2, groups.Count, "25ms 以内は同じ組(1030 は最初から30msなので別の組)");
        Assert.AreEqual(2, groups[0].Count);
    }

    [Test]
    public void DensityPeakAboveTheCurrentMaximumIsReported()
    {
        var notes = Enumerable.Range(0, 10).Select(k => Note(1000 + k * 150, k % 2 == 0 ? -1 : 1, k % 2 == 0 ? "blue" : "red")).ToArray();
        Assert.AreEqual(SaberChartIssueLevel.Info, Issues(Chart(notes), "density", "normal").Single().level, "Normal の上限9個を越える");
        Assert.IsEmpty(Issues(Chart(notes), "density", "hard"));
    }

    [Test]
    public void IssuesAreSortedByTimeAndDescribedForTheAi()
    {
        var chart = Chart(Note(3000, -.5f, "blue", "direction", "right"), Note(3000, .5f, "red", "direction", "left"), Note(-200, 0, "red"));
        chart.offsetMs = 100;
        var issues = SaberChartLint.Check(chart, "hard");
        CollectionAssert.AreEqual(new[] { "before-start", "handclap" }, issues.Select(i => i.rule).ToArray(), "時刻順");
        StringAssert.Contains("3.100秒", SaberChartLint.Describe(chart, issues.First(i => i.rule == "handclap")), "音源上の秒で示す");
    }

    [Test]
    public void FingerprintChangesOnlyWhenTheContentChanges()
    {
        var chart = Chart(Note(1000, 0, "red"));
        int before = SaberChartLint.Fingerprint(chart);
        Assert.AreEqual(before, SaberChartLint.Fingerprint(SaberChartUtility.Clone(chart)));
        chart.notes[0].x = .5f;
        Assert.AreNotEqual(before, SaberChartLint.Fingerprint(chart));
    }
}
