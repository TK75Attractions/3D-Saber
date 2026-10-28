using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

// 可変テンポの実譜面が、本編とエディターの往復で同じ音源時刻を保つことを確認する。
public class PhobiaSongTests
{
    const string SongId = "Phobia PARTⅠ";
    const double Duration = 110.968163;
    static string Folder => Path.Combine(Application.streamingAssetsPath, "Songs", SongId);

    [TestCase("easy")]
    [TestCase("normal")]
    [TestCase("hard")]
    public void TempoChangeSurvivesEditorRoundTripWithoutRetimingNotes(string difficulty)
    {
        string json = File.ReadAllText(Path.Combine(Folder, "chart_" + difficulty + ".json"));
        var source = ChartLoader.Parse(json);
        var document = SaberChartUtility.FromJson(json);
        var clone = SaberChartUtility.Clone(document);
        var grid = new SaberChartGrid(clone.bpm, clone.beatZeroMs, clone.tempoMap);
        Assert.AreEqual(96f, grid.TempoAt(32), .001f);
        Assert.AreEqual(132f, grid.TempoAt(84), .001f);
        Assert.AreEqual(40000f, grid.TimeAt(64), .01f);
        var roundTrip = ChartLoader.Parse(SaberChartUtility.ToJson(clone));
        Assert.AreEqual(source.notes.Count, roundTrip.notes.Count);
        Assert.AreEqual(26f, roundTrip.offsetMs);
        Assert.AreEqual(source.displayLevel, roundTrip.displayLevel);
        for (int i = 0; i < source.notes.Count; i++)
        {
            Assert.AreEqual(source.notes[i].time, roundTrip.notes[i].time, .001f);
            Assert.AreEqual(source.notes[i].lengthMs, roundTrip.notes[i].lengthMs, .001f);
            Assert.AreEqual(grid.TimeAt(source.notes[i].beat), roundTrip.notes[i].time, .03f);
            Assert.Less((roundTrip.notes[i].time + roundTrip.offsetMs + roundTrip.notes[i].lengthMs) / 1000, Duration);
        }
    }

    [Test]
    public void SongSelectionAndPreviewUseTheNewPlayableCharts()
    {
        CollectionAssert.Contains(SongSelectController.EnumerateSongIds(), SongId);
        Assert.AreEqual(SongId, SongSelectController.DisplaySongTitle(SongId));
        Assert.AreEqual(File.ReadAllText(Path.Combine(Folder, "chart_normal.json")),
            File.ReadAllText(Path.Combine(Folder, "chart.json")));
        var timeline = StagePerformanceTimeline.Load(SongId);
        Assert.IsTrue(timeline.hideBarLines, "固定BPMの本編ガイドを変速後に誤表示しない");
        var window = SongSelectChartPreview.ResolveWindow(SongId, timeline, Duration);
        foreach (string difficulty in SongSelectController.StandardDifficulties)
        {
            var chart = ChartLoader.LoadFromStreamingAssets(SongId, difficulty);
            Assert.Greater(ChartDifficultyRater.Rate(chart), 0);
            Assert.IsTrue(chart.notes.Any(n => SongPreviewWindow.Intersects(chart, n, window, 0)), difficulty);
        }
    }

    [UnityTest]
    public IEnumerator GameAudioDecoderLoadsTheProvidedMp3()
    {
        using (var request = UnityWebRequestMultimedia.GetAudioClip(
            new Uri(Path.Combine(Folder, "audio.mp3")).AbsoluteUri, AudioType.MPEG))
        {
            yield return request.SendWebRequest();
            Assert.AreEqual(UnityWebRequest.Result.Success, request.result, request.error);
            var clip = DownloadHandlerAudioClip.GetContent(request);
            Assert.IsNotNull(clip);
            try
            {
                Assert.AreEqual(2, clip.channels);
                Assert.AreEqual(44100, clip.frequency);
                // このMP3はUnityで約575msの無音末尾が付く。曲中の波形時計はffmpegと一致する。
                double decodedDuration = (double)clip.samples / clip.frequency;
                Assert.That(decodedDuration, Is.InRange(Duration - .10, Duration + .75));
                int sourceEnd = (int)Math.Ceiling(Duration * clip.frequency);
                if (clip.samples > sourceEnd)
                {
                    var padding = new float[(clip.samples - sourceEnd) * clip.channels];
                    Assert.IsTrue(clip.GetData(padding, sourceEnd));
                    Assert.Less(padding.Max(v => Mathf.Abs(v)), .0001f, "追加部分は無音であること");
                }
                var samples = new float[44100 * clip.channels];
                Assert.IsTrue(clip.GetData(samples, 44100 * 60));
                Assert.Greater(samples.Max(v => Mathf.Abs(v)), .05f);
            }
            finally { UnityEngine.Object.DestroyImmediate(clip); }
        }
    }
}
