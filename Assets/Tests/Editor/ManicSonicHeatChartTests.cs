using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

public class ManicSonicHeatChartTests
{
    static ChartData Load()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "ManicSonic", "chart_x.json");
        Assert.IsTrue(File.Exists(path), "先に gen_heat.py --write を実行する。Hardへの代替読込では通さない");
        return ChartLoader.LoadFromStreamingAssets("ManicSonic", "X");
    }

    [Test]
    public void XKeepsMixedTimingPositionsArrowsAndTempo()
    {
        var hard = ChartLoader.LoadFromStreamingAssets("ManicSonic", "Hard");
        var chart = Load();
        Assert.AreEqual(425, hard.notes.Count);
        MixedSecretChartAssertions.Check("ManicSonic", chart, "x", 53333.3333, 90, 217);
        Assert.AreEqual(193, chart.notes.Count(n => n.color == "gold"));
        Assert.AreEqual(109, chart.notes.Count(n => n.heat));
        Assert.AreEqual(36, chart.notes.Count(n => n.heat && n.color != "gold"));
        Assert.AreEqual(6, chart.notes.Count(n => n.IsDirection));
        Assert.IsFalse(chart.notes.Any(n => n.IsLong));
        Assert.AreEqual(10, chart.displayLevel);
        Assert.NotNull(chart.heat);
        Assert.AreEqual(3, chart.heat.max);
        Assert.AreEqual(2, chart.heat.coolBeats);
        Assert.AreEqual(1, chart.heat.perCut);
        Assert.IsFalse(chart.heat.blockAll);

        // ChartDataで読めるルート項目も、熱設定以外は共通のまま。
        hard.notes.Clear();
        chart.notes.Clear();
        hard.heat = chart.heat;
        Assert.AreEqual(JsonUtility.ToJson(hard), JsonUtility.ToJson(chart));
    }

    [Test]
    public void AuditCompletesAndEveryLegalChoiceHasAContinuation()
    {
        var chart = Load();
        var cuts = HeatForecast.FromChart(chart);
        var audit = HeatForecast.Audit(HeatForecast.CreateSession(chart), cuts, 2000000);
        Assert.IsTrue(audit.Complete, "探索打ち切りを成功扱いしない");
        Assert.AreEqual(true, audit.HasPlayablePath);
        Assert.AreEqual(true, audit.AllChoicesPlayable);
        // 通常ノーツは上限でも切れる。全色指定ノーツが低温、という条件とは区別する。
        Assert.AreEqual(false, audit.FixedHandsSafe);
        Assert.GreaterOrEqual(audit.FirstProblemSource, 0);
        Assert.IsFalse(chart.notes[audit.FirstProblemSource].heat);

        var trace = HeatForecast.Simulate(HeatForecast.CreateSession(chart), cuts);
        Assert.IsTrue(trace.All(row => row.Accepted), "熱の低い手を選ぶ標準試走");

        var tempo = new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap);
        foreach (var note in chart.notes.Where(n => n.heat && n.color != "gold"))
        {
            double beat = tempo.BeatAt(note.time);
            foreach (var previous in chart.notes.Where(n => !ReferenceEquals(n, note) &&
                n.time <= note.time && (n.color == note.color || n.color == "gold")))
                Assert.GreaterOrEqual(beat - tempo.BeatAt(previous.time), 2.74,
                    "色指定の熱ノーツ前は2.75拍空ける: " + note.beat);
        }
    }

    [TestCase("side")]
    [TestCase("near")]
    [TestCase("cool")]
    [TestCase("cool-near")]
    public void FamiliarHandChoicesDoNotGetBlocked(string policy)
    {
        var chart = Load();
        for (int jitter = 0; jitter < 4; jitter++)
        for (int order = 0; order < 2; order++)
        {
            var session = HeatForecast.CreateSession(chart);
            var positions = new[] { new Vector2(1.9f, .3f), new Vector2(-1.9f, .3f) };
            var cuts = HeatForecast.FromChart(chart).Select(c =>
            {
                long seed = (long)Math.Round((double)chart.notes[c.SourceIndex].time * 10);
                double delta = jitter == 0 ? 0 :
                    (((seed * 1103515245L + jitter * 12345L) % 2001) / 1000.0 - 1) * .04;
                return new HeatCut(c.Time + delta, c.RequiredHand, c.HeatNote, c.SourceIndex);
            }).OrderBy(c => c.Time).ThenBy(c => order == 0 ? c.SourceIndex : -c.SourceIndex);

            foreach (var cut in cuts)
            {
                Assert.IsTrue(session.Advance(cut.Time));
                var note = chart.notes[cut.SourceIndex];
                var point = new Vector2(note.x, note.y);
                var hand = cut.RequiredHand;
                if (hand == HeatHand.Any)
                {
                    var near = DistanceSquared(positions[0], point) <= DistanceSquared(positions[1], point)
                        ? HeatHand.Right : HeatHand.Left;
                    if (policy == "side") hand = note.x >= 0 ? HeatHand.Right : HeatHand.Left;
                    else if (policy == "near") hand = near;
                    else if (policy == "cool-near" &&
                        session.HeatOf(HeatHand.Right) == session.HeatOf(HeatHand.Left)) hand = near;
                    else hand = session.ChooseHand(HeatHand.Any);
                    // 左右優先は元の手のまま。他は金の熱ノーツで上限の手を避ける。
                    if (policy != "side" && !session.CanCut(hand, cut.HeatNote))
                        hand = hand == HeatHand.Right ? HeatHand.Left : HeatHand.Right;
                }
                Assert.IsTrue(session.TryCut(hand, cut.HeatNote, cut.Time),
                    $"{policy} / ずれ{jitter} / 順{order} / 拍{note.beat}");
                positions[hand == HeatHand.Right ? 0 : 1] = point;
            }
        }
    }

    static double DistanceSquared(Vector2 a, Vector2 b)
    {
        double x = (double)a.x - b.x, y = (double)a.y - b.y;
        return x * x + y * y;
    }

    [Test]
    public void XUsesTheSharedTableAndUnlocksOnTheTenthJacketCut()
    {
        var secret = RushESecretState.For("ManicSonic");
        Assert.AreSame(RushESecretState.ManicSonicHeat, secret);
        Assert.AreEqual("X", secret.Difficulty);
        Assert.AreSame(secret, RushESecretState.ForDifficulty(" x "));
        Assert.AreEqual(RushESecretState.Thermal, GameHUDSkin.DifficultyColor("X"));
        Assert.AreEqual(RushESecretState.Thermal, GameStartCountdown.AccentForDifficulty("x"));
        foreach (var other in RushESecretState.Charts)
            if (!ReferenceEquals(other, secret)) Assert.AreNotEqual(other.Accent, secret.Accent);

        var state = new RushESecretState();
        state.Select("ManicSonic");
        for (int i = 0; i < 9; i++)
        {
            Assert.IsTrue(state.TryHit(i * .2));
            Assert.IsFalse(state.Transitioning);
            Assert.IsFalse(state.Unlocked);
        }
        Assert.IsTrue(state.TryHit(2));
        Assert.IsTrue(state.Transitioning);
        Assert.IsTrue(state.Reveal());
        Assert.IsTrue(state.Finish());
        Assert.IsTrue(state.Unlocked);
        Assert.AreSame(secret, state.Current);
        state.Select("Epilogue");
        Assert.IsFalse(state.Unlocked);
    }
}
