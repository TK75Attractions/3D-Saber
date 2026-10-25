using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 生成済みのMを実際のローダーで読み、見本とまねの契約を確かめる。
public class BenkyouMemoryChartTests
{
    static readonly int[] Starts =
    {
        0, 16, 32, 48, 64, 80, 96, 120, 136,
        152, 168, 184, 200, 216, 232, 248, 264, 280
    };

    // JSONの小数3桁と、Unityのfloat時刻の丸めを許容する。
    const double TimeToleranceMs = .03;

    ChartData chart;
    ChartData normal;
    ChartData hard;
    ChartTempoMap tempo;

    [SetUp]
    public void LoadRequiredCharts()
    {
        chart = Read("m");
        normal = Read("normal");
        hard = Read("hard");
        tempo = new ChartTempoMap(normal.bpm, normal.beatZeroMs, normal.editorTempoMap);
    }

    static ChartData Read(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs",
            "BenkyouRemaster", "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path),
            "必須の譜面がありません。gen_memory.py --write を実行してください: " + path);
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    static int Cap(int index) => index < 3 ? 3 : index < 7 ? 4 : index < 17 ? 5 : 6;

    static float Hidden(int index) =>
        index == 0 ? 0f : index == 1 ? .35f : index == 2 ? .70f : .95f;

    static float Strength(int index) =>
        index == 0 ? .30f : index == 1 ? .55f : index == 2 ? .80f : .92f;

    static bool FreePart(NoteData note) =>
        (note.beat >= 112 && note.beat < 120) || note.beat >= 296;

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    static string Hand(NoteData note) =>
        note.color == "gold" ? (note.x < 0 ? "blue" : "red") : note.color;

    static NoteData[] Ordered(IEnumerable<NoteData> notes) =>
        notes.OrderBy(n => n.beat).ThenBy(n => n.color)
            .ThenBy(n => n.x).ThenBy(n => n.y)
            .ThenBy(n => n.type).ThenBy(n => n.direction).ToArray();

    NoteData[] Between(int start, int end) =>
        Ordered(chart.notes.Where(n => n.beat >= start && n.beat < end));

    static void SameShape(NoteData a, NoteData b, string message)
    {
        Assert.AreEqual(a.x, b.x, message);
        Assert.AreEqual(a.y, b.y, message);
        Assert.AreEqual(a.color, b.color, message);
        Assert.AreEqual(a.type, b.type, message);
        Assert.AreEqual(a.direction, b.direction, message);
        Assert.AreEqual(a.count, b.count, message);
        Assert.AreEqual(a.lengthMs, b.lengthMs, message);
    }

    [Test]
    public void HeaderAndNotesUseTheAuthoredTimelineAndCoordinateRange()
    {
        Assert.AreEqual(8, chart.displayLevel);
        Assert.AreEqual(160f, chart.bpm);
        Assert.AreEqual(1f, chart.coordScale);
        Assert.AreEqual(0f, chart.offsetMs);
        Assert.AreEqual(24f, chart.beatZeroMs);
        Assert.Greater(chart.notes.Count, 0);
        Assert.IsNotNull(chart.memory);
        Assert.IsTrue(chart.memory.HasContent);

        CollectionAssert.AreEqual(
            normal.timeSignatures.Select(x => JsonUtility.ToJson(x)),
            chart.timeSignatures.Select(x => JsonUtility.ToJson(x)));
        CollectionAssert.AreEqual(
            normal.editorTempoMap.Select(x => JsonUtility.ToJson(x)),
            chart.editorTempoMap.Select(x => JsonUtility.ToJson(x)));

        var sources = normal.notes.Concat(hard.notes).ToArray();
        float minX = sources.Min(n => n.x), maxX = sources.Max(n => n.x);
        float minY = sources.Min(n => n.y), maxY = sources.Max(n => n.y);
        float previous = float.NegativeInfinity;
        foreach (var n in chart.notes)
        {
            Assert.IsTrue(Finite(n.beat) && Finite(n.time)
                && Finite(n.x) && Finite(n.y) && Finite(n.veil));
            Assert.That(n.time, Is.InRange(0f, 137000f));
            Assert.GreaterOrEqual(n.time, previous);
            Assert.That(n.x, Is.InRange(minX, maxX));
            Assert.That(n.y, Is.InRange(minY, maxY));
            Assert.That(n.veil, Is.InRange(0f, 1f));
            Assert.IsTrue(FreePart(n)
                || Starts.Any(s => n.beat >= s && n.beat < s + 16),
                "予定外の区間: " + n.beat);
            previous = n.time;
        }
    }

    [Test]
    public void EighteenVeilsHaveExactWindowsAndTutorialStrengths()
    {
        Assert.AreEqual(18, chart.memory.veils.Count);
        for (int i = 0; i < Starts.Length; i++)
        {
            var v = chart.memory.veils[i];
            Assert.AreEqual(tempo.TimeAt(Starts[i] + 8), v.time, TimeToleranceMs);
            Assert.AreEqual(tempo.TimeAt(Starts[i] + 16), v.endTime, TimeToleranceMs);
            Assert.AreEqual(Strength(i), v.strength, .000001f);
            Assert.Less(v.time, v.endTime);
            if (i > 0)
                Assert.LessOrEqual(chart.memory.veils[i - 1].endTime, v.time);
        }
    }

