using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 提供曲を触らず、未制作・読み込み失敗・旧形式の組み合わせを実ファイルで確かめる。
public class SongChartAvailabilityTests
{
    const string EmptyChart = "{\"bpm\":120,\"notes\":[]}";
    const string ReadyChart = "{\"bpm\":120,\"notes\":[{\"time\":2000,\"x\":0,\"y\":0,\"color\":\"blue\"}]}";
    readonly List<string> directories = new List<string>();
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    GameObject root;
    SongSelectController controller;
    string songId;

    [SetUp]
    public void SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        songId = CreateSong();
        root = new GameObject("ChartAvailabilityTest");
        controller = root.AddComponent<SongSelectController>();
        controller.enabled = false;
        var start = new GameObject("Start", typeof(RectTransform), typeof(Image), typeof(Button));
        start.transform.SetParent(root.transform);
        controller.startButton = start.GetComponent<Button>();
        controller.gameSceneName = "__MustNotLoadUnavailableChart__";
    }

    string CreateSong()
    {
        string id = "__ChartAvailability_" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Application.streamingAssetsPath, "Songs", id);
        directories.Add(directory);
        Directory.CreateDirectory(directory);
        Write(id, "chart.json", EmptyChart);
        foreach (string difficulty in SongSelectController.StandardDifficulties)
            Write(id, "chart_" + difficulty.ToLowerInvariant() + ".json", EmptyChart);
        return id;
    }

    void Write(string id, string file, string json)
    {
        File.WriteAllText(Path.Combine(Application.streamingAssetsPath, "Songs", id, file), json);
    }

    void SelectSong(string id)
    {
        for (int i = 0; i < controller.SongCount; i++)
            if (controller.SongIdAt(i) == id) { controller.Select(i); return; }
        Assert.Fail("遊べる難易度を持つ曲が一覧に必要: " + id);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var saved in sessionValues) saved.Key.SetValue(null, saved.Value);
        sessionValues.Clear();
        if (root != null) Object.DestroyImmediate(root);
        string songsRoot = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs")) + Path.DirectorySeparatorChar;
        foreach (string directory in directories)
        {
            string full = Path.GetFullPath(directory);
            Assert.IsTrue(full.StartsWith(songsRoot, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Path.GetFileName(full).StartsWith("__ChartAvailability_", StringComparison.Ordinal));
            if (Directory.Exists(full)) Directory.Delete(full, true);
            if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
        }
        directories.Clear();
    }

    [TestCase("chart.json")]
    [TestCase("chart_easy.json")]
    public void BrokenChartDoesNotHideAnotherPlayableDifficulty(string brokenFile)
    {
        Write(songId, brokenFile, "{ broken");
        Write(songId, "chart_hard.json", ReadyChart);
        Assert.IsTrue(SongSelectController.HasPlayableChart(songId, SongSelectController.StandardDifficulties));
        CollectionAssert.Contains(SongSelectController.EnumerateSongIds(), songId);
    }

    [Test]
    public void EmptySpecificChartsDoNotAdvertiseAnUnreachableLegacyChart()
    {
        Write(songId, "chart.json", ReadyChart);
        Assert.IsFalse(SongSelectController.HasPlayableChart(songId, SongSelectController.StandardDifficulties));
        CollectionAssert.DoesNotContain(SongSelectController.EnumerateSongIds(), songId);
    }

    [Test]
    public void SelectingAnEmptyDifficultyDisablesStart()
    {
        Write(songId, "chart_hard.json", ReadyChart);
        controller.Populate();
        SelectSong(songId);
        Assert.AreEqual(0, controller.CurrentDifficultyLevel());
        Assert.IsFalse(controller.startButton.interactable);
    }

    [Test]
    public void DifficultyChangesUpdateStartBeforeNotifyingTheSkin()
    {
        Write(songId, "chart_easy.json", ReadyChart);
        Write(songId, "chart_hard.json", ReadyChart);
        controller.Populate();
        SelectSong(songId);
        Assert.IsTrue(controller.startButton.interactable);
        bool? notifiedState = null;
        controller.OnDifficultyChanged += _ => notifiedState = controller.startButton.interactable;
        controller.SetDifficulty(1);
        Assert.AreEqual(false, notifiedState);
        controller.SetDifficulty(2);
        Assert.AreEqual(true, notifiedState);
        controller.SetDifficulty(0);
        Assert.IsTrue(controller.startButton.interactable);
    }

    [Test]
    public void ChangingSongsRefreshesStartForTheSelectedDifficulty()
    {
        string other = CreateSong();
        Write(songId, "chart_easy.json", ReadyChart);
        Write(other, "chart_hard.json", ReadyChart);
        controller.Populate();
        SelectSong(songId);
        Assert.IsTrue(controller.startButton.interactable);
        SelectSong(other);
        Assert.IsFalse(controller.startButton.interactable);
        SelectSong(songId);
        Assert.IsTrue(controller.startButton.interactable);
    }

    [TestCase(EmptyChart)]
    [TestCase("{ broken")]
    public void DirectStartDoesNotLaunchOrResetResultsForAnUnavailableDifficulty(string selectedChart)
    {
        Write(songId, "chart_easy.json", selectedChart);
        Write(songId, "chart.json", ReadyChart);
        Write(songId, "chart_hard.json", ReadyChart);
        controller.Populate();
        SelectSong(songId);
        string oldSong = GameSession.SelectedSongId;
        int oldScore = GameSession.FinalScore;
        try
        {
            GameSession.SelectedSongId = "__ExistingSession__";
            GameSession.FinalScore = 12345;
            Assert.DoesNotThrow(() => controller.StartGame());
            Assert.AreEqual("__ExistingSession__", GameSession.SelectedSongId);
            Assert.AreEqual(12345, GameSession.FinalScore);
        }
        finally { GameSession.SelectedSongId = oldSong; GameSession.FinalScore = oldScore; }
    }

    [Test]
    public void ChartRemovedAfterSelectionCannotStartFromCachedAvailability()
    {
        Write(songId, "chart_easy.json", ReadyChart);
        controller.Populate();
        SelectSong(songId);
        Assert.IsTrue(controller.startButton.interactable);
        Write(songId, "chart_easy.json", EmptyChart);
        Assert.DoesNotThrow(() => controller.StartGame());
        Assert.IsFalse(controller.startButton.interactable);
        Assert.AreEqual(0, controller.CurrentDifficultyLevel());
    }

    [Test]
    public void StartIsDisabledUntilASongIsSelected()
    {
        controller.Populate();
        Assert.IsFalse(controller.startButton.interactable);
    }

    [Test]
    public void MissingSpecificFilesStillUseTheLegacyChart()
    {
        Write(songId, "chart.json", ReadyChart);
        foreach (string difficulty in SongSelectController.StandardDifficulties)
            File.Delete(Path.Combine(Application.streamingAssetsPath, "Songs", songId, "chart_" + difficulty.ToLowerInvariant() + ".json"));
        controller.Populate();
        SelectSong(songId);
        for (int i = 0; i < 3; i++)
        {
            controller.SetDifficulty(i);
            Assert.Greater(controller.CurrentDifficultyLevel(), 0);
            Assert.IsTrue(controller.startButton.interactable);
        }
    }

    [Test]
    public void EmptySongsRemainHidden()
    {
        Assert.IsFalse(SongSelectController.HasPlayableChart(songId, SongSelectController.StandardDifficulties));
        CollectionAssert.DoesNotContain(SongSelectController.EnumerateSongIds(), songId);
    }
}
