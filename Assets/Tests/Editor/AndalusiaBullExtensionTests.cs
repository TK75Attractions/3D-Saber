using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;

// v1.27の延長。従来区間の検査と安全判定の道具は BullfightTests.cs に置く。
public partial class AndalusiaBullChartTests
{
    [Test]
    public void PrefixOnlyContainsTheReviewedLongAndChargeChanges()
    {
        var chart = Read("t");
        Assert.AreEqual(209, chart.notes.Count);
        Assert.AreEqual(30, chart.bull.charges.Count);
        Assert.AreEqual(142, chart.bull.shots.Count);
        // v1.26からロング4個を除き、拍249の上段をy=1.35にした結果を固定する。
        // JSONの空白やキー順、現在のカルチャーに依存しない。
        using (var buffer = new MemoryStream())
        using (var writer = new BinaryWriter(buffer))
        {
            writer.Write(chart.notes.Count);
            foreach (var n in chart.notes)
            {
                writer.Write(n.beat); writer.Write(n.time); writer.Write(n.x); writer.Write(n.y);
                writer.Write(n.type ?? ""); writer.Write(n.color ?? ""); writer.Write(n.direction ?? "");
                writer.Write(n.count); writer.Write(n.lengthMs); writer.Write(n.veil);
                writer.Write(n.windX); writer.Write(n.windY); writer.Write(n.windSpin); writer.Write(n.windSway);
                writer.Write(n.vanish ?? "");
            }
            writer.Write(chart.bull.charges.Count);
            foreach (var c in chart.bull.charges)
            {
                writer.Write(c.time); writer.Write(c.endTime); writer.Write(c.x); writer.Write(c.y);
                writer.Write(c.angle); writer.Write(c.dir); writer.Write(c.warn);
            }
            writer.Write(chart.bull.shots.Count);
            foreach (var s in chart.bull.shots)
            {
                writer.Write(s.time); writer.Write(s.x); writer.Write(s.y);
                writer.Write(s.follow ?? ""); writer.Write(s.kind ?? "");
                writer.Write(s.stay); writer.Write(s.blast); writer.Write(s.amp);
                writer.Write(s.fromX); writer.Write(s.fromY); writer.Write(s.lead); writer.Write(s.angle);
            }
            writer.Flush();
            using (var sha = SHA256.Create())
                Assert.AreEqual("41737ed3127cc09e8df91febb05a3b59be4ad544b6a789b4a9edd810fc341274",
                    Hex(sha.ComputeHash(buffer.ToArray())), "指定の安全修正以外は拍295.5まで変更しない");
        }
    }

    [Test]
    public void ExtensionIsDenseVariedAndEndsWithThreeChargesAndASplitRing()
    {
        var chart = Read("t", true);
        var shots = chart.bull.shots.Where(s => s.time >= ExtensionMs).ToArray();
        Assert.That(shots.Length, Is.InRange(80, 130));
        Assert.IsTrue(shots.All(s => s.time <= 350 * BeatMs + .1));
        Assert.AreEqual(350 * BeatMs, shots.Max(s => s.time), .1);
        CollectionAssert.AreEquivalent(new[] { "", "big", "spear", "split", "mine", "bomb", "wave" },
            shots.Select(s => s.kind).Distinct());
        Assert.IsTrue(shots.Any(s => s.kind == "" && s.lead > 0));
        Assert.IsTrue(shots.Any(s => s.follow == "red"));
        Assert.IsTrue(shots.Any(s => s.follow == "blue"));
        double previousDensity = chart.bull.shots.Count(s => s.time >= LateMs && s.time < ExtensionMs) / 145.0;
        Assert.Greater(shots.Length / 54.0, previousDensity * 2, "従来の後半より明確に密にする");

        var groups = chart.bull.charges.Where(c => c.time >= ExtensionMs)
            .GroupBy(c => c.time).OrderBy(g => g.Key).ToArray();
        Assert.That(groups.Length, Is.InRange(6, 10));
        Assert.IsTrue(groups.SelectMany(g => g).Any(c => c.angle != 0 && Mathf.Abs(c.angle) != 90));
        for (int i = 0; i < groups.Length; i++)
        {
            Assert.That(groups[i].Count(), Is.InRange(2, 3));
            Assert.AreEqual(1, groups[i].Select(c => c.endTime).Distinct().Count());
            foreach (var c in groups[i])
            {
                Assert.GreaterOrEqual(c.warn, 6 * BeatMs - .1);
                Assert.GreaterOrEqual(c.time - c.warn, ExtensionMs - .1);
                Assert.AreEqual(2 * BeatMs, c.endTime - c.time, .2);
            }
            if (i > 0)
                Assert.GreaterOrEqual(groups[i].Key - groups[i - 1].Max(c => c.endTime), 2 * BeatMs - .2);
        }
        foreach (int beat in new[] { 340, 346, 350 })
        {
            Assert.AreEqual(3, groups.Single(g => Math.Abs(g.Key - beat * BeatMs) < .1).Count());
            var ending = shots.Where(s => Math.Abs(s.time - beat * BeatMs) < .1).ToArray();
            Assert.AreEqual(6, ending.Count(s => s.kind == "split"));
            Assert.IsTrue(ending.Any(s => s.kind == "" && s.lead > 0));
        }
    }

