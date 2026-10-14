using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

public class RushESongTests
{
    const string Id = "RushE";
    const double Duration = 144.21913832199547;
    static string Folder => Path.Combine(Application.streamingAssetsPath, "Songs", Id);
    [Serializable] sealed class CutRow { public int index; public double[] timesMs; public string[] directions; }
    [Serializable] sealed class CutPlan { public CutRow[] notes; }

    [TestCase("easy")]
    [TestCase("normal")]
    [TestCase("hard")]
    public void EditorRoundTripKeepsEveryAudioTimeAndTempoAnchor(string difficulty)
    {
        string json = File.ReadAllText(Path.Combine(Folder, "chart_" + difficulty + ".json"));
        var source = ChartLoader.Parse(json);
        var document = SaberChartUtility.Clone(SaberChartUtility.FromJson(json));
        var grid = new SaberChartGrid(document.bpm, document.beatZeroMs, document.tempoMap);
        var result = ChartLoader.Parse(SaberChartUtility.ToFileJson(document));
        Assert.Greater(source.notes.Count, 100);
        Assert.AreEqual(source.notes.Count, result.notes.Count);
        Assert.AreEqual(source.editorTempoMap.Count, result.editorTempoMap.Count);
        Assert.AreEqual(source.displayLevel, result.displayLevel);
        foreach (var pair in source.notes.Zip(result.notes, (a, b) => (a, b)))
        {
            Assert.AreEqual(pair.a.time, pair.b.time, .001f);
            Assert.AreEqual(pair.a.lengthMs, pair.b.lengthMs, .001f);
            Assert.AreEqual(grid.TimeAt(pair.b.beat), pair.b.time, .10f);
            Assert.Less((pair.b.time + pair.b.lengthMs) / 1000.0, Duration);
        }
        var window = SongPreviewWindow.Resolve(StagePerformanceTimeline.Load(Id), Duration);
        Assert.IsTrue(source.notes.Any(n => SongPreviewWindow.Intersects(source, n, window, 0)));
    }

    [Test]
    public void CreditsAndLocalAudioRequirementSurviveSongInfoEditing()
    {
        SongDisplayInfo.ClearCache();
        var info = SongDisplayInfo.Get(Id);
        Assert.AreEqual("RUSH E", info.DisplayName);
        Assert.AreEqual("Andrew Wrangell", info.Composer);
        Assert.AreEqual("Sheet Music Boss", info.Artist);
        Assert.AreEqual("https://www.youtube.com/watch?v=Qskm9MTz2V4", info.SourceUrl);
        Assert.AreEqual("audio.wav", info.RequiredAudioFile);
        var edited = SaberStageInfo.Parse(File.ReadAllText(Path.Combine(Folder, "stage.json")));
        edited.DisplayName = "Changed for round trip only";
        StringAssert.Contains("Qskm9MTz2V4", edited.Write());
        StringAssert.Contains("requiredAudioFile", edited.Write());
        Assert.AreEqual(File.ReadAllText(Path.Combine(Folder, "chart_normal.json")), File.ReadAllText(Path.Combine(Folder, "chart.json")));
        Assert.AreEqual(File.Exists(Path.Combine(Folder, "audio.wav")), SongSelectController.EnumerateSongIds().Contains(Id));
    }

    [TestCase(null, "")]
    [TestCase("javascript:alert(1)", "")]
    [TestCase("file:///C:/example", "")]
    [TestCase(" https://example.com/music ", "https://example.com/music")]
    public void SourceReferenceAcceptsOnlyPublicWebUrls(string input, string expected)
    {
        var info = new SongDisplayInfo.Info("Example", "", "Artist", true, "Composer", input);
        Assert.AreEqual(expected, info.SourceUrl);
    }

