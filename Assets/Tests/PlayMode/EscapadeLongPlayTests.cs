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

// 本編の選曲・読込・説明を通し、実際の SaberCutJudge で動いたロングを切る。
public class EscapadeLongPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;
    GameObject saberObject;
    float oldApproach;
    bool hadApproach;

    [SetUp]
    public void SetUp()
    {
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        hadApproach = PlayerPrefs.HasKey("noteApproachTime");
        oldApproach = GameSession.NoteApproachTime;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (manager != null)
        {
            manager.enabled = false;
            if (manager.songPlayer != null) manager.songPlayer.Stop();
        }
        if (saberObject != null) Object.Destroy(saberObject);
        if (SceneManager.GetActiveScene().name == "Game")
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        if (hadApproach) GameSession.NoteApproachTime = oldApproach;
        else GameSession.ResetNoteApproachTime();
        manager = null;
        saberObject = null;
    }

    [UnityTest]
    public IEnumerator LLoadsMovesDuringLingerAndCanBeCutAtItsCurrentPosition()
    {
        Assert.IsTrue(File.Exists(Path.Combine(Application.streamingAssetsPath,
            "Songs", "escapade", "chart_l.json")), "先に gen_long.py --write");
        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }
        GameSession.SelectedSongId = "escapade";
        GameSession.SelectedSongTitle = "escapade";
        GameSession.SelectedDifficulty = "L";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.NoteApproachTime = 1f;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.songPlayer == null || !manager.songPlayer.IsScheduled
            || manager.noteSpawner == null || manager.noteSpawner.CurrentChart == null
            || ScreenTransition.IsBusy) && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;

        Assert.NotNull(manager.songPlayer);
        Assert.IsTrue(manager.songPlayer.IsScheduled);
        Assert.IsFalse(ScreenTransition.IsBusy);
        var spawner = manager.noteSpawner;
        Assert.NotNull(spawner);
        Assert.IsTrue(spawner.HasSlideNotes);
        Assert.IsFalse(spawner.HasFall);
        var label = GameObject.Find("FirstHandHint")?.GetComponentInChildren<TMPro.TextMeshProUGUI>();
        Assert.NotNull(label);
        Assert.AreEqual(LongSlideRules.RuleCaption, label.text);
        Assert.AreEqual(RushESecretState.Cobalt.r, label.color.r, 1e-6f);
        Assert.AreEqual(RushESecretState.Cobalt.g, label.color.g, 1e-6f);
        Assert.AreEqual(RushESecretState.Cobalt.b, label.color.b, 1e-6f);

        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
            judge.enabled = false;
        manager.cameraImuJudgment = null;
        manager.enabled = false;
        manager.songPlayer.Stop();

        // 本編で読み込んだデータとプレハブを使い、音声フレームの端数だけを取り除く。
        var chart = spawner.CurrentChart;
        spawner.SetChart(chart);
        var source = chart.notes.First();
        double hit = spawner.EffectiveTime(source);
        double duration = source.lengthMs / 1000.0;
        spawner.Tick(hit - .5);
        var target = spawner.LiveNotes.First(n => Math.Abs(n.HitTime - hit) < .001
            && Vector2.Distance(n.LandingPosition, new Vector2(source.x, source.y) * chart.coordScale) < .001f);
        var start = target.LandingPosition;
        uint version = target.SpawnVersion;
        spawner.Tick(hit);
        Assert.AreEqual(start, target.LandingPosition);
        spawner.Tick(hit + duration * .5);
        Assert.AreEqual(version, target.SpawnVersion);
        Assert.Greater(Vector2.Distance(start, target.LandingPosition), .1f);
        Assert.AreEqual(target.LandingPosition, (Vector2)target.transform.position);
        Assert.AreEqual(target.LandingPosition, (Vector2)target.TimingCue.GhostRoot.transform.position);
        Assert.AreEqual(target.transform.position + LongNoteCountStyle.WorldOffset, target.countLabel.transform.position);
        Assert.IsTrue(target.IsJudgeable);
        Assert.IsFalse(target.IsMissed);

        saberObject = new GameObject("LongSlideSaber");
        var tracker = saberObject.AddComponent<SaberTracker>();
        tracker.enabled = false;
        var testJudge = saberObject.AddComponent<SaberCutJudge>();
        testJudge.autonomous = false;
        testJudge.saber = tracker;
        testJudge.hand = source.x < 0 ? SaberHand.Left : SaberHand.Right;
        // 点の軌跡で実際の接触判定を通す。Cut を直接呼んで位置検査を飛ばさない。
        for (int cut = 0; cut < target.RequiredCutCount; cut++)
        {
            double now = hit + duration * (.5 + .5 * cut / (target.RequiredCutCount - 1));
            spawner.RefreshJudgmentWindows(now);
            spawner.Tick(now);
            Vector3 center = target.transform.position;
            tracker.ResetTo(center - Vector3.up * 2);
            for (int step = 1; step <= 8; step++)
            {
                tracker.Tick(center + Vector3.up * (-2 + step * .5f), .025f);
                testJudge.RunJudge(now);
            }
            Assert.AreEqual(cut + 1, target.CutsAchieved, "動いた位置で1振り1カット");
        }
        Assert.IsTrue(target.IsCut);
        Assert.IsFalse(target.IsMissed);

        // 同じプールへ戻した後、滑り指定の無いロングへ状態を持ち越さない。
        var plain = new ChartData { bpm = 120 };
        plain.notes.Add(new NoteData
        {
            time = 10000, x = -.7f, y = 0, color = "gold",
            type = "long", count = 4, lengthMs = 900,
            slide = true, slideToX = -1.7f, slideToY = .4f
        });
        spawner.SetChart(plain);
        double plainHit = spawner.EffectiveTime(plain.notes[0]);
        spawner.Tick(plainHit);
        var moving = spawner.LiveNotes.Single();
        spawner.Tick(plainHit + .4);
        plain.notes[0].slide = false;
        spawner.SetChart(plain);
        spawner.Tick(plainHit);
        var reused = spawner.LiveNotes.Single();
        Assert.AreSame(moving, reused);
        Assert.IsFalse(reused.HasSlide);
        Assert.AreEqual(new Vector2(-.7f, 0), reused.LandingPosition);
        spawner.Tick(plainHit + .5);
        Assert.AreEqual(new Vector2(-.7f, 0), (Vector2)reused.transform.position);
    }
}
