using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public class SongLoudnessTests
{
    static readonly Type Store = typeof(SaberChartEditorWindow).Assembly.GetType("Saber.ChartEditor.SaberChartFileStore");
    string id, folder, source;

    [SetUp]
    public void Setup()
    {
        id = "__LoudnessTest_" + Guid.NewGuid().ToString("N");
        folder = Path.Combine(Application.streamingAssetsPath, "Songs", id);
        Directory.CreateDirectory(folder);
        source = Path.Combine(folder, "audio.wav");
        // 最終ブロックが半端になる長さで、ステレオの末尾まで検証する。
        const int samples = 40007;
        using (var w = new BinaryWriter(File.Create(source)))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 4);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
            w.Write((short)1); w.Write((short)2); w.Write(8000); w.Write(32000);
            w.Write((short)4); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(samples * 4);
            for (int i = 0; i < samples; i++) { w.Write((short)8192); w.Write((short)-16384); }
        }
    }

    static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    SongLoudness.Metadata Metadata(float db = -6.0206f) => new SongLoudness.Metadata
        { version = 1, sourceFile = "audio.wav", sourceSha256 = Hash(source), gainDb = db };

    void Save(SongLoudness.Metadata data)
    {
        File.WriteAllText(Path.Combine(folder, "loudness.json"), JsonUtility.ToJson(data));
        SongLoudness.Invalidate(source);
    }

    AudioClip EditorClip() => (AudioClip)Store.GetMethod("LoadAudioClip").Invoke(null, new object[] { id });

    [TearDown]
    public void Cleanup()
    {
        var cache = (IDictionary)Store.GetField("audioCache", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        if (cache.Contains(source))
        {
            var entry = cache[source];
            var clip = (AudioClip)entry.GetType().GetField("clip").GetValue(entry);
            if (clip != null) Object.DestroyImmediate(clip);
            cache.Remove(source);
        }
        SongLoudness.Invalidate(source);
        Assert.That(Path.GetFileName(folder), Does.StartWith("__LoudnessTest_"));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs")), Path.GetDirectoryName(Path.GetFullPath(folder)));
        Directory.Delete(folder, true);
        if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
    }

    [Test]
    public void MissingBrokenOrStaleMetadataUsesOriginalAudio()
    {
        Assert.AreEqual(1, SongLoudness.Resolve(source).Gain);
        File.WriteAllText(Path.Combine(folder, "loudness.json"), "{broken");
        Assert.AreEqual(1, SongLoudness.Resolve(source).Gain);
        var data = Metadata(); data.sourceSha256 = new string('0', 64); Save(data);
        Assert.AreEqual(1, SongLoudness.Resolve(source).Gain);
        Save(Metadata());
        Assert.AreEqual(.5f, SongLoudness.Resolve(source).Gain, .00001);
        // ファイル更新後はSHAを再照合し、前の曲の補正を使わない。
        File.AppendAllText(source, "changed");
        Assert.AreEqual(1, SongLoudness.Resolve(source).Gain);
    }

    [Test]
    public void DerivedAssetMustBeLocalAndMatchItsHash()
    {
        var data = Metadata(0);
        data.playbackFile = "../outside.wav"; data.playbackSha256 = data.sourceSha256; Save(data);
        Assert.AreEqual(source, SongLoudness.Resolve(source).Path);
        data.playbackFile = "normalized.wav"; Save(data);
        Assert.AreEqual(source, SongLoudness.Resolve(source).Path);
        string derived = Path.Combine(folder, data.playbackFile);
        File.Copy(source, derived);
        Assert.AreEqual(Path.GetFullPath(derived), Path.GetFullPath(SongLoudness.Resolve(source).Path), "後から復元した派生音源も再照合する");
        File.AppendAllText(derived, "changed");
        Assert.AreEqual(source, SongLoudness.Resolve(source).Path);
    }

    [Test]
    public void EditorScalesBothChannelsAndTailOnceWithoutChangingTimingOrOriginal()
    {
        string originalHash = Hash(source);
        Save(Metadata());
        var clip = EditorClip();
        Assert.NotNull(clip); Assert.AreEqual(40007, clip.samples); Assert.AreEqual(8000, clip.frequency);
        foreach (int frame in new[] { 0, 32767, 32768, 40006 })
        {
            var samples = new float[2]; Assert.True(clip.GetData(samples, frame));
            Assert.AreEqual(.125, samples[0], .0001); Assert.AreEqual(-.25, samples[1], .0001);
        }
        Assert.AreSame(clip, EditorClip(), "再描画で二重補正・再デコードしない");
        Assert.AreEqual(originalHash, Hash(source));
        Save(Metadata(0));
        var replacement = EditorClip();
        var unscaled = new float[2]; replacement.GetData(unscaled, 40006);
        Assert.AreEqual(.25, unscaled[0], .0001);
        Assert.IsTrue(clip == null, "古い補正クリップを解放する");
    }

    [Test]
    public void ReimportWithSameTimestampAndSizeInvalidatesOldNormalization()
    {
        Save(Metadata()); EditorClip();
        string input = Path.Combine(folder, "incoming.wav");
        File.Copy(source, input);
        using (var stream = File.OpenWrite(input)) { stream.Position = 44; stream.WriteByte(42); }
        File.SetLastWriteTimeUtc(input, File.GetLastWriteTimeUtc(source));
        Store.GetMethod("ImportAudio").Invoke(null, new object[] { input, id, true });
        Assert.AreEqual(1, SongLoudness.Resolve(source).Gain);
        var data = new float[2]; Assert.True(EditorClip().GetData(data, 10));
        Assert.AreEqual(.25, data[0], .0001);
    }

    [Test]
    public void EveryBundledSongHasValidNormalizationForItsCurrentSource()
    {
        var folders = Directory.GetDirectories(Path.Combine(Application.streamingAssetsPath, "Songs"))
            .Where(f => !Path.GetFileName(f).StartsWith("__"));
        int count = 0;
        foreach (string f in folders)
        {
            string path = new[] { "audio.ogg", "audio.wav", "audio.mp3" }.Select(n => Path.Combine(f, n)).FirstOrDefault(File.Exists);
            if (path == null) continue;
            Assert.True(File.Exists(Path.Combine(f, "loudness.json")), f);
            var resolved = SongLoudness.Resolve(path);
            Assert.True(resolved.Gain < 1 || resolved.Path != path, f + " / 補正が原音へフォールバックしていない");
            Assert.That(resolved.Gain, Is.GreaterThan(0).And.LessThanOrEqualTo(1));
            count++;
        }
        Assert.AreEqual(14, count);
    }

    [UnityTest]
    public IEnumerator NormalizedLogicHasTheSameAudioTimelineInUnity()
    {
        string logic = Path.Combine(Application.streamingAssetsPath, "Songs", "logic");
        AudioClip original = null, normalized = null;
        try
        {
            foreach (string file in new[] { "audio.mp3", "normalized.wav" })
            {
                using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(Path.Combine(logic, file)).AbsoluteUri,
                    file.EndsWith(".wav") ? AudioType.WAV : AudioType.MPEG))
                {
                    yield return request.SendWebRequest();
                    Assert.AreEqual(UnityWebRequest.Result.Success, request.result, request.error);
                    var clip = DownloadHandlerAudioClip.GetContent(request);
                    if (file.EndsWith(".mp3")) original = clip; else normalized = clip;
                }
            }
            Assert.AreEqual(original.frequency, normalized.frequency);
            Assert.AreEqual(original.channels, normalized.channels);
            Assert.That(normalized.length, Is.EqualTo(original.length).Within(2)); // MP3デコーダの末尾無音を許容
            foreach (int second in new[] { 10, 60, 100 })
            {
                var a = new float[original.frequency * original.channels]; var b = new float[a.Length];
                Assert.True(original.GetData(a, original.frequency * second));
                Assert.True(normalized.GetData(b, normalized.frequency * second));
                double aa = 0, bb = 0, ab = 0;
                for (int i = 0; i < a.Length; i++) { aa += a[i] * a[i]; bb += b[i] * b[i]; ab += a[i] * b[i]; }
                Assert.Greater(ab / Math.Sqrt(aa * bb), .995, second + "秒: 時間シフトなしで波形が一致する");
                Assert.Greater(bb, aa, "静かな原音を音割れさせず増幅する");
            }
        }
        finally
        {
            if (original) Object.DestroyImmediate(original);
            if (normalized) Object.DestroyImmediate(normalized);
        }
    }
}
