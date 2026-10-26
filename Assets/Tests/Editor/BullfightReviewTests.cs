using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class BullfightReviewTests
{
    GameObject root;
    BullfightMode mode;

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ImuNotesStillUseCameraPositionLatencyForEachHand(bool swap)
    {
        root = new GameObject("BullClockReview");
        var judges = new SaberCutJudge[2];
        var bridges = new SaberInputBridge[2];
        for (int i = 0; i < 2; i++)
        {
            var go = new GameObject("Hand" + i);
            go.transform.SetParent(root.transform, false);
            bridges[i] = go.AddComponent<SaberInputBridge>();
            judges[i] = go.AddComponent<SaberCutJudge>();
            judges[i].hand = i == 0 ? SaberHand.Right : SaberHand.Left;
            judges[i].bladeProvider = bridges[i];
            judges[i].ExternalJudgment = true;
            bool camera = (i == 0) != swap;
            bridges[i].OverrideCameraInput(camera);
            bridges[i].OverrideMouseFallback(!camera);
            Assert.AreEqual(camera ? .12 : 0, BullfightMode.PositionLatency(bridges[i], .12), 1e-9);
        }
        var chart = new BullChartData();
        chart.charges.Add(new BullChargeData { time = 1000, endTime = 1694, dir = 1 });
        mode = BullfightMode.Create(root.transform, chart, null, judges[0], judges[1]);
        var charge = mode.Session.Charges[0];
        const double songTime = 1.467;
        mode.BladeOverride = hand =>
        {
            int i = hand == SaberHand.Left ? 1 : 0;
            double t = songTime - (((i == 0) != swap) ? .12 : 0);
            Vector2 p = BullRules.ChargePosition(charge, t);
            return new BladeSample(p + Vector2.down * .1f, p + Vector2.up * .1f);
        };
        mode.TickFromInput(songTime, .12);
        Assert.AreEqual(2, mode.StunCount, "カメラの手だけ120ms戻す。IMUのノーツ判定には依存しない");
        mode.BladeOverride = hand => default(BladeSample);
        mode.TickFromInput(3.46, .12);
        Assert.IsTrue(mode.IsStunned(SaberHand.Right) && mode.IsStunned(SaberHand.Left));
        mode.TickFromInput(3.47, .12);
        Assert.IsFalse(mode.IsStunned(SaberHand.Right) || mode.IsStunned(SaberHand.Left));
        foreach (var bridge in bridges) bridge.OverrideCameraInput(false);
        mode.TickFromInput(3.48, .12);
        Assert.AreEqual(.12, mode.MaxPositionLatencySeconds, 1e-9, "終了の待ち時間は見失いで短くしない");
        Assert.AreEqual(130.12,
            GamePlayManager.ResolveEndThreshold(122.8, 121, 126.5, mode.MaxPositionLatencySeconds, 2, 3.5), 1e-6);
    }

    [Test]
    public void TwoClocksCheckBothHandsBeforeConsumingAShot()
    {
        var chart = new BullChartData();
        chart.shots.Add(new BullShotData { time = 1500, kind = "bomb" });
        var blade = new BladeSample(new Vector2(.55f, -.1f), new Vector2(.55f, .1f));
        var session = new BullSession(chart, 0);
        session.Tick(1.51, 1.65, blade, blade, null);
        Assert.IsFalse(session.IsStunned(SaberHand.Right, 1.51));
        Assert.IsTrue(session.IsStunned(SaberHand.Left, 1.65));
        session = new BullSession(chart, 0);
        session.Tick(1.65, 1.66, blade, blade, null);
        Assert.AreEqual(2, session.StunCount);
        Assert.IsTrue(session.Shots[0].Spent);
    }

    [Test]
    public void ShotKindsAndFxReuseTheirObjectsAndResetTheirState()
    {
        root = new GameObject("BullPoolReview");
        string[] kinds = { "", "big", "spear", "split", "mine", "bomb", "wave" };
        var chart = new BullChartData();
        for (int batch = 0; batch < 12; batch++)
        foreach (string kind in kinds)
            chart.shots.Add(new BullShotData { time = (3 + batch * 4) * 1000,
                kind = kind, angle = batch % 2 == 0 ? 45 : 90,
                lead = kind == "split" ? 500 : 0, blast = batch % 2 == 0 ? .7f : 1.1f });
        mode = BullfightMode.Create(root.transform, chart, null, null, null);
        mode.BladeOverride = hand => default(BladeSample);
        int shotCount = mode.CreatedShotViewCount, fxCount = mode.CreatedFxCount;
        int objects = mode.GetComponentsInChildren<Transform>(true).Length;
        var advance = typeof(BullfightMode).GetMethod("AdvanceEffects", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int batch = 0; batch < 12; batch++)
        {
            double time = 3 + batch * 4;
            mode.Tick(time - 1, time - 1);
            var bodies = mode.transform.Cast<Transform>().Where(t => t.name == "Shot").ToArray();
            Assert.AreEqual(7, bodies.Length);
            Assert.IsTrue(bodies.All(t => t.gameObject.activeSelf), "爆弾・分裂弾の非表示を持ち越さない");
            var spear = bodies.Single(t => t.Find("Shaft") != null);
            Assert.Less(Quaternion.Angle(Quaternion.Euler(0, 0, batch % 2 == 0 ? 45 : 90), spear.localRotation), .001f);
            mode.Tick(time, time);
            Assert.AreEqual(batch + 1, mode.SplitBurstCount, "再利用しても分裂の輪は組ごとに1回");
            mode.Tick(time + .8, time + .8);
            Assert.IsNull(mode.transform.Find("Shot"), "返却物は表示階層から外す");
            advance.Invoke(mode, new object[] { 1f });
            Assert.IsNull(mode.transform.Find("BullFx"));
            Assert.AreEqual(shotCount, mode.CreatedShotViewCount);
            Assert.AreEqual(fxCount, mode.CreatedFxCount);
            Assert.AreEqual(objects, mode.GetComponentsInChildren<Transform>(true).Length);
        }
        mode.EndPlay();
        Assert.IsNull(mode.transform.Find("ShotMarker"));
        Assert.IsNull(mode.transform.Find("BullFx"));
    }
}

