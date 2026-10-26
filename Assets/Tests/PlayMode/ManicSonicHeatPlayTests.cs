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

// 本編でXを読み、通常カットで加熱し、本物の判定部品で熱ノーツの拒否と復帰を調べる。
// 実機入力と音声時計だけを止め、同じ判定窓内でテストから刃を動かす。
public class ManicSonicHeatPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;
    bool hadApproach, oldReducedEffects;
    float oldApproach, oldTimeScale;

    [SetUp]
    public void SetUp()
    {
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly)
                sessionValues[field] = field.GetValue(null);
        hadApproach = PlayerPrefs.HasKey("noteApproachTime");
        oldApproach = GameSession.NoteApproachTime;
        oldTimeScale = Time.timeScale;
        oldReducedEffects = DisplaySettings.ReducedEffects;
        DisplaySettings.SetReducedEffectsForTest(false);
        Time.timeScale = 1;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (manager != null)
        {
            manager.enabled = false;
            if (manager.songPlayer != null) manager.songPlayer.Stop();
            if (manager.HeatMode != null) manager.HeatMode.EndPlay();
        }
        if (SceneManager.GetActiveScene().name == "Game")
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        if (hadApproach) GameSession.NoteApproachTime = oldApproach;
        else GameSession.ResetNoteApproachTime();
        Time.timeScale = oldTimeScale;
        DisplaySettings.SetReducedEffectsForTest(oldReducedEffects);
        manager = null;
    }

    [UnityTest]
    public IEnumerator LoadedXRejectsTheHotHandAndAcceptsItAfterCooling()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "ManicSonic", "chart_x.json");
        Assert.IsTrue(File.Exists(path), "先に gen_heat.py --write を実行する");
        var expected = ChartLoader.Parse(File.ReadAllText(path));

        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }
        GameSession.SelectedSongId = "ManicSonic";
        GameSession.SelectedSongTitle = "Manic Sonic";
        GameSession.SelectedDifficulty = "X";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.NoteApproachTime = 1;
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
        var spawner = manager.noteSpawner;
        var chart = spawner.CurrentChart;
        Assert.AreEqual(JsonUtility.ToJson(expected), JsonUtility.ToJson(chart), "Xそのものを読む");
        var heat = manager.HeatMode;
        Assert.NotNull(heat);
        Assert.NotNull(chart.heat);
        Assert.IsNull(manager.AimMode);
        Assert.IsNull(manager.MemoryMode);
        Assert.IsNull(manager.BullMode);

        Assert.IsNull(GameObject.Find("FirstHandHint"), "裏譜面は始まりに決まりの一言も手の予告も出さない");

        manager.enabled = false;
        manager.songPlayer.Stop();
        if (manager.cameraImuJudgment != null) manager.cameraImuJudgment.enabled = false;
        manager.cameraImuJudgment = null;
        foreach (var bridge in Object.FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None))
            bridge.enabled = false;
        foreach (var other in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
            other.enabled = false;

        var judge = manager.cutJudge;
        Assert.NotNull(judge);
        Assert.AreSame(heat, judge.Heat, "本編が接続した判定部品を使う");
        var driver = new GameObject("HeatTestDriver");
        driver.transform.SetParent(manager.transform, false);
        var tracker = driver.AddComponent<SaberTracker>();
        tracker.enabled = false;
        var visualBridge = driver.AddComponent<SaberInputBridge>();
        visualBridge.useInputPoint = visualBridge.fallbackToMouse = false;
        visualBridge.enabled = false;
        judge.saber = tracker;
        judge.bladeProvider = null;
        judge.hand = SaberHand.Right;
        judge.autonomous = false;
        judge.ExternalJudgment = false;
        judge.minCutSpeed = 1;
        judge.maxCutDistance = 5;
        judge.bladeRadius = .1f;
        judge.noteHitRadiusXY = .25f;
        judge.enabled = true;
        spawner.JudgeLatencySeconds = 0;

        var source = chart.notes.First(n => n.heat && n.color == "red");
        double hit = spawner.EffectiveTime(source);
        double blockedTime = hit - .03;
        var tempo = new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap);
        double warmTime = tempo.TimeAt(
            tempo.BeatAt((blockedTime - spawner.TotalOffsetSeconds) * 1000) - 1.95) / 1000
            + spawner.TotalOffsetSeconds;
        Assert.LessOrEqual(heat.Session.Time, warmTime);
        heat.Tick(warmTime, warmTime);

        // 通常ノーツの受理済みカット通知で右だけを上限へ。熱の値は直接書き換えない。
        var fixture = new GameObject("OrdinaryHeatSource");
        fixture.transform.SetParent(driver.transform, false);
        fixture.transform.position = new Vector3(100, 100, 0);
        var ordinary = fixture.AddComponent<CuttableNote>();
        ordinary.RequiredCutCount = ordinary.RemainingCuts = 4;
        ordinary.RequiredHand = SaberHand.Right;
        ordinary.HitTime = warmTime;
        heat.Observe(ordinary);
        for (int i = 0; i < 3; i++)
            Assert.IsTrue(ordinary.CutAtSongTime(fixture.transform.position, Vector3.up * 10,
                CutDirection.None, SaberHand.Right, warmTime));
        Assert.AreEqual(3, heat.Session.HeatOf(HeatHand.Right));
        Assert.AreEqual(0, heat.Session.HeatOf(HeatHand.Left));
        Assert.IsTrue(judge.CanCutNote(ordinary), "blockAll=falseなので通常ノーツは切れる");
        heat.DrawFeedback();
        Assert.NotNull(heat.View);
        Assert.NotNull(heat.Sfx);
        Assert.AreEqual(1, heat.Sfx.Played(HeatCue.Full));

        spawner.Tick(blockedTime);
        heat.Tick(blockedTime, blockedTime);
        var right = spawner.LiveNotes.Single(n => n.RequiredHand == SaberHand.Right &&
            Math.Abs(n.HitTime - hit) < 1e-6);
        var left = spawner.LiveNotes.Single(n => n.RequiredHand == SaberHand.Left &&
            Math.Abs(n.HitTime - hit) < 1e-6);
        Assert.IsTrue(right.IsHeat && left.IsHeat);
        Assert.IsTrue(right.IsJudgeable);
        PresentBlade(heat, judge, visualBridge, right.transform.position);
        Assert.GreaterOrEqual(heat.View.VisibleFlameNotes, 2, "本編の赤・青の炎枠を描く");
        Assert.AreEqual(3, heat.View.LitBladeSegments(HeatHand.Right));
        Assert.AreEqual(3, heat.View.GaugeSegments(HeatHand.Right));
        Assert.AreEqual(3, heat.View.LitGaugeSegments(HeatHand.Right));
        Assert.Greater(heat.View.VertexCount, 0);
        double cooling = heat.Session.NextCoolTime(HeatHand.Right);
        Assert.IsFalse(judge.CanCutNote(right));
        Vector3 blockedPosition = right.transform.position;
        Swing(judge, right, blockedTime);
        heat.DrawFeedback();
        Assert.AreEqual(blockedPosition, right.transform.position);
        Assert.AreEqual(1, heat.View.SparkBursts);
        Assert.AreEqual(1, heat.Sfx.Played(HeatCue.Reject));
        Assert.IsFalse(right.IsCut);
        Assert.IsFalse(right.IsMissed);
        Assert.AreEqual(0, right.CutsAchieved);
        Assert.AreEqual(0, judge.PendingCount);
        Assert.AreEqual(cooling, heat.Session.NextCoolTime(HeatHand.Right), "拒否で冷却を延期しない");

        judge.hand = SaberHand.Left;
        Assert.IsTrue(judge.CanCutNote(left));
        Swing(judge, left, blockedTime);
        Assert.IsTrue(left.IsCut, "冷えている左は同時刻の青を切れる");
        Assert.AreEqual(1, heat.Session.HeatOf(HeatHand.Left));
        Assert.AreEqual(3, heat.Session.HeatOf(HeatHand.Right));

        // 冷却の直後も同じノーツの受付窓内。判定可否の差が熱だけで生じる。
        double recovered = cooling + .005;
        Assert.Less(Math.Abs(recovered - hit), .05);
        spawner.Tick(recovered);
        heat.Tick(recovered, recovered);
        judge.hand = SaberHand.Right;
        Assert.AreEqual(2, heat.Session.HeatOf(HeatHand.Right));
        Assert.IsTrue(right.IsJudgeable);
        Assert.IsTrue(judge.CanCutNote(right));
        PresentBlade(heat, judge, visualBridge, right.transform.position);
        Assert.AreEqual(2, heat.View.LitBladeSegments(HeatHand.Right));
        Assert.AreEqual(2, heat.View.LitGaugeSegments(HeatHand.Right));
        Assert.Greater(heat.View.CoolingRings, 0);
        Assert.AreEqual(1, heat.Sfx.Played(HeatCue.Cool));
        Swing(judge, right, recovered);
        Assert.IsTrue(right.IsCut);
        Assert.AreEqual(1, right.CutsAchieved);
        Assert.AreEqual(SaberHand.Right, right.LastCutterHand);
        Assert.AreEqual(3, heat.Session.HeatOf(HeatHand.Right));

        heat.EndPlay();
        Assert.IsNull(judge.Heat);
    }

    // 通信だけを置き換え、本編に接続されたHeatViewで実際のメッシュを作る。
    static void PresentBlade(HeatMode heat, SaberCutJudge judge, SaberInputBridge bridge, Vector3 center)
    {
        bridge.enabled = true;
        bridge.OverrideBlade(center - Vector3.up * .75f, center + Vector3.up * .75f);
        judge.bladeProvider = bridge;
        heat.DrawFeedback();
        bridge.enabled = false;
        judge.bladeProvider = null;
    }

    static void Swing(SaberCutJudge judge, CuttableNote note, double time)
    {
        Vector3 p = note.transform.position;
        judge.saber.ResetTo(p - Vector3.up * 2);
        judge.saber.Tick(p, .01f);
        judge.TryCut(time);
        judge.saber.Tick(p + Vector3.up * 2, .01f);
        judge.TryCut(time);
    }
}
