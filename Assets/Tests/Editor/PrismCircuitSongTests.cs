using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

// 本物のローダー・選曲列挙・UnityのOGGデコーダーを使った曲データの統合確認。
public class PrismCircuitSongTests
{
    const string SongId = "PrismCircuit";
    const double Duration = 119.756757;
    static string Folder => Path.Combine(Application.streamingAssetsPath, "Songs", SongId);

    [Test]
    public void SongIsDiscoverableWithThreeOrderedDifficulties()
    {
        CollectionAssert.Contains(SongSelectController.EnumerateSongIds(), SongId);
        Assert.IsTrue(SongSelectController.HasPlayableChart(SongId, SongSelectController.StandardDifficulties));
        Assert.AreEqual("PRISM CIRCUIT", ResultSkin.SongIdToDisplayTitle(SongId));
        var charts = new[] { "easy", "normal", "hard" }.Select(d => ChartLoader.LoadFromStreamingAssets(SongId, d)).ToArray();
        Assert.Less(charts[0].notes.Count, charts[1].notes.Count);
        Assert.Less(charts[1].notes.Count, charts[2].notes.Count);
        Assert.Less(charts[0].displayLevel, charts[1].displayLevel);
        Assert.Less(charts[1].displayLevel, charts[2].displayLevel);
        Assert.AreEqual(File.ReadAllText(Path.Combine(Folder,"chart_normal.json")), File.ReadAllText(Path.Combine(Folder,"chart.json")));
    }

    [TestCase("easy")]
    [TestCase("normal")]
    [TestCase("hard")]
    public void AudioGridHandsAndLongWindowsArePlayable(string difficulty)
    {
        var c = ChartLoader.LoadFromStreamingAssets(SongId,difficulty);
        Assert.AreEqual(148, c.bpm, .0001);
        Assert.AreEqual(0, c.offsetMs);
        Assert.AreEqual(0, c.beatZeroMs);
        Assert.Greater(c.notes[0].TimeSeconds, 3);
        Assert.Less(c.notes.Last().TimeSeconds, Duration-3);
        foreach (var n in c.notes)
        {
            Assert.AreEqual(n.beat*60000.0/148, n.time, .02, "音源先頭と同じ拍時計");
            Assert.That(n.x, Is.InRange(-2.5f,2.5f));
            Assert.That(n.y, Is.InRange(-1.5f,1.5f));
            Assert.Less(n.TimeSeconds+n.lengthMs/1000, Duration);
            if (n.color=="red") Assert.Greater(n.x,0);
            if (n.color=="blue") Assert.Less(n.x,0);
            if (difficulty=="easy") Assert.AreEqual(Math.Round(n.beat), n.beat, .0001, "初級は表拍");
            if (n.IsLong)
            {
                Assert.Greater(n.lengthMs,0);
                Assert.GreaterOrEqual(n.count,2);
                Assert.IsFalse(c.notes.Any(other=>other!=n && other.time>n.time && other.time<n.time+n.lengthMs-.02), "連続切り中は他のノーツを要求しない");
            }
        }
        foreach(var hand in new[]{"red","blue"})
        {
            var ns=c.notes.Where(n=>n.color==hand||n.color=="gold").ToArray();
            for(int i=1;i<ns.Length;i++)
            {
                double gap=ns[i].TimeSeconds-ns[i-1].TimeSeconds-ns[i-1].lengthMs/1000;
                Assert.GreaterOrEqual(gap, (difficulty=="easy"?2:1)*60.0/148-.0001, "同じ手の回復時間");
            }
        }
    }

    [Test]
    public void PreviewAndStageOpenAtTheFinalChorus()
    {
        var stage=StagePerformanceTimeline.Load(SongId);
        Assert.AreEqual(224*60.0/148,stage.previewStartSeconds,.001);
        Assert.AreEqual(2,stage.sections.Length);
        Assert.Greater(stage.Evaluate(stage.previewStartSeconds+3), .9);
        Assert.AreEqual(0,stage.Evaluate(170*60.0/148));
        foreach(var s in stage.sections) Assert.Less(s.endSeconds,Duration);
    }

    [UnityTest]
    public IEnumerator GameAudioDecoderLoadsTheWholeSong()
    {
        using(var req=UnityWebRequestMultimedia.GetAudioClip(new Uri(Path.Combine(Folder,"audio.ogg")).AbsoluteUri,AudioType.OGGVORBIS))
        {
            yield return req.SendWebRequest();
            Assert.AreEqual(UnityWebRequest.Result.Success,req.result,req.error);
            var clip=DownloadHandlerAudioClip.GetContent(req);
            Assert.IsNotNull(clip);
            try
            {
                Assert.AreEqual(2,clip.channels);
                Assert.AreEqual(44100,clip.frequency);
                Assert.AreEqual(Duration,(double)clip.samples/clip.frequency,.002);
                var data=new float[44100*2];
                Assert.IsTrue(clip.GetData(data,44100*40));
                Assert.Greater(data.Select(Math.Abs).Max(),.05f);
                Assert.Less(data.Select(Math.Abs).Max(),.99f);
            }
            finally { UnityEngine.Object.DestroyImmediate(clip); }
        }
    }
}
