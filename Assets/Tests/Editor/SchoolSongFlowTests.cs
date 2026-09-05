using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 校歌v2の実データを読み、上下/左右の分布と複合の成立条件を確認する。
public class SchoolSongFlowTests
{
    [TestCase("easy", 2, 1)]
    [TestCase("normal", 5, 2)]
    [TestCase("hard", 10, 3)]
    public void AuthoredSpreadHasThreeHeightsAndIntentionalTypes(string difficulty, int level, int maxSimultaneous)
    {
        var chart = ChartLoader.LoadFromStreamingAssets("Epilogue", difficulty);
        Assert.AreEqual(level, chart.displayLevel);
        foreach (var n in chart.notes)
        {
            Assert.That(Mathf.Abs(n.x), Is.InRange(.85f, 2.15f));
            Assert.That(n.y, Is.InRange(-1.05f, 1.05f));
        }
        Assert.Greater(chart.notes.Count(n => n.y > .35f), 10);
        Assert.Greater(chart.notes.Count(n => n.y < -.35f), 10);
        Assert.Greater(chart.notes.Count(n => Mathf.Abs(n.y) <= .35f), 10);
        Assert.IsTrue(chart.notes.Any(n => n.count > 1));
        Assert.IsTrue(chart.notes.Any(n => n.IsDirection));
        Assert.IsTrue(chart.notes.Any(n => n.color == "gold"));
        Assert.AreEqual(maxSimultaneous, chart.notes.GroupBy(n => n.time).Max(g => g.Count()));
    }

    [TestCase("normal")]
    [TestCase("hard")]
    public void AllEightDirectionsAppear(string difficulty)
    {
        var chart = ChartLoader.LoadFromStreamingAssets("Epilogue", difficulty);
        Assert.AreEqual(8, chart.notes.Where(n => n.IsDirection).Select(n => n.direction).Distinct().Count());
    }

    [Test]
    public void LongCounterpointUsesTheOtherHandAndSeparateSpace()
    {
        var chart = ChartLoader.LoadFromStreamingAssets("Epilogue", "hard");
        int overlaps = 0;
        foreach (var hold in chart.notes.Where(n => n.count > 1))
            foreach (var other in chart.notes.Where(n => n != hold && n.time >= hold.time && n.time < hold.time + hold.lengthMs))
            {
                overlaps++;
                Assert.AreEqual(1, other.count);
                Assert.AreNotEqual("gold", other.color);
                Assert.AreNotEqual("gold", hold.color);
                Assert.AreNotEqual(hold.color, other.color);
                Assert.GreaterOrEqual(Mathf.Abs(hold.x - other.x), 2.6f);
            }
        Assert.GreaterOrEqual(overlaps, 8);
    }

    [Test]
    public void TripleChordHasOnlyTwoHandGestures()
    {
        var chart = ChartLoader.LoadFromStreamingAssets("Epilogue", "hard");
        var triples = chart.notes.GroupBy(n => n.time).Where(g => g.Count() == 3).ToArray();
        Assert.AreEqual(3, triples.Length);
        foreach (var chord in triples)
        {
            var stack = chord.Where(n => n.color == "blue").ToArray();
            Assert.AreEqual(2, stack.Length);
            Assert.AreEqual(stack[0].direction, stack[1].direction);
            Assert.AreNotEqual("none", stack[0].direction);
            var v = new Vector2(stack[1].x - stack[0].x, stack[1].y - stack[0].y);
            Assert.That(v.magnitude, Is.InRange(1.19f, 1.21f));
            Assert.Less(Mathf.Min(Vector2.Angle(v, CutDirectionHelper.ToVector(CutDirectionHelper.Parse(stack[0].direction))),
                Vector2.Angle(-v, CutDirectionHelper.ToVector(CutDirectionHelper.Parse(stack[0].direction)))), .1f);
        }
    }

    [Test]
    public void TripleChordsCanActuallyBeCutWithOneStrokePerHand()
    {
        var chart = ChartLoader.LoadFromStreamingAssets("Epilogue", "hard");
        foreach (var chord in chart.notes.GroupBy(n => n.time).Where(g => g.Count() == 3))
        {
            var root = new GameObject("TripleStrokeCheck");
            try
            {
                var notes = chord.Select(n =>
                {
                    var go = new GameObject("StackNote"); go.transform.SetParent(root.transform);
                    go.transform.position = new Vector3(n.x, n.y, 0);
                    var note = go.AddComponent<CuttableNote>();
                    note.IsJudgeable = true;
                    note.RequiredHand = SaberHandHelper.FromColor(n.color);
                    note.RequiredDirection = CutDirectionHelper.Parse(n.direction);
                    return note;
                }).ToArray();
                var saber = new GameObject("SimulatedSaber"); saber.transform.SetParent(root.transform);
                var tracker = saber.AddComponent<SaberTracker>();
                var judge = saber.AddComponent<SaberCutJudge>();
                judge.saber = tracker; judge.autonomous = false;
                judge.bladeRadius = .32f; judge.noteHitRadiusXY = .60f; judge.minCutSpeed = 2f;
                var left = notes.Where(n => n.RequiredHand == SaberHand.Left).ToArray();
                Stroke(judge, tracker, SaberHand.Left, (left[0].transform.position + left[1].transform.position) * .5f, left[0].RequiredDirection);
                Assert.IsTrue(left.All(n => n.IsCut), "左手の一振りで2個を同時処理できる");
                var right = notes.First(n => n.RequiredHand != SaberHand.Left);
                Assert.IsFalse(right.IsCut, "左の軌道が右ノーツを巻き込まない");
                Stroke(judge, tracker, SaberHand.Right, right.transform.position, right.RequiredDirection);
                Assert.IsTrue(right.IsCut);
            }
            finally
            {
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                    if (renderer.sharedMaterial != null) Object.DestroyImmediate(renderer.sharedMaterial);
                Object.DestroyImmediate(root);
            }
        }
    }

    private static void Stroke(SaberCutJudge judge, SaberTracker tracker, SaberHand hand, Vector3 center, CutDirection direction)
    {
        var xy = CutDirectionHelper.ToVector(direction);
        var v = new Vector3(xy.x, xy.y, 0);
        judge.hand = hand;
        tracker.ResetTo(center - v * 2f);
        for (int step = 1; step <= 8; step++)
        {
            tracker.Tick(center + v * (-2f + step * .5f), .05f);
            judge.RunJudge();
        }
    }
}
