using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;

public class SongStagePerformanceTests
{
    [Serializable] private sealed class SourceInfo { public string audioSha256; public string timeOrigin; }

    [TestCase("2_23_AM",2,196.075)] [TestCase("ElDorado",4,223.248)] [TestCase("Epilogue",3,166.408707)]
    [TestCase("Morning",2,165.329)] [TestCase("揺籠",4,184.24163)]
    public void AuthoredSectionsMatchAudioAndRemainSmooth(string song,int count,double duration)
    {
        string folder=Path.Combine(Application.streamingAssetsPath,"Songs",song);
        string audio=Directory.GetFiles(folder,"audio.*").Single(p=>!p.EndsWith(".meta"));
        var source=JsonUtility.FromJson<SourceInfo>(File.ReadAllText(Path.Combine(folder,"stage.json")));
        using(var sha=SHA256.Create())
            Assert.AreEqual(source.audioSha256,BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(audio))).Replace("-","").ToLowerInvariant());
        Assert.AreEqual("audio-file-start",source.timeOrigin);
        // StreamingAssetsはAudioClipとしてインポートされない。上で照合した音源のデコード実測長を使う。
        // ゲーム内で読み込まれた実AudioClipの長さとの比較はPlayMode側でも行う。
        var timeline=StagePerformanceTimeline.Load(song);
        Assert.AreEqual(count,timeline.sections.Length);
        double previous=0;
        foreach(var s in timeline.sections)
        {
            Assert.That(s.startSeconds,Is.GreaterThanOrEqualTo(previous)); previous=s.startSeconds;
            Assert.That(s.endSeconds,Is.GreaterThan(s.startSeconds).And.LessThan(duration));
            Assert.That(s.intensity,Is.InRange(.1f,1f));
            Assert.That(s.fadeInSeconds,Is.InRange(.5f,4f)); Assert.That(s.fadeOutSeconds,Is.InRange(1f,4f));
            Assert.GreaterOrEqual(timeline.Evaluate((s.startSeconds+s.endSeconds)/2),s.intensity-.0001f);
        }
        // すべての入口・重なり・出口を含めて急な点滅がないことを検証。
        for(double t=0;t<duration;t+=.01)
        {
            float a=timeline.Evaluate(t),b=timeline.Evaluate(t+.001);
            Assert.That(a,Is.InRange(0f,1f)); Assert.Less(Mathf.Abs(a-b),.003f);
        }
        Assert.AreEqual(0,timeline.Evaluate(0)); Assert.AreEqual(0,timeline.Evaluate(duration));
    }

    [Test]
    public void EveryPlayableSongHasOneDifficultyIndependentTimeline()
    {
        var songs=Directory.GetDirectories(Path.Combine(Application.streamingAssetsPath,"Songs"))
            .Where(p=>Directory.GetFiles(p,"chart*.json").Length>0).ToArray();
        Assert.AreEqual(5,songs.Length);
        foreach(var folder in songs)
        {
            Assert.IsNotEmpty(StagePerformanceTimeline.Load(Path.GetFileName(folder)).sections,folder);
            Assert.AreEqual(1,Directory.GetFiles(folder,"stage*.json").Length,folder);
        }
    }

    [TestCase("2_23_AM",95)] [TestCase("ElDorado",153)] [TestCase("Epilogue",150)]
    [TestCase("Morning",76)] [TestCase("揺籠",81)] [TestCase("揺籠",136)]
    public void BreaksAndAfterglowReturnToAmbient(string song,double seconds)
    {
        Assert.AreEqual(0,StagePerformanceTimeline.Load(song).Evaluate(seconds));
    }
}
