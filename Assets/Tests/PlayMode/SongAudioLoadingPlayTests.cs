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

// 合成音源の専用フォルダーで実際のロードを通し、ファイル名と音声資源の寿命を確認する。
public class SongAudioLoadingPlayTests
{
    readonly List<GameObject> objects = new List<GameObject>();
    readonly List<AudioClip> clips = new List<AudioClip>();
    readonly List<string> folders = new List<string>();
    Scene temporaryScene;

    string MakeSong(bool reservedCharacters = false)
    {
        string id = "__SongAudioTest_" + (reservedCharacters ? "日本語 # % " : "") + Guid.NewGuid().ToString("N");
        string folder = Path.Combine(Application.streamingAssetsPath, "Songs", id);
        folders.Add(folder);
        Directory.CreateDirectory(folder);
        using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, "audio.wav"))))
        {
            const int samples = 8000;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
            for (int index = 0; index < samples; index++)
                writer.Write((short)(Math.Sin(index * 220 * Math.PI * 2 / 8000) * 12000));
        }
        return id;
    }

    GamePlayManager MakeManager()
    {
        var go = new GameObject("SongAudioLoadingProbe", typeof(AudioSource), typeof(SongPlayer));
        objects.Add(go);
        var manager = go.AddComponent<GamePlayManager>();
        // 本編全体のStartは既存実シーンテストで確認し、ここでは音源ロードと破棄を単独検証する。
        manager.enabled = false;
        manager.songPlayer = go.GetComponent<SongPlayer>();
        go.GetComponent<AudioSource>().playOnAwake = false;
        return manager;
    }

    IEnumerator Load(GamePlayManager manager, string songId)
    {
        yield return (IEnumerator)typeof(GamePlayManager).GetMethod("LoadAudio", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(manager, new object[] { songId });
        if (manager.songPlayer.Clip != null) clips.Add(manager.songPlayer.Clip);
    }

    [UnityTest]
    public IEnumerator SongFolderWithJapaneseSpacesAndUrlCharactersLoadsAudio()
    {
        var manager = MakeManager();
        yield return Load(manager, MakeSong(true));
        Assert.IsNotNull(manager.songPlayer.Clip, "使用可能な曲フォルダー名の#や%をURLの区切りにしない");
        Assert.AreEqual(8000, manager.songPlayer.Clip.samples);
        var data = new float[20];
        Assert.True(manager.songPlayer.Clip.GetData(data, 0));
        Assert.Greater(Mathf.Abs(data[5]), .1f);
    }

    [UnityTest]
    public IEnumerator SceneUnloadReleasesTheDecodedSongClip()
    {
        temporaryScene = SceneManager.CreateScene("SongAudioOwnership_" + Guid.NewGuid().ToString("N"));
        var manager = MakeManager();
        SceneManager.MoveGameObjectToScene(manager.gameObject, temporaryScene);
        yield return Load(manager, MakeSong());
        var clip = manager.songPlayer.Clip;
        Assert.IsNotNull(clip);
        yield return SceneManager.UnloadSceneAsync(temporaryScene);
        yield return null;
        Assert.IsTrue(clip == null, "曲のシーンを退出したらランタイムでデコードした音声を解放する");
    }

    [UnityTest]
    public IEnumerator ReloadReleasesOnlyThePreviouslyDecodedClip()
    {
        var manager = MakeManager();
        string song = MakeSong();
        yield return Load(manager, song);
        var previous = manager.songPlayer.Clip;
        Assert.IsNotNull(previous);
        yield return Load(manager, song);
        var current = manager.songPlayer.Clip;
        yield return null;
        Assert.IsTrue(previous == null, "再ロード前の音声データを残さない");
        Assert.IsTrue(current != null && current.samples == 8000);
    }

    [UnityTest]
    public IEnumerator DestroyingManagerLeavesExternallyAssignedClipAlive()
    {
        var manager = MakeManager();
        yield return Load(manager, MakeSong());
        var owned = manager.songPlayer.Clip;
        var external = AudioClip.Create("ExternalSongAudioProbe", 8000, 1, 8000, false);
        clips.Add(external);
        var player = manager.songPlayer;
        player.Clip = external;
        Object.Destroy(manager);
        yield return null;
        Assert.IsTrue(owned == null);
        Assert.IsTrue(external != null);
        Assert.AreSame(external, player.Clip, "他の仕組みが割り当てた音源を外したり破棄したりしない");
    }

    [UnityTearDown]
    public IEnumerator Cleanup()
    {
        foreach (var go in objects) if (go != null) Object.Destroy(go);
        yield return null;
        if (temporaryScene.IsValid() && temporaryScene.isLoaded)
            yield return SceneManager.UnloadSceneAsync(temporaryScene);
        foreach (var clip in clips) if (clip != null) Object.Destroy(clip);
        yield return null;
        objects.Clear(); clips.Clear();
        string songsRoot = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs"));
        foreach (string folder in folders)
        {
            string full = Path.GetFullPath(folder);
            Assert.That(Path.GetDirectoryName(full), Is.EqualTo(songsRoot).IgnoreCase);
            Assert.That(Path.GetFileName(full), Does.StartWith("__SongAudioTest_"));
            if (Directory.Exists(full)) Directory.Delete(full, true);
            if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
        }
        folders.Clear();
    }
}
