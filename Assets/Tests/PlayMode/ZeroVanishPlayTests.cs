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

// Zero の裏譜面 V(消える、2026-10-26)を本編で読み、序盤の fade のノーツが届く前に本体だけ消え、着地の枠は残ることを確かめる。
// 結果画面には進めず、失敗時もUnityTearDownで片付ける。
public class ZeroVanishPlayTests
{
    const string SongId = "製作中";
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
    public IEnumerator AnEarlyFadeNoteLosesItsBodyButKeepsItsFrame()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", SongId, "chart_v.json");
        Assert.IsTrue(File.Exists(path), "chart_v.jsonは必須。通常譜面へのフォールバックで通さない");

        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }

        GameSession.SelectedSongId = SongId;
        GameSession.SelectedDifficulty = "V";
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
        Assert.IsTrue(spawner.HasVanishNotes, "V の譜面は見え方の変わるノーツを持つ");
        Assert.IsFalse(spawner.HasScroll);

        Assert.IsNull(GameObject.Find("FirstHandHint"), "裏譜面は始まりに決まりの一言も手の予告も出さない");

        double now = manager.songPlayer.SongTime;
        var source = chart.notes.First(n => n.vanish == "fade" && spawner.EffectiveTime(n) - spawner.approachTime > now + .1);
        double hit = spawner.EffectiveTime(source);
        CuttableNote target = null;
        spawnObserver = note => { if (target == null && Math.Abs(note.HitTime - hit) < 1e-6) target = note; };
        spawner.OnNoteSpawned += spawnObserver;

        yield return WaitForSong(hit - .8);
        Assert.NotNull(target, "fade のノーツが出ている");
        Assert.AreEqual(VanishMode.Fade, target.Vanish);
        Assert.AreEqual(0f, target.GetComponent<NoteVisuals>().Veil, 1e-6f, "遠くでは見える");

        yield return WaitForSong(hit - .2);
        Assert.IsFalse(target.IsCut);
        Assert.AreEqual(1f, target.GetComponent<NoteVisuals>().Veil, 1e-6f, "届く前に本体が消える");
        Assert.AreEqual(0f, target.Veil, 1e-6f, "合図は残る");
        Assert.NotNull(target.TimingCue);
        Assert.IsTrue(target.TimingCue.GhostRoot.activeSelf, "着地の枠で切れる");
        Assert.AreEqual(hit, target.HitTime, 1e-9, "判定の時刻は変わらない");

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
