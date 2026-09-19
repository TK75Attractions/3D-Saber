using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 実シーンを通すが、完走とリザルト表示による個人スコア保存は行わない。
public class AndalusiaSongFlowPlayTests
{
    const string SongId = "Andalusia";
    const string SpeedKey = "noteApproachTime";
    const string OffsetKey = "judgmentOffsetMs";
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    readonly Dictionary<string, string> scoreValues = new Dictionary<string, string>();
    bool hadSpeed, hadOffset;
    float speed;
    int offset;
    SongSelectController controller;
    GamePlayManager manager;
    CuttableNote firstNote;
    double firstSpawnTime;
    InputPoint testInput;

    [SetUp]
    public void SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        foreach (string difficulty in new[] { "Easy", "Normal", "Hard" })
        {
            string key = HighScoreStore.Key(SongId, difficulty);
            scoreValues[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;
        }
        hadSpeed = PlayerPrefs.HasKey(SpeedKey); speed = PlayerPrefs.GetFloat(SpeedKey);
        hadOffset = PlayerPrefs.HasKey(OffsetKey); offset = PlayerPrefs.GetInt(OffsetKey);
        PlayerPrefs.SetFloat(SpeedKey, 4f);
        PlayerPrefs.SetInt(OffsetKey, 0);
        GameSession.IsCalibrationMode = false;
        controller = null; manager = null; firstNote = null; firstSpawnTime = double.NaN;
        SceneManager.sceneLoaded += ObserveGame;
        // 起動中のゲーム・他の検証の受信口と競合させない。製品のポート設定は維持する。
        if (InputPoint.Instance == null)
        {
            var receiver = new GameObject("AndalusiaTestInput");
            receiver.SetActive(false); Object.DontDestroyOnLoad(receiver);
            testInput = receiver.AddComponent<InputPoint>();
            testInput.port = FreePort();
            do { testInput.port2 = FreePort(); } while (testInput.port2 == testInput.port);
            receiver.SetActive(true);
        }
    }

    static int FreePort()
    {
        using (var socket = new UdpClient(0)) return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
    }

    void ObserveGame(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Game") return;
        manager = Object.FindFirstObjectByType<GamePlayManager>();
        firstNote = null; firstSpawnTime = double.NaN;
        if (manager == null) return;
        manager.enableStartCountdown = true;
        manager.noteSpawner.OnNoteSpawned += note =>
        {
            if (firstNote != null) return;
            firstNote = note;
            firstSpawnTime = manager.songPlayer.SongTime;
        };
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        SceneManager.sceneLoaded -= ObserveGame;
        if (manager != null && manager.songPlayer != null) manager.songPlayer.Stop();
        if (controller != null) controller.StopPreview();
        var previous = SceneManager.GetActiveScene();
        var empty = SceneManager.CreateScene("AndalusiaCleanup_" + Guid.NewGuid().ToString("N"));
        SceneManager.SetActiveScene(empty);
        if (previous.IsValid() && previous.isLoaded) yield return SceneManager.UnloadSceneAsync(previous);
        if (testInput != null) Object.DestroyImmediate(testInput.gameObject);
        testInput = null;
        if (hadSpeed) PlayerPrefs.SetFloat(SpeedKey, speed); else PlayerPrefs.DeleteKey(SpeedKey);
        if (hadOffset) PlayerPrefs.SetInt(OffsetKey, offset); else PlayerPrefs.DeleteKey(OffsetKey);
        foreach (var item in sessionValues) item.Key.SetValue(null, item.Value);
        sessionValues.Clear();
        // 予期しない保存があってもユーザーの値を戻してから失敗を知らせる。
        bool unchanged = true;
        foreach (var item in scoreValues)
        {
            string current = PlayerPrefs.HasKey(item.Key) ? PlayerPrefs.GetString(item.Key) : null;
            unchanged &= current == item.Value;
            if (item.Value == null) PlayerPrefs.DeleteKey(item.Key); else PlayerPrefs.SetString(item.Key, item.Value);
        }
        scoreValues.Clear();
        PlayerPrefs.Save();
        Assert.IsTrue(unchanged, "確認中にアンダルシアのハイスコアを保存していない");
    }

