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

// logic!!! の裏譜面 S(緩急、2026-10-26)を本編で読み、最初の「止」でノーツが空中に止まり、届く時刻どおりに来ることを確かめる。
// 結果画面には進めず、失敗時もUnityTearDownで片付ける。
public class LogicSpeedPlayTests
{
    const string SongId = "logic";
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;
    Action<CuttableNote> spawnObserver;

    [SetUp]
    public void SetUp()
    {
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly)
                sessionValues[field] = field.GetValue(null);
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
        spawnObserver = null;
        manager = null;
    }

    [UnityTest]
    public IEnumerator NotesFreezeInTheAirAtTheFirstStopAndStillLandOnTheBeat()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", SongId, "chart_s.json");
        Assert.IsTrue(File.Exists(path), "chart_s.jsonは必須。通常譜面へのフォールバックで通さない");

        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }

        GameSession.SelectedSongId = SongId;
        GameSession.SelectedDifficulty = "S";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.NoteApproachTime = 1f;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);

        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.songPlayer == null || !manager.songPlayer.IsScheduled
            || manager.noteSpawner.CurrentChart == null)
            && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;

        Assert.IsTrue(manager.songPlayer.IsScheduled);
        var spawner = manager.noteSpawner;
        var chart = spawner.CurrentChart;
        Assert.IsTrue(spawner.HasScroll, "S の譜面は流れる速さの表を持つ");
        Assert.IsFalse(spawner.HasWindNotes);
        Assert.IsNull(manager.AimMode);
        Assert.IsNull(manager.MemoryMode);

        Assert.IsNull(GameObject.Find("FirstHandHint"), "裏譜面は始まりに決まりの一言も手の予告も出さない");

        // 最初の「止」と、その間に空中で待つノーツ。
        var stop = chart.scroll.First(p => p.speed == 0);
        var resume = chart.scroll.First(p => p.time > stop.time);
        double offset = spawner.TotalOffsetSeconds;
        double stopFrom = stop.time / 1000.0 + offset, stopTo = resume.time / 1000.0 + offset;
        var source = chart.notes.First(n => spawner.EffectiveTime(n) > stopTo);
        double hit = spawner.EffectiveTime(source);
        Assert.Less(spawner.SpawnTimeFor(hit), stopFrom, "止まる前に出ている");

        CuttableNote target = null;
        spawnObserver = note => { if (target == null && Math.Abs(note.HitTime - hit) < 1e-6) target = note; };
        spawner.OnNoteSpawned += spawnObserver;

        var floor = Object.FindFirstObjectByType<FloorRenderer>();
        yield return WaitForSong(stopFrom + (stopTo - stopFrom) * .25);
        Assert.NotNull(target, "止まる前に出たノーツがある");
        float frozenZ = target.transform.position.z;
        Assert.Greater(frozenZ, spawner.judgeZ + .5f, "空中で待っている");
        Assert.Less(frozenZ, spawner.spawnZ + .01f);
        double floorClock = floor != null ? floor.LastTickSeconds : double.NaN;
        yield return WaitForSong(stopFrom + (stopTo - stopFrom) * .75);
        Assert.AreEqual(frozenZ, target.transform.position.z, 1e-4f, "止まっている間は動かない");
        if (floor != null)
            Assert.AreEqual(floorClock, floor.LastTickSeconds, 1e-6, "床の流れもノーツと一緒に止まる");

        yield return WaitForSong(stopTo + (hit - stopTo) * .5);
        Assert.Less(target.transform.position.z, frozenZ - .5f, "止まりが終わると動き出す");
        Assert.IsFalse(target.IsCut);
        // 判定の時刻は音楽どおり(見え方だけが変わる)。
        Assert.AreEqual(hit, target.HitTime, 1e-9);

        spawner.OnNoteSpawned -= spawnObserver;
        spawnObserver = null;
        manager.enabled = false;
        manager.songPlayer.Stop();
    }

    IEnumerator WaitForSong(double target)
    {
        double deadline = Time.realtimeSinceStartupAsDouble
            + Math.Max(0, target - manager.songPlayer.SongTime) + 20;
        while (manager.songPlayer.SongTime < target && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.GreaterOrEqual(manager.songPlayer.SongTime, target, "曲時計が検査する時刻まで進む");
    }
}