    [Test]
    public void ExtensionHasSafeGoldEveryTwoBeatsAndNoOtherNotes()
    {
        var chart = Read("t", true);
        var gold = chart.notes.Where(n => n.time >= ExtensionMs).ToArray();
        Assert.GreaterOrEqual(gold.Length, 20, "金は2拍ごとが目安(10/26「もっとおいていいよ」)。安全な所がない拍は半拍ずらすか飛ばす");
        Assert.IsTrue(gold.All(n => n.beat >= 296 && n.beat <= 350 && System.Math.Abs(n.beat * 2 - System.Math.Round(n.beat * 2)) < 1e-4), "金は延長の中の半拍の目");
        Assert.AreEqual(350f, gold.Last().beat, 1e-4, "最後の打音に金");
        foreach (var n in gold)
        {
            Assert.AreEqual("gold", n.color);
            Assert.AreEqual("tap", n.type);
            Assert.AreEqual("none", n.direction);
            Assert.AreEqual(1, n.count);
            Assert.AreEqual(0, n.lengthMs);
            Assert.AreEqual(n.beat * BeatMs, n.time, .1);
        }
        Assert.AreEqual(173, chart.bpm);
        Assert.AreEqual(0, chart.beatZeroMs);
        Assert.AreEqual(0, chart.offsetMs);
        Assert.AreEqual(1, chart.coordScale);
        Assert.AreEqual(10, chart.displayLevel);
        var meter = new ChartMeterMap(chart.timeSignatures);
        CollectionAssert.AreEqual(Enumerable.Range(0, 14).Select(i => 298.0 + 4 * i),
            meter.BarStarts(296, 350));
        Assert.AreEqual(4, meter.At(350).Numerator);
        Assert.AreEqual(4, meter.At(350).Denominator);

        var session = new BullSession(chart.bull, 0);
        foreach (var n in gold)
            CheckSpan(session, n.TimeSeconds - .25, n.TimeSeconds + .25, new Vector2(n.x, n.y));
        // 境目の既存ノーツも含め、点としての余白は刃の安全候補より広く取る。
        foreach (var n in chart.notes)
            CheckNoteClearance(session, n);
    }

