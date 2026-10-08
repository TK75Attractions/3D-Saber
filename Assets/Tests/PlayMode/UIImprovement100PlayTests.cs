using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 試聴の実コルーチンを使い、失敗時の再試行と消音設定の寿命を確認する。
public class UIImprovement100PlayTests
{
    string directory, songId;
    GameObject root;
    bool hadMuted;
    int oldMuted;

    [SetUp]
    public void Setup()
    {
        hadMuted = PlayerPrefs.HasKey("songSelectPreviewMuted");
        oldMuted = PlayerPrefs.GetInt("songSelectPreviewMuted");
        songId = "__UIImprovement100Preview_" + Guid.NewGuid().ToString("N");
        directory = Path.Combine(Application.streamingAssetsPath, "Songs", songId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "chart_hard.json"),
            "{\"bpm\":120,\"notes\":[{\"time\":2000,\"color\":\"blue\"}]}");
        root = new GameObject("UIImprovementPreviewTest");
    }

    [TearDown]
    public void Cleanup()
    {
        if (root != null) Object.DestroyImmediate(root);
        if (hadMuted) PlayerPrefs.SetInt("songSelectPreviewMuted", oldMuted); else PlayerPrefs.DeleteKey("songSelectPreviewMuted");
        PlayerPrefs.Save();
        string full = Path.GetFullPath(directory);
        string songs = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs")) + Path.DirectorySeparatorChar;
        Assert.True(full.StartsWith(songs, StringComparison.OrdinalIgnoreCase));
        Assert.True(Path.GetFileName(full).StartsWith("__UIImprovement100Preview_", StringComparison.Ordinal));
        if (Directory.Exists(full)) Directory.Delete(full, true);
        if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
    }

    [UnityTest]
    public IEnumerator MissingAudioReportsFailureAndAllowsFreshRetry()
    {
        var source = root.AddComponent<AudioSource>(); source.playOnAwake = false;
        var preview = root.AddComponent<SongSelectChartPreview>(); preview.Initialize(source);
        preview.Select(songId, "Hard", 10);
        Assert.AreEqual(SongSelectChartPreview.PreviewState.Waiting, preview.State);
        double deadline = Time.realtimeSinceStartupAsDouble + 5;
        while (preview.IsLoading && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.False(preview.IsLoading);
        Assert.AreEqual(SongSelectChartPreview.PreviewState.Failed, preview.State);
        StringAssert.Contains("再試行", preview.StatusText);
        preview.Select(songId, "Hard", 10);
        Assert.AreEqual(SongSelectChartPreview.PreviewState.Waiting, preview.State);
        preview.Cancel();
        Assert.AreEqual(SongSelectChartPreview.PreviewState.Idle, preview.State);
        Assert.False(preview.IsLoading); Assert.IsNull(source.clip);
    }

    [Test]
    public void PreviewMuteOnlyChangesPreviewAndSurvivesReinitialization()
    {
        var source = root.AddComponent<AudioSource>();
        var preview = root.AddComponent<SongSelectChartPreview>(); preview.Initialize(source);
        var musicObject = new GameObject("OtherAudio"); musicObject.transform.SetParent(root.transform);
        var music = musicObject.AddComponent<AudioSource>();
        preview.SetMuted(true);
        Assert.True(source.mute); Assert.False(music.mute);
        preview.Initialize(source); Assert.True(preview.Muted);
        preview.SetMuted(false); Assert.False(source.mute);
    }
}
