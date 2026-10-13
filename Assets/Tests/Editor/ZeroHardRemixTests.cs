using System.IO;
using System.Linq;
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
            var notes = chart.notes.Where(n => n.color == color).ToArray();
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
        Assert.GreaterOrEqual(connected, 60, "2連・3連に連続フリックを残す");
    }

    static Vector2 DirectionVector(string direction)
    {
        float x = direction.Contains("left") ? -1f : direction.Contains("right") ? 1f : 0f;
        float y = direction.Contains("up") ? 1f : direction.Contains("down") ? -1f : 0f;
        return new Vector2(x, y).normalized;
    }
}
