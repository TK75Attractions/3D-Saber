using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 下位28譜面を実際のローダー・生成・切断・採点へ通す。実機の運動評価とは分ける。
public class BeginnerChartTests
{
    public static readonly string[] Songs = {
        "2_23_AM", "Andalusia", "Aobasou", "BenkyouRemaster", "ElDorado",
        "Epilogue", "escapade", "logic", "ManicSonic", "Morning",
        "Phobia PARTⅠ", "RushE", "揺籠", "製作中"
    };
    [Serializable] sealed class CutRow
    {
        public int index;
        public string hand;
        public double[] timesMs;
        public string[] directions;
    }
    [Serializable] sealed class CutPlan { public double musicStartMs; public CutRow[] notes; }

    static IEnumerable<TestCaseData> Charts
    {
        get
        {
            foreach (string song in Songs)
                foreach (string difficulty in new[] { "easy", "normal" })
                    yield return new TestCaseData(song, difficulty);
        }
    }

    static double[] Times(NoteData n) => Enumerable.Range(0, n.count)
        .Select(k => (double)n.time + (n.count > 1 ? n.lengthMs * (double)k / (n.count - 1) : 0)).ToArray();

    [TestCaseSource(nameof(Charts))]
    public void SavedCutsHaveShortRestsAndSparsePreparedFlicks(string song, string difficulty)
    {
        var chart = ChartLoader.LoadFromStreamingAssets(song, difficulty);
        var times = chart.notes.SelectMany(Times).OrderBy(t => t).ToArray();
        var distinct = new List<double>();
        foreach (double t in times)
            if (distinct.Count == 0 || t - distinct.Last() > .1) distinct.Add(t);
        Assert.IsNotEmpty(distinct);
        for (int i = 1; i < distinct.Count; i++)
        {
            double gap = distinct[i] - distinct[i - 1];
            Assert.Less(gap, 5000, "ロングを含む実カット間に5秒以上の休みを作らない");
            if (difficulty == "easy")
                Assert.GreaterOrEqual(gap, 499, "両手交互でもEasyの連打を速くしない");
        }
        var flicks = chart.notes.Where(n => n.IsDirection).ToArray();
        Assert.GreaterOrEqual(flicks.Length, 1, "各譜面で最低1回は方向切りを使う");
        Assert.LessOrEqual(flicks.Length, Math.Max(1, Math.Ceiling(chart.notes.Count * (difficulty == "easy" ? .05 : .12))));
        foreach (var flick in flicks)
        {
            Assert.Contains(flick.direction, new[] { "up", "down" });
            Assert.AreEqual(1, flick.count);
            double before = distinct.Where(t => t < flick.time - .1).DefaultIfEmpty(flick.time - 10000).Max();
            double after = distinct.Where(t => t > flick.time + .1).DefaultIfEmpty(flick.time + 10000).Min();
            Assert.GreaterOrEqual(Math.Min(flick.time - before, after - flick.time), difficulty == "easy" ? 739 : 479);
        }
        foreach (var note in chart.notes.Where(n => n.IsLong))
        {
            Assert.AreEqual(2, note.count, "下位はゆっくり2回切る形");
            Assert.GreaterOrEqual(note.lengthMs, 499);
            Assert.AreEqual("none", note.direction);
        }
        string folder = Path.Combine(Application.streamingAssetsPath, "Songs", song);
        if (difficulty == "normal")
            Assert.AreEqual(File.ReadAllText(Path.Combine(folder, "chart_normal.json")),
                File.ReadAllText(Path.Combine(folder, "chart.json")));
    }

    [TestCaseSource(nameof(Charts))]
    public void EveryPlannedCutSpawnsCompletesAndScores(string song, string difficulty)
    {
        var random = UnityEngine.Random.state;
        var root = new GameObject("BeginnerChartVerification");
        try
        {
            var chart = ChartLoader.LoadFromStreamingAssets(song, difficulty);
            var plan = JsonUtility.FromJson<CutPlan>(File.ReadAllText(Path.Combine(
                "Tools", "ChartDesign", "Beginner20261021", "plans", song, difficulty + ".json")));
            Assert.AreEqual(chart.notes.Count, plan.notes.Length);
            for (int i = 0; i < chart.notes.Count; i++)
            {
                var expected = Times(chart.notes[i]);
                Assert.AreEqual(i, plan.notes[i].index);
                Assert.AreEqual(expected.Length, plan.notes[i].timesMs.Length);
                for (int k = 0; k < expected.Length; k++)
                    Assert.AreEqual(expected[k], plan.notes[i].timesMs[k], .03, "保存後のカット時刻");
            }
            var spawner = root.AddComponent<NoteSpawner>();
            var score = root.AddComponent<ScoreManager>();
            score.Bind(spawner);
            spawner.noteRoot = root.transform;
            spawner.buildTimingCues = false;
            spawner.simultaneousGuideEnabled = false;
            var prefab = new GameObject("Note", typeof(CuttableNote));
            prefab.transform.SetParent(root.transform);
            prefab.GetComponent<CuttableNote>().shatterDebrisCount = 0;
            spawner.notePrefab = spawner.notePrefabRed = spawner.notePrefabBlue = prefab;
            var spawned = new Dictionary<int, CuttableNote>();
            int next = 0;
            spawner.OnNoteSpawned += n => {
                spawned[next++] = n;
                if (n.countLabel != null) n.countLabel.transform.SetParent(root.transform, true);
            };
            spawner.SetChart(chart);
            var cuts = plan.notes.SelectMany(row => row.timesMs.Select((time, k) =>
                (row.index, seconds: (time + chart.offsetMs) / 1000, direction: row.directions[k], row.hand)))
                .OrderBy(c => c.seconds).ToArray();
            Assert.Less(cuts.First().seconds * 1000 - chart.offsetMs - plan.musicStartMs, 5000,
                "音のある導入も5秒以上待たせない");
            foreach (var cut in cuts)
            {
                spawner.Tick(cut.seconds);
                Assert.IsTrue(spawned.TryGetValue(cut.index, out var note), "生成漏れ " + cut.index);
                Assert.IsNotNull(note, "切断前の退場 " + cut.index);
                var direction = CutDirectionHelper.ToVector(CutDirectionHelper.Parse(cut.direction)).normalized;
                Assert.IsTrue(note.TryCutWithCameraTiming(note.transform.position,
                    new Vector3(direction.x, direction.y, 0) * 12,
                    SaberHandHelper.FromColor(cut.hand), cut.seconds, .866f), "切断失敗 " + cut.index);
            }
            spawner.Tick(cuts.Last().seconds + 5);
            Assert.AreEqual(cuts.Length, score.PerfectCount);
            Assert.AreEqual(0, score.MissCount);
            Assert.AreEqual(cuts.Length, score.MaxCombo);
            Assert.AreEqual(ScoreManager.NoteScore, score.Score);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Random.state = random;
        }
    }
}
