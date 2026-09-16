using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 実際の選曲画面で、開始を拒否する経路と遊べる難易度への切替をつなげて確認する。
public class SongChartAvailabilityPlayTests
{
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    string directory, songId;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        GameSession.IsCalibrationMode = false;
        songId = "__ChartAvailabilityPlay_" + Guid.NewGuid().ToString("N");
        directory = Path.Combine(Application.streamingAssetsPath, "Songs", songId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "chart.json"), "{\"bpm\":120,\"notes\":[]}");
        File.WriteAllText(Path.Combine(directory, "chart_easy.json"), "{\"bpm\":120,\"notes\":[]}");
        File.WriteAllText(Path.Combine(directory, "chart_normal.json"), "{ broken");
        File.WriteAllText(Path.Combine(directory, "chart_hard.json"),
            "{\"bpm\":120,\"notes\":[{\"time\":2000,\"x\":0,\"y\":0,\"color\":\"blue\"}]}");
        // 他の曲を借用せず、読み込みと再生時間だけを確認する3秒の無音WAV。
        using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, "audio.wav"))))
        {
            int byteCount = 8000 * 3 * 2;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + byteCount);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(byteCount);
            writer.Write(new byte[byteCount]);
        }
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        var active = SceneManager.GetActiveScene();
        var empty = SceneManager.CreateScene("ChartAvailabilityCleanup_" + Guid.NewGuid().ToString("N"));
        SceneManager.SetActiveScene(empty);
        if (active.IsValid() && active.isLoaded) yield return SceneManager.UnloadSceneAsync(active);
        foreach (var saved in sessionValues) saved.Key.SetValue(null, saved.Value);
        sessionValues.Clear();
        if (directory != null)
        {
            string full = Path.GetFullPath(directory);
            string root = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs")) + Path.DirectorySeparatorChar;
            Assert.IsTrue(full.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Path.GetFileName(full).StartsWith("__ChartAvailabilityPlay_", StringComparison.Ordinal));
            if (Directory.Exists(full)) Directory.Delete(full, true);
            if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
        }
    }

    [UnityTest]
    public IEnumerator UnavailableDifficultyStaysInMenuAndPlayableDifficultyStartsGame()
    {
        SongSelectController controller = null;
        double deadline = Time.realtimeSinceStartupAsDouble + 15;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            controller = Object.FindFirstObjectByType<SongSelectController>();
            if (controller != null && controller.ChartPreview?.View != null) break;
            yield return null;
        }
        Assert.IsNotNull(controller);
        Assert.IsNotNull(controller.ChartPreview?.View, "選曲スキンの準備が完了している");
        int selected = -1;
        for (int i = 0; i < controller.SongCount; i++) if (controller.SongIdAt(i) == songId) selected = i;
        Assert.GreaterOrEqual(selected, 0, "Hardが遊べる曲は一覧に残る");
        controller.Select(selected);
        string previousSong = GameSession.SelectedSongId;
        int previousScore = GameSession.FinalScore;
        foreach (int difficulty in new[] { 0, 1 })
        {
            controller.SetDifficulty(difficulty);
            Assert.IsFalse(controller.startButton.interactable);
            // Enter/Spaceが通る入口。Buttonの見た目だけを無効にしても、この呼出しは防げない。
            controller.StartGame();
            yield return null;
            Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
            Assert.AreEqual(previousSong, GameSession.SelectedSongId);
            Assert.AreEqual(previousScore, GameSession.FinalScore);
        }
        controller.SetDifficulty(2);
        Assert.IsTrue(controller.startButton.interactable);
        controller.startButton.onClick.Invoke();
        yield return null;
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.AreEqual(songId, GameSession.SelectedSongId);
        Assert.AreEqual("Hard", GameSession.SelectedDifficulty);
        GamePlayManager game = null;
        deadline = Time.realtimeSinceStartupAsDouble + 15;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            game = Object.FindFirstObjectByType<GamePlayManager>();
            if (game != null && game.songPlayer != null && game.songPlayer.IsPlaying) break;
            yield return null;
        }
        Assert.IsNotNull(game);
        Assert.IsTrue(game.songPlayer.IsPlaying, "カウントイン後に曲の時計が進む");
        Assert.AreEqual(3, game.songPlayer.Duration, .02);
        Assert.AreEqual(1, game.noteSpawner.TotalNoteCount);
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        yield return null;
        Assert.IsNotNull(Object.FindFirstObjectByType<SongSelectController>());
    }
}