public partial class AndalusiaBullChartTests
{
    [Test]
    public void LongClearanceIncludesTheMiddleAndLateWindow()
    {
        var chart = new BullChartData();
        chart.shots.Add(new BullShotData { time = 90346.8f, kind = "split", x = .8f });
        var note = new NoteData { time = 89134.40625f, x = 1.477f, y = .12f,
            color = "red", count = 3, lengthMs = 1389.411f };
        Assert.Throws<AssertionException>(() => CheckNoteClearance(new BullSession(chart, 0), note));
        chart.shots[0] = new BullShotData { time = 2230 };
        note = new NoteData { time = 1000, count = 2, lengthMs = 1000 };
        Assert.Throws<AssertionException>(() => CheckNoteClearance(new BullSession(chart, 0), note));
        note.count = 1;
        Assert.DoesNotThrow(() => CheckNoteClearance(new BullSession(chart, 0), note),
            "単発はlengthMsを滞在時間として扱わない");
        note.count = 3; note.lengthMs = 0;
        chart.shots[0].time = 2500;
        Assert.Throws<AssertionException>(() => CheckNoteClearance(new BullSession(chart, 0), note),
            "長さ未指定は(count-1)×0.7秒");
    }

    [Test]
    public void ReportedVerticalBladeAtBeat318IsRejected()
    {
        var session = new BullSession(Read("t", true).bull, 0);
        var charge = session.Charges.First(c => Math.Abs(c.Start - 318 * BeatMs / 1000) < .001 && c.Angle == 15);
        var blade = new BladeSample(new Vector2(-1.65f, -1.05f), new Vector2(-1.65f, .45f));
        Assert.IsTrue(BullRules.Touches(BullRules.ChargePosition(charge, 110.459), BullRules.ChargeRadius, blade));
        Assert.Throws<AssertionException>(() => CheckSpan(session, charge.Start - .25, charge.Start + .25,
            new Vector2(-1.65f, -.3f)));
    }

    [TestCase(.8f)]
    [TestCase(1f)]
    [TestCase(1.5f)]
    [TestCase(1.6f)]
    public void EveryGoldHasClearanceForHorizontalDiagonalAndVerticalBlades(float length)
    {
        var chart = Read("t", true);
        var session = new BullSession(chart.bull, 0);
        foreach (var n in chart.notes.Where(n => n.time >= ExtensionMs))
        {
            double lo = n.TimeSeconds - .25, hi = n.TimeSeconds + .25;
            var shots = session.Shots.Where(s => BullRules.HitStart(s) <= hi && BullRules.HitEnd(s) >= lo).ToArray();
            var charges = session.Charges.Where(c => c.Start <= hi && c.End >= lo).ToArray();
            for (int angle = 0; angle < 180; angle += 15)
            {
                var half = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)) * (length / 2);
                var a = new Vector2(n.x, n.y) - half;
                var b = new Vector2(n.x, n.y) + half;
                foreach (var c in charges)
                    Assert.Greater(BullRules.SegmentDistance(a, b,
                        BullRules.ChargePosition(c, Math.Max(lo, c.Start)),
                        BullRules.ChargePosition(c, Math.Min(hi, c.End))), BullRules.ChargeRadius + SafetyPadding);
                foreach (var s in shots)
                {
                    Assert.AreEqual(SaberHand.Any, s.Follow);
                    if (s.Kind == "spear")
                    {
                        BullRules.SpearEnds(s, out var c, out var d);
                        Assert.Greater(BullRules.SegmentDistance(a, b, c, d), BullRules.SpearRadius + SafetyPadding);
                    }
                    else
                        Assert.Greater(BullRules.DistanceToSegment(s.Position, a, b),
                            (s.Kind == "bomb" ? s.Blast : BullRules.Radius(s)) + SafetyPadding);
                }
            }
            CheckSpan(session, lo, hi, new Vector2(n.x, n.y));
        }
    }
}
