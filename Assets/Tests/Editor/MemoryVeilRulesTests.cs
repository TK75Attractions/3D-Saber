using NUnit.Framework;

public class MemoryVeilRulesTests
{
    [Test]
    public void ClipsBeforeTheNextSampleAppearsAndAppliesOffsetOnlyOnce()
    {
        var chart = new ChartData { offsetMs = 200 };
        chart.memory.veils.Add(new MemoryVeilData { time = 1000, endTime = 5000, strength = .8f });
        // 並び順に依存しない。区間より前の見えるノーツ・隠れたノーツは次の見本に数えない。
        chart.notes.Add(new NoteData { time = 5500, veil = 0 });
        chart.notes.Add(new NoteData { time = 500, veil = 0 });
        chart.notes.Add(new NoteData { time = 2000, veil = .7f });

        var spans = MemoryVeilRules.BuildIntervals(chart, .2, 1);
        Assert.AreEqual(1, spans.Count);
        Assert.AreEqual(1.2, spans[0].Start, 1e-8);
        // 次の見本(5.5秒)が奥に出る 4.5秒までに消し切る。+ offset 0.2秒。
        Assert.AreEqual(4.7, spans[0].End, 1e-8);
        Assert.AreEqual(.8f, spans[0].Strength);
        Assert.AreEqual(5000, chart.memory.veils[0].endTime, "元の譜面は変更しない");
    }

    [Test]
    public void VisibleNotesInsideTheWindowDoNotShortenIt()
    {
        // 説明の1回目のまねは veil=0 のまま区間の中にある。区間を削らない。
        var chart = new ChartData();
        chart.memory.veils.Add(new MemoryVeilData { time = 1000, endTime = 5000, strength = .3f });
        chart.notes.Add(new NoteData { time = 1000, veil = 0 });
        chart.notes.Add(new NoteData { time = 4000, veil = 0 });
        chart.notes.Add(new NoteData { time = 7000, veil = 0 });

        var spans = MemoryVeilRules.BuildIntervals(chart, 0, 1);
        Assert.AreEqual(1, spans.Count);
        Assert.AreEqual(1, spans[0].Start, 1e-8);
        Assert.AreEqual(5, spans[0].End, 1e-8);
    }

    [Test]
    public void ASampleRightAfterTheWindowCountsEvenWithRoundedTimes()
    {
        // 次の見本が区間の終わりちょうど(小数3桁の丸めで 0.4ms 早い)でも次の見本として扱う。
        var chart = new ChartData();
        chart.memory.veils.Add(new MemoryVeilData { time = 1000, endTime = 5000 });
        chart.notes.Add(new NoteData { time = 4999.6f, veil = 0 });

        var spans = MemoryVeilRules.BuildIntervals(chart, 0, 1);
        Assert.AreEqual(1, spans.Count);
        Assert.AreEqual(3.9996, spans[0].End, 1e-6);
    }

    [Test]
    public void ApproachSettingChangesTheEnd()
    {
        var chart = new ChartData();
        chart.memory.veils.Add(new MemoryVeilData { time = 1000, endTime = 5000 });
        chart.notes.Add(new NoteData { time = 5500 });

        var fast = MemoryVeilRules.BuildIntervals(chart, 0, 1);
        var slow = MemoryVeilRules.BuildIntervals(chart, 0, 2);
        Assert.AreEqual(4.5, fast[0].End, 1e-8);
        Assert.AreEqual(3.5, slow[0].End, 1e-8);
        Assert.Less(slow[0].End, fast[0].End);
    }

    [Test]
    public void KeepsTheAuthoredEndWhenItIsEarlier()
    {
        var chart = new ChartData();
        chart.memory.veils.Add(new MemoryVeilData { time = 1000, endTime = 2000 });
        chart.notes.Add(new NoteData { time = 4000 });
        Assert.AreEqual(2, MemoryVeilRules.BuildIntervals(chart, 0, 1)[0].End);
    }

    [Test]
    public void OmitsLessThanPointThreeSecondsAndKeepsTheBoundary()
    {
        var chart = new ChartData();
        chart.memory.veils.Add(new MemoryVeilData { time = 4000, endTime = 4300 });
        chart.memory.veils.Add(new MemoryVeilData { time = 1000, endTime = 1299 });
        chart.memory.veils.Add(new MemoryVeilData { time = 2000, endTime = 2300 });

        var spans = MemoryVeilRules.BuildIntervals(chart, 0, 1);
        Assert.AreEqual(2, spans.Count);
        Assert.AreEqual(2, spans[0].Start);
        Assert.AreEqual(4, spans[1].Start);
    }

    [Test]
    public void ASampleTooSoonAfterTheWindowRemovesIt()
    {
        // 次の見本が早すぎて、出る前に消す時間(0.3秒)が残らない区間は出さない。
        var chart = new ChartData();
        chart.memory.veils.Add(new MemoryVeilData { time = 1000, endTime = 1500 });
        chart.notes.Add(new NoteData { time = 1600, veil = 0 });
        Assert.IsEmpty(MemoryVeilRules.BuildIntervals(chart, 0, 1));
    }

    [Test]
    public void StrengthFadesSmoothlyAndIsZeroOutside()
    {
        var spans = new[] { new MemoryVeilRules.Interval(1, 3, .8f) };
        Assert.AreEqual(0, MemoryVeilRules.StrengthAt(spans, .9));
        Assert.AreEqual(0, MemoryVeilRules.StrengthAt(spans, 1));
        Assert.AreEqual(.4f, MemoryVeilRules.StrengthAt(spans, 1.06), 1e-6);
        Assert.AreEqual(.8f, MemoryVeilRules.StrengthAt(spans, 1.12), 1e-6);
        Assert.AreEqual(.8f, MemoryVeilRules.StrengthAt(spans, 2), 1e-6);
        Assert.AreEqual(.4f, MemoryVeilRules.StrengthAt(spans, 2.875), 1e-6);
        Assert.AreEqual(0, MemoryVeilRules.StrengthAt(spans, 3));
        Assert.AreEqual(0, MemoryVeilRules.StrengthAt(spans, 4));
        Assert.AreEqual(0, MemoryVeilRules.StrengthAt(null, 2));
        Assert.AreEqual(0, MemoryVeilRules.StrengthAt(spans, double.NaN));
    }

    [Test]
    public void OverlapsUseTheMaximumRatherThanAddingBrightness()
    {
        var spans = new[]
        {
            new MemoryVeilRules.Interval(0, 4, .3f),
            new MemoryVeilRules.Interval(1, 3, .7f)
        };
        Assert.AreEqual(.7f, MemoryVeilRules.StrengthAt(spans, 2), 1e-6);
    }
}
