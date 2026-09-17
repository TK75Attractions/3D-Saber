using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 毎回専用の曲フォルダーと短い合成音を作り、利用者の音源・譜面は触らない。
public class ChartAudioImportTests
{
    static readonly Type Store = typeof(SaberChartEditorWindow).Assembly.GetType("Saber.ChartEditor.SaberChartFileStore");
    readonly List<AudioClip> clips = new List<AudioClip>();
    string songId, folder, sourceFolder, source;
    Dictionary<string, byte[]> originals;

    [SetUp]
    public void SetUp()
    {
        songId = "__AudioImportTest_" + Guid.NewGuid().ToString("N");
        folder = (string)Call("SongFolderPath", songId);
        sourceFolder = Path.Combine(Application.dataPath, "..", "Library", songId);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(sourceFolder);
        source = Path.Combine(sourceFolder, "incoming.wav");
        WriteWave(source, 220, 8000);
        WriteWave(Path.Combine(folder, "audio.wav"), 110, 4000);
        File.WriteAllText(Path.Combine(folder, "audio.ogg"), "previous ogg");
        File.WriteAllText(Path.Combine(folder, "audio.mp3"), "previous mp3");
        File.WriteAllText(Path.Combine(folder, "chart.json"), "{\"bpm\":123,\"notes\":[]}");
        foreach (string name in new[] { "audio.ogg", "audio.wav", "audio.mp3" })
            File.WriteAllText(Path.Combine(folder, name + ".meta"), "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
        originals = new Dictionary<string, byte[]>();
        foreach (string path in Directory.GetFiles(folder)) originals.Add(Path.GetFileName(path), File.ReadAllBytes(path));
    }

    [TearDown]
    public void TearDown()
    {
        // 内部キャッシュに残る試験用クリップも解放する。実曲のキャッシュは変更しない。
        var cache = (IDictionary)Store.GetField("audioCache", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        foreach (string name in new[] { "audio.ogg", "audio.wav", "audio.mp3" })
        {
            string path = Path.Combine(folder, name);
            if (!cache.Contains(path)) continue;
            var cached = cache[path];
            var clip = (AudioClip)cached.GetType().GetField("clip").GetValue(cached);
            if (clip) Object.DestroyImmediate(clip);
            cache.Remove(path);
        }
        foreach (var clip in clips) if (clip) Object.DestroyImmediate(clip);
        clips.Clear();
        DeleteTestDirectory(folder, Path.Combine(Application.dataPath, "StreamingAssets", "Songs"));
        DeleteTestDirectory(sourceFolder, Path.Combine(Application.dataPath, "..", "Library"));
        if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
        AssetDatabase.Refresh();
    }

    [Test]
    public void LockedSourceDoesNotRemoveAnyExistingAudioOrMetadata()
    {
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => Import(source));
        AssertOriginals();
    }

    [Test]
    public void LockedDestinationRestoresPreviouslyMovedFormats()
    {
        using (new FileStream(Path.Combine(folder, "audio.wav"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => Import(source));
        AssertOriginals();
    }

    [Test]
    public void LockedObsoleteMetadataRestoresAudioAndMetadataTogether()
    {
        using (new FileStream(Path.Combine(folder, "audio.mp3.meta"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => Import(source));
        AssertOriginals();
    }

    [Test]
    public void UndecodableAudioDoesNotReplaceTheSong()
    {
        File.WriteAllText(source, "This is not a WAV file");
        bool oldIgnore = LogAssert.ignoreFailingMessages;
        try
        {
            // デコーダーが出す破損音源のエラー自体は想定内。取り込み失敗と元ファイル保全を検証する。
            LogAssert.ignoreFailingMessages = true;
            Assert.Throws<InvalidOperationException>(() => Import(source));
        }
        finally { LogAssert.ignoreFailingMessages = oldIgnore; }
        AssertOriginals();
    }

    [Test]
    public void SuccessfulReplacementKeepsDestinationGuidAndReturnsNewAudio()
    {
        AudioClip clip = Import(source);
        Assert.That(clip, Is.Not.Null);
        Assert.That(clip.samples, Is.EqualTo(8000));
        CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(folder, "audio.wav")));
        CollectionAssert.AreEqual(originals["audio.wav.meta"], File.ReadAllBytes(Path.Combine(folder, "audio.wav.meta")));
        CollectionAssert.AreEqual(originals["chart.json"], File.ReadAllBytes(Path.Combine(folder, "chart.json")));
        foreach (string name in new[] { "audio.ogg", "audio.ogg.meta", "audio.mp3", "audio.mp3.meta" })
            Assert.False(File.Exists(Path.Combine(folder, name)), name);
        Assert.True((bool)Call("IsAudioClipForSong", clip, songId));
    }

    [Test]
    public void ImportingTheCurrentFilePreservesItsBytesAndMetadata()
    {
        AudioClip clip = Import(Path.Combine(folder, "audio.wav"));
        Assert.That(clip.samples, Is.EqualTo(4000));
        CollectionAssert.AreEqual(originals["audio.wav"], File.ReadAllBytes(Path.Combine(folder, "audio.wav")));
        CollectionAssert.AreEqual(originals["audio.wav.meta"], File.ReadAllBytes(Path.Combine(folder, "audio.wav.meta")));
        Assert.False(File.Exists(Path.Combine(folder, "audio.ogg")));
    }

    [Test]
    public void RetainingOtherFormatsDoesNotDeleteTheirFilesOrMetadata()
    {
        // この試験の他形式はダミーなので、保持したファイルのデコード失敗ログだけを許容する。
        bool oldIgnore = LogAssert.ignoreFailingMessages;
        try
        {
            LogAssert.ignoreFailingMessages = true;
            Assert.That(Import(source, false), Is.Not.Null);
        }
        finally { LogAssert.ignoreFailingMessages = oldIgnore; }
        foreach (string name in new[] { "audio.ogg", "audio.ogg.meta", "audio.mp3", "audio.mp3.meta" })
            CollectionAssert.AreEqual(originals[name], File.ReadAllBytes(Path.Combine(folder, name)));
    }

    [Test]
    public void EqualSizeAndTimestampStillRefreshTheDecodedWaveform()
    {
        Import(source);
        var oldClip = (AudioClip)Call("LoadAudioClip", songId);
        clips.Add(oldClip);
        var before = new float[8000];
        Assert.True(oldClip.GetData(before, 0));
        DateTime timestamp = File.GetLastWriteTimeUtc(Path.Combine(folder, "audio.wav"));
        WriteWave(source, 440, 8000);
        File.SetLastWriteTimeUtc(source, timestamp);
        var newClip = Import(source);
        Assert.False(oldClip, "差し替えた古いキャッシュの音声データも解放する");
        var after = new float[8000];
        Assert.True(newClip.GetData(after, 0));
        Assert.That(after[10], Is.Not.EqualTo(before[10]).Within(.01f));
        Assert.AreSame(newClip, Call("LoadAudioClip", songId));
    }

    [Test]
    public void FailedReplacementKeepsThePreviouslyLoadedClip()
    {
        var previous = Import(source);
        byte[] previousBytes = File.ReadAllBytes(Path.Combine(folder, "audio.wav"));
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => Import(source));
        Assert.True(previous, "失敗時は現在の編集・試聴用音源を破棄しない");
        Assert.AreSame(previous, Call("LoadAudioClip", songId));
        CollectionAssert.AreEqual(previousBytes, File.ReadAllBytes(Path.Combine(folder, "audio.wav")));
    }

    AudioClip Import(string path, bool removeOtherFormats = true)
    {
        var clip = (AudioClip)Call("ImportAudio", path, songId, removeOtherFormats);
        if (clip) clips.Add(clip);
        return clip;
    }

    void AssertOriginals()
    {
        CollectionAssert.AreEquivalent(originals.Keys, Array.ConvertAll(Directory.GetFiles(folder), Path.GetFileName));
        foreach (var item in originals)
            CollectionAssert.AreEqual(item.Value, File.ReadAllBytes(Path.Combine(folder, item.Key)), item.Key);
    }

    static object Call(string method, params object[] args)
    {
        try { return Store.GetMethod(method, BindingFlags.Static | BindingFlags.Public).Invoke(null, args); }
        catch (TargetInvocationException exception) { throw exception.InnerException; }
    }

    static void WriteWave(string path, double frequency, int samples)
    {
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
            for (int index = 0; index < samples; index++)
                writer.Write((short)(Math.Sin(index * frequency * Math.PI * 2 / 8000) * 16000));
        }
    }

    static void DeleteTestDirectory(string path, string root)
    {
        string full = Path.GetFullPath(path);
        Assert.That(Path.GetFileName(full), Does.StartWith("__AudioImportTest_"));
        Assert.That(Path.GetDirectoryName(full), Is.EqualTo(Path.GetFullPath(root)).IgnoreCase);
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
