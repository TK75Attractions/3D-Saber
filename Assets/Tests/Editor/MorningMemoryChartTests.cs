using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// Morning の裏譜面 M(覚える)。生成済みの chart_m.json を本物のローダーで読み、見本とまねの決まりを確かめる。
// 2026-10-25 勉強(テンポが変わる)から 118BPM 一定の Morning へ移した。
// 2026-10-26 ユーザー「下に出る解説いらないから消して」で字幕なし、「後半に面白みがないから直してもっとむずくしてもいい」で
// 後半(160拍〜)は見本4小節 → まね4小節(上級3回・超上級2回)。最後は超上級のまねで曲が終わる。
// 譜面は C:\Users\shike\dev\tools\ChartMorningMemory\gen_memory.py --write で作る。
public class MorningMemoryChartTests
{
    // (見本の頭の拍, 見本とまねそれぞれの拍数)
    static readonly (int start, int half)[] Loops =
    {
        (16, 8), (32, 8), (48, 8),
        (64, 8), (80, 8), (96, 8), (112, 8), (128, 8), (144, 8),
        (160, 16), (192, 16), (224, 16),
        (256, 16), (288, 16),
    };
    const double Bpm = 118, Beat0Ms = 524;
    const double TimeToleranceMs = .03;

    ChartData chart, normal;

    [SetUp]
    public void Load()
    {
        chart = Read("m");
        normal = Read("normal");
    }

