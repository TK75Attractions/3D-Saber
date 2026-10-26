using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// SはHardの配置を保ち、枠が見える間の変速を譜面側で禁止する。
public class LogicSpeedChartTests
{
    const double Approach = 1, Cue = .7, Frame = 1.0 / 30, Epsilon = 1e-5;
    const double LongCamera = .12;

    static ChartData Read(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "logic", "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path), path);
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    [Test]
    public void NotesAndTimingAreExactlyTheHardChart()
    {
        var chart = Read("s");
        var hard = Read("hard");
        Assert.AreEqual(9, chart.displayLevel);
        Assert.AreEqual(424, chart.notes.Count);
        Assert.AreEqual(hard.notes.Count, chart.notes.Count);
        for (int i = 0; i < hard.notes.Count; i++)
            Assert.AreEqual(JsonUtility.ToJson(hard.notes[i]), JsonUtility.ToJson(chart.notes[i]), "note " + i);
        Assert.AreEqual(hard.bpm, chart.bpm);
        Assert.AreEqual(hard.beatZeroMs, chart.beatZeroMs);
        Assert.AreEqual(hard.offsetMs, chart.offsetMs);
        CollectionAssert.AreEqual(hard.editorTempoMap.Select(p => JsonUtility.ToJson(p)).ToArray(),
            chart.editorTempoMap.Select(p => JsonUtility.ToJson(p)).ToArray());
        CollectionAssert.AreEqual(hard.timeSignatures.Select(p => JsonUtility.ToJson(p)).ToArray(),
            chart.timeSignatures.Select(p => JsonUtility.ToJson(p)).ToArray());
        Assert.AreEqual(0, hard.scroll.Count);
        Assert.AreEqual(22, chart.scroll.Count);
        Assert.AreSame(RushESecretState.LogicSpeed, RushESecretState.ForDifficulty("S"));
    }

    [Test]
    public void EveryCueGetsItsWholeRealTimeWindowEvenOneFrameLate()
    {
        var chart = Read("s");
        var map = ScrollSpeedMap.Build(chart.scroll);
        Assert.NotNull(map);
        foreach (var p in chart.scroll)
        {
            Assert.That(p.speed, Is.InRange(0f, 2f));
            Assert.AreEqual(1f, p.ease, "手前で曲げない");
        }
        foreach (var n in chart.notes)
        {
            double hit = n.TimeSeconds;
            double spawn = map.TimeAtPosition(map.PositionAt(hit) - Approach);
            Assert.GreaterOrEqual(hit - spawn - Frame, Cue - Epsilon, hit + "s");
            Assert.GreaterOrEqual(map.SpeedAt(hit - .001), .3);
            // 残り時間と距離の比は、枠の表示中ずっと一定。
            double v = map.SpeedAt(hit - .001);
            foreach (double remaining in new[] { .7, .35, .1, 0 })
            {
                Assert.AreEqual(v * remaining, map.Remaining(hit - remaining, hit), Epsilon, hit + "s");
                Assert.AreEqual(map.Remaining(hit - remaining, hit),
                    map.Shape(map.Remaining(hit - remaining, hit), Approach, hit - remaining), Epsilon);
            }
        }
    }

    [Test]
    public void ChangesAvoidCuesAndLongCutCompletion()
    {
        var chart = Read("s");
        double previousSpeed = 1, previousEnd = double.NegativeInfinity;
        foreach (var p in chart.scroll)
        {
            double from = p.time / 1000.0, to = from + p.ramp / 1000.0;
            Assert.GreaterOrEqual(from, previousEnd - Epsilon, "rampを重ねない");
            Assert.GreaterOrEqual(p.ramp, 0);
            previousEnd = to;
            if (Math.Abs(previousSpeed - p.speed) > Epsilon)
                foreach (var n in chart.notes)
                {
                    double begin = n.TimeSeconds - Cue - Frame;
                    double end = n.TimeSeconds + n.LingerSeconds(NoteSpawner.DefaultSecondsPerLongCut)
                        + JudgmentTierHelper.LateBadSeconds + (n.count > 1 ? LongCamera : 0);
                    Assert.IsFalse(from < end - Epsilon && to > begin + Epsilon,
                        n.TimeSeconds + "sの保護区間で変速: " + from);
                }
            previousSpeed = p.speed;
        }
    }

    [Test]
    public void ThreeQuietStopsHaveDistantNotesAndEnoughRunUp()
    {
        var chart = Read("s");
        var map = ScrollSpeedMap.Build(chart.scroll);
        double[,] expected = { {68.903, 69.403}, {100.803, 101.800}, {141.853, 142.153} };
        double[,] quiet = { {68.90, 70.12}, {100.40, 101.80}, {141.42, 142.62} };
        int stopIndex = 0;
        for (int i = 0; i < chart.scroll.Count; i++)
        {
            var p = chart.scroll[i];
            if (p.speed != 0) continue;
            Assert.Less(stopIndex, 3);
            Assert.Less(i + 1, chart.scroll.Count);
            Assert.AreEqual(0f, p.ramp);
            double from = p.time / 1000.0, to = chart.scroll[i + 1].time / 1000.0;
            Assert.AreEqual(expected[stopIndex, 0], from, Epsilon);
            Assert.AreEqual(expected[stopIndex, 1], to, Epsilon);
            Assert.GreaterOrEqual(from, quiet[stopIndex, 0] - Epsilon);
            Assert.LessOrEqual(to, quiet[stopIndex, 1] + Epsilon);
            Assert.IsFalse(chart.notes.Any(n => n.TimeSeconds >= from && n.TimeSeconds <= to));
            var frozen = chart.notes.Where(n => n.TimeSeconds > to &&
                map.TimeAtPosition(map.PositionAt(n.TimeSeconds) - Approach) <= from - Frame).ToArray();
            Assert.That(frozen.Length, Is.InRange(1, 2));
            foreach (var n in frozen)
            {
                Assert.That(20 * map.Remaining(from, n.TimeSeconds), Is.InRange(10.0, 19.0));
                Assert.GreaterOrEqual(n.TimeSeconds - to, Cue + Frame - Epsilon);
            }
            Assert.AreEqual(map.FlowTime(from + .05), map.FlowTime(to - .05), Epsilon);
            stopIndex++;
        }
        Assert.AreEqual(3, stopIndex);
    }

    [Test]
    public void ReadableTargetsDoNotPileUp()
    {
        var chart = Read("s");
        var map = ScrollSpeedMap.Build(chart.scroll);
        var events = new List<(double time, int delta)>();
        foreach (var n in chart.notes)
        {
            events.Add((map.TimeAtPosition(map.PositionAt(n.TimeSeconds) - Approach), 1));
            events.Add((n.TimeSeconds + n.LingerSeconds(NoteSpawner.DefaultSecondsPerLongCut)
                + JudgmentTierHelper.LateBadSeconds + (n.count > 1 ? LongCamera : 0), -1));
        }
        int live = 0, peak = 0;
        foreach (var e in events.OrderBy(e => e.time).ThenBy(e => e.delta))
        {
            live += e.delta;
            peak = Math.Max(peak, live);
        }
        Assert.LessOrEqual(peak, 6, "ミス後の退場を除く、読む対象の密度");
    }
}
