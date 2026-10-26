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

// 本編の先頭ノーツを曲時計で観測する。失敗時もシーン・選曲・速度設定を戻す。
public class BenkyouChameleonPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GamePlayManager manager;
    Action<CuttableNote> spawnObserver;
    bool hadApproach;
    float oldApproach;

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
            if (manager.noteSpawner != null && spawnObserver != null)
                manager.noteSpawner.OnNoteSpawned -= spawnObserver;
            if (manager.songPlayer != null) manager.songPlayer.Stop();
        }
        if (SceneManager.GetActiveScene().name == "Game")
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        if (hadApproach) GameSession.NoteApproachTime = oldApproach;
        else GameSession.ResetNoteApproachTime();
        spawnObserver = null;
        manager = null;
    }

    static Color Body(CuttableNote note)
    {
        var material = note.BodyRenderer.sharedMaterial;
        return material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : material.color;
    }

    static void Rgb(Color expected, Color actual)
    {
        Assert.AreEqual(expected.r, actual.r, 1e-5);
        Assert.AreEqual(expected.g, actual.g, 1e-5);
        Assert.AreEqual(expected.b, actual.b, 1e-5);
    }

    [UnityTest]
    public IEnumerator FirstNoteIsWhiteThenRevealsAndArrivesUnchangedWithoutACaption()
    {
        string folder = Path.Combine(Application.streamingAssetsPath, "Songs", "BenkyouRemaster");
        string path = Path.Combine(folder, "chart_c.json");
        Assert.IsTrue(File.Exists(path), "chart_c.jsonは必須。通常譜面へのフォールバックで通さない");
        var normal = ChartLoader.Parse(File.ReadAllText(Path.Combine(folder, "chart_normal.json")));

        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }
        GameSession.SelectedSongId = "BenkyouRemaster";
        GameSession.SelectedSongTitle = "勉強(Remaster)";
        GameSession.SelectedDifficulty = "C";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.NoteApproachTime = 1f;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.songPlayer == null || !manager.songPlayer.IsScheduled
            || manager.noteSpawner == null || manager.noteSpawner.CurrentChart == null)
            && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.NotNull(manager.songPlayer);
        Assert.IsTrue(manager.songPlayer.IsScheduled);
        var spawner = manager.noteSpawner;
        Assert.NotNull(spawner);
        Assert.NotNull(spawner.CurrentChart);
        Assert.AreEqual(261, spawner.TotalNoteCount);
        Assert.AreEqual(9, spawner.CurrentChart.displayLevel);
        Assert.IsFalse(spawner.HasScroll);
        Assert.IsFalse(spawner.HasFall);
        Assert.IsFalse(spawner.HasWindNotes);
        Assert.IsFalse(spawner.HasVanishNotes);
        Assert.IsNull(GameObject.Find("FirstHandHint"), "カウントインの下に一言を作らない");

        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None))
            judge.enabled = false;
        manager.cameraImuJudgment = null;

        var source = spawner.CurrentChart.notes[0];
        Assert.AreEqual(.4f, source.reveal);
        Assert.AreEqual(normal.notes[0].time, source.time);
        Assert.AreEqual(normal.notes[0].x, source.x);
        Assert.AreEqual(normal.notes[0].y, source.y);
        double hit = spawner.EffectiveTime(source);
        Vector2 landing = new Vector2(source.x, source.y) * spawner.CurrentChart.coordScale;
        Assert.Less(manager.songPlayer.SongTime, hit - spawner.approachTime,
            "最初のノーツが出る前から観測する");
        CuttableNote target = null;
        Color spawnColor = Color.clear;
        uint version = 0;
        spawnObserver = note =>
        {
            if (target != null || Math.Abs(note.HitTime - hit) > 1e-6
                || !note.LandingPosition.Equals(landing)) return;
            target = note;
            version = note.SpawnVersion;
            spawnColor = Body(note);
        };
        spawner.OnNoteSpawned += spawnObserver;
        deadline = Time.realtimeSinceStartupAsDouble + 20;
        while (target == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.NotNull(target);
        Rgb(ChameleonRules.Chalk, spawnColor);
        var visuals = target.GetComponent<NoteVisuals>();
        Assert.AreEqual(0f, visuals.RevealAmount, 1e-5);
        Assert.AreEqual(landing, (Vector2)target.transform.position);

        // zは最後に描画した曲時計の残り時間を表す。コルーチン再開時の音声時計との端数を混ぜない。
        deadline = Time.realtimeSinceStartupAsDouble + 10;
        while (visuals.RevealAmount <= 0f && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.Greater(visuals.RevealAmount, 0f);
        double firstColorRemaining = (target.transform.position.z - spawner.judgeZ) / spawner.Speed;
        Assert.That(firstColorRemaining, Is.InRange(.3, .40001));
        while (visuals.RevealAmount < 1f && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.AreEqual(1f, visuals.RevealAmount);
        double fullColorRemaining = (target.transform.position.z - spawner.judgeZ) / spawner.Speed;
        Assert.That(fullColorRemaining, Is.InRange(.3, .34001),
            "30fpsの1コマ遅れを超える停止があった場合は撮影環境も確認する");
        Rgb(visuals.baseColor, Body(target));

        yield return WaitForSong(hit + .02);
        Assert.AreEqual(version, target.SpawnVersion);
        Assert.IsFalse(target.IsCut);
        Assert.IsFalse(target.IsMissed);
        Assert.AreEqual(hit, target.HitTime, 1e-9);
        Assert.AreEqual(landing, (Vector2)target.transform.position);
        Assert.AreEqual(SaberHandHelper.FromColor(source.color), target.RequiredHand);
        Assert.AreEqual(CutDirectionHelper.Parse(source.direction), target.RequiredDirection);
        Assert.AreEqual(source.count, target.RequiredCutCount);
        Assert.IsNull(GameObject.Find("FirstHandHint"));

        spawner.OnNoteSpawned -= spawnObserver;
        spawnObserver = null;
        manager.enabled = false;
        manager.songPlayer.Stop();
        // 同じ本体で境界と到着時刻ちょうども調べる。位置を手動で合わせる処理は入れない。
        spawner.Tick(hit - .4001);
        Rgb(ChameleonRules.Chalk, Body(target));
        spawner.Tick(hit - .37);
        Assert.AreEqual(.5f, visuals.RevealAmount, 1e-5);
        spawner.Tick(hit - .34);
        Rgb(visuals.baseColor, Body(target));
        spawner.Tick(hit);
        Assert.AreEqual(new Vector3(landing.x, landing.y, spawner.judgeZ), target.transform.position);
        CheckPool(spawner);
    }

    IEnumerator WaitForSong(double target)
    {
        double deadline = Time.realtimeSinceStartupAsDouble
            + Math.Max(0, target - manager.songPlayer.SongTime) + 20;
        while (manager.songPlayer.SongTime < target && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.GreaterOrEqual(manager.songPlayer.SongTime, target);
    }

    static void CheckPool(NoteSpawner spawner)
    {
        ChartData Chart(float reveal)
        {
            var chart = new ChartData { bpm = 160 };
            chart.notes.Add(new NoteData {
                time = 2000, x = 1.05f, y = -.45f, color = "red", type = "long",
                count = 2, lengthMs = 1200, direction = "left",
                reveal = reveal, revealArrow = reveal > 0f
            });
            return chart;
        }
        spawner.SetExtraOffsetSeconds(0);
        spawner.reuseNotes = true;
        spawner.SetChart(Chart(.4f));
        spawner.Tick(1.45);
        var first = spawner.LiveNotes.Single();
        uint version = first.SpawnVersion;
        int created = spawner.CreatedNoteCount;
        Rgb(ChameleonRules.Chalk, Body(first));
        Assert.IsFalse(first.GetComponentInChildren<NoteArrowMaterials>(true).gameObject.activeSelf);

        spawner.SetChart(Chart(0f));
        spawner.Tick(1.45);
        var plain = spawner.LiveNotes.Single();
        Assert.AreSame(first, plain, "revealの有無でプールのキーを分けない");
        Assert.AreNotEqual(version, plain.SpawnVersion);
        Assert.AreEqual(created, spawner.CreatedNoteCount);
        Assert.AreEqual(0f, plain.RevealSeconds);
        Rgb(plain.GetComponent<NoteVisuals>().baseColor, Body(plain));
        Rgb(Color.white, plain.countLabel.color);
        Assert.IsTrue(plain.countLabel.enableVertexGradient);
        Assert.IsTrue(plain.GetComponentInChildren<NoteArrowMaterials>(true).gameObject.activeSelf);

        spawner.SetChart(Chart(.4f));
        spawner.Tick(1.45);
        Assert.AreSame(first, spawner.LiveNotes.Single());
        Rgb(ChameleonRules.Chalk, Body(first));
        Assert.IsFalse(first.countLabel.enableVertexGradient);
        Assert.IsFalse(first.GetComponentInChildren<NoteArrowMaterials>(true).gameObject.activeSelf);
        spawner.Tick(1.67);
        Rgb(first.GetComponent<NoteVisuals>().baseColor, Body(first));
        Assert.IsTrue(first.GetComponentInChildren<NoteArrowMaterials>(true).gameObject.activeSelf);
    }
}