    static ChartData Read(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "Morning", "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path), "必須の譜面がありません。gen_memory.py --write を実行してください: " + path);
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    static double TimeAt(double beat) => Beat0Ms + beat * 60000.0 / Bpm;
    static string Stage(int i) => i < 3 ? "初級" : i < 9 ? "中級" : i < 12 ? "上級" : "超上級";
    static int Cap(int i) => i < 3 ? 3 : i < 9 ? 4 : i < 12 ? 8 : 10;
    static int ArrowLimit(int i) => i < 3 ? 0 : i < 9 ? int.MaxValue : i < 12 ? 2 : 4;
    static float Hidden(int i) => i == 0 ? 0f : i == 1 ? .35f : i == 2 ? .70f : .95f;
    static float Strength(int i) => i == 0 ? .30f : i == 1 ? .55f : i == 2 ? .80f : .92f;
    // Normal の拍は 15.9996 のような丸めのずれを含むので、道具と同じく 1/4 拍へそろえてから区間を見る。
    static double Snap(double beat) { double q = Math.Round(beat * 4) / 4; return Math.Abs(beat - q) <= .01 ? q : beat; }
    static bool Intro(NoteData n) => Snap(n.beat) < 16;

    NoteData[] Between(double start, double end) => chart.notes.Where(n => Snap(n.beat) >= start && Snap(n.beat) < end)
        .OrderBy(n => n.beat).ThenBy(n => n.color).ThenBy(n => n.x).ThenBy(n => n.y).ToArray();

    [Test]
    public void TheTempoNeverChangesAndThereAreNoCaptions()
    {
        Assert.AreEqual(118f, chart.bpm);
        Assert.AreEqual(524f, chart.beatZeroMs);
        Assert.AreEqual(0f, chart.offsetMs);
        Assert.AreEqual(8, chart.displayLevel);
        Assert.IsEmpty(chart.editorTempoMap, "BPM が変わらない曲");
        Assert.IsEmpty(chart.memory.captions, "下の解説は出さない");
        Assert.IsTrue(chart.memory.HasContent);
        Assert.AreSame(RushESecretState.MorningMemory, RushESecretState.For("Morning"));
    }

    [Test]
    public void EachLoopCopiesItsSampleAndTheSecondHalfRemembersFourBars()
    {
        Assert.AreEqual(Loops.Length, chart.memory.veils.Count);
        for (int i = 0; i < Loops.Length; i++)
        {
            var (start, half) = Loops[i];
            string label = start + "拍(" + Stage(i) + ")";
            if (i > 0) Assert.AreEqual(Loops[i - 1].start + 2 * Loops[i - 1].half, start, label + " すき間なく続く");
            Assert.AreEqual(start >= 160 ? 16 : 8, half, label + " 後半は4小節");
            var v = chart.memory.veils[i];
            Assert.AreEqual(TimeAt(start + half), v.time, TimeToleranceMs, label);
            Assert.AreEqual(TimeAt(start + 2 * half), v.endTime, TimeToleranceMs, label);
            Assert.AreEqual(Strength(i), v.strength, 1e-6f, label);

            var sample = Between(start, start + half);
            var repeat = Between(start + half, start + 2 * half);
            Assert.That(sample.Length, Is.InRange(2, Cap(i)), label);
            Assert.AreEqual(sample.Length, repeat.Length, label);
            Assert.LessOrEqual(sample.Count(n => n.IsDirection), ArrowLimit(i), label + " 矢印の数");
            for (int j = 0; j < sample.Length; j++)
            {
                NoteData a = sample[j], b = repeat[j];
                Assert.AreEqual(a.beat + half, b.beat, 1e-4f, label);
                Assert.AreEqual(a.x, b.x, label);
                Assert.AreEqual(a.y, b.y, label);
                Assert.AreEqual(a.color, b.color, label);
                Assert.AreEqual(a.type, b.type, label);
                Assert.AreEqual(a.direction, b.direction, label);
                Assert.AreEqual(0f, a.veil, label);
                Assert.AreEqual(Hidden(i), b.veil, 1e-6f, label);
                foreach (var n in new[] { a, b })
                {
                    Assert.AreEqual(TimeAt(n.beat), n.time, TimeToleranceMs, label);
                    Assert.Contains(n.color, new[] { "red", "blue" });
                    Assert.AreEqual(1, n.count, label);
                }
            }
        }
        // 後半は前半より覚える数が多い。
        int lastEarly = Between(144, 152).Length, firstLate = Between(160, 176).Length;
        Assert.Greater(firstLate, lastEarly);
    }

    [Test]
    public void OnlyTheIntroStaysVisibleAndTheSongEndsInsideTheLastRecall()
    {
        var expected = normal.notes.Where(Intro).OrderBy(n => n.time).ToArray();
        var actual = chart.notes.Where(Intro).OrderBy(n => n.time).ToArray();
        Assert.AreEqual(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].time, actual[i].time);
            Assert.AreEqual(0f, actual[i].veil);
        }
        var last = chart.notes.OrderBy(n => n.time).Last();
        Assert.Greater(last.veil, 0f, "最後のノーツは超上級のまね");
        Assert.LessOrEqual(last.time, chart.memory.veils.Last().endTime);
    }

    [Test]
    public void TheNoiseCoversEveryWholeRecall()
    {
        var intervals = MemoryVeilRules.BuildIntervals(chart, 0);
        Assert.AreEqual(Loops.Length, intervals.Count);
        for (int i = 0; i < Loops.Length; i++)
        {
            var (start, half) = Loops[i];
            Assert.AreEqual(half * 60.0 / Bpm, intervals[i].End - intervals[i].Start, .002);
            foreach (var n in Between(start + half, start + 2 * half))
                Assert.IsTrue(MemoryVeilRules.Covers(intervals, n.TimeSeconds), n.beat + "拍のまねはノイズの下");
            foreach (var n in Between(start, start + half))
                Assert.IsFalse(MemoryVeilRules.Covers(intervals, n.TimeSeconds), n.beat + "拍の見本はノイズの上");
        }
    }

    [Test]
    public void HandsHaveTimeToRecover()
    {
        foreach (string hand in new[] { "blue", "red" })
        {
            var notes = chart.notes.Where(n => (n.color == "gold" ? (n.x < 0 ? "blue" : "red") : n.color) == hand)
                .OrderBy(n => n.time).ToArray();
            for (int i = 1; i < notes.Length; i++)
                Assert.GreaterOrEqual(notes[i].time - notes[i - 1].time, 250 - TimeToleranceMs, hand + ": " + notes[i].beat);
        }
    }
}
