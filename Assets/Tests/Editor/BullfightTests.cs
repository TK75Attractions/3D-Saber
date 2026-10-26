using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 闘牛(アンダルシアの「T」、2026-10-26)。突進と弾に刃が触れた剣は2秒しびれて切れない。
public class BullRulesTests
{
    [TestCase(2.899, false)]
    [TestCase(2.95, true)]
    [TestCase(3.0, true)]
    [TestCase(3.5, true)]
    [TestCase(3.701, false)]
    public void MineHasAWindowBeforeArrivalAndStaysForSevenHundredMs(double time, bool hit)
    {
        var chart = new BullChartData();
        chart.shots.Add(new BullShotData { time = 3000, kind = "mine" });
        var session = new BullSession(chart, 0);
        session.Tick(time, Vertical(0, 0), default, null);
        Assert.AreEqual(hit ? 1 : 0, session.StunCount);
        Assert.AreEqual(3.7, session.LastTime, 1e-6);
    }

    [Test]
    public void MineIsConsumedOnceAndZeroStayEndsAtArrival()
    {
        var chart = new BullChartData();
        chart.shots.Add(new BullShotData { time = 3000, kind = "mine", stay = 2000 });
        var session = new BullSession(chart, .5);
        session.Tick(3.5, Vertical(0, 0), default, null);
        Assert.IsTrue(session.Shots[0].Spent);
        session.Tick(4.0, default, Vertical(0, 0), null);
        Assert.IsFalse(session.IsStunned(SaberHand.Left, 4));
        Assert.AreEqual(1, session.StunCount);
        Assert.AreEqual(5.5, session.LastTime, 1e-6);
        chart.shots[0].stay = 0;
        session = new BullSession(chart, 0);
        session.Tick(3.001, Vertical(0, 0), default, null);
        Assert.AreEqual(0, session.StunCount);
    }

    [Test]
    public void BombExpandsOnlyAfterArrivalUsingTheJudgeClock()
    {
        var chart = new BullChartData();
        chart.shots.Add(new BullShotData { time = 3000, kind = "bomb" });
        var session = new BullSession(chart, 0);
        var shot = session.Shots[0];
        session.Tick(2.999, Vertical(0, 0), default, null);
        Assert.AreEqual(0, session.StunCount);
        Assert.IsFalse(BullRules.Touches(shot, Vertical(0, 0), 2.999));
        Assert.AreEqual(.2f, BullRules.Radius(shot, 3), 1e-5);
        Assert.AreEqual(.55f, BullRules.Radius(shot, 3.15), 1e-5);
        Assert.AreEqual(.9f, BullRules.Radius(shot, 3.3), 1e-5);
        Assert.IsTrue(BullRules.Touches(shot, Vertical(.59f, 0), 3.15));
        Assert.IsFalse(BullRules.Touches(shot, Vertical(.61f, 0), 3.15));
        Assert.IsFalse(BullRules.Touches(shot, Vertical(0, 0), 3.301));
        session.Tick(3.15, Vertical(.59f, 0), default, null);
        session.Tick(3.2, default, Vertical(0, 0), null);
        Assert.AreEqual(1, session.StunCount);
        Assert.IsTrue(shot.Spent);
    }

    [Test]
    public void WaveMovesWithDefaultLeadAndSettlesBeforeTheWindow()
    {
        var s = new BullShot { Time = 3, Kind = "wave", Position = new Vector2(.4f, -.2f) };
        Assert.Greater(Vector2.Distance(s.Position, BullRules.ShotPosition(s, 2.25)), .1f);
        Assert.AreEqual(s.Position.y, BullRules.ShotPosition(s, 2.25).y, 1e-6);
        foreach (double t in new[] { 2.75, 2.9, 3.0, 3.1 })
            Assert.AreEqual(s.Position, BullRules.ShotPosition(s, t));
        s.Angle = 90;
        Assert.AreEqual(s.Position.x, BullRules.ShotPosition(s, 2.25).x, 1e-6);
        s.Amp = 0;
        Assert.AreEqual(s.Position, BullRules.ShotPosition(s, 2.25));
        s.Lead = .1;
        Assert.AreEqual(s.Position, BullRules.ShotPosition(s, 2.75));
    }

