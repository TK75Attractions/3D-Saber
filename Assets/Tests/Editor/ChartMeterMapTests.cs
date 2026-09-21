using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;

public class ChartMeterMapTests
{
    static ChartTimeSignature Meter(float beat, int numerator, int denominator) =>
        new ChartTimeSignature { beat = beat, numerator = numerator, denominator = denominator };

    [Test]
    public void MixedMetersKeepFractionalBarStartsAndContinuousMeasureNumbers()
    {
        var map = new ChartMeterMap(new[] { Meter(8, 7, 8), Meter(15, 3, 4) });
        var expected = new[] { 0d, 4, 8, 11.5, 15, 18, 21 };
        CollectionAssert.AreEqual(expected, map.BarStarts(0, 21));
        for (int i = 0; i < expected.Length; i++)
        {
            var position = map.At(expected[i]);
            Assert.AreEqual(i + 1, position.Measure);
            Assert.AreEqual(1, position.Beat);
            Assert.AreEqual(expected[i], position.BarStart);
        }
        Assert.AreEqual(7, map.At(11).Beat);
        Assert.AreEqual(4, map.At(11.5).Measure);
        Assert.AreEqual(8, map.At(11.5).Denominator);
    }

    [Test]
    public void AChangeInsideABarStartsANewMeasureWithoutDuplicateLines()
    {
        var map = new ChartMeterMap(new[] { Meter(5, 5, 8), Meter(10, 4, 4) });
        CollectionAssert.AreEqual(new[] { 0d, 4, 5, 7.5, 10, 14 }, map.BarStarts(0, 14));
        Assert.AreEqual(3, map.At(5).Measure);
        Assert.AreEqual(5, map.At(10).Measure);
        Assert.AreEqual(5, map.AdjacentBar(4, true));
        Assert.AreEqual(4, map.AdjacentBar(5, false));
        Assert.AreEqual(7.5, map.AdjacentBar(8, false));
    }

    [Test]
    public void UnsortedDuplicateAndInvalidChangesHaveADeterministicFallback()
    {
        var map = new ChartMeterMap(new[] { Meter(8, 3, 4), null, Meter(-1, 9, 8),
            Meter(0, 5, 8), Meter(8, 7, 8), Meter(float.NaN, 3, 4), Meter(1, 0, 4), Meter(2, 7, 3) });
        CollectionAssert.AreEqual(new[] { 0d, 2.5, 5, 7.5, 8, 11.5 }, map.BarStarts(0, 12));
        Assert.AreEqual(7, map.At(8).Numerator);
        CollectionAssert.AreEqual(new[] { 0d, 3, 6 }, new ChartMeterMap(null, 3).BarStarts(0, 6));
    }

    [Test]
    public void SerializationAndClonePreserveTheMeterAndRawNoteTimes()
    {
        var doc = new SaberChartDocument { beatZeroMs = 123, offsetMs = -47,
            timeSignatures = new List<ChartTimeSignature> { Meter(0, 7, 8), Meter(7, 4, 4) },
            notes = new List<SaberChartNote> { new SaberChartNote { time = 1878.839f, beat = 3.5f } } };
        var copy = SaberChartUtility.Clone(doc);
        copy.timeSignatures[0].numerator = 5;
        Assert.AreEqual(7, doc.timeSignatures[0].numerator, "履歴間で拍子の参照を共有しない");
        var game = ChartLoader.Parse(SaberChartUtility.ToJson(doc));
        Assert.AreEqual(123, game.beatZeroMs);
        Assert.AreEqual(-47, game.offsetMs);
        Assert.AreEqual(1878.839f, game.notes[0].time);
        CollectionAssert.AreEqual(new[] { 0d, 3.5, 7, 11 }, new ChartMeterMap(game.timeSignatures).BarStarts(0, 11));
    }

    [Test]
    public void WholeBeatSnapCanReachAnEighthNoteBarStart()
    {
        var doc = new SaberChartDocument { timeSignatures = new List<ChartTimeSignature> { Meter(0, 7, 8) } };
        Assert.AreEqual(3.5f, SaberChartUtility.QuantizeBeat(3.51f, 4, doc));
        Assert.AreEqual(4.5f, SaberChartUtility.QuantizeBeat(4.4f, 4, doc));
        Assert.AreEqual("002 : 03 : 00", SaberChartUtility.FormatMusicalPosition(4.5f, doc, 16));
    }

    [Test]
    public void SpawnerUsesMeterOriginAndBothOffsetsExactlyOnce()
    {
        var go = new GameObject("MeterSpawner");
        var root = new GameObject("MeterLines");
        var prefab = new GameObject("MeterLine");
        try
        {
            var spawner = go.AddComponent<BarLineSpawner>();
            spawner.root = root.transform; spawner.barLinePrefab = prefab;
            spawner.approachTime = 1; spawner.spawnZ = 10; spawner.judgeZ = 0;
            var chart = new ChartData { bpm = 120, beatZeroMs = 200, offsetMs = 100,
                timeSignatures = new List<ChartTimeSignature> { Meter(0, 7, 8), Meter(7, 3, 4) },
                notes = new List<NoteData> { new NoteData { time = 6000 } } };
            spawner.SetChart(chart, .05);
            spawner.Tick(-.65);
            Assert.AreEqual(1, spawner.NextIndex);
            Assert.AreEqual(10, root.transform.GetChild(0).position.z, .0001);
            spawner.Tick(.35);
            Assert.AreEqual(0, root.transform.GetChild(0).position.z, .0001);
            spawner.Tick(2.1);
            Assert.AreEqual(2, spawner.NextIndex);
            Assert.AreEqual(0, root.transform.GetChild(root.transform.childCount - 1).position.z, .0001);
            spawner.Tick(3.85);
            Assert.AreEqual(3, spawner.NextIndex);
            spawner.Tick(5.35);
            Assert.AreEqual(4, spawner.NextIndex);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(prefab);
        }
    }
}
