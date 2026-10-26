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

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
        if (prefab != null) Object.DestroyImmediate(prefab);
    }

    [Test]
    public void LinearSlideClampsTimeAndDoesNotAccumulateError()
    {
        var start = new Vector2(-2, -.5f);
        var end = new Vector2(-1, 1);
        Assert.AreEqual(start, LongSlideRules.Position(start, end, -1, 2));
        Assert.AreEqual(start, LongSlideRules.Position(start, end, double.NaN, 2));
        Assert.AreEqual(start, LongSlideRules.Position(start, end, 1, 0));
        Assert.AreEqual((start + end) / 2, LongSlideRules.Position(start, end, 1, 2));
        Assert.AreEqual(end, LongSlideRules.Position(start, end, 2, 2));
        Assert.AreEqual(end, LongSlideRules.Position(start, end, 8, 2));
        for (int i = 0; i <= 20; i++)
            Assert.Less(Vector2.Distance(Vector2.Lerp(start, end, i / 20f),
                LongSlideRules.Position(start, end, i / 10.0, 2)), 1e-6f);
    }

    [Test]
    public void LoaderRequiresOptInAndExplicitLengthAndClampsInWorldSpace()
    {
        var plain = ChartLoader.Parse("{\"notes\":[{\"count\":4,\"lengthMs\":900}]}").notes.Single();
        Assert.IsFalse(LongSlideRules.Enabled(plain));
        var noLength = ChartLoader.Parse("{\"notes\":[{\"slide\":true,\"count\":4,\"slideToX\":1}]}").notes.Single();
        Assert.IsFalse(LongSlideRules.Enabled(noLength));
        var tap = ChartLoader.Parse("{\"notes\":[{\"slide\":true,\"count\":1,\"lengthMs\":900}]}").notes.Single();
        Assert.IsFalse(LongSlideRules.Enabled(tap));
        var scaled = ChartLoader.Parse(
            "{\"coordScale\":2,\"notes\":[{\"slide\":true,\"count\":4,\"lengthMs\":900,\"slideToX\":99,\"slideToY\":-99}]}");
        var n = scaled.notes.Single();
        Assert.AreEqual(LongSlideRules.MaxX, n.slideToX * scaled.coordScale);
        Assert.AreEqual(LongSlideRules.MinY, n.slideToY * scaled.coordScale);
        n.slideToX = float.NaN;
        LongSlideRules.Normalize(n, 2);
        Assert.IsFalse(n.slide);
    }

    [Test]
    public void JudgmentRefreshBodyCuesFloorAndCounterUseTheSameMovingPosition()
    {
        MakeSpawner();
        spawner.ConfigureFloorGuide(-2);
        var chart = OneNote(true);
        spawner.SetChart(chart);
        spawner.Tick(.5);
        var note = spawner.LiveNotes.Single();
        var start = note.LandingPosition;
        Assert.AreEqual(new Vector2(-1.8f, -.4f), start);
        spawner.Tick(1);
        Assert.AreEqual(start, note.LandingPosition);

        // 判定だけを先に進めても、古い着地点を判定に使わない。
        spawner.RefreshJudgmentWindows(1.45);
        var expected = new Vector2(-1.3f, -.1f);
        Assert.Less(Vector2.Distance(expected, note.transform.position), 1e-5f);
        Assert.Less(Vector2.Distance(expected, note.LandingPosition), 1e-5f);
        spawner.Tick(1.45);
        Assert.Less(Vector2.Distance(expected, note.TimingCue.GhostRoot.transform.position), 1e-5f);
        Assert.Less(Vector2.Distance(expected, note.TimingCue.ApproachRoot.transform.position), 1e-5f);
        Assert.AreEqual(note.transform.position + LongNoteCountStyle.WorldOffset, note.countLabel.transform.position);
        Assert.AreEqual(1, spawner.FloorGuide.LongProgressCount);
        var vertices = spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh.vertices.Skip(8).ToArray();
        Assert.IsNotEmpty(vertices);
        Assert.AreEqual(expected.x, (vertices.Min(v => v.x) + vertices.Max(v => v.x)) / 2, 1e-5f);

        note.RemainingCuts = 2;
        spawner.Tick(1.45);
        Assert.Less(Vector2.Distance(expected, note.LandingPosition), 1e-5f, "切断数で時間を進めない");
        spawner.Tick(1.9);
        Assert.Less(Vector2.Distance(new Vector2(-.8f, .2f), note.LandingPosition), 1e-5f);
    }

    [Test]
    public void OrdinaryLongKeepsItsOriginalXYAndLinger()
    {
        MakeSpawner();
        spawner.SetChart(OneNote(false));
        spawner.Tick(.5);
        var note = spawner.LiveNotes.Single();
        var start = note.LandingPosition;
        spawner.RefreshJudgmentWindows(1.6);
        spawner.Tick(1.6);
        Assert.IsFalse(note.HasSlide);
        Assert.IsFalse(spawner.HasSlideNotes);
        Assert.AreEqual(start, note.LandingPosition);
        Assert.AreEqual(start, (Vector2)note.transform.position);
        Assert.AreEqual(.9f, spawner.LingerSecondsFor(note), 1e-6f);
    }

    [Test]
    public void LIsRegisteredAfterXAndYStillBelongsToTwoTwentyThree()
    {
        var secret = RushESecretState.For("escapade");
        Assert.AreSame(RushESecretState.EscapadeLong, secret);
        Assert.AreEqual("L", secret.Difficulty);
        Assert.AreSame(secret, RushESecretState.ForDifficulty(" l "));
        Assert.AreSame(secret, RushESecretState.Charts.Last());
        Assert.AreEqual("X", RushESecretState.Charts[RushESecretState.Charts.Length - 2].Difficulty);
        Assert.AreEqual("2_23_AM", RushESecretState.ForDifficulty("Y").SongId);
        Assert.AreEqual(RushESecretState.Cobalt, GameHUDSkin.DifficultyColor("L"));
        Assert.AreEqual(RushESecretState.Cobalt, GameStartCountdown.AccentForDifficulty("l"));
        foreach (var other in RushESecretState.Charts)
            if (other != secret) Assert.AreNotEqual(other.Accent, secret.Accent);
        var state = new RushESecretState();
        state.Select("escapade");
        for (int i = 0; i < 10; i++) Assert.IsTrue(state.TryHit(i * .2));
        Assert.IsTrue(state.Reveal());
        Assert.IsTrue(state.Finish());
    }

    [Serializable] public class BuildInfo
    {
        public int cutMultiplier;
        public double switchMs;
        public string[] omitted;
    }
    [Serializable] public class TraceNote { public string _lSource; }
    [Serializable] public class Manifest { public BuildInfo _lBuild; public TraceNote[] notes; }

    [Test]
    public void GeneratedChartPreservesSourcesCountsLengthsBoundsAndHandRecovery()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "escapade", "chart_l.json");
        Assert.IsTrue(File.Exists(path), "先に gen_long.py --write。通常譜面へのフォールバックで通さない");
        string json = File.ReadAllText(path);
        var manifest = JsonUtility.FromJson<Manifest>(json);
        var chart = ChartLoader.LoadFromStreamingAssets("escapade", "L");
        var raw = JsonUtility.FromJson<ChartData>(json);
        var originals = new Dictionary<string, ChartData>
        {
            ["normal"] = ChartLoader.LoadFromStreamingAssets("escapade", "Normal"),
            ["hard"] = ChartLoader.LoadFromStreamingAssets("escapade", "Hard")
        };
        Assert.NotNull(manifest._lBuild);
        Assert.GreaterOrEqual(manifest._lBuild.cutMultiplier, 2);
        Assert.AreEqual(35388.4211, manifest._lBuild.switchMs, .001);
        Assert.AreEqual(10, chart.displayLevel);
        Assert.AreEqual(manifest.notes.Length, chart.notes.Count);
        Assert.IsNotEmpty(chart.notes);
        var seen = new HashSet<string>();
        for (int i = 0; i < chart.notes.Count; i++)
        {
            var n = chart.notes[i];
            string id = manifest.notes[i]._lSource;
            Assert.IsTrue(seen.Add(id), "元ノーツの重複");
            var tokens = id.Split(':');
            var original = originals[tokens[0]].notes[int.Parse(tokens[1])];
            Assert.AreEqual(n.time < manifest._lBuild.switchMs ? "normal" : "hard", tokens[0]);
            Assert.AreEqual(original.time, n.time);
            Assert.AreEqual(original.beat, n.beat);
            Assert.AreEqual(original.x, n.x);
            Assert.AreEqual(original.y, n.y);
            Assert.AreEqual(original.color, n.color);
            Assert.AreEqual(original.direction, n.direction);
            Assert.AreEqual("long", n.type);
            Assert.IsTrue(LongSlideRules.Enabled(n));
            Assert.AreEqual(raw.notes[i].slideToX, n.slideToX, "読込の丸めで範囲外を隠さない");
            Assert.AreEqual(raw.notes[i].slideToY, n.slideToY, "読込の丸めで範囲外を隠さない");
            // 回数は倍率どおり。ただし元のロングは切る間隔が 0.1 秒を下回らないよう抑える(gen_long.py の capped_count)。
            int expectedCuts = original.count * manifest._lBuild.cutMultiplier;
            if (original.count > 1) expectedCuts = System.Math.Max(2, System.Math.Min(expectedCuts, (int)(n.lengthMs / 100f) + 1));
            Assert.AreEqual(expectedCuts, n.count);
            Assert.Greater(n.lengthMs, 0);
            if (original.count > 1)
                Assert.AreEqual(original.LingerSeconds(NoteSpawner.DefaultSecondsPerLongCut) * 1000,
                    n.lengthMs, .01);
            else
                Assert.GreaterOrEqual(n.lengthMs / (n.count - 1), original.IsDirection ? 179.999 : 99.999);
            Assert.GreaterOrEqual(n.lengthMs / (n.count - 1), 99.999, "どのロングも 0.1 秒に1回より速く切らせない");
            Assert.LessOrEqual(n.time + n.lengthMs, 107850.01);
            Assert.That(n.x, Is.InRange(LongSlideRules.MinX, LongSlideRules.MaxX));
            Assert.That(n.y, Is.InRange(LongSlideRules.MinY, LongSlideRules.MaxY));
            Assert.That(n.slideToX, Is.InRange(LongSlideRules.MinX, LongSlideRules.MaxX));
            Assert.That(n.slideToY, Is.InRange(LongSlideRules.MinY, LongSlideRules.MaxY));
            Assert.LessOrEqual(Vector2.Distance(new Vector2(n.x, n.y), new Vector2(n.slideToX, n.slideToY))
                / (n.count - 1), LongSlideRules.MaxStepPerCut + 1e-5f);
        }
        // 間引きを隠さず、元譜面の全項目を「採用」か「省略」のどちらかで説明する。
        foreach (var id in manifest._lBuild.omitted)
        {
            Assert.IsTrue(seen.Add(id), "省略と採用が重複");
            var tokens = id.Split(':');
            Assert.AreEqual(1, originals[tokens[0]].notes[int.Parse(tokens[1])].count, "元ロングを省かない");
        }
        var expected = new HashSet<string>();
        foreach (var pair in originals)
            for (int i = 0; i < pair.Value.notes.Count; i++)
                if ((pair.Value.notes[i].time < manifest._lBuild.switchMs) == (pair.Key == "normal"))
                    expected.Add(pair.Key + ":" + i);
        CollectionAssert.AreEquivalent(expected, seen);
        foreach (string hand in new[] { "blue", "red" })
        {
            var notes = chart.notes.Where(n => Lane(n) == hand).ToArray();
            for (int i = 1; i < notes.Length; i++)
            {
                var a = notes[i - 1];
                var b = notes[i];
                float gap = b.time - a.time - a.lengthMs;
                Assert.GreaterOrEqual(gap, 119.98f);
                Assert.LessOrEqual(Vector2.Distance(new Vector2(a.slideToX, a.slideToY), new Vector2(b.x, b.y)),
                    gap / 1000 * 16 + .001f);
            }
        }
    }

    static string Lane(NoteData n) => n.color == "blue" || n.color == "red"
        ? n.color : n.x < 0 ? "blue" : "red";

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
    }

    static ChartData OneNote(bool slide)
    {
        var chart = new ChartData { bpm = 120 };
        chart.notes.Add(new NoteData
        {
            time = 1000, x = -1.8f, y = -.4f, color = "blue",
            type = "long", count = 4, lengthMs = 900,
            slide = slide, slideToX = -.8f, slideToY = .2f
        });
        return chart;
    }
}
