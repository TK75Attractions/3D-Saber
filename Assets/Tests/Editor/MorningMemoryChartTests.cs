using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// Morning の裏譜面 M(覚える、2026-10-25)。生成済みの chart_m.json を本物のローダーで読み、見本とまねの決まりを確かめる。
// 最初は勉強(テンポが変わる)に作ったが、ユーザー「BPMが変わんない曲にしてほしい」で 118BPM 一定の Morning へ移した。
// 譜面は C:\Users\shike\dev\tools\ChartMorningMemory\gen_memory.py --write で作る。
public class MorningMemoryChartTests
{
    static readonly int[] Starts = { 16, 32, 48, 64, 80, 96, 112, 128, 144, 160, 176, 192, 208, 224, 240, 256, 272, 288 };
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
    static int Cap(int i) => i < 3 ? 3 : i < 9 ? 4 : i < 16 ? 5 : 6;
    static float Hidden(int i) => i == 0 ? 0f : i == 1 ? .35f : i == 2 ? .70f : .95f;
    static float Strength(int i) => i == 0 ? .30f : i == 1 ? .55f : i == 2 ? .80f : .92f;
    // Normal の拍は 15.9996 のような丸めのずれを含むので、道具と同じく 1/4 拍へそろえてから区間を見る。
    static double Snap(double beat) { double q = Math.Round(beat * 4) / 4; return Math.Abs(beat - q) <= .01 ? q : beat; }
    static bool FreePart(NoteData n) => Snap(n.beat) < 16 || Snap(n.beat) >= 304;

    NoteData[] Between(int start, int end) => chart.notes.Where(n => Snap(n.beat) >= start && Snap(n.beat) < end)
        .OrderBy(n => n.beat).ThenBy(n => n.color).ThenBy(n => n.x).ThenBy(n => n.y).ToArray();

    [Test]
    public void TheTempoNeverChangesAndTheHeaderMatchesMorning()
    {
        Assert.AreEqual(118f, chart.bpm);
        Assert.AreEqual(524f, chart.beatZeroMs);
        Assert.AreEqual(0f, chart.offsetMs);
        Assert.AreEqual(7, chart.displayLevel);
        Assert.IsEmpty(chart.editorTempoMap, "BPM が変わらない曲");
        Assert.IsTrue(chart.memory.HasContent);
        Assert.AreSame(RushESecretState.MorningMemory, RushESecretState.For("Morning"));
    }

    [Test]
    public void EighteenLoopsCopyEachSampleEightBeatsLater()
    {
        Assert.AreEqual(Starts.Length, chart.memory.veils.Count);
        for (int i = 0; i < Starts.Length; i++)
        {
            int start = Starts[i];
            string label = start + "拍";
            var v = chart.memory.veils[i];
            Assert.AreEqual(TimeAt(start + 8), v.time, TimeToleranceMs, label);
            Assert.AreEqual(TimeAt(start + 16), v.endTime, TimeToleranceMs, label);
            Assert.AreEqual(Strength(i), v.strength, 1e-6f, label);

            var sample = Between(start, start + 8);
            var repeat = Between(start + 8, start + 16);
            Assert.That(sample.Length, Is.InRange(2, Cap(i)), label);
            Assert.AreEqual(sample.Length, repeat.Length, label);
            if (i >= 9 && i < 16)
                Assert.LessOrEqual(sample.Count(n => n.IsDirection), 2, label + " 上級の矢印は2個まで");
            for (int j = 0; j < sample.Length; j++)
            {
                NoteData a = sample[j], b = repeat[j];
                Assert.AreEqual(a.beat + 8, b.beat, 1e-4f, label);
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
                    if (i < 3) Assert.AreEqual("none", n.direction, label + " 初級は矢印なし");
                }
            }
        }
    }

    [Test]
    public void TheIntroAndTheLastFourBarsStayVisibleAsInNormal()
    {
        var expected = normal.notes.Where(FreePart).OrderBy(n => n.time).ToArray();
        var actual = chart.notes.Where(FreePart).OrderBy(n => n.time).ToArray();
        Assert.AreEqual(expected.Length, actual.Length);
        Assert.Greater(actual.Length, 0);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].time, actual[i].time);
            Assert.AreEqual(expected[i].x, actual[i].x);
            Assert.AreEqual(expected[i].color, actual[i].color);
            Assert.AreEqual(0f, actual[i].veil);
        }
    }

    [Test]
    public void CaptionsTeachTheRuleThenNameEachStage()
    {
        CollectionAssert.AreEqual(new[]
        {
            "見本：場所とリズムを覚えて", "同じ2小節を、もう一度", "ノイズが強くなる", "見えなくても、同じ場所・同じリズム",
            "中級", "上級：矢印の向きも覚えて", "超上級：矢印も全部覚えて", "最後は見えるまま",
        }, chart.memory.captions.Select(c => c.text));
        for (int i = 1; i < chart.memory.captions.Count; i++)
            Assert.LessOrEqual(chart.memory.captions[i - 1].endTime, chart.memory.captions[i].time);
    }

    [Test]
    public void TheNoiseCoversTheWholeTwoBarsOfEveryRecall()
    {
        // まねの2小節(118BPM で約4.07秒)の間はずっとノイズ。まねのノーツは全部区間の中、見本は全部外。
        var intervals = MemoryVeilRules.BuildIntervals(chart, 0);
        Assert.AreEqual(Starts.Length, intervals.Count);
        foreach (var span in intervals)
            Assert.AreEqual(8 * 60.0 / Bpm, span.End - span.Start, .002);
        for (int i = 0; i < Starts.Length; i++)
        {
            foreach (var n in Between(Starts[i] + 8, Starts[i] + 16))
                Assert.IsTrue(MemoryVeilRules.Covers(intervals, n.TimeSeconds), n.beat + "拍のまねはノイズの下");
            foreach (var n in Between(Starts[i], Starts[i] + 8))
                Assert.IsFalse(MemoryVeilRules.Covers(intervals, n.TimeSeconds), n.beat + "拍の見本はノイズの上");
        }
    }

    [Test]
    public void HandsHaveTimeToRecoverAndAtMostTwoNotesComeTogether()
    {
        foreach (string hand in new[] { "blue", "red" })
        {
            var notes = chart.notes.Where(n => (n.color == "gold" ? (n.x < 0 ? "blue" : "red") : n.color) == hand).ToArray();
            for (int i = 1; i < notes.Length; i++)
                Assert.GreaterOrEqual(notes[i].time - notes[i - 1].time, 250 - TimeToleranceMs, hand + ": " + notes[i].beat);
        }
    }
}
