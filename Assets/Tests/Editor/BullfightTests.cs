using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 闘牛(アンダルシアの「T」、2026-10-26)。突進と弾に刃が触れた剣は2秒しびれて切れない。
public class BullRulesTests
{
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

// アンダルシアの裏譜面 T。ノーツは Hard から突進の通る側と弾のまわりを抜いたもの。
// 譜面は C:\Users\shike\dev\tools\ChartAndalusiaBull\gen_bull.py --write で作る。
public class AndalusiaBullChartTests
{
    static ChartData Read(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "Andalusia", "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path), "必須の譜面がありません: " + path);
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    [Test]
    public void TheChartHasMoreVariedLateShotsAndFewerFlicks()
    {
        var bull = Read("t");
        var hard = Read("hard");
        Assert.AreEqual(9, bull.displayLevel);
        Assert.That(bull.bull.charges.Count, Is.InRange(10, 20));
        const double beatMs = 60000.0 / 173;
        double latterFrom = 151 * beatMs;
        var earlyShots = bull.bull.shots.Where(s => s.time < latterFrom).ToArray();
        var lateShots = bull.bull.shots.Where(s => s.time >= latterFrom).ToArray();
        var lateNotes = bull.notes.Where(n => n.time >= latterFrom).ToArray();
        Assert.That(bull.bull.shots.Count, Is.InRange(66, 120), "後半は種類を増やした弾幕");
        Assert.AreEqual(16, earlyShots.Length, "前半の弾は維持");
        Assert.That(lateShots.Length, Is.InRange(50, 70));
        Assert.Greater(lateShots.Length, earlyShots.Length);
        Assert.GreaterOrEqual(lateNotes.Length, 88, "後半のノーツを抜きすぎない");
        Assert.LessOrEqual(lateNotes.Count(n => n.type == "direction" && n.direction != "none"), 6);
        Assert.IsTrue(lateShots.Any(s => s.kind == "big"));
        Assert.IsTrue(lateShots.Any(s => s.kind == "" && s.lead > 0));
        Assert.IsTrue(lateShots.Any(s => s.kind == "split"));
        Assert.IsTrue(lateShots.Any(s => s.kind == "spear"));
        foreach (var s in bull.bull.shots)
        {
            Assert.That(s.x, Is.InRange(-2.2f, 2.2f));
            Assert.That(s.y, Is.InRange(-1.3f, 1.3f));
            Assert.IsFalse(bull.bull.charges.Any(c => s.time >= c.time - 350 && s.time <= c.endTime + 350),
                "弾の着地と突進を重ねない");
        }
        var introductions = new System.Func<BullShotData, bool>[]
        {
            s => s.kind == "big", s => s.kind == "" && s.lead > 0,
            s => s.kind == "split", s => s.kind == "spear"
        };
        foreach (var kind in introductions)
        {
            var first = lateShots.First(kind);
            Assert.IsFalse(bull.bull.shots.Any(s => System.Math.Abs(s.time - first.time) <= 3 * beatMs
                && !(kind(s) && System.Math.Abs(s.time - first.time) < .1)), "初登場の前後3拍は空ける");
        }
        foreach (var n in bull.notes)
        {
            var original = hard.notes.First(h => h.time == n.time && h.x == n.x && h.y == n.y);
            Assert.AreEqual(original.color, n.color, "色と金は変えない");
            Assert.AreEqual(original.count, n.count);
            Assert.AreEqual(original.lengthMs, n.lengthMs);
            if (n.time < latterFrom)
            {
                Assert.AreEqual(original.type, n.type);
                Assert.AreEqual(original.direction, n.direction);
            }
            if (n.time >= latterFrom && n.type == "direction" && n.direction != "none" && n.time < 293 * beatMs)
            {
                Assert.IsFalse(bull.bull.shots.Any(s => System.Math.Abs(s.time - n.time) <= 2 * beatMs));
                Assert.IsFalse(bull.bull.charges.Any(c => n.time >= c.time - 2 * beatMs && n.time <= c.endTime + 2 * beatMs));
            }
        }
        var hardKeys = new HashSet<string>(hard.notes.Select(n => n.time + "/" + n.x + "/" + n.y));
        foreach (var n in bull.notes) Assert.IsTrue(hardKeys.Contains(n.time + "/" + n.x + "/" + n.y), "Hard にあるノーツだけ");
        foreach (var c in bull.bull.charges)
        {
            Assert.That(System.Math.Abs(c.y), Is.GreaterThan(1f), "突進は上段か下段");
            foreach (var n in bull.notes.Where(n => n.time >= c.time && n.time <= c.endTime))
                Assert.IsTrue(c.y > 0 ? n.y <= -.25f : n.y >= .25f, n.time + "ms のノーツは突進の通らない側");
        }
        Assert.AreSame(RushESecretState.AndalusiaBull, RushESecretState.ForDifficulty("T"));
    }
}
