using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 緩急(logic!!! の「S」、2026-10-26)の流れる速さの表。見かけの位置 X(t)=∫v と、それを使うノーツ・小節線の動き。
public class ScrollSpeedMapTests
{
    static List<ScrollSpeedData> Points(params (double time, double speed, double ramp)[] points)
    {
        var list = new List<ScrollSpeedData>();
        foreach (var p in points) list.Add(new ScrollSpeedData { time = (float)p.time, speed = (float)p.speed, ramp = (float)p.ramp });
        return list;
    }

    [Test]
    public void NoPointsMeansTheUsualConstantSpeed()
    {
        Assert.IsNull(ScrollSpeedMap.Build(null));
        Assert.IsNull(ScrollSpeedMap.Build(new List<ScrollSpeedData>()));
        // 最初の変わり目より前は 1 倍。
        var map = ScrollSpeedMap.Build(Points((5000, 2, 0)));
        Assert.AreEqual(-2, map.PositionAt(3), 1e-9);
        Assert.AreEqual(1, map.SpeedAt(4.9), 1e-9);
        Assert.AreEqual(2, map.SpeedAt(5), 1e-9);
        Assert.AreEqual(4, map.PositionAt(7), 1e-9);
    }

    [Test]
    public void AStopFreezesThePositionAndTheMoveResumesAfterIt()
    {
        var map = ScrollSpeedMap.Build(Points((1000, 0, 0), (2000, 1, 0)));
        Assert.AreEqual(0, map.PositionAt(1), 1e-9);
        Assert.AreEqual(0, map.PositionAt(1.6), 1e-9, "止まっている間は進まない");
        Assert.AreEqual(0, map.SpeedAt(1.5), 1e-9);
        Assert.AreEqual(.5, map.PositionAt(2.5), 1e-9);
        // 0.5 秒前(止まる前)から 2.5 秒のノーツを見ると、見かけの残りは止まる時間のぶん短い。
        Assert.AreEqual(1.0, map.Remaining(.5, 2.5), 1e-9);
        // 止まっている間に着く位置は、止まり始めの時刻を返す(そこで初めて着く)。
        Assert.AreEqual(1, map.TimeAtPosition(0), 1e-9);
        Assert.AreEqual(2.25, map.TimeAtPosition(.25), 1e-9);
    }

    [Test]
    public void ARampChangesTheSpeedSteadilyAndANewPointCutsIt()
    {
        var map = ScrollSpeedMap.Build(Points((0, 1, 0), (1000, 3, 1000)));
        Assert.AreEqual(2, map.SpeedAt(1.5), 1e-9);
        Assert.AreEqual(3, map.SpeedAt(2.5), 1e-9);
        Assert.AreEqual(1 + 2, map.PositionAt(2), 1e-9, "1→3 を1秒で: 平均2");
        Assert.AreEqual(3 + 3, map.PositionAt(3), 1e-9);

        // 2 秒かけて 3 倍へ向かう途中、1 秒後に止まる指示が来たら、そこまで上がった速さ(2)で切れる。
        var cut = ScrollSpeedMap.Build(Points((0, 1, 0), (1000, 3, 2000), (2000, 0, 0)));
        Assert.AreEqual(2, cut.SpeedAt(1.999), 1e-3);
        Assert.AreEqual(1 + 1.5, cut.PositionAt(2), 1e-9);
        Assert.AreEqual(2.5, cut.PositionAt(9), 1e-9, "最後が止まったまま");
        Assert.IsTrue(double.IsPositiveInfinity(cut.TimeAtPosition(3)), "止まったままなら着かない");
    }

    [Test]
    public void ThePositionNeverGoesBackAndTimeAtPositionIsItsInverse()
    {
        var map = ScrollSpeedMap.Build(Points((500, .4, 300), (1500, 0, 0), (2200, 2.5, 0), (3000, .7, 900), (5200, 1.6, 2400)));
        double last = double.NegativeInfinity;
        for (int i = 0; i <= 900; i++)
        {
            double t = i * .01;
            double x = map.PositionAt(t);
            Assert.GreaterOrEqual(x, last - 1e-12, t + " 秒");
            last = x;
            // 動いている所では、位置から時刻へ戻すと元の時刻。
            if (map.SpeedAt(t) > 1e-6 && (t < 1.5 || t > 2.2))
                Assert.AreEqual(t, map.TimeAtPosition(x), 1e-6, t + " 秒");
        }
    }

    [Test]
    public void LoaderClampsBrokenOrTooFastPoints()
    {
        var chart = ChartLoader.Parse("{\"bpm\":120,\"notes\":[],\"scroll\":[{\"time\":3000,\"speed\":9,\"ramp\":-5},{\"time\":1000,\"speed\":-1,\"ramp\":0},{\"time\":2000,\"speed\":1.5,\"ramp\":250}]}");
        Assert.AreEqual(3, chart.scroll.Count);
        Assert.AreEqual(1000, chart.scroll[0].time, "時刻順");
        Assert.AreEqual(0, chart.scroll[0].speed, "負の速さは止まる");
        Assert.AreEqual(1.5f, chart.scroll[1].speed);
        Assert.AreEqual(250, chart.scroll[1].ramp);
        Assert.AreEqual(ScrollSpeedMap.MaxSpeed, chart.scroll[2].speed, 1e-6);
        Assert.AreEqual(0, chart.scroll[2].ramp, "負のかける時間は 0");
        Assert.AreEqual(0, ChartLoader.Parse("{\"bpm\":120,\"notes\":[]}").scroll.Count, "ふつうの譜面は空");
    }