    [Test]
    public void LoaderDefaultsAndClampsMineBombAndWaveFields()
    {
        var parsed = ChartLoader.Parse("{\"notes\":[],\"bull\":{\"shots\":[{\"time\":1000},"
            + "{\"time\":2000,\"kind\":\" MINE \",\"stay\":-1,\"blast\":0,\"amp\":-1},"
            + "{\"time\":3000,\"kind\":\" BOMB \",\"stay\":9000,\"blast\":9,\"amp\":9},"
            + "{\"time\":4000,\"kind\":\" WAVE \"}]}}");
        Assert.AreEqual(700, parsed.bull.shots[0].stay);
        Assert.AreEqual(.9f, parsed.bull.shots[0].blast);
        Assert.AreEqual(.6f, parsed.bull.shots[0].amp);
        Assert.AreEqual("mine", parsed.bull.shots[1].kind);
        Assert.AreEqual(0, parsed.bull.shots[1].stay);
        Assert.AreEqual(.3f, parsed.bull.shots[1].blast);
        Assert.AreEqual(0, parsed.bull.shots[1].amp);
        Assert.AreEqual("bomb", parsed.bull.shots[2].kind);
        Assert.AreEqual(2000, parsed.bull.shots[2].stay);
        Assert.AreEqual(1.4f, parsed.bull.shots[2].blast);
        Assert.AreEqual(1.2f, parsed.bull.shots[2].amp);
        Assert.AreEqual("wave", parsed.bull.shots[3].kind);
        var raw = new BullChartData();
        raw.shots.Add(new BullShotData { stay = float.NaN, blast = float.PositiveInfinity, amp = float.NaN });
        var normalize = typeof(ChartLoader).GetMethod("NormalizeBull",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var normalized = (BullChartData)normalize.Invoke(null, new object[] { raw });
        Assert.AreEqual(700, normalized.shots[0].stay);
        Assert.AreEqual(.9f, normalized.shots[0].blast);
        Assert.AreEqual(.6f, normalized.shots[0].amp);
    }

    [Test]
    public void SegmentDistanceHandlesCrossingParallelEndsAndPoints()
    {
        Assert.AreEqual(0, BullRules.SegmentDistance(new Vector2(-1, 0), new Vector2(1, 0),
            new Vector2(0, -1), new Vector2(0, 1)), 1e-6);
        Assert.AreEqual(2, BullRules.SegmentDistance(Vector2.zero, Vector2.right,
            new Vector2(0, 2), new Vector2(1, 2)), 1e-6);
        Assert.AreEqual(Mathf.Sqrt(2), BullRules.SegmentDistance(Vector2.zero, Vector2.right,
            new Vector2(2, 1), new Vector2(3, 1)), 1e-6);
        Assert.AreEqual(0, BullRules.SegmentDistance(Vector2.zero, Vector2.right,
            new Vector2(.5f, 0), new Vector2(2, 0)), 1e-6);
        Assert.AreEqual(1, BullRules.SegmentDistance(Vector2.zero, Vector2.zero,
            Vector2.right, Vector2.right), 1e-6);
    }

    [Test]
    public void BigShotsUseTheirOwnRadiusAndAreSpentOnce()
    {
        Assert.AreEqual(.45f, BullRules.BigRadius);
        var chart = new BullChartData();
        chart.shots.Add(new BullShotData { time = 3000, kind = "big" });
        var session = new BullSession(chart, 0);
        var blade = Vertical(.49f, 0);
        Assert.IsFalse(BullRules.Touches(Vector2.zero, BullRules.ShotRadius, blade));
        Assert.IsTrue(BullRules.Touches(session.Shots[0], blade));
        Assert.IsFalse(BullRules.Touches(session.Shots[0], Vertical(.51f, 0)));
        var events = new List<BullEvent>();
        session.Tick(2.8, blade, default, events);
        Assert.AreEqual(0, session.StunCount);
        session.Tick(3, blade, default, events);
        Assert.AreEqual(1, session.StunCount);
        session.Tick(3.05, default, blade, events);
        Assert.IsFalse(session.IsStunned(SaberHand.Left, 3.05), "使った弾はもう当たらない");
    }

    [Test]
    public void SpearCollisionUsesItsAngleAndTheBladeSegment()
    {
        foreach (float angle in new[] { 0f, 90f })
        {
            var chart = new BullChartData();
            chart.shots.Add(new BullShotData { time = 3000, kind = "spear", angle = angle });
            var session = new BullSession(chart, 0);
            session.Tick(3, Vertical(.5f, 0, .02f), default, null);
            Assert.AreEqual(angle == 0 ? 1 : 0, session.StunCount);
        }
        var spear = new BullShot { Kind = "spear", Position = Vector2.zero, Angle = 45 };
        Assert.IsTrue(BullRules.Touches(spear,
            new BladeSample(new Vector2(-1, 1), new Vector2(1, -1))), "端が遠くても交差すれば当たる");
        spear.Angle = 0;
        Assert.IsTrue(BullRules.Touches(spear, new BladeSample(new Vector2(-.3f, .10f), new Vector2(.3f, .10f))));
        Assert.IsFalse(BullRules.Touches(spear, new BladeSample(new Vector2(-.3f, .12f), new Vector2(.3f, .12f))));
    }

    [TestCase("")]
    [TestCase("split")]
    public void CurvesWaitThenEaseOutAndStopBeforeTheHitWindow(string kind)
    {
        var chart = new BullChartData();
        chart.shots.Add(new BullShotData { time = 3000, kind = kind, fromX = -2, fromY = 1,
            x = 1, y = -.5f, lead = 500 });
        var session = new BullSession(chart, 0);
        var shot = session.Shots[0];
        Assert.AreEqual(shot.From, BullRules.ShotPosition(shot, 2.4));
        Assert.AreEqual(shot.From, BullRules.ShotPosition(shot, 2.5));
        Assert.Less(Vector2.Distance(Vector2.Lerp(shot.From, shot.Position, .75f),
            BullRules.ShotPosition(shot, 2.625)), 1e-5);
        foreach (double t in new[] { 2.75, 2.91, 3.0, 3.09 })
            Assert.AreEqual(shot.Position, BullRules.ShotPosition(shot, t));
        session.Tick(2.6, Vertical(1, -.5f), default, null);
        Assert.AreEqual(0, session.StunCount, "寄っている途中は当たらない");
        session.Tick(3, Vertical(-2, 1), default, null);
        Assert.AreEqual(0, session.StunCount, "出発点は判定しない");
        session.Tick(3, Vertical(1, -.5f), default, null);
        Assert.AreEqual(1, session.StunCount);
        shot.Lead = 1;
        Assert.AreEqual(shot.From, BullRules.ShotPosition(shot, 2), "見え始めから曲がる");
        shot.Lead = 0;
        Assert.AreEqual(shot.Position, BullRules.ShotPosition(shot, 0), "旧譜面は直進");
        shot.Lead = .1;
        Assert.AreEqual(shot.Position, BullRules.ShotPosition(shot, 2.75), "短すぎる lead でも窓の前に止まる");
    }

    [Test]
    public void LoaderNormalizesNewShotFieldsAndPreservesOldDefaults()
    {
        var parsed = ChartLoader.Parse("{\"bpm\":173,\"notes\":[],\"bull\":{\"shots\":["
            + "{\"time\":1000},{\"time\":2000,\"kind\":\" BIG \",\"fromX\":9,\"fromY\":-9,\"lead\":500,\"angle\":45,\"follow\":\"red\"}]}}");
        var old = parsed.bull.shots[0];
        Assert.AreEqual("", old.kind);
        Assert.AreEqual(0, old.lead);
        Assert.AreEqual(0, old.fromX);
        Assert.AreEqual(0, old.fromY);
        Assert.AreEqual(0, old.angle);
        var big = parsed.bull.shots[1];
        Assert.AreEqual("big", big.kind);
        Assert.AreEqual(3.4f, big.fromX);
        Assert.AreEqual(-1.8f, big.fromY);
        Assert.AreEqual(500, big.lead);
        Assert.AreEqual(45, big.angle);
        Assert.AreEqual("", big.follow);
        // JSONに書けないNaN/Infinityも、正規化そのものに渡して検査する。
        var raw = new BullChartData();
        raw.shots.Add(new BullShotData { kind = "???", fromX = float.NaN, fromY = float.PositiveInfinity,
            lead = float.NaN, angle = float.NegativeInfinity, follow = " BLUE " });
        raw.shots.Add(new BullShotData { kind = " SpLiT ", lead = -1, follow = "red" });
        raw.shots.Add(new BullShotData { kind = " SPEAR ", lead = float.PositiveInfinity });
        raw.shots.Add(new BullShotData { lead = 500, follow = "red" });
        var method = typeof(ChartLoader).GetMethod("NormalizeBull",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var normalized = (BullChartData)method.Invoke(null, new object[] { raw });
        Assert.AreEqual("", normalized.shots[0].kind);
        Assert.AreEqual(0, normalized.shots[0].fromX);
        Assert.AreEqual(0, normalized.shots[0].fromY);
        Assert.AreEqual(0, normalized.shots[0].lead);
        Assert.AreEqual(0, normalized.shots[0].angle);
        Assert.AreEqual("blue", normalized.shots[0].follow);
        Assert.AreEqual("split", normalized.shots[1].kind);
        Assert.AreEqual(0, normalized.shots[1].lead);
        Assert.AreEqual("", normalized.shots[1].follow);
        Assert.AreEqual("spear", normalized.shots[2].kind);
        Assert.AreEqual(0, normalized.shots[2].lead);
        Assert.AreEqual("", normalized.shots[3].follow);
    }

    static BullChartData Chart(params BullChargeData[] charges) => new BullChartData { charges = charges.ToList() };

    static BladeSample Vertical(float x, float y, float half = .5f) => new BladeSample(new Vector2(x, y - half), new Vector2(x, y + half));

    [TestCase(30f, 1)]
    [TestCase(-55f, -1)]
    [TestCase(90f, 1)]
    [TestCase(90f, -1)]
    public void AngledChargesFollowTheirLineAndDirection(float angle, int dir)
    {
        var c = new BullSession(Chart(new BullChargeData
            { time = 1000, endTime = 3000, x = .7f, y = -.2f, angle = angle, dir = dir }), .5).Charges[0];
        var center = new Vector2(.7f, -.2f);
        Assert.AreEqual(angle, c.Angle);
        Assert.Less(Vector2.Distance(center, BullRules.ChargePosition(c, 2.5)), 1e-5f);
        float radians = angle * Mathf.Deg2Rad;
        Vector2 axis = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
        Vector2 before = BullRules.ChargePosition(c, 2) - center;
        Assert.AreEqual(-dir * BullRules.ChargeHalfSpan(c) * .5f, Vector2.Dot(before, axis), 1e-5f);
        Assert.AreEqual(0, Vector2.Dot(before, new Vector2(-axis.y, axis.x)), 1e-5f);
        Assert.AreEqual(BullRules.ChargePosition(c, c.Start), BullRules.ChargePosition(c, -10));
        Assert.AreEqual(BullRules.ChargePosition(c, c.End), BullRules.ChargePosition(c, 10));
    }

    [Test]
    public void EveryAngleAndOffsetStartsAndEndsOutsideTheGate()
    {
        foreach (float x in new[] { -2f, 0f, 2f })
        foreach (float y in new[] { -1.6f, 0f, 1.6f })
        foreach (int dir in new[] { -1, 1 })
        for (int angle = -180; angle <= 180; angle++)
        {
            var c = new BullCharge { Start = 1, End = 2, X = x, Y = y, Angle = angle, Dir = dir };
            foreach (double t in new[] { c.Start, c.End })
            {
                Vector2 p = BullRules.ChargePosition(c, t);
                Assert.IsTrue(Mathf.Abs(p.x) >= BullRules.ChargeSpan - 1e-4f
                    || Mathf.Abs(p.y) >= BullRules.ChargeSpan - 1e-4f, angle + "度の端");
                Assert.AreEqual(0, BullRules.ChargeLaneDistance(c, p, p), 1e-4f);
            }
            Vector2 motion = BullRules.ChargePosition(c, 2) - BullRules.ChargePosition(c, 1);
            Assert.Greater(Vector2.Dot(motion, BullRules.ChargeAxis(c)) * dir, 0);
        }
    }

    [Test]
    public void ChargeLoaderPreservesOldDefaultsAndClampsNewFields()
    {
        var chart = ChartLoader.Parse("{\"notes\":[],\"bull\":{\"charges\":["
            + "{\"time\":1000,\"endTime\":2000,\"y\":-1.15,\"dir\":1},"
            + "{\"time\":3000,\"endTime\":4000,\"x\":9,\"angle\":270},"
            + "{\"time\":5000,\"endTime\":6000,\"x\":-9,\"angle\":-270}]}}");
        var old = chart.bull.charges[0];
        Assert.AreEqual(0, old.x);
        Assert.AreEqual(0, old.angle);
        var session = new BullSession(chart.bull, 0);
        Assert.AreEqual(-3.4f, BullRules.ChargePosition(session.Charges[0], 1).x, 1e-5f);
        Assert.AreEqual(3.4f, BullRules.ChargePosition(session.Charges[0], 2).x, 1e-5f);
        Assert.AreEqual(2, chart.bull.charges[1].x);
        Assert.AreEqual(180, chart.bull.charges[1].angle);
        Assert.AreEqual(-2, chart.bull.charges[2].x);
        Assert.AreEqual(-180, chart.bull.charges[2].angle);
        var raw = Chart(new BullChargeData { time = 1000, endTime = 2000,
            x = float.NaN, angle = float.PositiveInfinity });
        var method = typeof(ChartLoader).GetMethod("NormalizeBull",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var normalized = (BullChartData)method.Invoke(null, new object[] { raw });
        Assert.AreEqual(0, normalized.charges[0].x);
        Assert.AreEqual(0, normalized.charges[0].angle);
    }

    [Test]
    public void SimultaneousChargesHitBothHandsUsingRoundBodies()
    {
        var session = new BullSession(Chart(
            new BullChargeData { time = 1000, endTime = 2000, x = -1, angle = 90 },
            new BullChargeData { time = 1000, endTime = 2000, x = 1, angle = -35 }), 0);
        session.Tick(1.5, Vertical(-1, 0), Vertical(1, 0), null);
        Assert.AreEqual(2, session.StunCount);
        Assert.IsTrue(session.IsStunned(SaberHand.Right, 1.5));
        Assert.IsTrue(session.IsStunned(SaberHand.Left, 1.5));
        var c = session.Charges[0];
        Vector2 p = BullRules.ChargePosition(c, 1.5);
        Assert.IsTrue(BullRules.Touches(p, BullRules.ChargeRadius, Vertical(p.x + .44f, p.y)));
        Assert.IsFalse(BullRules.Touches(p, BullRules.ChargeRadius, Vertical(p.x + .46f, p.y)));
    }

    [Test]
    public void LaneDistanceUsesTheWholeLineAndDetectsBladeCrossings()
    {
        var c = new BullCharge { X = .5f, Y = .2f, Angle = 90 };
        Assert.AreEqual(0, BullRules.ChargeLaneDistance(c, Vector2.zero, Vector2.right), 1e-5f);
        Assert.AreEqual(.5f, BullRules.ChargeLaneDistance(c, Vector2.zero, Vector2.up), 1e-5f);
        c.Angle = 45;
        Assert.AreEqual(Mathf.Sqrt(.5f), BullRules.ChargeLaneDistance(c,
            new Vector2(.5f, 1.2f), new Vector2(.5f, 1.2f)), 1e-5f);
    }

    [Test]
    public void TheChargeCrossesTheGateFromEdgeToEdge()
    {
        var session = new BullSession(Chart(new BullChargeData { time = 1000, endTime = 2000, y = 1.15f, dir = 1, warn = 500 }), 0);
        var charge = session.Charges[0];
        Assert.AreEqual(.5, charge.WarnFrom, 1e-9);
        Assert.AreEqual(-BullRules.ChargeSpan, BullRules.ChargePosition(charge, 1).x, 1e-5);
        Assert.AreEqual(0, BullRules.ChargePosition(charge, 1.5).x, 1e-5);
        Assert.AreEqual(BullRules.ChargeSpan, BullRules.ChargePosition(charge, 2).x, 1e-5);
        Assert.AreEqual(1.15f, BullRules.ChargePosition(charge, 1.5).y, 1e-6);
        var back = new BullSession(Chart(new BullChargeData { time = 1000, endTime = 2000, y = -1.15f, dir = -1 }), 0).Charges[0];
        Assert.AreEqual(BullRules.ChargeSpan, BullRules.ChargePosition(back, 1).x, 1e-5, "右から左");
    }

    [Test]
    public void TouchingTheChargeStunsOnlyThatSaberForTwoSeconds()
    {
        var session = new BullSession(Chart(new BullChargeData { time = 1000, endTime = 2000, y = 1.15f, dir = 1 }), 0);
        var events = new List<BullEvent>();
        // 赤の刃は道の中(x=0 を 1.5 秒に通る)、青は下で休んでいる。
        var red = Vertical(0, 1.0f);
        var blue = Vertical(-1.5f, -1.0f);
        session.Tick(1.5, red, blue, events);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(BullEventKind.Stun, events[0].Kind);
        Assert.AreEqual(SaberHand.Right, events[0].Hand);
        Assert.IsTrue(session.IsStunned(SaberHand.Right, 2.0));
        Assert.IsFalse(session.IsStunned(SaberHand.Left, 2.0));
        Assert.AreEqual(1.0, session.StunRemaining(SaberHand.Right, 2.5), 1e-9);
        events.Clear();
        session.Tick(3.4, red, blue, events);
        Assert.AreEqual(0, events.Count, "まだしびれている");
        session.Tick(3.5, red, blue, events);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(BullEventKind.Recover, events[0].Kind);
        Assert.IsFalse(session.IsStunned(SaberHand.Right, 3.5));
        Assert.AreEqual(1, session.StunCount);
    }

    [Test]
    public void ABladeBelowTheLaneOrOutOfSightIsSafe()
    {
        var session = new BullSession(Chart(new BullChargeData { time = 1000, endTime = 2000, y = 1.15f, dir = 1 }), 0);
        var events = new List<BullEvent>();
        // 縦の刃(長さ 1.0)を真ん中に構えても、上段(1.15、半径 0.4)には届かない。
        session.Tick(1.5, Vertical(0, 0), default, events);
        Assert.AreEqual(0, events.Count);
        // 見失い中(Valid = false)は当たりにしない。
        session.Tick(1.5, default, default, events);
        Assert.AreEqual(0, events.Count);
    }

    [Test]
    public void AShotHitsOnlyAroundItsArrivalAndOnlyOnce()
    {
        var chart = new BullChartData();
        chart.shots.Add(new BullShotData { time = 3000, x = 0, y = 0 });
        var session = new BullSession(chart, 0);
        var events = new List<BullEvent>();
        var blade = Vertical(0, 0);
        session.Tick(2.85, blade, default, events);
        Assert.AreEqual(0, events.Count, "届く前(窓の外)は当たらない");
        session.Tick(3.05, blade, default, events);
        Assert.AreEqual(1, events.Count);
        Assert.IsTrue(session.Shots[0].Spent);
        // 少し横にずれた刃は当たらない(弾 0.2 + 刃 0.05)。
        var miss = new BullSession(chart, 0);
        events.Clear();
        miss.Tick(3.0, Vertical(.3f, 0), default, events);
        Assert.AreEqual(0, events.Count);
    }

    [Test]
    public void AFollowingShotHeadsForWhereThatSaberWasWhenItAppeared()
    {
        var chart = new BullChartData();
        chart.shots.Add(new BullShotData { time = 3000, x = 1.2f, y = 0, follow = "blue" });
        var session = new BullSession(chart, 0);
        session.PlaceFollowers(1.9, 1, Vertical(1, 1), Vertical(-.8f, .4f));
        Assert.AreEqual(new Vector2(1.2f, 0), session.Shots[0].Position, "見えはじめる前は動かさない");
        session.PlaceFollowers(2.0, 1, Vertical(1, 1), Vertical(-.8f, .4f));
        Assert.AreEqual(-.8f, session.Shots[0].Position.x, 1e-5);
        Assert.AreEqual(.4f, session.Shots[0].Position.y, 1e-5);
        session.PlaceFollowers(2.5, 1, Vertical(1, 1), Vertical(1.5f, -1));
        Assert.AreEqual(-.8f, session.Shots[0].Position.x, 1e-5, "一度決めたら追い直さない");
    }

    [Test]
    public void ASecondHitRightAfterRecoveringIsForgiven()
    {
        var chart = new BullChartData();
        chart.shots.Add(new BullShotData { time = 1000, x = 0, y = 0 });
        chart.shots.Add(new BullShotData { time = 3200, x = 0, y = 0 });
        chart.shots.Add(new BullShotData { time = 5000, x = 0, y = 0 });
        var session = new BullSession(chart, 0);
        var events = new List<BullEvent>();
        var blade = Vertical(0, 0);
        session.Tick(1.0, blade, default, events);
        session.Tick(3.0, blade, default, events);
        session.Tick(3.2, blade, default, events);
        Assert.AreEqual(1, events.Count(e => e.Kind == BullEventKind.Stun), "解けてすぐ(0.4秒以内)はもう一度しびれない");
        session.Tick(5.0, blade, default, events);
        Assert.AreEqual(2, events.Count(e => e.Kind == BullEventKind.Stun));
    }

    [Test]
    public void LoaderKeepsBullObjectsInOrderAndInBounds()
    {
        var chart = ChartLoader.Parse("{\"bpm\":173,\"notes\":[],\"bull\":{\"charges\":[{\"time\":3000,\"endTime\":2000,\"y\":1},"
            + "{\"time\":1000,\"endTime\":1700,\"y\":9,\"dir\":-5,\"warn\":1400}],\"shots\":[{\"time\":500,\"x\":7,\"y\":-9,\"follow\":\" RED \"}]}}");
        Assert.AreEqual(1, chart.bull.charges.Count, "長さの無い突進は捨てる");
        Assert.AreEqual(1.6f, chart.bull.charges[0].y, 1e-6);
        Assert.AreEqual(-1, chart.bull.charges[0].dir);
        Assert.AreEqual(2.4f, chart.bull.shots[0].x, 1e-6);
        Assert.AreEqual(-1.5f, chart.bull.shots[0].y, 1e-6);
        Assert.AreEqual("red", chart.bull.shots[0].follow);
        Assert.IsFalse(ChartLoader.Parse("{\"bpm\":120,\"notes\":[]}").bull.HasContent);
    }
}

public class BullfightModeTests
{
    GameObject root;
    SaberCutJudge right, left;
    BullfightMode mode;

    [SetUp]
    public void SetUp()
    {
        root = new GameObject("BullfightModeTests");
        right = new GameObject("RightJudge").AddComponent<SaberCutJudge>();
        right.transform.SetParent(root.transform, false);
        right.hand = SaberHand.Right;
        left = new GameObject("LeftJudge").AddComponent<SaberCutJudge>();
        left.transform.SetParent(root.transform, false);
        left.hand = SaberHand.Left;
        var chart = new BullChartData();
        chart.charges.Add(new BullChargeData { time = 1000, endTime = 2000, y = -1.15f, dir = 1, warn = 500 });
        mode = BullfightMode.Create(root.transform, chart, null, right, left);
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
    }

    [TestCase(35f, 1)]
    [TestCase(90f, -1)]
    public void ChargeBandChevronsHornsAndTrailTurnWithTheLane(float angle, int dir)
    {
        Object.DestroyImmediate(mode.gameObject);
        var chart = new BullChartData();
        chart.charges.Add(new BullChargeData
            { time = 1000, endTime = 2000, warn = 500, x = .5f, y = .2f, angle = angle, dir = dir });
        mode = BullfightMode.Create(root.transform, chart, null, right, left);
        mode.BladeOverride = hand => default(BladeSample);
        mode.Tick(.75, .75);
        var charge = mode.transform.Find("Charge");
        var lane = charge.Find("Lane");
        var c = mode.Session.Charges[0];
        var rotation = Quaternion.Euler(0, 0, angle);
        Assert.Less(Quaternion.Angle(rotation, lane.rotation), .001f);
        Assert.AreEqual(.5f, lane.position.x, 1e-5f);
        Assert.AreEqual(.2f, lane.position.y, 1e-5f);
        Assert.AreEqual(2 * BullRules.ChargeHalfSpan(c), lane.localScale.x, 1e-5f);
        Assert.AreEqual(2 * BullRules.ChargeRadius, lane.localScale.y, 1e-5f);
        foreach (var chevron in charge.Cast<Transform>().Where(t => t.name == "Chevron"))
        {
            Assert.Less(Quaternion.Angle(rotation, chevron.rotation), .001f);
            Vector2 p = chevron.position;
            Assert.AreEqual(0, BullRules.ChargeLaneDistance(c, p, p), 1e-5f);
            Vector2 forward = chevron.TransformVector(Vector3.right).normalized;
            Assert.Greater(Vector2.Dot(forward, BullRules.ChargeAxis(c)) * dir, .999f);
        }
        mode.Tick(1.25, 1.25);
        var body = charge.Find("Bull");
        var trail = charge.Find("Trail");
        Assert.Less(Vector2.Distance(BullRules.ChargePosition(c, 1.25), body.position), 1e-5f);
        Assert.Less(Quaternion.Angle(rotation, body.rotation), .001f);
        Assert.Less(Quaternion.Angle(rotation, trail.rotation), .001f);
        Vector2 horns = body.Find("Horns").TransformVector(Vector3.right).normalized;
        Vector2 tail = trail.TransformVector(Vector3.left).normalized;
        Assert.Greater(Vector2.Dot(horns, BullRules.ChargeAxis(c)) * dir, .999f);
        Assert.Less(Vector2.Dot(tail, BullRules.ChargeAxis(c)) * dir, -.999f);
    }

    [Test]
    public void LongerWarningsCanAppearBeforeEarlierChargesAndAllThreeSpawn()
    {
        Object.DestroyImmediate(mode.gameObject);
        var chart = new BullChartData();
        chart.charges.Add(new BullChargeData { time = 4000, endTime = 4500, warn = 0 });
        for (int i = 0; i < 3; i++)
            chart.charges.Add(new BullChargeData
                { time = 5000, endTime = 6000, warn = 4000, y = i - 1, angle = i * 30 });
        mode = BullfightMode.Create(root.transform, chart, null, right, left);
        mode.BladeOverride = hand => default(BladeSample);
        mode.Tick(1.5, 1.5);
        Assert.AreEqual(3, mode.transform.Cast<Transform>().Count(t => t.name == "Charge"));
        mode.Tick(4, 4);
        Assert.AreEqual(4, mode.transform.Cast<Transform>().Count(t => t.name == "Charge"));
        Assert.AreEqual(4, mode.Session.Charges[0].Start, "判定用の開始順は変えない");
    }

    [Test]
    public void TheStunnedSaberCannotCutUntilItRecovers()
    {
        // 青の刃を下段の道に置く。赤は上。
        mode.BladeOverride = hand => hand == SaberHand.Left
            ? new BladeSample(new Vector2(-.2f, -1.4f), new Vector2(.2f, -1.0f))
            : new BladeSample(new Vector2(1f, .5f), new Vector2(1f, 1.2f));
        mode.Tick(1.4, 1.4);
        Assert.IsFalse(left.Stunned, "まだ届いていない");
        mode.Tick(1.5, 1.5);
        Assert.IsTrue(left.Stunned, "触れた青はしびれる");
        Assert.IsFalse(right.Stunned);
        Assert.IsTrue(mode.IsStunned(SaberHand.Left));
        Assert.AreEqual(0, left.TryCut(1.6), "しびれている間は切れない");
        mode.Tick(3.49, 3.49);
        Assert.IsTrue(left.Stunned);
        mode.Tick(3.51, 3.51);
        Assert.IsFalse(left.Stunned, "2秒で解ける");
        Assert.AreEqual(1, mode.StunCount);
    }

    void ReplaceShots(params BullShotData[] shots)
    {
        Object.DestroyImmediate(mode.gameObject);
        mode = BullfightMode.Create(root.transform, new BullChartData { shots = shots.ToList() }, null, right, left);
        mode.BladeOverride = hand => default(BladeSample);
    }

    [Test]
    public void SplitChildrenShareOneVisibleParentAndOnePopPerGroup()
    {
        ReplaceShots(
            new BullShotData { time = 3000, kind = "split", fromX = -1, fromY = .2f, x = -1.5f, lead = 500 },
            new BullShotData { time = 3000, kind = "split", fromX = -1, fromY = .2f, x = -.5f, lead = 500 },
            new BullShotData { time = 3000, kind = "split", fromX = 1, fromY = .2f, x = .5f, lead = 500 },
            new BullShotData { time = 3000, kind = "split", fromX = 1, fromY = .2f, x = 1.5f, lead = 500 });
        mode.Tick(2.4, 2.4);
        var bodies = mode.transform.Cast<Transform>().Where(t => t.name == "Shot").ToArray();
        var markers = mode.transform.Cast<Transform>().Where(t => t.name == "ShotMarker").ToArray();
        Assert.AreEqual(4, bodies.Length);
        Assert.AreEqual(bodies[0].position, bodies[1].position);
        Assert.AreEqual(bodies[2].position, bodies[3].position);
        Assert.AreEqual(-1, bodies[0].position.x);
        Assert.AreEqual(.2f, bodies[0].position.y);
        Assert.AreEqual(2, bodies.Count(t => t.gameObject.activeSelf));
        Assert.AreEqual(BullRules.ShotRadius * 1.4f * 1.6f, bodies[0].localScale.x, 1e-5);
        Assert.IsTrue(markers.All(t => !t.gameObject.activeSelf));
        Assert.AreEqual(0, mode.SplitBurstCount);
        mode.Tick(2.5, 2.5);
        Assert.AreEqual(2, mode.SplitBurstCount);
        Assert.AreEqual(2, mode.transform.Cast<Transform>().Count(t => t.name == "BullFx"));
        Assert.IsTrue(markers.All(t => t.gameObject.activeSelf));
        mode.Tick(2.6, 2.6);
        Assert.AreNotEqual(bodies[0].position, bodies[1].position);
        mode.Tick(2.75, 2.75);
        var settled = bodies.Select(t => t.position).ToArray();
        mode.Tick(3.05, 3.05);
        for (int i = 0; i < bodies.Length; i++)
        {
            Assert.AreEqual(settled[i].x, bodies[i].position.x);
            Assert.AreEqual(settled[i].y, bodies[i].position.y);
        }
        Assert.AreEqual(2, mode.SplitBurstCount, "何度 Tick しても組ごとに1回");
        mode.Tick(3.2, 3.2);
        Assert.IsFalse(mode.transform.Cast<Transform>().Any(t => t.name == "Shot"), "EditModeでも安全に捨てる");
    }

    [Test]
    public void SpearHasAFixedShaftPapersAndABandMarker()
    {
        ReplaceShots(new BullShotData { time = 3000, kind = "spear", angle = 45 });
        mode.Tick(2.2, 2.2);
        var body = mode.transform.Find("Shot");
        var marker = mode.transform.Find("ShotMarker");
        Assert.NotNull(body.Find("Shaft"));
        Assert.NotNull(body.Find("Point"));
        Assert.AreEqual(3, body.Cast<Transform>().Count(t => t.name.StartsWith("Paper")));
        Assert.NotNull(marker.Find("Band"));
        Assert.IsNull(marker.Find("Ring"));
        Quaternion rotation = body.localRotation;
        Assert.Less(Quaternion.Angle(Quaternion.Euler(0, 0, 45), rotation), .001f);
        mode.Tick(2.9, 2.9);
        Assert.Less(Quaternion.Angle(rotation, body.localRotation), .001f);
        Assert.Less(Quaternion.Angle(rotation, marker.localRotation), .001f);
    }

    [Test]
    public void MineTimerShrinksAndTheMineDisappearsInEditMode()
    {
        ReplaceShots(new BullShotData { time = 3000, kind = "mine" });
        mode.Tick(2.2, 2.2);
        var timer = mode.transform.Find("ShotMarker/MineTimer");
        Assert.NotNull(timer);
        Assert.IsFalse(timer.GetComponent<MeshRenderer>().enabled);
        mode.Tick(3, 3);
        Assert.IsTrue(timer.GetComponent<MeshRenderer>().enabled);
        float start = timer.localScale.x;
        mode.Tick(3.35, 3.35);
        Assert.Less(timer.localScale.x, start);
        Assert.AreEqual(.42f, timer.localScale.x, 1e-5);
        mode.Tick(3.73, 3.73);
        Assert.IsNull(mode.transform.Find("Shot"));
        Assert.IsNull(mode.transform.Find("ShotMarker"));
        Assert.NotNull(mode.transform.Find("BullFx"));
    }

    [Test]
    public void BombShowsMaximumWarningBeforeItsExpandingRing()
    {
        ReplaceShots(new BullShotData { time = 3000, kind = "bomb", blast = 1.1f });
        mode.Tick(2.1, 2.1);
        var warning = mode.transform.Find("ShotMarker/BombWarning");
        var blast = mode.transform.Find("ShotMarker/BombBlast");
        Assert.NotNull(warning);
        Assert.IsTrue(warning.GetComponent<MeshRenderer>().enabled);
        Assert.AreEqual(1.1f, warning.localScale.x, 1e-5);
        Assert.IsFalse(blast.GetComponent<MeshRenderer>().enabled);
        mode.Tick(3, 3);
        Assert.IsFalse(mode.transform.Find("Shot").gameObject.activeSelf);
        Assert.IsTrue(blast.GetComponent<MeshRenderer>().enabled);
        Assert.AreEqual(.2f, blast.localScale.x, 1e-5);
        mode.Tick(3.15, 3.15);
        Assert.AreEqual(.65f, blast.localScale.x, 1e-5);
        Assert.AreEqual(1.1f, warning.localScale.x, 1e-5);
    }

    [Test]
    public void SpecialShotCollisionUsesJudgeTimeEvenWhenThePictureHasArrived()
    {
        ReplaceShots(new BullShotData { time = 3000, kind = "bomb" });
        mode.BladeOverride = hand => hand == SaberHand.Right
            ? new BladeSample(new Vector2(.55f, -.1f), new Vector2(.55f, .1f))
            : default(BladeSample);
        mode.Tick(3.15, 2.99);
        Assert.AreEqual(0, mode.StunCount);
        mode.Tick(3.15, 3.15);
        Assert.AreEqual(1, mode.StunCount);
    }

    [Test]
    public void WaveHasALandingRingAndAShortTailThatSettles()
    {
        ReplaceShots(new BullShotData { time = 3000, kind = "wave" });
        mode.Tick(2.25, 2.25);
        Assert.IsTrue(mode.transform.Find("ShotMarker/Ring").GetComponent<MeshRenderer>().enabled);
        var tail = mode.transform.Find("Shot/WaveTail0").GetComponent<MeshRenderer>();
        Assert.IsTrue(tail.enabled);
        mode.Tick(2.75, 2.75);
        Assert.IsFalse(tail.enabled);
    }

    [Test]
    public void EndingThePlayClearsTheStun()
    {
        mode.BladeOverride = hand => new BladeSample(new Vector2(0, -1.3f), new Vector2(0, -.9f));
        mode.Tick(1.5, 1.5);
        Assert.IsTrue(left.Stunned && right.Stunned);
        mode.EndPlay();
        Assert.IsFalse(left.Stunned);
        Assert.IsFalse(right.Stunned);
    }
}

// アンダルシア T v1.26。拍96より前はNormal、以後はHardから安全なノーツを残す。
// 譜面は C:\Users\shike\dev\tools\ChartAndalusiaBull\gen_bull.py --write で作る。
public class AndalusiaBullChartTests
{
    const double BeatMs = 60000.0 / 173, SwitchMs = 96 * BeatMs, LateMs = 151 * BeatMs;

    static ChartData Read(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "Andalusia", "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path), "必須の譜面がありません: " + path);
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    [Test]
    public void NotesComeFromNormalThenHardAndStayWithinRemovalBudgets()
    {
        var bull = Read("t");
        var normal = Read("normal");
        var hard = Read("hard");
        Assert.AreEqual(10, bull.displayLevel);
        foreach (var n in bull.notes)
        {
            var source = n.time < SwitchMs ? normal : hard;
            var original = source.notes.FirstOrDefault(h => h.time == n.time && h.x == n.x && h.y == n.y && h.color == n.color);
            Assert.NotNull(original, n.time + "ms のノーツが元譜面にない");
            Assert.AreEqual(original.count, n.count);
            Assert.AreEqual(original.lengthMs, n.lengthMs);
            if (n.time < LateMs || !original.IsDirection)
            {
                Assert.AreEqual(original.type, n.type);
                Assert.AreEqual(original.direction, n.direction);
            }
            else
                Assert.IsTrue((n.type == original.type && n.direction == original.direction)
                    || (n.type == "tap" && n.direction == "none"));
        }
        Assert.GreaterOrEqual(bull.notes.Count(n => n.time < SwitchMs),
            Mathf.CeilToInt(normal.notes.Count(n => n.time < SwitchMs) * .8f));
        Assert.GreaterOrEqual(bull.notes.Count(n => n.time >= SwitchMs && n.time < LateMs),
            Mathf.CeilToInt(hard.notes.Count(n => n.time >= SwitchMs && n.time < LateMs) * .7f));
        Assert.GreaterOrEqual(bull.notes.Count(n => n.time < LateMs),
            Mathf.CeilToInt(normal.notes.Count(n => n.time < LateMs) * .8f));
        Assert.GreaterOrEqual(bull.notes.Count(n => n.time >= LateMs),
            Mathf.CeilToInt(hard.notes.Count(n => n.time >= LateMs) * .7f));
        foreach (var a in bull.notes.Where(n => n.time < SwitchMs))
            Assert.IsFalse(bull.notes.Any(b => b.time >= SwitchMs && b.time - a.time <= 120
                && (a.color == b.color || a.color == "gold" || b.color == "gold")), "境目の同色が120ms以内");
        var flicks = bull.notes.Where(n => n.time >= LateMs && n.IsDirection).ToArray();
        Assert.LessOrEqual(flicks.Length, 6);
        var session = new BullSession(bull.bull, 0);
        foreach (var n in flicks)
        {
            double t = n.TimeSeconds, margin = 2 * BeatMs / 1000;
            Assert.IsFalse(session.Shots.Any(s => t >= BullRules.HitStart(s) - margin && t <= BullRules.HitEnd(s) + margin));
            Assert.IsFalse(session.Charges.Any(c => t >= c.Start - margin && t <= c.End + margin));
        }
        foreach (var c in session.Charges)
            foreach (var n in bull.notes.Where(n => n.TimeSeconds >= c.Start - .3 && n.TimeSeconds <= c.End + .3))
            {
                var p = new Vector2(n.x, n.y);
                Assert.GreaterOrEqual(BullRules.ChargeLaneDistance(c, p, p),
                    BullRules.ChargeRadius + .65f - 1e-5f, "突進の直線から余白を残す");
            }
        Assert.AreSame(RushESecretState.AndalusiaBull, RushESecretState.ForDifficulty("T"));
    }

    [Test]
    public void SevenKindsHaveQuietIntroductionsAndAvoidAllChargeWindows()
    {
        var bull = Read("t");
        var shots = bull.bull.shots;
        Assert.That(shots.Count, Is.LessThanOrEqualTo(200));
        Assert.That(shots.Count(s => s.time < LateMs), Is.InRange(30, 40));
        Assert.That(shots.Count(s => s.time >= LateMs), Is.InRange(90, 120));
        CollectionAssert.AreEquivalent(new[] { "", "big", "spear", "split", "mine", "bomb", "wave" },
            shots.Select(s => s.kind).Distinct().ToArray());
        Assert.IsTrue(shots.Any(s => s.follow == "red"));
        Assert.IsTrue(shots.Any(s => s.follow == "blue"));
        var intros = new List<System.Func<BullShotData, bool>>();
        foreach (string kind in new[] { "", "big", "spear", "split", "mine", "bomb", "wave" })
        {
            string k = kind;
            intros.Add(s => s.kind == k);
        }
        intros.Add(s => s.kind == "" && s.lead > 0);
        foreach (var predicate in intros)
        {
            var first = shots.First(predicate);
            Assert.IsFalse(shots.Any(s => System.Math.Abs(s.time - first.time) <= 3 * BeatMs + .2
                && !(predicate(s) && System.Math.Abs(s.time - first.time) < .1)), "初登場の前後3拍");
        }
        Assert.IsTrue(shots.Where(s => s.kind == "mine" || s.kind == "bomb" || s.kind == "wave")
            .All(s => s.time >= LateMs));
        var session = new BullSession(bull.bull, 0);
        foreach (var s in session.Shots)
        {
            Assert.That(s.Position.x, Is.InRange(-2.2f, 2.2f));
            Assert.That(s.Position.y, Is.InRange(-1.3f, 1.3f));
            Assert.IsFalse(session.Charges.Any(c =>
                BullRules.HitStart(s) <= c.End + .35 && BullRules.HitEnd(s) >= c.Start - .35),
                "地雷の滞在・爆弾の広がりも突進と重ねない");
        }
    }

    [Test]
    public void ChargesIntroduceAnglesThenPairsAndLateTriplesWithGapsBetweenGroups()
    {
        var charges = Read("t").bull.charges;
        var groups = charges.GroupBy(c => c.time).OrderBy(g => g.Key).ToArray();
        Assert.That(groups.Length, Is.InRange(18, 24));
        Assert.IsTrue(charges.Any(c => c.angle != 0));
        Assert.IsTrue(charges.Any(c => Mathf.Abs(c.angle) == 90));
        Assert.IsTrue(groups.Any(g => g.Count() == 2));
        Assert.That(groups.Count(g => g.Count() == 3), Is.InRange(1, 2));
        for (int i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            Assert.That(group.Count(), Is.InRange(1, group.Key >= 249 * BeatMs - .1 ? 3 : 2));
            Assert.AreEqual(1, group.Select(c => c.endTime).Distinct().Count());
            if (i > 0)
                Assert.GreaterOrEqual(group.Key - groups[i - 1].Max(c => c.endTime), 2000,
                    "同時の組の中を除き、前の組が抜けてから2秒以上");
            float warnBeats = group.Count() > 1 || group.Any(c => Mathf.Abs(c.angle) >= 80) ? 6
                : group.Any(c => Mathf.Abs(c.angle) >= 50) ? 5 : 4;
            foreach (var c in group)
            {
                Assert.That(c.x, Is.InRange(-2f, 2f));
                Assert.That(c.angle, Is.InRange(-180f, 180f));
                Assert.GreaterOrEqual(c.warn, warnBeats * BeatMs - .1);
            }
        }
        for (int i = 0; i < 2; i++)
        {
            Assert.AreEqual(1, groups[i].Count());
            var c = groups[i].Single();
            int beat = i == 0 ? 24 : 64;
            Assert.AreEqual(beat * BeatMs, c.time, .1);
            Assert.AreEqual((beat + 2) * BeatMs, c.endTime, .1);
            Assert.AreEqual(0, c.x);
            Assert.AreEqual(0, c.angle);
            Assert.AreEqual(i == 0 ? -1.15f : 1.15f, c.y);
            Assert.AreEqual(i == 0 ? 1 : -1, c.dir);
            Assert.AreEqual(4 * BeatMs, c.warn, .1);
        }
    }

    [Test]
    public void EveryTimeIntervalLeavesSpaceForBothBladesEvenWithFollowers()
    {
        var session = new BullSession(Read("t").bull, 0);
        var edges = session.Shots.SelectMany(s => new[] { BullRules.HitStart(s), BullRules.HitEnd(s) })
            .Concat(session.Charges.SelectMany(c => new[] { c.Start, c.End })).Distinct().OrderBy(t => t).ToArray();
        for (int i = 0; i < edges.Length; i++)
        {
            CheckSpan(session, edges[i], edges[i]);
            if (i + 1 < edges.Length) CheckSpan(session, edges[i], edges[i + 1]);
        }
    }

    [Test]
    public void SafetyRejectsThreeLanesThatLeaveNoRoom()
    {
        var chart = new BullChartData();
        foreach (float y in new[] { -.8f, 0f, .8f })
            chart.charges.Add(new BullChargeData { time = 1000, endTime = 2000, y = y });
        Assert.Throws<AssertionException>(() => CheckSpan(new BullSession(chart, 0), 1, 2));
    }

    static void CheckSpan(BullSession session, double lo, double hi)
    {
        bool Overlap(double start, double end) => lo == hi ? start <= lo && lo <= end : start < hi && end > lo;
        var shots = session.Shots.Where(s => Overlap(BullRules.HitStart(s), BullRules.HitEnd(s))).ToArray();
        var charges = session.Charges.Where(c => Overlap(c.Start, c.End)).ToArray();
        Assert.LessOrEqual(shots.Length + charges.Length, 8);
        int followers = shots.Count(s => s.Follow != SaberHand.Any);
        var safe = new List<Vector2>();
        // 長さ1.0の水平な刃。刃半径0.05に余白0.08を加えて検査する。
        for (int ix = 0; ix < 12; ix++)
        for (int iy = 0; iy < 9; iy++)
        {
            var p = new Vector2(-1.65f + ix * .3f, -1.2f + iy * .3f);
            var left = p + Vector2.left * .5f;
            var right = p + Vector2.right * .5f;
            if (safe.Any(q => Vector2.Distance(p, q) <= 1.7f)) continue;
            // 横切る間は直線全体を危険とみなす。端の時刻も斜め・縦・同時を同じ条件で調べる。
            if (charges.Any(c => BullRules.ChargeLaneDistance(c, left, right)
                <= BullRules.ChargeRadius + .13f)) continue;
            bool blocked = false;
            foreach (var s in shots.Where(s => s.Follow == SaberHand.Any))
            {
                if (s.Kind == "spear")
                {
                    BullRules.SpearEnds(s, out var a, out var b);
                    blocked |= BullRules.SegmentDistance(left, right, a, b) <= BullRules.SpearRadius + .13f;
                }
                else
                    blocked |= BullRules.DistanceToSegment(s.Position, left, right) <= (s.Kind == "bomb" ? s.Blast : BullRules.Radius(s)) + .13f;
            }
            if (!blocked) safe.Add(p);
        }
        // 候補の間隔は1.7、追う弾と刃の包絡円の直径は1.66。追う弾1発で失う候補は最大1つ。
        Assert.GreaterOrEqual(safe.Count, followers + 2, lo + "〜" + hi + "秒の両手用安全地帯");
    }
}
