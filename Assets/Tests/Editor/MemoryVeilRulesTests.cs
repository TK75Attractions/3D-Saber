using NUnit.Framework;

// まねの2小節の間はずっとノイズ(2026-10-25 ユーザー「その二小節の間はずっとノイズかけて」)。
public class MemoryVeilRulesTests
{
    [Test]
    public void TheNoiseCoversTheWholeWindowEvenWhenTheNextSampleIsComing()
    {
        var chart = new ChartData { offsetMs = 200 };
        chart.memory.veils.Add(new MemoryVeilData { time = 1000, endTime = 5000, strength = .8f });
        // 次の見本が区間の終わりちょうどに来ても、区間は削らない(見本はノイズの上に描く)。
        chart.notes.Add(new NoteData { time = 5000, veil = 0 });
        chart.notes.Add(new NoteData { time = 2000, veil = .95f });

        var spans = MemoryVeilRules.BuildIntervals(chart, .2);
        Assert.AreEqual(1, spans.Count);
        Assert.AreEqual(1.2, spans[0].Start, 1e-8);
        Assert.AreEqual(5.2, spans[0].End, 1e-8, "offset は一度だけ足す");
        Assert.AreEqual(.8f, spans[0].Strength);
        Assert.AreEqual(5000, chart.memory.veils[0].endTime, "元の譜面は変更しない");
    }

    [Test]
    public void NotesInsideTheWindowAreRecallsAndTheNextSampleIsOutside()
    {
        var spans = new[] { new MemoryVeilRules.Interval(4, 8, .92f) };
        Assert.IsTrue(MemoryVeilRules.Covers(spans, 4), "区間の頭ちょうどはまね");
        Assert.IsTrue(MemoryVeilRules.Covers(spans, 3.9995), "丸めのずれ(1ms 以内)はまね");
        Assert.IsTrue(MemoryVeilRules.Covers(spans, 7.5));
        Assert.IsFalse(MemoryVeilRules.Covers(spans, 8), "区間の終わりちょうどは次の見本");
        Assert.IsFalse(MemoryVeilRules.Covers(spans, 7.9995), "丸めのずれ(1ms 以内)は次の見本");
        Assert.IsFalse(MemoryVeilRules.Covers(spans, 3));
        Assert.IsFalse(MemoryVeilRules.Covers(null, 5));
        Assert.IsFalse(MemoryVeilRules.Covers(spans, double.NaN));
    }

    [Test]
    public void OmitsLessThanPointThreeSecondsAndKeepsTheBoundary()
    {
        var chart = new ChartData();
        chart.memory.veils.Add(new MemoryVeilData { time = 4000, endTime = 4300 });
        chart.memory.veils.Add(new MemoryVeilData { time = 1000, endTime = 1299 });
        chart.memory.veils.Add(new MemoryVeilData { time = 2000, endTime = 2300 });

        var spans = MemoryVeilRules.BuildIntervals(chart, 0);
        Assert.AreEqual(2, spans.Count);
        Assert.AreEqual(2, spans[0].Start);
        Assert.AreEqual(4, spans[1].Start);
    }

    [Test]
    public void StrengthStaysOnUntilTheEndAndFadesOutExactlyThere()
    {
        var spans = new[] { new MemoryVeilRules.Interval(1, 3, .8f) };
        Assert.AreEqual(0, MemoryVeilRules.StrengthAt(spans, .9));
        Assert.AreEqual(0, MemoryVeilRules.StrengthAt(spans, 1));
        Assert.AreEqual(.4f, MemoryVeilRules.StrengthAt(spans, 1.06), 1e-6);
        Assert.AreEqual(.8f, MemoryVeilRules.StrengthAt(spans, 1.12), 1e-6);
        Assert.AreEqual(.8f, MemoryVeilRules.StrengthAt(spans, 2.85), 1e-6, "終わりの 0.15 秒前までは全部の強さ");
        Assert.AreEqual(.4f, MemoryVeilRules.StrengthAt(spans, 2.925), 1e-6);
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