    GameObject root;
    NoteSpawner spawner;

    [SetUp]
    public void SetUp()
    {
        root = new GameObject("ScrollSpeedMapTests");
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.name = "ScrollPrefab";
        prefab.transform.SetParent(root.transform, false);
        prefab.AddComponent<CuttableNote>();
        prefab.SetActive(false);
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.simultaneousGuideEnabled = false;
        spawner.buildTimingCues = false;
        spawner.approachTime = 1;
        spawner.spawnZ = 20;
        spawner.judgeZ = 0;
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
    }

    static ChartData Chart(List<ScrollSpeedData> scroll, params float[] timesMs)
    {
        var chart = new ChartData { bpm = 120, scroll = scroll ?? new List<ScrollSpeedData>() };
        foreach (float t in timesMs) chart.notes.Add(new NoteData { time = t, x = .5f, y = 0, color = "red" });
        return chart;
    }

    [Test]
    public void NotesHangInTheAirDuringAStopAndStillLandOnTheBeat()
    {
        // 1〜2 秒は止まる。2.5 秒のノーツは 0.5 秒手前で止まって待つ。
        spawner.SetChart(Chart(Points((1000, 0, 0), (2000, 1, 0)), 2500));
        Assert.IsTrue(spawner.HasScroll);
        Assert.AreEqual(.5, spawner.SpawnTimeFor(2.5), 1e-9, "止まる時間のぶん早く出る");
        spawner.Tick(.4);
        Assert.AreEqual(0, spawner.AliveCount);
        spawner.Tick(.6);
        Assert.AreEqual(1, spawner.AliveCount);
        var note = spawner.LiveNotes[0];
        spawner.Tick(1.2);
        float frozen = note.transform.position.z;
        Assert.AreEqual(10, frozen, 1e-4);
        spawner.Tick(1.9);
        Assert.AreEqual(frozen, note.transform.position.z, 1e-6, "止まっている間は動かない");
        spawner.Tick(2.25);
        Assert.AreEqual(5, note.transform.position.z, 1e-4);
        spawner.Tick(2.5);
        Assert.AreEqual(0, note.transform.position.z, 1e-4, "届く時刻は音楽どおり");
        Assert.AreEqual(2.5, note.HitTime, 1e-9, "判定の時刻は変わらない");
    }

    [Test]
    public void FastSectionsShowNotesLaterAndSlowSectionsEarlier()
    {
        spawner.SetChart(Chart(Points((0, 2, 0), (5000, .5, 0)), 3000, 8000));
        Assert.AreEqual(2.5, spawner.SpawnTimeFor(3), 1e-9, "2倍なら 0.5 秒前に出る");
        Assert.AreEqual(6, spawner.SpawnTimeFor(8), 1e-9, "0.5倍なら 2 秒前に出る");
        spawner.Tick(2.4);
        Assert.AreEqual(0, spawner.AliveCount);
        spawner.Tick(2.75);
        Assert.AreEqual(1, spawner.AliveCount);
        Assert.AreEqual(10, spawner.LiveNotes[0].transform.position.z, 1e-4);
        spawner.Tick(6.5);
        Assert.AreEqual(1, spawner.AliveCount);
        Assert.AreEqual(15, spawner.LiveNotes[0].transform.position.z, 1e-4);
    }

    [Test]
    public void ChartsWithoutScrollMoveExactlyAsBefore()
    {
        spawner.SetChart(Chart(null, 2000));
        Assert.IsFalse(spawner.HasScroll);
        Assert.AreEqual(1, spawner.SpawnTimeFor(2), 1e-12);
        Assert.AreEqual(.7, spawner.VisualRemaining(2, 1.3), 1e-12);
        spawner.Tick(1.25);
        Assert.AreEqual(15, spawner.LiveNotes[0].transform.position.z, 1e-4);
    }

    [Test]
    public void BarLinesStopWithTheNotes()
    {
        var bar = new GameObject("BarLinePrefab");
        bar.transform.SetParent(root.transform, false);
        var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
        line.name = "Line";
        line.transform.SetParent(bar.transform, false);
        bar.SetActive(false);
        var lines = new GameObject("BarLines");
        lines.transform.SetParent(root.transform, false);
        var bars = root.AddComponent<BarLineSpawner>();
        bars.barLinePrefab = bar;
        bars.root = lines.transform;
        bars.approachTime = 1;
        bars.spawnZ = 20;
        bars.judgeZ = 0;
        // BPM120・4/4 なら小節線は 0・2・4・6 秒。2〜3 秒の間は止まる。2.2 秒には 2 秒の線(通過後)と 4 秒の線(奥)が出ている。
        bars.SetChart(Chart(Points((2000, 0, 0), (3000, 1, 0)), 6000));
        bars.Tick(2.2);
        Assert.AreEqual(2, bars.AliveCount);
        Transform lineOf4 = null;
        foreach (Transform child in lines.transform)
            if (child.gameObject.activeSelf && (lineOf4 == null || child.position.z > lineOf4.position.z)) lineOf4 = child;
        Assert.NotNull(lineOf4);
        float frozen = lineOf4.position.z;
        Assert.AreEqual(20, frozen, 1e-4, "止まる間の見かけの残りは 1 秒ぶん");
        bars.Tick(2.9);
        Assert.AreEqual(frozen, lineOf4.position.z, 1e-6, "小節線も止まる");
        bars.Tick(3.5);
        Assert.AreEqual(10, lineOf4.position.z, 1e-4);
    }
}
