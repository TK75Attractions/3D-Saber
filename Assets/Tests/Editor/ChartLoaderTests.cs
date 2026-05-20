using System.IO;
using NUnit.Framework;
using UnityEngine;

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

    [Test]
    public void LoadFromStreamingAssets_NullDifficulty_FallsBackToLegacyChart()
    {
        // 既存の chart.json が読めるか（既知の ElDorado を使う）
        ChartData d = ChartLoader.LoadFromStreamingAssets("ElDorado");
        Assert.IsNotNull(d);
        Assert.Greater(d.notes.Count, 0);
    }

    [Test]
    public void LoadFromStreamingAssets_NormalDifficulty_LoadsNormalChart()
    {
        ChartData d = ChartLoader.LoadFromStreamingAssets("ElDorado", "Normal");
        Assert.IsNotNull(d);
        Assert.Greater(d.notes.Count, 0);
    }

    [Test]
    public void LoadFromStreamingAssets_EasyDifficulty_HasFewerNotesThanNormal()
    {
        ChartData easy = ChartLoader.LoadFromStreamingAssets("ElDorado", "Easy");
        ChartData normal = ChartLoader.LoadFromStreamingAssets("ElDorado", "Normal");
        Assert.IsNotNull(easy);
        Assert.IsNotNull(normal);
        Assert.Less(easy.notes.Count, normal.notes.Count, "Easy は Normal より少ない");
    }

    [Test]
    public void LoadFromStreamingAssets_HardDifficulty_HasMoreNotesThanNormal()
    {
        ChartData hard = ChartLoader.LoadFromStreamingAssets("ElDorado", "Hard");
        ChartData normal = ChartLoader.LoadFromStreamingAssets("ElDorado", "Normal");
        Assert.IsNotNull(hard);
        Assert.IsNotNull(normal);
        Assert.Greater(hard.notes.Count, normal.notes.Count, "Hard は Normal より多い");
    }

    [Test]
    public void LoadFromStreamingAssets_UnknownDifficulty_FallsBackToLegacy()
    {
        // 存在しない難易度名 → chart.json にフォールバック
        ChartData d = ChartLoader.LoadFromStreamingAssets("ElDorado", "Lunatic");
        Assert.IsNotNull(d);
        Assert.Greater(d.notes.Count, 0);
    }

    [Test]
    public void LoadFromStreamingAssets_CaseInsensitive()
    {
        ChartData lower = ChartLoader.LoadFromStreamingAssets("ElDorado", "easy");
        ChartData upper = ChartLoader.LoadFromStreamingAssets("ElDorado", "EASY");
        Assert.AreEqual(lower.notes.Count, upper.notes.Count);
    }
}
