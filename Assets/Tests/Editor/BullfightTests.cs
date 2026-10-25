using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 闘牛(アンダルシアの「T」、2026-10-26)。突進と弾に刃が触れた剣は2秒しびれて切れない。
public class BullRulesTests
{
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
    public void TheChartHasChargesAFewShotsAndHardNotesOutOfTheirWay()
    {
        var bull = Read("t");
        var hard = Read("hard");
        Assert.AreEqual(9, bull.displayLevel);
        Assert.That(bull.bull.charges.Count, Is.InRange(10, 20));
        Assert.That(bull.bull.shots.Count, Is.InRange(20, 60), "弾はちょっとだけ");
        Assert.Greater(bull.notes.Count, hard.notes.Count * 3 / 4, "ノーツが主役");
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
