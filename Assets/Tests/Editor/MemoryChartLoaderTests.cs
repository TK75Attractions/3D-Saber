using System.Linq;
using NUnit.Framework;
using UnityEngine;

// memoryとveilの正規化だけを検査する。シーンや本編の描画は作らない。
public class MemoryChartLoaderTests
{
    static MemoryVeilData Veil(float time, float endTime, float strength = .5f) =>
        new MemoryVeilData { time = time, endTime = endTime, strength = strength };

    static MemoryCaptionData Caption(float time, float endTime, string text) =>
        new MemoryCaptionData { time = time, endTime = endTime, text = text };

    static ChartData Parse(ChartData source) =>
        ChartLoader.Parse(JsonUtility.ToJson(source));

    [Test]
    public void Parse_DiscardsNonFiniteEmptyAndReversedMemoryIntervals()
    {
        var source = new ChartData();
        source.memory.veils.Add(Veil(300, 400));
        source.memory.veils.Add(Veil(-100, 0));
        source.memory.captions.Add(Caption(300, 400, "後"));
        source.memory.captions.Add(Caption(-100, 0, "前"));

        var invalid = new[]
        {
            (float.NaN, 10f),
            (0f, float.NaN),
            (float.PositiveInfinity, 10f),
            (float.NegativeInfinity, 10f),
            (0f, float.PositiveInfinity),
            (0f, float.NegativeInfinity),
            (20f, 20f),
            (30f, 20f)
        };
        foreach (var interval in invalid)
        {
            source.memory.veils.Add(Veil(interval.Item1, interval.Item2));
            source.memory.captions.Add(
                Caption(interval.Item1, interval.Item2, "捨てる字幕"));
        }

        // NaNの表記はUnity自身に任せ、有効な要素まで失われていないことも検査する。
        var parsed = Parse(source);
        CollectionAssert.AreEqual(
            new[] { -100f, 300f }, parsed.memory.veils.Select(v => v.time));
        CollectionAssert.AreEqual(
            new[] { 0f, 400f }, parsed.memory.veils.Select(v => v.endTime));
        CollectionAssert.AreEqual(
            new[] { -100f, 300f }, parsed.memory.captions.Select(c => c.time));
        CollectionAssert.AreEqual(
            new[] { 0f, 400f }, parsed.memory.captions.Select(c => c.endTime));
        CollectionAssert.AreEqual(
            new[] { "前", "後" }, parsed.memory.captions.Select(c => c.text));
        Assert.IsTrue(parsed.memory.HasContent);
    }

    [TestCase(-3f, 0f)]
    [TestCase(3f, 1f)]
    [TestCase(0f, 0f)]
    [TestCase(1f, 1f)]
    [TestCase(.35f, .35f)]
    [TestCase(float.NaN, 0f)]
    [TestCase(float.PositiveInfinity, 0f)]
    [TestCase(float.NegativeInfinity, 0f)]
    public void Parse_ClampsStrengthAndNoteVeilWithoutChangingTiming(float value, float expected)
    {
        var source = new ChartData { offsetMs = 123f };
        source.notes.Add(new NoteData { time = 150f, x = 1f, y = -.2f, veil = value });
        source.memory.veils.Add(Veil(100f, 200f, value));

        var parsed = Parse(source);
        Assert.AreEqual(1, parsed.notes.Count);
        Assert.AreEqual(1, parsed.memory.veils.Count);
        Assert.AreEqual(expected, parsed.notes[0].veil, .000001f);
        Assert.AreEqual(expected, parsed.memory.veils[0].strength, .000001f);
        Assert.AreEqual(150f, parsed.notes[0].time);
        Assert.AreEqual(100f, parsed.memory.veils[0].time);
        Assert.AreEqual(200f, parsed.memory.veils[0].endTime);
        Assert.AreEqual(123f, parsed.offsetMs, "offsetMsは再生側で一度だけ足す");
    }

    [Test]
    public void Parse_SortsStablyAndDiscardsOnlyBlankCaptions()
    {
        var source = new ChartData();
        source.notes.Add(new NoteData { time = 300, veil = .3f });
        source.notes.Add(new NoteData { time = 100, veil = .1f });
        source.notes.Add(new NoteData { time = 100, veil = .2f });

        source.memory.veils.Add(Veil(300, 400, .3f));
        source.memory.veils.Add(Veil(100, 150, .1f));
        source.memory.veils.Add(Veil(100, 200, .2f));

        source.memory.captions.Add(Caption(300, 400, "後"));
        source.memory.captions.Add(Caption(100, 150, " 先 "));
        source.memory.captions.Add(Caption(100, 200, "同時"));
        source.memory.captions.Add(Caption(10, 20, ""));
        source.memory.captions.Add(Caption(20, 30, " \t\n "));
        source.memory.captions.Add(Caption(30, 40, "　"));
        source.memory.captions.Add(Caption(40, 50, null));

        var parsed = Parse(source);
        CollectionAssert.AreEqual(
            new[] { 100f, 100f, 300f }, parsed.notes.Select(n => n.time));
        CollectionAssert.AreEqual(
            new[] { .1f, .2f, .3f }, parsed.notes.Select(n => n.veil));
        CollectionAssert.AreEqual(
            new[] { 100f, 100f, 300f }, parsed.memory.veils.Select(v => v.time));
        CollectionAssert.AreEqual(
            new[] { .1f, .2f, .3f }, parsed.memory.veils.Select(v => v.strength));
        CollectionAssert.AreEqual(
            new[] { 100f, 100f, 300f }, parsed.memory.captions.Select(c => c.time));
        CollectionAssert.AreEqual(
            new[] { " 先 ", "同時", "後" }, parsed.memory.captions.Select(c => c.text));
    }

    [TestCase("")]
    [TestCase("{}")]
    [TestCase(@"{""memory"":null}")]
    [TestCase(@"{""memory"":{}}")]
    [TestCase(@"{""memory"":{""veils"":null,""captions"":null}}")]
    [TestCase(@"{""memory"":{""veils"":[],""captions"":[]}}")]
    public void Parse_MissingOrNullMemoryBecomesEmptyContainers(string json)
    {
        var parsed = ChartLoader.Parse(json);
        Assert.IsNotNull(parsed.memory);
        Assert.IsNotNull(parsed.memory.veils);
        Assert.IsNotNull(parsed.memory.captions);
        Assert.IsEmpty(parsed.memory.veils);
        Assert.IsEmpty(parsed.memory.captions);
        Assert.IsFalse(parsed.memory.HasContent);
    }

    [Test]
    public void Parse_LegacyNoteWithoutMemoryOrVeilRemainsVisible()
    {
        var parsed = ChartLoader.Parse(
            @"{""bpm"":160,""notes"":[{""beat"":2,""time"":774,""x"":1,""y"":0}]}");

        Assert.AreEqual(1, parsed.notes.Count);
        Assert.AreEqual(774f, parsed.notes[0].time);
        Assert.AreEqual(0f, parsed.notes[0].veil);
        Assert.IsNotNull(parsed.memory);
        Assert.IsEmpty(parsed.memory.veils);
        Assert.IsEmpty(parsed.memory.captions);
        Assert.IsFalse(parsed.memory.HasContent);
    }
}