    [TestCase("easy")]
    [TestCase("normal")]
    [TestCase("hard")]
    public void EntireChartCanBeCutAndScoredWithThePlannedHands(string difficulty)
    {
        var random = UnityEngine.Random.state;
        var root = new GameObject("RushEPlayability");
        try
        {
            var spawner = root.AddComponent<NoteSpawner>();
            var score = root.AddComponent<ScoreManager>(); score.Bind(spawner);
            spawner.noteRoot = root.transform; spawner.buildTimingCues = false; spawner.simultaneousGuideEnabled = false;
            var prefab = new GameObject("Note", typeof(CuttableNote)); prefab.transform.SetParent(root.transform);
            prefab.GetComponent<CuttableNote>().shatterDebrisCount = 0;
            spawner.notePrefab = spawner.notePrefabRed = spawner.notePrefabBlue = prefab;
            var spawned = new Dictionary<int, CuttableNote>(); int index = 0;
            spawner.OnNoteSpawned += n => { spawned[index++] = n; if (n.countLabel != null) n.countLabel.transform.SetParent(root.transform, true); };
            var chart = ChartLoader.LoadFromStreamingAssets(Id, difficulty);
            var plan = JsonUtility.FromJson<CutPlan>(File.ReadAllText(Path.Combine("Tools", "ChartDesign", Id, difficulty + "-cut-plan.json")));
            Assert.AreEqual(chart.notes.Count, plan.notes.Length);
            var cuts = plan.notes.SelectMany(row => row.timesMs.Select((time, k) => (row.index, time, direction: row.directions[k]))).OrderBy(c => c.time).ToArray();
            spawner.SetChart(chart);
            foreach (var cut in cuts)
            {
                double seconds = cut.time / 1000.0;
                spawner.Tick(seconds);
                Assert.IsTrue(spawned.TryGetValue(cut.index, out var note), "生成漏れ: " + cut.index);
                Assert.IsNotNull(note, "カット前の退場: " + cut.index);
                var hand = chart.notes[cut.index].x > 0 ? SaberHand.Right : SaberHand.Left;
                var direction = CutDirectionHelper.ToVector(CutDirectionHelper.Parse(cut.direction)).normalized;
                Assert.IsTrue(note.TryCutWithCameraTiming(note.transform.position, new Vector3(direction.x, direction.y, 0) * 12,
                    hand, seconds, .866f), difficulty + " / " + cut.index);
            }
            spawner.Tick(cuts.Last().time / 1000.0 + 5);
            Assert.AreEqual(chart.notes.Count, score.PerfectCount);
            Assert.AreEqual(0, score.MissCount);
            Assert.AreEqual(chart.notes.Count, score.MaxCombo);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Random.state = random; }
    }

    [UnityTest]
    public IEnumerator LocalRecordingDecodesWithoutChangingTheChartClock()
    {
        string path = Path.Combine(Folder, "audio.wav");
        if (!File.Exists(path)) Assert.Ignore("文化祭用の許諾音源はGitに含めないため、この端末ではデコード検証を省略します。");
        using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.WAV))
        {
            yield return request.SendWebRequest();
            Assert.AreEqual(UnityWebRequest.Result.Success, request.result, request.error);
            var clip = DownloadHandlerAudioClip.GetContent(request);
            Assert.IsNotNull(clip);
            try
            {
                Assert.AreEqual(44100, clip.frequency); Assert.AreEqual(2, clip.channels);
                Assert.AreEqual(Duration, (double)clip.samples / clip.frequency, .0001);
                var pcm = new float[44100 * 2]; Assert.IsTrue(clip.GetData(pcm, 44100 * 90));
                Assert.Greater(pcm.Max(v => Mathf.Abs(v)), .1);
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(path))
                    Assert.AreEqual(JsonUtility.FromJson<AudioHash>(File.ReadAllText(Path.Combine(Folder, "stage.json"))).audioSha256,
                        BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant());
            }
            finally { UnityEngine.Object.DestroyImmediate(clip); }
        }
    }
    [Serializable] sealed class AudioHash { public string audioSha256; }
}