    IEnumerator OpenSelection()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        double deadline = Time.realtimeSinceStartupAsDouble + 15;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            controller = Object.FindFirstObjectByType<SongSelectController>();
            if (controller != null && controller.ChartPreview?.View != null) break;
            yield return null;
        }
        Assert.IsNotNull(controller);
        Assert.IsNotNull(controller.ChartPreview?.View);
        int index = Enumerable.Range(0, controller.SongCount).Single(i => controller.SongIdAt(i) == SongId);
        controller.Select(index);
    }

    IEnumerator AwaitPreview()
    {
        double deadline = Time.realtimeSinceStartupAsDouble + 12;
        while (Time.realtimeSinceStartupAsDouble < deadline &&
            (!controller.ChartPreview.IsPlaying || !controller.ChartPreview.View.IsVisible)) yield return null;
        Assert.IsTrue(controller.ChartPreview.IsPlaying);
        Assert.IsTrue(controller.ChartPreview.View.IsVisible);
    }

    IEnumerator AwaitGame()
    {
        double deadline = Time.realtimeSinceStartupAsDouble + 25;
        var ready = typeof(GamePlayManager).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic);
        while (Time.realtimeSinceStartupAsDouble < deadline &&
            (manager == null || !(bool)ready.GetValue(manager))) yield return null;
        Assert.IsNotNull(manager);
        Assert.IsTrue((bool)ready.GetValue(manager));
        Assert.IsNotNull(manager.songPlayer.Clip);
    }

    [UnityTest]
    public IEnumerator EveryDifficultyPreviewsTheNamedSongAndAuthorWithTheSameAudioClock()
    {
        yield return OpenSelection();
        Assert.AreEqual("アンダルシア", Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None)
            .Single(t => t.name == "PanelSongTitle").text);
        Assert.AreEqual("もり　わきお", Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None)
            .Single(t => t.name == "PanelSongArtist").text);
        var names = new[] { "Easy", "Normal", "Hard" };
        var levels = new[] { 3, 5, 7 };
        for (int i = 0; i < names.Length; i++)
        {
            controller.SetDifficulty(i);
            yield return AwaitPreview();
            var preview = controller.ChartPreview;
            Assert.AreEqual(SongId, preview.SongId);
            Assert.AreEqual(names[i], preview.Difficulty);
            Assert.AreEqual(levels[i], controller.CurrentDifficultyDisplayLevel());
            Assert.AreEqual(53.44, preview.Window.Start, .001);
            Assert.AreEqual(10, preview.Window.Duration, .001);
            Assert.IsNotNull(controller.previewSource.clip);
            Assert.That(controller.previewSource.clip.length, Is.InRange(112.15f, 112.30f));
            Assert.Greater(preview.View.ExcerptNoteCount, 0);
            Assert.That(controller.previewSource.time, Is.EqualTo(preview.SongTime).Within(.35));
            Assert.That(preview.View.DisplayedSongTime, Is.EqualTo(preview.SongTime).Within(.15));
        }
        controller.StopPreview();
        yield return null;
        Assert.IsNull(controller.previewSource.clip);
    }

    [UnityTest]
    public IEnumerator SelectionStartsFullAudioWithLeadInAndHidesOnlyThisSongsBarLines()
    {
        yield return OpenSelection();
        controller.SetDifficulty(0);
        controller.startButton.onClick.Invoke();
        yield return null;
        yield return AwaitGame();
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.AreEqual(SongId, GameSession.SelectedSongId);
        Assert.AreEqual("アンダルシア", GameSession.SelectedSongTitle);
        Assert.AreEqual("Easy", GameSession.SelectedDifficulty);
        Assert.That(manager.songPlayer.Duration, Is.InRange(112.15, 112.30));
        var chart = ChartLoader.LoadFromStreamingAssets(SongId, "easy");
        Assert.AreEqual(chart.notes.Count, manager.noteSpawner.TotalNoteCount);
        Assert.IsNull(manager.barLineSpawner, "変拍子の曲に4拍固定の小節線を流さない");
        Assert.IsFalse(Object.FindObjectsByType<BarLineSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Any(b => b.gameObject.activeInHierarchy));
        double deadline = Time.realtimeSinceStartupAsDouble + 6;
        while (firstNote == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.IsNotNull(firstNote);
        Assert.Less(firstSpawnTime, 0, "冒頭にも音源開始前の助走を確保する");
        Assert.That(firstNote.HitTime - firstSpawnTime, Is.EqualTo(4).Within(.2));
        Assert.That(firstNote.HitTime,
            Is.EqualTo(chart.notes[0].TimeSeconds + manager.noteSpawner.TotalOffsetSeconds).Within(.001));
        Assert.AreEqual(0, manager.scoreManager.MissCount);
        deadline = Time.realtimeSinceStartupAsDouble + 6;
        while (!manager.songPlayer.IsPlaying && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.IsTrue(manager.songPlayer.IsPlaying);
        var audio = manager.songPlayer.GetComponent<AudioSource>();
        Assert.That(audio.time, Is.EqualTo(manager.songPlayer.SongTime).Within(.35));
        manager.songPlayer.Stop();

        // 曲を切り替えても全曲共通の小節線設定を変えていないことを実シーンで確認する。
        GameSession.SelectedSongId = "Morning";
        GameSession.SelectedSongTitle = "Morning";
        GameSession.SelectedDifficulty = "Normal";
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
        yield return AwaitGame();
        Assert.IsNotNull(manager.barLineSpawner);
        Assert.IsTrue(manager.barLineSpawner.gameObject.activeInHierarchy);
        manager.songPlayer.Stop();
    }
}
