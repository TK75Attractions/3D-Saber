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

// 校歌の F を本編で読み、二つの飛来方式・カウントイン・再利用を確かめる。
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
    public IEnumerator BothKindsArriveCorrectlyInTheGameWithoutRestoringTheRemovedCaption()
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
        Assert.NotNull(manager.barLineSpawner);
        Assert.IsTrue(manager.barLineSpawner.isActiveAndEnabled);
        Assert.IsNull(manager.AimMode);
        Assert.IsNull(manager.MemoryMode);

        // 実機やマウスの入力で、観測する最初のノーツを切らない。
        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
            judge.enabled = false;
        manager.cameraImuJudgment = null;

        Assert.IsNull(GameObject.Find("FirstHandHint"), "裏譜面は始まりに決まりの一言も手の予告も出さない");
        var hud = Object.FindFirstObjectByType<GameHUDSkin>();
        Assert.NotNull(hud);
        Assert.AreEqual(.28f, hud.transform.Find("SongTitle").GetComponent<CanvasGroup>().alpha);

        var chart = spawner.CurrentChart;
        Assert.IsFalse(chart.notes[0].fall);
        Assert.IsTrue(chart.notes.Any(n => n.fall));
        // 実際の曲時計で最初の通常ノーツ、最初の落下ノーツを順に観測する。
        foreach (var source in new[] { chart.notes.First(n => !n.fall), chart.notes.First(n => n.fall) })
        {
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

            Assert.NotNull(target, "先頭の通常・落下ノーツそのものを観測する");
            Assert.AreEqual(source.fall, target.IsFall);
            Assert.IsTrue(spawner.FallLineVisible);
            Assert.Greater(manager.barLineSpawner.NextIndex, 0);
            Assert.Greater(spawnRemaining, .7, "出現区間をコマ飛びで失っていない");
            if (source.fall)
            {
                Assert.AreEqual(FallRules.LineY, landing.y, 1e-6);
                Assert.Greater(spawnView.y, landing.y + 1f);
                Assert.AreEqual(spawner.judgeZ, spawnView.z);
                Assert.AreEqual(FallRules.Offset(chart.fall.height, spawnRemaining, spawner.approachTime),
                    spawnView.y - landing.y, .04f);
                AssertLanding(target, spawner);
            }
            else
            {
                Assert.AreSame(target.transform, target.VisualTransform);
                Assert.AreEqual(landing, (Vector2)spawnView);
                Assert.Greater(spawnView.z, spawner.judgeZ);
            }

            yield return WaitForSong(hit - .45);
            Assert.AreEqual(version, target.SpawnVersion);
            if (source.fall)
            {
                Assert.Greater(target.VisualTransform.position.y, landing.y);
                Assert.Less(target.VisualTransform.position.y, spawnView.y);
                AssertLanding(target, spawner);
            }
            else
            {
                Assert.AreEqual(landing, (Vector2)target.transform.position);
                Assert.Greater(target.transform.position.z, spawner.judgeZ);
                Assert.Less(target.transform.position.z, spawnView.z);
            }

            yield return WaitForSong(hit + .02);
            Assert.AreEqual(version, target.SpawnVersion);
            Assert.IsFalse(target.IsCut);
            Assert.IsFalse(target.IsMissed);
            if (source.fall) AssertLanding(target, spawner);
            else Assert.AreEqual(spawner.judgeZ + spawner.Speed * (float)(hit - manager.songPlayer.SongTime),
                target.transform.position.z, spawner.Speed * .05f);
            spawner.OnNoteSpawned -= spawnObserver;
            spawnObserver = null;

            // 音声フレームの端数に依存せず、到着時刻ちょうども同じ本体で確かめる。
            spawner.Tick(hit);
            Assert.AreEqual(target.transform.position, target.VisualTransform.position);
            AssertLanding(target, spawner);
        }

        manager.enabled = false;
        manager.songPlayer.Stop();
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
        fall.notes.Add(new NoteData { time = 4000, color = "red", x = .25f, y = -.2f, fall = true });
        fall.notes.Add(new NoteData { time = 8000, color = "red", x = .25f, y = -.2f });
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

        // 同じFの中で通常ノーツも借りる。外形が同じでも落下の個体を使わない。
        spawner.Tick(7.5);
        var front = spawner.LiveNotes.Single();
        Assert.AreNotSame(reused, front);
        Assert.IsFalse(front.IsFall);
        Assert.AreSame(front.transform, front.VisualTransform);
        Assert.AreEqual(0, spawner.FallPetalCount);
        Assert.IsTrue(spawner.FallLineVisible);
        Assert.AreEqual(spawner.judgeZ + spawner.Speed * .5f, front.transform.position.z, 1e-5f);

        var normal = new ChartData { bpm = 120 };
        normal.notes.Add(new NoteData { time = 4000, color = "red", x = .25f, y = -.2f });
        spawner.SetChart(normal);
        spawner.Tick(3.5);
        var plain = spawner.LiveNotes.Single();
        Assert.IsFalse(spawner.HasFall);
        Assert.AreSame(front, plain, "F内の通常個体は通常譜面でもそのまま再利用する");
        Assert.AreSame(plain.transform, plain.VisualTransform);
        Assert.Greater(plain.transform.position.z, spawner.judgeZ + .1f);
        Assert.AreEqual(0, spawner.FallPetalCount);
        Assert.IsFalse(spawner.FallLineVisible);
    }
}
