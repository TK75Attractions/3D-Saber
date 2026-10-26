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

// 校歌の F を本編で読み、最初のノーツ・カウントイン・再利用を確かめる。
// 失敗時もシーンと選択状態、保存された接近時間を戻す。
public class EpilogueFallPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;
    Action<CuttableNote> spawnObserver;
    float oldApproach;
    bool hadApproach;

    [SetUp]
    public void SetUp()
    {
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly)
                sessionValues[field] = field.GetValue(null);
        hadApproach = PlayerPrefs.HasKey("noteApproachTime");
        oldApproach = GameSession.NoteApproachTime;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (manager != null)
        {
            manager.enabled = false;
            if (manager.noteSpawner != null && spawnObserver != null)
                manager.noteSpawner.OnNoteSpawned -= spawnObserver;
            if (manager.songPlayer != null) manager.songPlayer.Stop();
        }
        if (SceneManager.GetActiveScene().name == "Game")
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        if (hadApproach) GameSession.NoteApproachTime = oldApproach;
        else GameSession.ResetNoteApproachTime();
        manager = null;
        spawnObserver = null;
    }

    [UnityTest]
    public IEnumerator FirstNoteFallsOntoItsFixedLandingAndCaptionExplainsTheRule()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "Epilogue", "chart_f.json");
        Assert.IsTrue(File.Exists(path), "chart_f.json は必須。先に生成する");

        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }
        GameSession.SelectedSongId = "Epilogue";
        GameSession.SelectedSongTitle = "校歌";
        GameSession.SelectedDifficulty = "F";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.NoteApproachTime = 1f;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);

        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.songPlayer == null || !manager.songPlayer.IsScheduled
            || manager.noteSpawner == null || manager.noteSpawner.CurrentChart == null)
            && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;

        Assert.NotNull(manager.songPlayer);
        Assert.IsTrue(manager.songPlayer.IsScheduled);
        var spawner = manager.noteSpawner;
        Assert.NotNull(spawner);
        Assert.IsTrue(spawner.HasFall);
        Assert.IsFalse(spawner.HasWindNotes);
        Assert.IsFalse(spawner.HasScroll);
        Assert.IsNull(manager.barLineSpawner);
        Assert.IsNull(manager.AimMode);
        Assert.IsNull(manager.MemoryMode);

        // 実機やマウスの入力で、観測する最初のノーツを切らない。
        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
            judge.enabled = false;
        manager.cameraImuJudgment = null;

        var hint = GameObject.Find("FirstHandHint");
        Assert.NotNull(hint);
        var label = hint.GetComponentInChildren<TMPro.TextMeshProUGUI>();
        Assert.NotNull(label);
        Assert.AreEqual(FallRules.RuleCaption, label.text);
        Assert.AreEqual(RushESecretState.Sakura.r, label.color.r, 1e-6f);
        Assert.AreEqual(RushESecretState.Sakura.g, label.color.g, 1e-6f);
        Assert.AreEqual(RushESecretState.Sakura.b, label.color.b, 1e-6f);
        var hud = Object.FindFirstObjectByType<GameHUDSkin>();
        Assert.NotNull(hud);
        Assert.AreEqual(.28f, hud.transform.Find("SongTitle").GetComponent<CanvasGroup>().alpha);

        var chart = spawner.CurrentChart;
        var source = chart.notes[0];
        double hit = spawner.EffectiveTime(source);
        Vector2 landing = new Vector2(source.x, source.y) * chart.coordScale;
        CuttableNote target = null;
        Vector3 spawnView = Vector3.zero;
        double spawnRemaining = 0;
        uint version = 0;
        spawnObserver = note =>
        {
            if (target != null || Math.Abs(note.HitTime - hit) > 1e-6
                || !note.LandingPosition.Equals(landing)) return;
            target = note;
            version = note.SpawnVersion;
            spawnView = note.VisualTransform.position;
            spawnRemaining = hit - manager.songPlayer.SongTime;
        };
        spawner.OnNoteSpawned += spawnObserver;
        deadline = Time.realtimeSinceStartupAsDouble
            + Math.Max(0, hit - spawner.approachTime - manager.songPlayer.SongTime) + 20;
        while (target == null && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;

        Assert.NotNull(target, "最初のノーツそのものを観測する");
        Assert.IsTrue(spawner.FallLineVisible);
        Assert.AreEqual(FallRules.LineY, target.LandingPosition.y, 1e-6);
        Assert.IsTrue(chart.notes.All(n => Mathf.Abs(n.y * chart.coordScale - FallRules.LineY) < 1e-6));
        Assert.Greater(spawnRemaining, .7, "出現の区間をコマ飛びで失っていない");
        Assert.Greater(spawnView.y, landing.y + 1f);
        Assert.AreEqual(landing.x, spawnView.x);
        Assert.AreEqual(spawner.judgeZ, spawnView.z);
        Assert.AreEqual(FallRules.Offset(chart.fall.height, spawnRemaining, spawner.approachTime),
            spawnView.y - landing.y, .04f);
        AssertLanding(target, spawner);

        yield return WaitForSong(hit - .45);
        Assert.AreEqual(version, target.SpawnVersion);
        Assert.Greater(target.VisualTransform.position.y, landing.y);
        Assert.Less(target.VisualTransform.position.y, spawnView.y);
        AssertLanding(target, spawner);

        yield return WaitForSong(hit + .02);
        Assert.AreEqual(version, target.SpawnVersion);
        Assert.IsFalse(target.IsCut);
        Assert.IsFalse(target.IsMissed);
        Assert.AreEqual(target.transform.position, target.VisualTransform.position);
        AssertLanding(target, spawner);

        spawner.OnNoteSpawned -= spawnObserver;
        spawnObserver = null;
        manager.enabled = false;
        manager.songPlayer.Stop();

        // 音声フレームの端数に依存せず、到着時刻ちょうども検査する。
        spawner.Tick(hit);
        Assert.AreEqual(target.transform.position, target.VisualTransform.position);
        CheckPoolAndNormalChart(spawner);
        hud.SetFallHeaderDim(false);
        Assert.AreEqual(1f, hud.transform.Find("SongTitle").GetComponent<CanvasGroup>().alpha);
    }

    IEnumerator WaitForSong(double target)
    {
        double deadline = Time.realtimeSinceStartupAsDouble
            + Math.Max(0, target - manager.songPlayer.SongTime) + 20;
        while (manager.songPlayer.SongTime < target && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.GreaterOrEqual(manager.songPlayer.SongTime, target);
    }

    static void AssertLanding(CuttableNote note, NoteSpawner spawner)
    {
        Assert.AreEqual(note.LandingPosition, (Vector2)note.transform.position);
        Assert.AreEqual(spawner.judgeZ, note.transform.position.z);
        Assert.NotNull(note.TimingCue);
        Assert.AreEqual(note.LandingPosition, (Vector2)note.TimingCue.GhostRoot.transform.position);
        Assert.AreEqual(note.LandingPosition, (Vector2)note.TimingCue.ApproachRoot.transform.position);
    }

    static void CheckPoolAndNormalChart(NoteSpawner spawner)
    {
        spawner.SetExtraOffsetSeconds(0);
        spawner.reuseNotes = true;
        var fall = new ChartData
        {
            bpm = 120,
            fall = new FallChartData { enabled = true, height = 2.4f }
        };
        fall.notes.Add(new NoteData { time = 4000, color = "red", x = .25f, y = -.2f });
        spawner.SetChart(fall);
        spawner.Tick(3);
        var first = spawner.LiveNotes.Single();
        uint version = first.SpawnVersion;
        spawner.Tick(3.7);

        // 風なし F の個体を、風の指定がある F へ再利用しても描画の親を循環させない。
        fall.notes[0].windX = .4f;
        fall.notes[0].windY = -.2f;
        fall.notes[0].windSpin = .3f;
        fall.notes[0].windSway = .5f;
        spawner.SetChart(fall);
        spawner.Tick(3);
        var reused = spawner.LiveNotes.Single();
        Assert.AreSame(first, reused);
        Assert.AreNotEqual(version, reused.SpawnVersion);
        Assert.IsFalse(spawner.HasWindNotes);
        Assert.IsFalse(reused.HasWind);
        Assert.IsNull(reused.transform.Find("WindVisual"));
        Assert.AreSame(reused.transform, reused.VisualTransform.parent);
        Assert.AreSame(reused.VisualTransform, reused.BodyRenderer.transform);
        Assert.IsTrue(reused.BodyRenderer.enabled);
        Assert.AreEqual(2.4f, reused.VisualTransform.position.y - reused.LandingPosition.y, 1e-5f);

        var normal = new ChartData { bpm = 120 };
        normal.notes.Add(new NoteData { time = 4000, color = "red", x = .25f, y = -.2f });
        spawner.SetChart(normal);
        spawner.Tick(3.5);
        var plain = spawner.LiveNotes.Single();
        Assert.IsFalse(spawner.HasFall);
        Assert.AreSame(plain.transform, plain.VisualTransform);
        Assert.Greater(plain.transform.position.z, spawner.judgeZ + .1f);
        Assert.AreEqual(0, spawner.FallPetalCount);
        Assert.IsFalse(spawner.FallLineVisible);
    }
}
