using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using UnityEngine;

// 改作の核として指定された、作者の同時ロングを保存データと本編ローダーで守る。
public class ZeroHardRemixTests
{
    [TestCase(34250f, 56f)]
    [TestCase(148125f, 242f)]
    public void AuthoredPairedLongsRetainShapeAndDeadlineAtCorrectedBeat(float startMs, float beat)
    {
        string saved = Path.Combine(Application.dataPath, "../Tools/ChartDesign/Seisakuchu/ZeroHardRemix/authored-hard.json");
        var original = ChartLoader.Parse(File.ReadAllText(saved));
        var current = ChartLoader.LoadFromStreamingAssets("製作中", "hard");
        var before = original.notes.Where(n => n.time == startMs && n.count > 1).OrderBy(n => n.x).ToArray();
        var after = current.notes.Where(n => n.beat == beat && n.count > 1).OrderBy(n => n.x).ToArray();
        Assert.AreEqual(2, before.Length);
        Assert.AreEqual(2, after.Length);
        for (int i = 0; i < before.Length; i++)
        {
            before[i].beat = beat;
            before[i].time = after[i].time;
            Assert.AreEqual(JsonUtility.ToJson(before[i]), JsonUtility.ToJson(after[i]), "拍への時刻修正後も長さ・回数・座標・色を維持する");
        }
        Assert.AreEqual(original.bpm, current.bpm);
        Assert.AreEqual(original.offsetMs, current.offsetMs);
        Assert.AreEqual(original.beatZeroMs, current.beatZeroMs);
        Assert.AreEqual(original.displayLevel, current.displayLevel);
    }

    [Test]
    public void SignatureLongsShareFiftyCutsAndTheLongerDeadline()
    {
        string saved = Path.Combine(Application.dataPath, "../Tools/ChartDesign/Seisakuchu/ZeroHardRemix/authored-hard.json");
        var original = ChartLoader.Parse(File.ReadAllText(saved));
        var current = ChartLoader.LoadFromStreamingAssets("製作中", "hard");
        var before = original.notes.Where(n => n.time == 88750f && n.count > 1).OrderBy(n => n.x).ToArray();
        var after = current.notes.Where(n => n.beat == 145f && n.count > 1).OrderBy(n => n.x).ToArray();
        Assert.AreEqual(2, before.Length);
        Assert.AreEqual(2, after.Length);
        for (int i = 0; i < before.Length; i++)
        {
            before[i].count = 50;
            before[i].lengthMs = 21677f;
            before[i].y = -.3f;
            before[i].beat = 145f;
            before[i].time = after[i].time;
            Assert.AreEqual(JsonUtility.ToJson(before[i]), JsonUtility.ToJson(after[i]));
        }
        var extras = current.notes.Where(n => n.time > after[0].time && n.time < after[0].time + 21677f).ToArray();
        Assert.AreEqual(8, extras.Length);
        Assert.IsTrue(extras.All(n => n.count == 1 && n.type == "tap" && n.direction == "none"));
        Assert.AreEqual(2, extras.Select(n => new Vector2(n.x, n.y)).Distinct().Count());
        Assert.IsTrue(extras.All(n => Mathf.Abs(n.x) >= 2f), "超ロング中の単発は外側の固定レーンへ置く");
        Assert.IsTrue(extras.All(n => n.y == 1.5f), "配置可能域内の上段へ置く");
    }

    [Test]
    public void HardHeadsFollowTheConfirmedMetronomeInAudioTime()
    {
        var chart = ChartLoader.LoadFromStreamingAssets("製作中", "hard");
        Assert.AreEqual(98f, chart.bpm);
        Assert.AreEqual(20f, chart.beatZeroMs + chart.offsetMs);
        foreach (var note in chart.notes)
        {
            double audioMs = note.time + (double)chart.offsetMs;
            double eighth = (audioMs - 20) * 98 / 30000;
            Assert.That(eighth, Is.EqualTo(System.Math.Round(eighth)).Within(.00006), "実際の再生時刻が8分拍に一致: " + audioMs);
            Assert.That(note.beat * 2, Is.EqualTo(eighth).Within(.00006));
        }
    }

    [Test]
    public void FastSameColorFlicksReverseAndEnterAlongTheirArrow()
    {
        var chart = ChartLoader.LoadFromStreamingAssets("製作中", "hard");
        int connected = 0;
        foreach (string color in new[] { "red", "blue" })
        {
            var notes = chart.notes.Where(n => n.color == color || (n.color == "gold" && (n.x < 0 ? "blue" : "red") == color)).ToArray();
            for (int i = 1; i < notes.Length; i++)
            {
                var a = notes[i - 1];
                var b = notes[i];
                if (!a.IsDirection || !b.IsDirection || b.time - a.time > 620f) continue;
                Vector2 av = DirectionVector(a.direction), bv = DirectionVector(b.direction);
                Vector2 travel = new Vector2(b.x - a.x, b.y - a.y).normalized;
                Assert.LessOrEqual(Vector2.Dot(av, bv), -.5f, "速いフリックは切り返せる角度にする");
                Assert.GreaterOrEqual(Vector2.Dot(travel, bv), .70f, "次の矢印へ逆側から進入させない");
                connected++;
            }
        }
        Assert.GreaterOrEqual(connected, 40, "単発の受け渡しを混ぜても2連・3連の連続フリックを残す");
    }

