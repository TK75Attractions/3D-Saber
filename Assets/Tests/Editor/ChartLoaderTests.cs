using NUnit.Framework;

public class ChartLoaderTests
{
    private const string SampleJson = @"{
        ""bpm"": 188.0,
        ""notes"": [
            { ""beat"": 6.5, ""time"": 2074.46, ""x"": 5.64, ""y"": -1.04, ""type"": ""tap"", ""color"": ""default"", ""direction"": ""none"" },
            { ""beat"": 5.0, ""time"": 1595.74, ""x"": -0.66, ""y"": -1.76, ""type"": ""tap"", ""color"": ""default"", ""direction"": ""none"" }
        ]
    }";

    [Test]
    public void Parse_ReadsBpmAndNotes()
    {
        ChartData d = ChartLoader.Parse(SampleJson);
        Assert.AreEqual(188.0f, d.bpm, 0.001f);
        Assert.AreEqual(2, d.notes.Count);
    }

    [Test]
    public void Parse_SortsNotesByTime()
    {
        ChartData d = ChartLoader.Parse(SampleJson);
        Assert.Less(d.notes[0].time, d.notes[1].time);
        Assert.AreEqual(5.0f, d.notes[0].beat, 0.001f);
    }

    [Test]
    public void Parse_TimeSeconds_IsMsDivided()
    {
        ChartData d = ChartLoader.Parse(SampleJson);
        Assert.AreEqual(1.59574, d.notes[0].TimeSeconds, 0.001);
    }

    [Test]
    public void Parse_EmptyString_ReturnsEmptyChart()
    {
        ChartData d = ChartLoader.Parse("");
        Assert.IsNotNull(d);
        Assert.IsNotNull(d.notes);
        Assert.AreEqual(0, d.notes.Count);
    }
}
