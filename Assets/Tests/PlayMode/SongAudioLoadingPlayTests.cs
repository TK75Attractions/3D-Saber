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

    string MakeSong(bool reservedCharacters = false, int seconds = 1)
    {
        string id = "__SongAudioTest_" + (reservedCharacters ? "日本語 # % " : "") + Guid.NewGuid().ToString("N");
        string folder = Path.Combine(Application.streamingAssetsPath, "Songs", id);
        folders.Add(folder);
        Directory.CreateDirectory(folder);
        using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, "audio.wav"))))
        {
            int samples = 8000 * seconds;
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

    void AddLoudness(string id)
    {
        string folder = Path.Combine(Application.streamingAssetsPath, "Songs", id);
        string path = Path.Combine(folder, "audio.wav");
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            var metadata = new SongLoudness.Metadata { version = 1, sourceFile = "audio.wav", gainDb = -6.0206f,
                sourceSha256 = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "") };
            File.WriteAllText(Path.Combine(folder, "loudness.json"), JsonUtility.ToJson(metadata));
        }
    }

    [UnityTest]
    public IEnumerator GameplayReloadDoesNotMultiplyGainAndNextRawSongRestoresBaseVolume()
    {
        string id = MakeSong(); AddLoudness(id);
        var manager = MakeManager();
        var audio = manager.songPlayer.GetComponent<AudioSource>(); audio.volume = .6f;
        yield return Load(manager, id);
        Assert.AreEqual(.3f, audio.volume, .0001f);
        Assert.AreEqual(8000, manager.songPlayer.Clip.samples);
        yield return Load(manager, id);
        Assert.AreEqual(.3f, audio.volume, .0001f);
        yield return Load(manager, MakeSong());
        Assert.AreEqual(.6f, audio.volume, .0001f);
    }

    [UnityTest]
    public IEnumerator PreviewFadeReplayAndSongChangeUseTheSameNormalization()
    {
        string id = MakeSong(seconds: 4); AddLoudness(id);
        var chart = new ChartData { bpm = 120, notes = new List<NoteData> { new NoteData { time = 1000, color = "red" } } };
        string folder = Path.Combine(Application.streamingAssetsPath, "Songs", id);
        File.WriteAllText(Path.Combine(folder, "chart_normal.json"), JsonUtility.ToJson(chart));
        var go = new GameObject("LoudnessPreview", typeof(AudioSource)); objects.Add(go);
        var source = go.GetComponent<AudioSource>(); source.volume = .6f; source.playOnAwake = false;
        var preview = go.AddComponent<SongSelectChartPreview>(); preview.Initialize(source);
        for (int repeat = 0; repeat < 3; repeat++)
        {
            if (repeat == 2) File.Delete(Path.Combine(folder, "loudness.json"));
            preview.Select(id, "normal", 4);
            double deadline = Time.realtimeSinceStartupAsDouble + 8;
            while ((!preview.IsPlaying || preview.SongTime < preview.Window.Start + .2) && Time.realtimeSinceStartupAsDouble < deadline)
            { preview.Tick(); yield return null; }
            Assert.True(preview.IsPlaying);
            preview.Tick();
            Assert.AreEqual(repeat == 2 ? .6f : .3f, source.volume, .001f);
            Assert.AreEqual(32000, source.clip.samples);
            preview.Cancel();
            Assert.AreEqual(.6f, source.volume, .0001f);
            yield return null;
        }
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

    // 音の出力先が変わったあとに曲を読み直せなかったら、スタッフ向けの知らせを画面に出す
    // (「続きから再開します」の知らせのまま、ログだけ残して無音で続いていた。2026-10-23 点検)。
    [UnityTest]
    public IEnumerator FailedReloadAfterAudioChangeShowsAStaffNotice()
    {
        string id = MakeSong();
        var manager = MakeManager();
        typeof(GamePlayManager).GetField("loadedSongId", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, id);
        File.Delete(Path.Combine(Application.streamingAssetsPath, "Songs", id, "audio.wav"));
        foreach (var old in Object.FindObjectsByType<OperatorNotice>(FindObjectsSortMode.None)) Object.Destroy(old.gameObject);
        yield return null;
        yield return (IEnumerator)typeof(GamePlayManager).GetMethod("ReloadSongInPlace", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(manager, null);
        var notice = Object.FindFirstObjectByType<OperatorNotice>();
        Assert.NotNull(notice, "読み直せなかったことを画面に出す");
        Assert.AreEqual(AudioOutputRecovery.ReloadFailedNotice, notice.GetComponentInChildren<TMPro.TextMeshProUGUI>().text);
        Assert.IsNull(manager.songPlayer.Clip, "読めなかった音は使わない");
        objects.Add(notice.gameObject);
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