    static void CheckNoteClearance(BullSession session, NoteData n)
    {
        var p = new Vector2(n.x, n.y);
        double lo = n.TimeSeconds - .25;
        double hi = n.TimeSeconds + n.LingerSeconds(NoteSpawner.DefaultSecondsPerLongCut) + .25;
        foreach (var c in session.Charges.Where(c => c.Start <= hi + .05 && c.End >= lo - .05))
            Assert.GreaterOrEqual(BullRules.ChargeLaneDistance(c, p, p), BullRules.ChargeRadius + .65f - 1e-5f);
        foreach (var s in session.Shots)
        {
            if (s.Follow != SaberHand.Any)
            {
                if (n.color == "gold" || n.color == (s.Follow == SaberHand.Left ? "blue" : "red"))
                    Assert.IsFalse(lo - .15 <= s.Time && s.Time <= hi + .15, "滞在中に同じ手を追う弾");
                continue;
            }
            if (hi < BullRules.HitStart(s) || lo > BullRules.HitEnd(s)) continue;
            float distance, margin;
            if (s.Kind == "spear")
            {
                BullRules.SpearEnds(s, out var a, out var b);
                distance = BullRules.DistanceToSegment(p, a, b);
                margin = 1f;
            }
            else
            {
                distance = Vector2.Distance(p, s.Position);
                margin = s.Kind == "bomb" ? s.Blast + 1.05f : s.Kind == "big" ? 1.5f : 1.25f;
            }
            Assert.GreaterOrEqual(distance, margin - 1e-5f, n.time + "msのノーツの余白");
        }
    }

    [Test]
    public void GoldSafetyRejectsARewardPlacedInsideACharge()
    {
        var chart = new BullChartData();
        chart.charges.Add(new BullChargeData { time = 1000, endTime = 2000 });
        Assert.Throws<AssertionException>(() => CheckSpan(new BullSession(chart, 0), 1.25, 1.75, Vector2.zero));
    }

    [Test]
    public void VariantAudioMatchesStageAndPlaysPastTheFinalJudgments()
    {
        string folder = Path.Combine(Application.streamingAssetsPath, "Songs", "Andalusia");
        var stage = StagePerformanceTimeline.Load("Andalusia", "T");
        Assert.AreEqual("_t", SongLoudness.VariantSuffix(folder, "T"));
        Assert.AreEqual("", SongLoudness.VariantSuffix(folder, "Hard"));
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(Path.Combine(folder, "audio_t.ogg")))
            Assert.AreEqual(stage.audioSha256.ToLowerInvariant(), Hex(sha.ComputeHash(stream)));
        Assert.IsTrue(SongLoudness.SourceMatches(folder, stage.audioSha256, "T"));
        Assert.IsFalse(SongLoudness.SourceMatches(folder, stage.audioSha256, "Hard"));
        var range = stage.ResolvePlayRange(130.4, true);
        Assert.AreEqual(122.8, range.End, 1e-6);
        var chart = Read("t", true);
        var bull = new BullSession(chart.bull, chart.offsetMs / 1000.0);
        Assert.Greater(bull.LastTime, chart.notes.Max(n => n.TimeSeconds));
        Assert.Less(bull.LastTime + .12, range.End);
        double end = GamePlayManager.ResolveEndThreshold(range.End,
            chart.notes.Max(n => n.TimeSeconds), bull.LastTime, .12, 2, 3.5);
        Assert.GreaterOrEqual(end, 124.8);
        Assert.GreaterOrEqual(end, bull.LastTime + .12 + 3.5);
    }

    [Test]
    public void EndThresholdWaitsForLateHazardsWithOffsetAndCameraLatency()
    {
        var chart = new BullChartData();
        chart.charges.Add(new BullChargeData { time = 123000, endTime = 124000 });
        chart.shots.Add(new BullShotData { time = 124000, kind = "mine", stay = 2000 });
        var bull = new BullSession(chart, .5);
        Assert.AreEqual(126.5, bull.LastTime, 1e-6);
        Assert.AreEqual(130.12, GamePlayManager.ResolveEndThreshold(122.8, 121, bull.LastTime, .12, 2, 3.5), 1e-6);
        Assert.AreEqual(124.8, GamePlayManager.ResolveEndThreshold(122.8, 102.5, double.NegativeInfinity, .12, 2, 3.5), 1e-6);
        Assert.AreEqual(133.5, GamePlayManager.ResolveEndThreshold(122.8, 130, bull.LastTime, .12, 2, 3.5), 1e-6);
    }

    static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
