using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;

public class SongStagePerformanceTests
{
    [Serializable] private sealed class SourceInfo { public string audioSha256; public string timeOrigin; }

    [TestCase("2_23_AM",1,110.741769)] [TestCase("ElDorado",3,144.065306)] [TestCase("Epilogue",3,166.408707)]
    [TestCase("Morning",2,165.329)] [TestCase("揺籠",4,184.24163)]
    [TestCase("製作中",2,174.864)]
    [TestCase("Phobia PARTⅠ",2,110.968163)]
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
        CollectionAssert.AreEquivalent(new[]{"2_23_AM","ElDorado","Epilogue","Morning","揺籠","Andalusia","製作中","Phobia PARTⅠ","ManicSonic","escapade","logic","BenkyouRemaster","Aobasou","RushE"},
            songs.Select(Path.GetFileName).ToArray());
        foreach(var folder in songs)
        {
            var timeline = StagePerformanceTimeline.Load(Path.GetFileName(folder));
            Assert.IsNotNull(timeline.sections,folder);
            Assert.IsNotEmpty(timeline.sections,folder);
            Assert.GreaterOrEqual(timeline.previewStartSeconds,0,folder);
            // 難易度ごとの演出は、その難易度だけの音源があるときだけ(2026-10-24、ElDorado の G は長い版の音源とその区間)。
            var stages=Directory.GetFiles(folder,"stage*.json").Select(Path.GetFileName).ToArray();
            CollectionAssert.Contains(stages,"stage.json",folder);
            foreach(var stage in stages.Where(s=>s!="stage.json"))
            {
                string difficulty=stage.Substring(6,stage.Length-11);
                Assert.AreEqual("_"+difficulty,SongLoudness.VariantSuffix(folder,difficulty),folder+"/"+stage+" はその難易度の音源に合わせた演出だけ");
                Assert.IsTrue(SongLoudness.SourceMatches(folder,StagePerformanceTimeline.Load(Path.GetFileName(folder),difficulty).audioSha256,difficulty),
                    folder+"/"+stage+" の時刻はその音源のもの");
            }
        }
    }

    [TestCase("2_23_AM",95)] [TestCase("ElDorado",85.803)] [TestCase("Epilogue",150)]
    [TestCase("Morning",76)] [TestCase("揺籠",81)] [TestCase("揺籠",136)]

    [TestCase("製作中",125)] [TestCase("製作中",173)]
    public void BreaksAndAfterglowReturnToAmbient(string song,double seconds)
    {
        Assert.AreEqual(0,StagePerformanceTimeline.Load(song).Evaluate(seconds));
    }
}