    [TestCase(2)]
    [TestCase(4)]
    public void DenseMeasurePhrasesDoNotReuseTheSameHandAndRhythmSequence(int length)
    {
        var chart = ChartLoader.LoadFromStreamingAssets("製作中", "hard");
        var meter = new ChartMeterMap(chart.timeSignatures);
        var bars = meter.BarStarts(0, chart.notes.Max(n => n.beat)).Select(b => meter.At(b)).ToArray();
        var seen = new Dictionary<string, int>();
        int checkedPhrases = 0;
        for (int i = 0; i + length <= bars.Length; i++)
        {
            var window = bars.Skip(i).Take(length).ToArray();
            var groups = window.Select(b => chart.notes.Where(n => n.beat >= b.BarStart && n.beat < b.BarEnd).ToArray()).ToArray();
            // 指定して残す長い連打・固定レーン・休符を、密な反復配置の比較から除く。
            if (groups.Any(g => g.Length == 0) || groups.Sum(g => g.Length) < length * 3) continue;
            if (window.Any(b => HasActiveLong(chart, b))) continue;
            string key = string.Join("/", window.Select((b, k) => Number(b.BarEnd - b.BarStart) + ":" +
                string.Join(";", groups[k].Select(n => Number(n.beat - b.BarStart) + n.color + n.count))));
            Assert.IsFalse(seen.TryGetValue(key, out int prior), $"{prior}小節目と{bars[i].Measure}小節目で{length}小節分の手順が同じ");
            seen[key] = bars[i].Measure;
            checkedPhrases++;
        }
        Assert.Greater(checkedPhrases, 0, "連続した通常配置を比較したことを確認する");
    }

    [Test]
    public void AdjacentNormalMeasuresDoNotRepeatIdenticalLayouts()
    {
        var chart = ChartLoader.LoadFromStreamingAssets("製作中", "hard");
        var meter = new ChartMeterMap(chart.timeSignatures);
        string previous = null;
        foreach (double start in meter.BarStarts(0, chart.notes.Max(n => n.beat)))
        {
            var bar = meter.At(start);
            var notes = chart.notes.Where(n => n.beat >= bar.BarStart && n.beat < bar.BarEnd).ToArray();
            if (notes.Length < 2 || HasActiveLong(chart, bar)) { previous = null; continue; }
            string signature = Number(bar.BarEnd - bar.BarStart) + ":" + string.Join(";", notes.Select(n =>
                Number(n.beat - bar.BarStart) + n.color + Number(n.x) + "," + Number(n.y) + n.direction));
            Assert.AreNotEqual(previous, signature, $"{bar.Measure - 1}〜{bar.Measure}小節で同じ配置を続けない");
            previous = signature;
        }
    }


    [Test]
    public void PeaksHaveMorePairsAndSpacedGoldAccents()
    {
        var chart = ChartLoader.LoadFromStreamingAssets("製作中", "hard");
        var pairs = chart.notes.GroupBy(n => n.beat).Where(g => g.Count() == 2).ToArray();
        Assert.GreaterOrEqual(pairs.Count(g => g.Key >= 80 && g.Key < 124), 10);
        Assert.GreaterOrEqual(pairs.Count(g => g.Key >= 220 && g.Key < 268), 18);
        var goldPairs = pairs.Where(g => g.All(n => n.color == "gold" && n.count == 1)).ToArray();
        Assert.GreaterOrEqual(goldPairs.Length, 12);
        foreach (var pair in goldPairs)
        {
            Assert.IsTrue(pair.All(n => n.direction == "none" && n.type == "tap"));
            Assert.Less(pair.Min(n => n.x), -1f);
            Assert.Greater(pair.Max(n => n.x), 1f);
        }
        Assert.IsTrue(chart.notes.GroupBy(n => n.beat).All(g => g.Count() <= 2), "同時入力は両手まで");
    }

    [TestCase(68f, 220f, "red", 3)]
    [TestCase(109.5f, 225.5f, "blue", 2)]
    [TestCase(114f, 231f, "red", 3)]
    [TestCase(120f, 236.5f, "blue", 2)]
    public void LaterMotifRetainsTheEarlierFlickAndAddsAnOppositeHandPath(float source, float target, string hand, int sourceCount)
    {
        var chart = ChartLoader.LoadFromStreamingAssets("製作中", "hard");
        string other = hand == "red" ? "blue" : "red";
        var points = new Vector2[3];
        for (int k = 0; k < 3; k++)
        {
            var before = chart.notes.Single(n => n.beat == source + (k % sourceCount) * .5f && n.color == hand);
            var after = chart.notes.Single(n => n.beat == target + k * .5f && n.color == hand);
            Assert.AreEqual(before.direction, after.direction);
            Assert.AreEqual(before.x, after.x);
            Assert.AreEqual(before.y, after.y);
            Assert.IsTrue(after.IsDirection);
            var companion = chart.notes.Single(n => n.beat == after.beat && n.color == other);
            Assert.AreEqual("none", companion.direction);
            Assert.AreEqual(1, companion.count);
            points[k] = new Vector2(companion.x, companion.y);
            if (k > 0) Assert.That(Vector2.Distance(points[k-1], points[k]), Is.InRange(.2f, .8f));
        }
        Assert.Greater(Vector2.Distance(points[0], points[2]), .8f, "反対の手は同じ位置の連打ではなく、弧状につなぐ");
    }

    static bool HasActiveLong(ChartData chart, ChartMeterMap.Position bar) => chart.notes.Any(n =>
        n.IsLong && n.beat < bar.BarEnd && n.beat + n.lengthMs * (double)chart.bpm / 60000 > bar.BarStart);

    static string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    static Vector2 DirectionVector(string direction)
    {
        float x = direction.Contains("left") ? -1f : direction.Contains("right") ? 1f : 0f;
        float y = direction.Contains("up") ? 1f : direction.Contains("down") ? -1f : 0f;
        return new Vector2(x, y).normalized;
    }
}
