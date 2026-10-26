using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class EscapadeLongTests
{
    GameObject root, prefab;
    NoteSpawner spawner;
    UnityEngine.Random.State randomState;

    [SetUp]
    public void SetUp() => randomState = UnityEngine.Random.state;

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
        if (prefab != null) Object.DestroyImmediate(prefab);
        // 切り終えたロングの破片はシーンの直下に残るので、ほかのテストへ持ち越さない。
        foreach (var piece in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (piece != null) Object.DestroyImmediate(piece.gameObject);
        UnityEngine.Random.state = randomState;
    }

    [Test]
    public void PathInterpolatesEachLegAndLegacyEndpointStillWorks()
    {
        var start = new Vector2(-2, 0);
        var end = new Vector2(0, 1);
        var path = new List<SlidePathPoint>
        {
            new SlidePathPoint { ms = 1000, x = 0, y = 0 },
            new SlidePathPoint { ms = 2000, x = 0, y = 1 }
        };
        Vector2 At(double t) => LongSlideRules.Position(start, end, path, 1, t, 2);
        Assert.AreEqual(start, At(-1));
        Assert.AreEqual(start, At(double.NaN));
        Assert.AreEqual(start, At(0));
        Assert.AreEqual(new Vector2(-1, 0), At(.5));
        Assert.AreEqual(Vector2.zero, At(1));
        Assert.AreEqual(new Vector2(0, .5f), At(1.5));
        Assert.AreEqual(end, At(2));
        Assert.AreEqual(end, At(100));
        Assert.AreEqual(start, LongSlideRules.Position(start, end, path, 1, 1, 0));
        Assert.AreEqual(new Vector2(0, 1),
            LongSlideRules.Position(start * 2, end * 2, path, 2, 1.5, 2));
        for (int i = 0; i <= 20; i++)
        {
            double t = i / 10.0;
            Assert.AreEqual(LongSlideRules.Position(start, end, t, 2),
                LongSlideRules.Position(start, end, null, 1, t, 2));
        }
        Assert.AreEqual((start + end) / 2, LongSlideRules.Position(start, end, 1, 2));
        Assert.AreEqual(start, LongSlideRules.Position(start, end, double.NaN, 2));
        Assert.AreEqual(end, LongSlideRules.Position(start, end, 4, 2));
    }

    [Test]
    public void LoaderSortsClampsDeduplicatesAndKeepsArrivalAsTheOrigin()
    {
        var chart = ChartLoader.Parse(
            "{\"coordScale\":2,\"notes\":[{\"x\":-99,\"y\":99,\"slide\":true,\"count\":4,\"lengthMs\":1000," +
            "\"slidePath\":[{\"ms\":2000,\"x\":99,\"y\":-99},{\"ms\":500,\"x\":1,\"y\":0}," +
            "{\"ms\":-2,\"x\":1,\"y\":1},{\"ms\":500,\"x\":-1,\"y\":0.25}]}]}");
        var n = chart.notes.Single();
        Assert.IsTrue(LongSlideRules.Enabled(n));
        Assert.AreEqual(-3, n.x * 2);
        Assert.AreEqual(1.35f, n.y * 2);
        CollectionAssert.AreEqual(new[] { 500f, 1000f }, n.slidePath.Select(p => p.ms));
        Assert.AreEqual(-1, n.slidePath[0].x, "同時刻は後の点");
        Assert.AreEqual(3, n.slidePath[1].x * 2);
        Assert.AreEqual(-.95f, n.slidePath[1].y * 2);
        Assert.AreEqual(n.slidePath[1].x, n.slideToX);
        Assert.AreEqual(n.slidePath[1].y, n.slideToY);
        Assert.AreEqual(JsonUtility.ToJson(n),
            JsonUtility.ToJson(ChartLoader.Parse(JsonUtility.ToJson(chart)).notes.Single()), "正規化は冪等");
    }

    [Test]
    public void InvalidPointsAndPointLimitDoNotBreakLegacyFallback()
    {
        var n = new NoteData { slide = true, count = 4, lengthMs = 1000, slideToX = 1 };
        n.slidePath = new List<SlidePathPoint>
        {
            null, new SlidePathPoint { ms = float.NaN },
            new SlidePathPoint { ms = 100, x = float.PositiveInfinity },
            new SlidePathPoint { ms = -1, x = 2 }
        };
        LongSlideRules.Normalize(n, 1);
        Assert.IsTrue(n.slide);
        Assert.IsEmpty(n.slidePath);
        Assert.AreEqual(1, n.slideToX);
        n.slideToX = float.NaN;
        LongSlideRules.Normalize(n, 1);
        Assert.IsFalse(n.slide);
        var plain = ChartLoader.Parse("{\"notes\":[{\"count\":4,\"lengthMs\":900}]}").notes.Single();
        Assert.IsFalse(LongSlideRules.Enabled(plain));
        foreach (var json in new[]
        {
            "{\"notes\":[{\"slide\":true,\"count\":4}]}",
            "{\"notes\":[{\"slide\":true,\"count\":1,\"lengthMs\":900}]}"
        })
            Assert.IsFalse(LongSlideRules.Enabled(ChartLoader.Parse(json).notes.Single()));
        var many = Enumerable.Range(1, 700).Select(i => new SlidePathPoint { ms = i, x = 0, y = 0 });
        var normalized = LongSlideRules.NormalizePath(many, 700, 1);
        Assert.AreEqual(LongSlideRules.MaxPathPoints, normalized.Count);
        Assert.AreEqual(1, normalized[0].ms);
        Assert.AreEqual(700, normalized.Last().ms);
        Assert.IsTrue(normalized.Zip(normalized.Skip(1), (a, b) => a.ms < b.ms).All(v => v));
    }

    [Test]
    public void BodyCuesFloorAndNumberFollowPathWithoutGrowingInZ()
    {
        MakeSpawner();
        spawner.ConfigureFloorGuide(-2);
        var chart = OneNote(true, true);
        spawner.SetChart(chart);
        spawner.Tick(.5);
        var note = spawner.LiveNotes.Single();
        Assert.AreEqual(.8f, note.transform.localScale.z, 1e-6f);
        Assert.AreEqual("4", note.countLabel.text);
        spawner.Tick(1);
        Assert.AreEqual(new Vector2(-1.8f, -.4f), note.LandingPosition);
        spawner.RefreshJudgmentWindows(1.675);
        var expected = new Vector2(-.8f, -.1f);
        Assert.Less(Vector2.Distance(expected, note.LandingPosition), 1e-5f);
        Assert.Less(Vector2.Distance(expected, note.transform.position), 1e-5f);
        spawner.Tick(1.675);
        Assert.Less(Vector2.Distance(expected, note.TimingCue.GhostRoot.transform.position), 1e-5f);
        Assert.Less(Vector2.Distance(expected, note.TimingCue.ApproachRoot.transform.position), 1e-5f);
        Assert.AreEqual(note.transform.position + LongNoteCountStyle.WorldOffset, note.countLabel.transform.position);
        Assert.AreEqual(1, spawner.FloorGuide.LongProgressCount);
        var vertices = spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh.vertices.Skip(8).ToArray();
        Assert.IsNotEmpty(vertices);
        Assert.AreEqual(expected.x, (vertices.Min(v => v.x) + vertices.Max(v => v.x)) / 2, 1e-5f);
        note.RemainingCuts = 2;
        spawner.Tick(1.675);
        Assert.Less(Vector2.Distance(expected, note.LandingPosition), 1e-5f, "切断数で位置を進めない");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OrdinaryAndLegacySlideKeepTheirTiming(bool slide)
    {
        MakeSpawner();
        spawner.SetChart(OneNote(slide, false));
        spawner.Tick(1.45);
        var note = spawner.LiveNotes.Single();
        var expected = slide ? new Vector2(-1.3f, -.1f) : new Vector2(-1.8f, -.4f);
        Assert.Less(Vector2.Distance(expected, note.LandingPosition), 1e-5f);
        Assert.IsFalse(note.HasSlidePath);
        Assert.AreEqual(slide, note.HasSlide);
        Assert.AreEqual(spawner.judgeWindow + .9f, spawner.LateWindowFor(note), 1e-6f);
        if (slide) Assert.AreEqual(.8f, note.transform.localScale.z, 1e-6f);
        else Assert.Greater(note.transform.localScale.z, .8f);
    }

    [TestCase(0)]
    [TestCase(24)]
    [TestCase(999)]
    [TestCase(1000)]
    public void ThousandCutsUseOrdinaryScoringAndOnlyRemainderMissesAtEnd(int achieved)
    {
        MakeSpawner();
        var score = root.AddComponent<ScoreManager>();
        score.Bind(spawner);
        var chart = OneNote(true, true);
        var data = chart.notes.Single();
        data.time = 5000;
        data.count = 1000;
        data.color = "gold";
        data.lengthMs = 102850;
        data.slidePath[0].ms = 51425;
        data.slidePath[1].ms = 102850;
        spawner.SetChart(chart);
        spawner.Tick(5);
        var note = spawner.LiveNotes.Single();
        Assert.AreEqual("1000", note.countLabel.text);
        // ここは採点通知とひびの上限を検査する。実際の刃の接触はPlayModeで通す。
        for (int i = 0; i < achieved; i++)
            note.Cut(note.transform.position, Vector3.right * 5);
        Assert.AreEqual(achieved, score.PerfectCount);
        if (achieved < 1000)
        {
            Assert.AreEqual((1000 - achieved).ToString(), note.countLabel.text);
            Assert.LessOrEqual(note.GetComponentsInChildren<Transform>(true).Count(t => t.name == "Crack"),
                LongSlideRules.MaxPathCracks);
            spawner.Tick(107.849);
            Assert.IsFalse(note.IsMissed);
            Assert.IsTrue(note.IsJudgeable);
            Assert.AreEqual(0, score.MissCount);
        }
        spawner.Tick(107.851);
        Assert.AreEqual(1000 - achieved, score.MissCount);
        Assert.AreEqual(achieved, score.CutCount);
        Assert.AreEqual(90000 * achieved / 1000, score.Score);
        spawner.Tick(107.852);
        Assert.AreEqual(1000 - achieved, score.MissCount, "期限切れを二重計上しない");
        if (achieved == 1000)
        {
            score.FinalizeScore();
            Assert.AreEqual(100000, score.Score);
        }
    }

    [Test]
    public void LRegistrationDoesNotDependOnBeingTheLastSecret()
    {
        var secret = RushESecretState.For("escapade");
        Assert.AreSame(RushESecretState.EscapadeLong, secret);
        Assert.AreEqual("L", secret.Difficulty);
        Assert.AreSame(secret, RushESecretState.ForDifficulty(" l "));
        Assert.Contains(secret, RushESecretState.Charts);
        Assert.AreEqual("2_23_AM", RushESecretState.ForDifficulty("Y").SongId);
        Assert.AreEqual(RushESecretState.Cobalt, GameHUDSkin.DifficultyColor("L"));
        Assert.AreEqual(RushESecretState.Cobalt, GameStartCountdown.AccentForDifficulty("l"));
        var state = new RushESecretState();
        state.Select("escapade");
        for (int i = 0; i < 10; i++) Assert.IsTrue(state.TryHit(i * .2));
        Assert.IsTrue(state.Reveal());
        Assert.IsTrue(state.Finish());
    }

    [Test]
    public void GeneratedLIsUnmodifiedMixPlusOneSafeThousandCutGold()
    {
        string file = Path.Combine(Application.streamingAssetsPath, "Songs", "escapade", "chart_l.json");
        Assert.IsTrue(File.Exists(file), "先に gen_long.py --write");
        string json = File.ReadAllText(file);
        var chart = ChartLoader.LoadFromStreamingAssets("escapade", "L");
        var raw = JsonUtility.FromJson<ChartData>(json);
        var gold = chart.notes.Single(n => n.slide);
        var rawGold = raw.notes.Single(n => n.slide);
        Assert.AreEqual(rawGold.x, gold.x);
        Assert.AreEqual(rawGold.y, gold.y);
        Assert.AreEqual(rawGold.slideToX, gold.slideToX);
        Assert.AreEqual(rawGold.slideToY, gold.slideToY);
        Assert.AreEqual(rawGold.slidePath.Count, gold.slidePath.Count);
        for (int i = 0; i < gold.slidePath.Count; i++)
            Assert.AreEqual(JsonUtility.ToJson(rawGold.slidePath[i]), JsonUtility.ToJson(gold.slidePath[i]),
                "読込時の補正で生成器の範囲外・重複を隠さない");
        var source = JsonUtility.FromJson<ChartData>(JsonUtility.ToJson(chart));
        source.notes.RemoveAll(n => n.slide);
        MixedSecretChartAssertions.Check("escapade", source, "l", 35388.4211, 40, 209);
        Assert.AreEqual(249, source.notes.Count);
        Assert.AreEqual(13, source.notes.Count(n => n.IsLong));
        Assert.AreEqual(250, chart.notes.Count);
        Assert.AreEqual(14, chart.notes.Count(n => n.IsLong), "元ロング13本と追加1本");
        Assert.IsTrue(source.notes.All(n => !n.slide));
        Assert.AreEqual(10, chart.displayLevel);
        Assert.AreSame(gold, chart.notes[0]);
        Assert.AreEqual("gold", gold.color);
        Assert.AreEqual("long", gold.type);
        Assert.AreEqual("none", gold.direction);
        Assert.AreEqual(1000, gold.count);
        Assert.AreEqual(5000, gold.time + chart.offsetMs, .01);
        Assert.AreEqual(107850, gold.time + chart.offsetMs + gold.lengthMs, .01);
        Assert.GreaterOrEqual(gold.lengthMs, 100000);
        Assert.GreaterOrEqual(gold.lengthMs / gold.count, 100);
        Assert.GreaterOrEqual(gold.lengthMs / (gold.count - 1), 100);
        Assert.LessOrEqual(gold.time, source.notes.Min(n => n.time));
        Assert.LessOrEqual(source.notes.Min(n => n.time) - gold.time, 250);
        Assert.That(gold.slidePath.Count, Is.InRange(2, LongSlideRules.MaxPathPoints));
        Assert.AreEqual(gold.lengthMs, gold.slidePath.Last().ms);
        var points = new List<SlidePathPoint> { new SlidePathPoint { ms = 0, x = gold.x, y = gold.y } };
        points.AddRange(gold.slidePath);
        foreach (var p in points)
        {
            Assert.That(p.x, Is.InRange(LongSlideRules.MinX, LongSlideRules.MaxX));
            Assert.That(p.y, Is.InRange(LongSlideRules.MinY, LongSlideRules.MaxY));
        }
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            Assert.Greater(b.ms, a.ms);
            var pa = new Vector2(a.x, a.y);
            var pb = new Vector2(b.x, b.y);
            float speed = Vector2.Distance(pa, pb) * 1000 / (b.ms - a.ms);
            Assert.Greater(speed, 0, "停止区間を作らない");
            Assert.LessOrEqual(speed, 2.0001f);
            double from = gold.time + a.ms, to = gold.time + b.ms;
            foreach (var other in source.notes)
            {
                double left = Math.Max(from, other.time - 250.0);
                double right = Math.Min(to, other.time + other.LingerSeconds(.7f) * 1000 + 250.0);
                if (left > right) continue;
                var p = Vector2.Lerp(pa, pb, (float)((left - from) / (to - from)));
                var q = Vector2.Lerp(pa, pb, (float)((right - from) / (to - from)));
                Assert.GreaterOrEqual(SegmentDistance(p, q, new Vector2(other.x, other.y)), 1.4f,
                    "線分の途中でも元ノーツを避ける: " + other.time);
            }
        }
        Assert.GreaterOrEqual(points.Max(p => p.x) - points.Min(p => p.x), 4);
        Assert.GreaterOrEqual(points.Max(p => p.y) - points.Min(p => p.y), 1);
        int turns = 0;
        for (int i = 2; i < points.Count; i++)
        {
            var a = points[i - 2]; var b = points[i - 1]; var c = points[i];
            if (Math.Abs((b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x)) > .01f) turns++;
        }
        Assert.GreaterOrEqual(turns, 20);
        ScoreManager.CountUnits(chart, out int weight, out int combos);
        Assert.AreEqual(2293, weight);
        Assert.AreEqual(1263, combos);
    }

    static float SegmentDistance(Vector2 a, Vector2 b, Vector2 p)
    {
        var v = b - a;
        float u = v.sqrMagnitude == 0 ? 0 : Mathf.Clamp01(Vector2.Dot(p - a, v) / v.sqrMagnitude);
        return Vector2.Distance(a + u * v, p);
    }

    void MakeSpawner()
    {
        root = new GameObject("LongSlideTest");
        prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.transform.localScale = Vector3.one * .8f;
        prefab.SetActive(false);
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.simultaneousGuideEnabled = false;
        spawner.OnNoteSpawned += note =>
        {
            if (note.countLabel != null) note.countLabel.transform.SetParent(root.transform, true);
        };
    }

    static ChartData OneNote(bool slide, bool path)
    {
        var note = new NoteData
        {
            time = 1000, x = -1.8f, y = -.4f, color = "blue",
            type = "long", count = 4, lengthMs = 900,
            slide = slide, slideToX = -.8f, slideToY = .2f
        };
        if (path)
            note.slidePath = new List<SlidePathPoint>
            {
                new SlidePathPoint { ms = 450, x = -.8f, y = -.4f },
                new SlidePathPoint { ms = 900, x = -.8f, y = .2f }
            };
        return new ChartData { bpm = 120, notes = new List<NoteData> { note } };
    }
}