    [Test]
    public void EveryRepeatCopiesItsSampleEightBeatsLater()
    {
        for (int i = 0; i < Starts.Length; i++)
        {
            int start = Starts[i];
            var sample = Between(start, start + 8);
            var repeat = Between(start + 8, start + 16);
            string label = start + "拍";
            Assert.That(sample.Length, Is.InRange(2, Cap(i)), label);
            Assert.AreEqual(sample.Length, repeat.Length, label);

            for (int j = 0; j < sample.Length; j++)
            {
                var a = sample[j];
                var b = repeat[j];
                SameShape(a, b, label);
                Assert.AreEqual(a.beat + 8, b.beat, .0001f, label);
                Assert.AreEqual(0f, a.veil, label);
                Assert.AreEqual(Hidden(i), b.veil, .000001f, label);

                foreach (var n in new[] { a, b })
                {
                    Assert.AreEqual(tempo.TimeAt(n.beat), n.time, TimeToleranceMs, label);
                    Assert.AreEqual(Math.Round(n.beat * 2), n.beat * 2, .0001, label);
                    Assert.Contains(n.color, new[] { "red", "blue" });
                    Assert.Contains(n.type, new[] { "tap", "direction" });
                    Assert.AreEqual(1, n.count, label);
                    Assert.AreEqual(0f, n.lengthMs, label);
                    if (i < 3)
                    {
                        Assert.AreEqual("tap", n.type, label);
                        Assert.AreEqual("none", n.direction, label);
                    }
                }
            }

            if (i < 3)
            {
                for (int j = 1; j < sample.Length; j++)
                    Assert.Greater(sample[j].time - sample[j - 1].time, 10f, label);
            }
        }
    }

    // 現在の元譜面で、選別後にも0.5拍の候補がある4回。
    [TestCase(152)]
    [TestCase(168)]
    [TestCase(232)]
    [TestCase(264)]
    public void AdvancedSamplesRetainOneOrTwoEighthNotes(int start)
    {
        int count = Between(start, start + 8)
            .Count(n => Math.Abs(n.beat - Math.Round(n.beat)) > .0001);
        Assert.That(count, Is.InRange(1, 2));
    }

    [Test]
    public void CaptionsAreShortOrderedAndDoNotOverlap()
    {
        string[] expected =
        {
            "見本：場所とリズムを覚えて",
            "同じ2小節を、もう一度",
            "ノイズが強くなる",
            "見えなくても、同じ場所・同じリズム",
            "中級",
            "上級",
            "超上級：ゆっくり長い2小節",
            "最後は見えるまま"
        };
        var captions = chart.memory.captions;
        CollectionAssert.AreEqual(expected, captions.Select(c => c.text));
        for (int i = 0; i < captions.Count; i++)
        {
            var c = captions[i];
            Assert.IsTrue(Finite(c.time) && Finite(c.endTime));
            Assert.IsFalse(string.IsNullOrWhiteSpace(c.text));
            Assert.Less(c.time, c.endTime);
            if (i > 0)
                Assert.LessOrEqual(captions[i - 1].endTime, c.time);
        }
        Assert.AreEqual(800f, captions[0].time, TimeToleranceMs);
        Assert.AreEqual(tempo.TimeAt(8) - 150, captions[0].endTime, TimeToleranceMs);
    }

    [Test]
    public void UnveiledPassagesKeepEveryNormalNoteIncludingGold()
    {
        var expected = Ordered(normal.notes.Where(FreePart));
        var actual = Ordered(chart.notes.Where(FreePart));
        Assert.AreEqual(expected.Length, actual.Length);
        Assert.IsTrue(expected.Any(n => n.color == "gold"));
        for (int i = 0; i < expected.Length; i++)
        {
            SameShape(expected[i], actual[i], "見本外: " + expected[i].beat);
            Assert.AreEqual(expected[i].beat, actual[i].beat);
            Assert.AreEqual(expected[i].time, actual[i].time);
            Assert.AreEqual(0f, actual[i].veil);
        }
    }

    [Test]
    public void WholeChartLeavesHandRecoveryAndAtMostTwoSimultaneousNotes()
    {
        // 金は左右の位置で担当を割り当てて検査し、色そのものは変えない。
        foreach (string hand in new[] { "blue", "red" })
        {
            var notes = chart.notes.Where(n => Hand(n) == hand).ToArray();
            for (int i = 1; i < notes.Length; i++)
                Assert.GreaterOrEqual(
                    (double)notes[i].time - notes[i - 1].time,
                    250 - TimeToleranceMs, hand + ": " + notes[i].beat);
        }

        for (int i = 0; i < chart.notes.Count; i++)
        {
            var group = chart.notes.Skip(i)
                .TakeWhile(n => n.time - chart.notes[i].time <= 10f).ToArray();
            Assert.LessOrEqual(group.Length, 2);
            Assert.AreEqual(group.Length, group.Select(Hand).Distinct().Count());
        }
    }

    [Test]
    public void VisibleNotesAvoidVeilsExceptTheFirstTutorialRepeat()
    {
        for (int i = 0; i < chart.memory.veils.Count; i++)
        {
            var v = chart.memory.veils[i];
            foreach (var n in chart.notes.Where(n =>
                n.veil == 0 && n.time >= v.time && n.time < v.endTime))
            {
                // 初回は仕様上veil=0。写しの一致は別テストで全項目を確かめる。
                Assert.IsTrue(i == 0 && n.beat >= 8 && n.beat < 16,
                    "見えるノーツがノイズ区間に入った: " + n.beat);
            }
        }
    }
}
