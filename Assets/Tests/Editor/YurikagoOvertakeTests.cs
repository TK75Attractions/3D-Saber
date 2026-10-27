using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class YurikagoOvertakeTests
{
    GameObject root, prefab;
    NoteSpawner spawner;

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
        if (prefab != null) Object.DestroyImmediate(prefab);
    }

    void MakeSpawner()
    {
        root = new GameObject("OvertakeTest");
        spawner = root.AddComponent<NoteSpawner>();
        prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.SetActive(false);
        prefab.AddComponent<CuttableNote>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.approachTime = 1;
        spawner.spawnZ = 20;
        spawner.judgeZ = 0;
        spawner.reuseNotes = false;
    }

    static ChartData ReadP()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "揺籠", "chart_p.json");
        Assert.IsTrue(File.Exists(path), "先にgen_overtake.py --writeを実行する。フォールバックで通さない");
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    [Test]
    public void MissingFieldsRemainNormalAndInvalidValuesAreNormalized()
    {
        var chart = ChartLoader.Parse(
            "{\"bpm\":120,\"notes\":[{},"
            + "{\"time\":1,\"approachScale\":-2,\"laneX\":8},"
            + "{\"time\":2,\"approachScale\":0.01,\"laneX\":-99},"
            + "{\"time\":3,\"approachScale\":99,\"laneX\":99}]}");
        Assert.IsFalse(chart.notes[0].HasOvertake);
        Assert.IsFalse(chart.notes[1].HasOvertake);
        Assert.AreEqual(0, chart.notes[1].laneX);
        Assert.AreEqual(.5f, chart.notes[2].approachScale);
        Assert.AreEqual(-8f, chart.notes[2].laneX);
        Assert.AreEqual(4.5f, chart.notes[3].approachScale);
        Assert.AreEqual(8f, chart.notes[3].laneX);
        Assert.AreEqual(0, OvertakeRules.NormalizeScale(float.NaN));
        Assert.AreEqual(0, OvertakeRules.NormalizeScale(float.PositiveInfinity));
        Assert.AreEqual(0, OvertakeRules.NormalizeLane(float.NaN));
    }

    [TestCase(.5)]
    [TestCase(1)]
    [TestCase(4)]
    public void NearPathIsCommonAndFastNoteCanPassAnEarlierSpawn(double saved)
    {
        double approach = OvertakeRules.BaseApproach(saved);
        var landing = new Vector2(-1.1f, .2f);
        foreach (float authored in new[] { .5f, 1f, 4.5f })
        {
            float scale = OvertakeRules.FlightScale(authored, approach);
            double duration = OvertakeRules.Duration(approach, scale);
            Assert.GreaterOrEqual(duration - 1.0 / 30, .45);
            Assert.LessOrEqual(duration, 4.730001);
            foreach (double r in new[] { .73, .7, .45, .1, 0.0 })
            {
                var p = OvertakeRules.Position(landing, 8, r, approach, scale, 20, 0);
                Assert.AreEqual(landing.x, p.x);
                Assert.AreEqual(landing.y, p.y);
                Assert.AreEqual(20 / approach * r, p.z, 1e-5);
            }
            var atJoin = OvertakeRules.Position(landing, 8, .73, approach, scale, 20, 0);
            var justBefore = OvertakeRules.Position(landing, 8, .7300001, approach, scale, 20, 0);
            Assert.Less(Vector3.Distance(atJoin, justBefore), 1e-4);
        }

        float fast = OvertakeRules.FlightScale(.55f, approach);
        float slow = OvertakeRules.FlightScale(4.5f, approach);
        double fastSpawn = 10 - OvertakeRules.Duration(approach, fast);
        double slowSpawn = 10.5 - OvertakeRules.Duration(approach, slow);
        Assert.Less(slowSpawn, fastSpawn);
        double before = fastSpawn + .05;
        double after = 10 - .73 - .05;
        Assert.Greater(OvertakeRules.Remaining(10 - before, fast),
            OvertakeRules.Remaining(10.5 - before, slow));
        Assert.Less(OvertakeRules.Remaining(10 - after, fast),
            OvertakeRules.Remaining(10.5 - after, slow));
        Assert.Greater(10 - after, .73, "追い越しは共通区間より奥");
        foreach (double r in new[] { -1.0, 0, .73, 1, 2 })
            Assert.AreEqual(r, OvertakeRules.Remaining(r, 1), 1e-12);
    }

    [Test]
    public void SpawnOrderOffsetsJudgmentRefreshAndNormalResetAreIndependent()
    {
        MakeSpawner();
        var fast = new NoteData { time = 10000, x = -1, approachScale = .55f, laneX = -8 };
        var slow = new NoteData { time = 10500, x = 1, approachScale = 4.5f, laneX = 8 };
        var chart = new ChartData
        {
            bpm = 120, offsetMs = 12,
            notes = new List<NoteData> { fast, slow }
        };
        spawner.SetExtraOffsetSeconds(.25);
        spawner.SetChart(chart);
        var events = new List<double>();
        spawner.OnNoteSpawned += n =>
        {
            events.Add(n.HitTime);
            Assert.LessOrEqual(n.transform.position.z, spawner.spawnZ + 1e-4);
            Assert.IsTrue(n.HasOvertake);
        };

        double first = spawner.SpawnTimeFor(slow);
        Assert.AreEqual(first, spawner.EarliestSpawnTime);
        Assert.IsTrue(spawner.UpcomingNeeds(first - .5, SaberHand.Any));
        spawner.Tick(first + 1.0 / 30);
        Assert.AreEqual(1, spawner.AliveCount);
        Assert.AreEqual(0, spawner.NextIndex, "時刻順の先頭はまだ出ていない");
        Assert.AreEqual(spawner.EffectiveTime(slow), spawner.LiveNotes[0].HitTime);
        Assert.AreEqual(fast, chart.notes[0], "譜面の判定順を並べ替えない");

        double second = spawner.SpawnTimeFor(fast);
        spawner.Tick(second + 1.0 / 30);
        Assert.AreEqual(2, spawner.AliveCount);
        Assert.AreEqual(2, spawner.NextIndex);
        CollectionAssert.AreEqual(new[] {
            spawner.EffectiveTime(slow), spawner.EffectiveTime(fast)
        }, events);
        spawner.Tick(second + 1.0 / 30);
        Assert.AreEqual(2, events.Count, "同じ時計を再度渡しても二重生成しない");

        var moving = spawner.LiveNotes.Single(n => n.HitTime == spawner.EffectiveTime(fast));
        double hit = moving.HitTime;
        spawner.RefreshJudgmentWindows(hit - .1);
        Assert.AreEqual(-1, moving.transform.position.x);
        Assert.AreEqual(20 / 1.5 * .1, moving.transform.position.z, 1e-4);
        spawner.Tick(hit);
        Assert.AreEqual(new Vector3(-1, 0, 0), moving.transform.position);
        Assert.AreEqual(-1, moving.TimingCue.GhostRoot.transform.position.x);

        // 新しいSpawnerで、値の無い譜面の既存の計算・出現時刻を検査する。
        Object.DestroyImmediate(root);
        root = null;
        Object.DestroyImmediate(prefab);
        prefab = null;
        MakeSpawner();
        var plain = new NoteData { time = 10000, x = .4f, y = .2f };
        spawner.SetChart(new ChartData { bpm = 120, notes = new List<NoteData> { plain } });
        Assert.IsFalse(spawner.HasOvertakeNotes);
        Assert.AreEqual(9, spawner.SpawnTimeFor(plain));
        Assert.AreEqual(.5, spawner.VisualRemaining(10, 9.5));
        Assert.AreEqual(.5, spawner.ShapedRemaining(10, 9.5));
        spawner.Tick(9.5);
        Assert.AreEqual(new Vector3(.4f, .2f, 10), spawner.LiveNotes.Single().transform.position);
    }

    [Test]
    public void LongKeepsItsDurationAndCountWithoutDepthStretch()
    {
        MakeSpawner();
        prefab.transform.localScale = Vector3.one * .8f;
        var data = new NoteData
        {
            time = 10000, x = -1, count = 4, type = "long", lengthMs = 2100,
            approachScale = 3, laneX = -6
        };
        spawner.SetChart(new ChartData { bpm = 120, notes = new List<NoteData> { data } });
        spawner.Tick(spawner.SpawnTimeFor(data) + .01);
        var note = spawner.LiveNotes.Single();
        Assert.AreEqual(4, note.RequiredCutCount);
        Assert.AreEqual(2.1f, spawner.LingerSecondsFor(note));
        Assert.AreEqual(.8f, note.transform.localScale.z);
        Assert.NotNull(note.countLabel);
        Assert.IsTrue(note.countLabel.gameObject.activeSelf, "ロングの回数は Hard と同じく見せる");
        spawner.Tick(10);
        Assert.AreEqual(new Vector3(-1, 0, 0), note.transform.position);
        spawner.Tick(11.05);
        Assert.AreEqual(-.5, note.transform.position.z, 1e-5);
        Assert.IsTrue(note.countLabel.gameObject.activeSelf);
        Assert.AreEqual(2450, note.BodyRenderer.sharedMaterial.renderQueue);
        Assert.AreEqual(1f, note.BodyRenderer.sharedMaterial.GetFloat("_ZWrite"),
            "生成順ではなく実際の奥行きで前後を描く");
    }

    [Test]
    public void PPreservesMixedSourceAndIsTheLastSecretWithItsOwnAccent()
    {
        var chart = ReadP();
        MixedSecretChartAssertions.Check("揺籠", chart, "p", 66976.7442, 63, 268);
        Assert.AreEqual(331, chart.notes.Count);
        Assert.AreEqual(104, chart.notes.Count(n => n.IsDirection));
        Assert.AreEqual(15, chart.notes.Count(n => n.IsLong));
        Assert.AreEqual(132, chart.notes.Count(n => n.color == "gold"));
        Assert.AreEqual(9, chart.displayLevel);
        Assert.IsTrue(chart.notes.All(n => n.HasOvertake && !n.HasWind && !n.slide
            && string.IsNullOrEmpty(n.vanish) && n.veil == 0));
        Assert.IsEmpty(chart.scroll);
        Assert.IsFalse(FallRules.Enabled(chart.fall));
        Assert.IsTrue(ChartLoader.LoadFromStreamingAssets("揺籠", "Hard")
            .notes.All(n => !n.HasOvertake));

        var secret = RushESecretState.YurikagoOvertake;
        Assert.AreSame(secret, RushESecretState.For("揺籠"));
        Assert.AreSame(secret, RushESecretState.ForDifficulty(" p "));
        // 並び順は RushESecretTests が見る(P のあとに勉強の Q が続く)。
        CollectionAssert.Contains(RushESecretState.Charts, secret);
        Assert.AreEqual("P", secret.Difficulty);
        Assert.AreEqual(RushESecretState.MoonSilver, GameHUDSkin.DifficultyColor("P"));
        Assert.AreEqual(RushESecretState.MoonSilver, GameStartCountdown.AccentForDifficulty("p"));
        foreach (var other in RushESecretState.Charts)
            if (other != secret) Assert.AreNotEqual(other.Accent, secret.Accent);
        var state = new RushESecretState();
        state.Select("揺籠");
        for (int i = 0; i < 10; i++) Assert.IsTrue(state.TryHit(i * .2));
        Assert.IsTrue(state.Reveal());
        Assert.IsTrue(state.Finish());
        Assert.IsTrue(state.Unlocked);
    }

    [TestCase(.5)]
    [TestCase(1)]
    [TestCase(4)]
    public void ChartHasEnoughLeadAndNoNearBodyIntersection(double saved)
    {
        var chart = ReadP();
        double approach = OvertakeRules.BaseApproach(saved);
        foreach (var n in chart.notes)
        {
            Assert.That(n.approachScale, Is.InRange(.5f, 4.5f));
            Assert.That(n.laneX, Is.InRange(-8f, 8f));
            float scale = OvertakeRules.FlightScale(n.approachScale, approach);
            Assert.GreaterOrEqual(OvertakeRules.Duration(approach, scale) - 1.0 / 30, .45);
            foreach (double r in new[] { .73, .7, 0.0 })
            {
                var p = OvertakeRules.Position(new Vector2(n.x, n.y), n.laneX,
                    r, approach, scale, 20, 0);
                Assert.AreEqual(n.x, p.x);
                Assert.AreEqual(n.y, p.y);
                Assert.AreEqual(20 / approach * r, p.z, 1e-4);
            }
        }

        double Z(NoteData n, double t)
        {
            double r = n.TimeSeconds - t;
            double length = n.LingerSeconds(.7f);
            if (r >= 0 || length == 0) return 20 / approach * r;
            return -r <= length ? r / length : -1 + 20 / approach * (r + length);
        }

        for (int i = 0; i < chart.notes.Count; i++)
        {
            var a = chart.notes[i];
            double ha = a.TimeSeconds, la = a.LingerSeconds(.7f);
            foreach (var b in chart.notes.Skip(i + 1))
            {
                double hb = b.TimeSeconds, lb = b.LingerSeconds(.7f);
                if (hb - ha > la + .73 + .24) break;
                if (Math.Abs(a.x - b.x) >= 1.3 || Math.Abs(a.y - b.y) >= 1.3) continue;
                double lo = hb - .73, hi = Math.Min(ha + la + .24, hb + lb + .24);
                if (hi < lo) continue;
                foreach (double t in new[] { lo, hi, ha, hb, ha + la, hb + lb }
                    .Where(t => t >= lo && t <= hi))
                    Assert.GreaterOrEqual(Z(b, t) - Z(a, t), .96 - 1e-4,
                        a.time + " / " + b.time + "ms の近距離外形");
            }
        }
    }

    [Test]
    public void BothCameraPosesContainTheFlightAndLongLinger()
    {
        var chart = ReadP();
        string scene = File.ReadAllText(Path.Combine(Application.dataPath, "Scenes", "Game.unity"));
        foreach (string expected in new[] {
            "cameraPosition: {x: 0, y: 1.6, z: -7}",
            "cameraRotationEuler: {x: 6, y: 0, z: 0}",
            "topDownCameraPosition: {x: 0, y: 2.35, z: -7}",
            "topDownCameraRotationEuler: {x: 12, y: 0, z: 0}",
            "field of view: 60"
        }) StringAssert.Contains(expected, scene, "カメラ変更時は検査も更新する");

        foreach (double approach in new[] { 1.5, 4.0 })
        foreach (var n in chart.notes)
        {
            float scale = OvertakeRules.FlightScale(n.approachScale, approach);
            double duration = OvertakeRules.Duration(approach, scale);
            for (int step = 0; step <= 80; step++)
            {
                double remaining = duration * step / 80;
                var p = OvertakeRules.Position(new Vector2(n.x, n.y), n.laneX,
                    remaining, approach, scale, 20, 0);
                AssertInside(p, 1.6f, 6);
                AssertInside(p, 2.35f, 12);
            }
            if (n.IsLong)
            {
                AssertInside(new Vector3(n.x, n.y, -1), 1.6f, 6);
                AssertInside(new Vector3(n.x, n.y, -1), 2.35f, 12);
            }
        }
    }

    static void AssertInside(Vector3 p, float cameraY, float degrees)
    {
        float angle = degrees * Mathf.Deg2Rad;
        foreach (float dx in new[] { -.65f, .65f })
        foreach (float dy in new[] { -.65f, .65f })
        foreach (float dz in new[] { -.48f, .48f })
        {
            float y = p.y + dy - cameraY, z = p.z + dz + 7;
            float cy = Mathf.Cos(angle) * y + Mathf.Sin(angle) * z;
            float cz = -Mathf.Sin(angle) * y + Mathf.Cos(angle) * z;
            float tangent = Mathf.Tan(30 * Mathf.Deg2Rad);
            Assert.Greater(cz, .3);
            Assert.That(.5f + (p.x + dx) / (2 * cz * tangent * 16 / 9), Is.InRange(0f, 1f));
            Assert.That(.5f + cy / (2 * cz * tangent), Is.InRange(0f, 1f));
        }
    }
}
