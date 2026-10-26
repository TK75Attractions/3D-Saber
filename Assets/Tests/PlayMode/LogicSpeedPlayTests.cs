using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 本編の読込・字幕と、実コンポーネントの停止/再開/枠/小節線を別々に確かめる。
// 動きは絶対時刻で進める。音源と体感の同期は動画で確認する。
public class LogicSpeedPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;
    GameObject root;
    bool loadedGame;

    static ChartData Read()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "logic", "chart_s.json");
        Assert.IsTrue(File.Exists(path), "通常譜面へのフォールバックで通さない");
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    [SetUp]
    public void SetUp()
    {
        loadedGame = false;
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (root != null) Object.Destroy(root);
        if (manager != null)
        {
            manager.enabled = false;
            if (manager.songPlayer != null) manager.songPlayer.Stop();
        }
        if (loadedGame) yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        yield return null;
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        manager = null;
        root = null;
    }

    [UnityTest]
    public IEnumerator GameLoadsSAndExplainsTheExactCutMoment()
    {
        Read();
        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }
        GameSession.SelectedSongId = "logic";
        GameSession.SelectedDifficulty = "S";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.NoteApproachTime = 1f;
        loadedGame = true;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.songPlayer == null || !manager.songPlayer.IsScheduled ||
                manager.noteSpawner == null || manager.noteSpawner.CurrentChart == null) &&
               Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.NotNull(manager.songPlayer);
        Assert.IsTrue(manager.songPlayer.IsScheduled);
        Assert.NotNull(manager.noteSpawner);
        Assert.IsTrue(manager.noteSpawner.HasScroll);
        Assert.IsFalse(manager.noteSpawner.HasWindNotes);
        Assert.IsNull(manager.AimMode);
        Assert.IsNull(manager.MemoryMode);
        Assert.AreEqual(424, manager.noteSpawner.CurrentChart.notes.Count);
        var hint = GameObject.Find("FirstHandHint");
        Assert.NotNull(hint);
        var label = hint.GetComponentInChildren<TMPro.TextMeshProUGUI>();
        Assert.NotNull(label);
        Assert.AreEqual(ScrollSpeedMap.RuleCaption, label.text);
        Assert.AreEqual(RushESecretState.Ice.r, label.color.r, 1e-6f);
        Assert.AreEqual(RushESecretState.Ice.b, label.color.b, 1e-6f);
        manager.enabled = false;
        manager.songPlayer.Stop();
    }

    NoteSpawner MakeSpawner(ChartData chart)
    {
        root = new GameObject("LogicSpeedProbe");
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.transform.SetParent(root.transform, false);
        prefab.AddComponent<CuttableNote>();
        prefab.SetActive(false);
        var spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.simultaneousGuideEnabled = false;
        spawner.buildTimingCues = true;
        spawner.approachTime = 1;
        spawner.spawnZ = 20;
        spawner.judgeZ = 0;
        spawner.SetExtraOffsetSeconds(.137);
        spawner.SetChart(chart);
        return spawner;
    }

    BarLineSpawner MakeBars(ChartData chart)
    {
        var prefab = new GameObject("BarPrefab");
        prefab.transform.SetParent(root.transform, false);
        var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
        line.name = "Line";
        line.transform.SetParent(prefab.transform, false);
        prefab.SetActive(false);
        var holder = new GameObject("Bars");
        holder.transform.SetParent(root.transform, false);
        var bars = root.AddComponent<BarLineSpawner>();
        bars.barLinePrefab = prefab;
        bars.root = holder.transform;
        bars.approachTime = 1;
        bars.spawnZ = 20;
        bars.judgeZ = 0;
        bars.SetChart(chart, .137);
        return bars;
    }

    static float Alpha(GameObject cueRoot)
    {
        var material = cueRoot.GetComponentInChildren<MeshRenderer>().sharedMaterial;
        return material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor").a : material.color.a;
    }

    static void AssertCue(NoteTimingCue cue, double remaining)
    {
        Assert.NotNull(cue);
        Assert.AreEqual(.7f, cue.ghostVisibleSeconds, 1e-6f);
        float scale = 1 + 1.4f * Mathf.Clamp01((float)(remaining / .7));
        Assert.AreEqual(cue.GhostBaseScale.x * scale,
            cue.ApproachRoot.transform.localScale.x, 1e-4f);
        Assert.AreEqual(cue.GhostBaseScale.x, cue.GhostRoot.transform.localScale.x, 1e-4f);
    }

    [UnityTest]
    public IEnumerator EveryStopFreezesNotesBarsAndFlowThenShowsARealTimeCue()
    {
        var full = Read();
        for (int i = 0; i < full.scroll.Count; i++)
        {
            if (full.scroll[i].speed != 0) continue;
            double from = full.scroll[i].time / 1000.0;
            double to = full.scroll[i + 1].time / 1000.0;
            var source = full.notes.First(n => n.TimeSeconds > to);
            var chart = Read();
            // 過去の数百ノーツを一度に生成せず、この停止で待つ実ノーツを検査する。
            chart.notes = new List<NoteData> { source };
            var spawner = MakeSpawner(chart);
            var bars = MakeBars(chart);
            var floor = root.AddComponent<FloorRenderer>();
            floor.Build(StageTheme.ObsidianRelay);
            var map = ScrollSpeedMap.Build(chart.scroll);
            double offset = spawner.TotalOffsetSeconds;
            double hit = spawner.EffectiveTime(source);
            double spawn = spawner.SpawnTimeFor(hit);
            Assert.Less(spawn, from + offset - 1.0 / 30);
            spawner.Tick(spawn - .001);
            Assert.AreEqual(0, spawner.AliveCount);
            spawner.Tick(spawn + 1.0 / 30);
            Assert.AreEqual(1, spawner.AliveCount);
            var target = spawner.LiveNotes[0];
            double a = from + offset + .05, b = to + offset - .05;
            spawner.Tick(a);
            bars.Tick(a);
            floor.Tick(spawner.FlowClock(a));
            float z = target.transform.position.z;
            Assert.That(z, Is.InRange(10f, 19f));
            Assert.AreEqual(0, Alpha(target.TimingCue.ApproachRoot), 1e-6, "停止中はまだ枠を見せない");
            // 退場目前の線を除き、判定面付近と奥の線を検査する。
            var frozenBars = bars.root.Cast<Transform>()
                .Where(t => t.gameObject.activeSelf && t.position.z >= -10).ToArray();
            Assert.IsNotEmpty(frozenBars);
            var positions = frozenBars.Select(t => t.position.z).ToArray();
            double flow = floor.LastTickSeconds;
            spawner.Tick(b);
            bars.Tick(b);
            floor.Tick(spawner.FlowClock(b));
            Assert.AreEqual(z, target.transform.position.z, 1e-4f);
            Assert.AreEqual(flow, floor.LastTickSeconds, 1e-6);
            for (int j = 0; j < frozenBars.Length; j++)
            {
                Assert.IsTrue(frozenBars[j].gameObject.activeSelf);
                Assert.AreEqual(positions[j], frozenBars[j].position.z, 1e-4f);
            }
            double resumed = to + offset + .05;
            spawner.Tick(resumed);
            bars.Tick(resumed);
            floor.Tick(spawner.FlowClock(resumed));
            Assert.Less(target.transform.position.z, z);
            Assert.Greater(floor.LastTickSeconds, flow);
            Assert.IsTrue(frozenBars.Where(t => t != null && t.gameObject.activeSelf)
                .Any(t => t.position.z < positions[Array.IndexOf(frozenBars, t)] - .01f));
            foreach (double remaining in new[] { .7, .35, .175, 0 })
            {
                spawner.Tick(hit - remaining);
                AssertCue(target.TimingCue, remaining);
                Assert.AreEqual(20 * map.Remaining(source.TimeSeconds - remaining, source.TimeSeconds),
                    target.transform.position.z, 1e-4);
            }
            Assert.AreEqual(0, target.transform.position.z, 1e-4f);
            Assert.AreEqual(hit, target.HitTime, 1e-9);
            Assert.IsTrue(target.TimingCue.InWindow);
            Object.Destroy(root);
            root = null;
            yield return null;
        }
    }

    [UnityTest]
    public IEnumerator FastAndEndingNotesDoNotAppearLateOrArriveEarly()
    {
        foreach (double time in new[] {185.153, 204.653})
        {
            var chart = Read();
            var source = chart.notes.First(n => Math.Abs(n.TimeSeconds - time) < 1e-4);
            chart.notes = new List<NoteData> {source};
            var spawner = MakeSpawner(chart);
            double hit = spawner.EffectiveTime(source), spawn = spawner.SpawnTimeFor(hit);
            spawner.Tick(spawn - .001);
            Assert.AreEqual(0, spawner.AliveCount);
            // 30fpsで生成が1フレーム遅れる最悪側でも、枠の開始に間に合う。
            spawner.Tick(spawn + 1.0 / 30);
            Assert.AreEqual(1, spawner.AliveCount);
            Assert.GreaterOrEqual(hit - spawn - 1.0 / 30, .7 - 1e-5);
            var note = spawner.LiveNotes[0];
            spawner.Tick(hit - .35);
            AssertCue(note.TimingCue, .35);
            float z = note.transform.position.z;
            spawner.Tick(hit - .175);
            AssertCue(note.TimingCue, .175);
            Assert.AreEqual(z * .5f, note.transform.position.z, 1e-4f, "最後まで等速");
            spawner.Tick(hit);
            AssertCue(note.TimingCue, 0);
            Assert.AreEqual(0, note.transform.position.z, 1e-4f);
            Object.Destroy(root);
            root = null;
            yield return null;
        }
    }
}
