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

    // データを差し替えても、金・両手の役割と回復時間を失わないための回帰確認。
    [TestCase("easy", 13, 18, 22, 30)]
    [TestCase("normal", 34, 42, 35, 46)]
    [TestCase("hard", 46, 56, 40, 52)]
    public void RechartHasMusicalGoldAndDoubleAccents(string difficulty, int minPairs, int maxPairs, int minGold, int maxGold)
    {
        var c = ChartLoader.LoadFromStreamingAssets(SongId, difficulty);
        var pairs = c.notes.GroupBy(n => n.beat).Where(g => g.Count() == 2).ToArray();
        Assert.That(pairs.Length, Is.InRange(minPairs, maxPairs));
        Assert.That(c.notes.Count(n => n.color == "gold"), Is.InRange(minGold, maxGold));
        Assert.LessOrEqual(Math.Abs(c.notes.Count(n => n.color == "red") - c.notes.Count(n => n.color == "blue")), 5);
        Assert.GreaterOrEqual(pairs.Count(g => (g.Key >= 96 && g.Key < 160) || (g.Key >= 224 && g.Key < 272)), pairs.Length * .65);
        int quiet = c.notes.Count(n => n.beat >= 160 && n.beat < 192);
        int chorus = c.notes.Count(n => n.beat >= 96 && n.beat < 160);
        Assert.Less(quiet / 32.0, chorus / 64.0 * .6, "間奏はサビより十分に休める");
        foreach (var g in pairs)
        {
            CollectionAssert.AreEquivalent(new[] { "red", "blue" }, g.Select(n => n.color));
            var a = g.First(); var b = g.Last();
            Assert.GreaterOrEqual(Math.Abs(a.x-b.x), 1.8f);
            Assert.AreEqual(a.y, b.y, .001f, "同時打ちは高さをそろえる");
            Assert.IsFalse(g.Any(n => n.color == "blue" && n.direction.EndsWith("right")));
            Assert.IsFalse(g.Any(n => n.color == "red" && n.direction.EndsWith("left")));
        }
    }

    [TestCase("easy")]
    [TestCase("normal")]
    [TestCase("hard")]
    public void GoldExitsAndTravelRemainNatural(string difficulty)
    {
        var c = ChartLoader.LoadFromStreamingAssets(SongId, difficulty);
        foreach (var gold in c.notes.Where(n => n.color == "gold"))
        {
            Assert.AreEqual(0, gold.x);
            Assert.IsFalse(gold.IsDirection);
            if (gold.IsLong)
                Assert.GreaterOrEqual(2*gold.lengthMs/(gold.count-1), (difficulty == "easy" ? 2 : 1)*60000f/148-.02f);
        }
        foreach (var hand in new[] { "red", "blue" })
        {
            var ns = c.notes.Where(n => n.color == hand || n.color == "gold").ToArray();
            for (int i=1; i<ns.Length; i++)
            {
                var a=ns[i-1]; var b=ns[i];
                double gap=b.TimeSeconds-a.TimeSeconds-a.lengthMs/1000;
                double distance=Math.Sqrt((b.x-a.x)*(b.x-a.x)+(b.y-a.y)*(b.y-a.y));
                Assert.LessOrEqual(distance/gap, 4.5, "短い間隔の大移動を避ける");
                if (a.color == "gold") Assert.IsFalse(b.IsDirection, "自由な金の直後に方向を強制しない");
                if (b.IsDirection)
                {
                    var bv=CutDirectionHelper.ToVector(CutDirectionHelper.Parse(b.direction));
                    Assert.Greater(Vector2.Dot(new Vector2(b.x-a.x,b.y-a.y),bv), .045f, "切る位置へ向かう動きと矢印を合わせる");
                }
                if (a.IsDirection && b.IsDirection)
                {
                    var av=CutDirectionHelper.ToVector(CutDirectionHelper.Parse(a.direction));
                    var bv=CutDirectionHelper.ToVector(CutDirectionHelper.Parse(b.direction));
                    Assert.LessOrEqual(Vector2.Dot(av,bv), .001f, "同方向への振り直しを強制しない");
                }
            }
        }
    }

    [TestCase("easy", 20)]
    [TestCase("normal", 35)]
    [TestCase("hard", 45)]
    public void HeightArrowsAndPhraseVarietyMatchAuthoringRules(string difficulty, int minLayouts)
    {
        var c=ChartLoader.LoadFromStreamingAssets(SongId,difficulty);
        foreach(var n in c.notes)
        {
            if(n.direction.StartsWith("up")) Assert.GreaterOrEqual(n.y,-.05f,"低い上向きを避ける");
            if(n.direction.StartsWith("down")) Assert.LessOrEqual(n.y,.05f,"高い下向きを避ける");
        }
        var signatures = Enumerable.Range(4,64)
            .Select(bar=>c.notes.Where(n=>n.beat>=bar*4 && n.beat<bar*4+4).OrderBy(n=>n.beat).ThenBy(n=>n.color).ToArray())
            .Where(ns=>ns.Length>=2)
            .Select(ns=>string.Join(";",ns.Select(n=>string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4}", n.beat%4,n.color,Math.Round(n.x*5),Math.Round(n.y*5),n.direction))))
            .GroupBy(s=>s).ToArray();
        Assert.GreaterOrEqual(signatures.Length,minLayouts,"微小ずらしだけに頼らず配置に変化を作る");
        Assert.LessOrEqual(signatures.Max(g=>g.Count()),4,"同じ小節配置を反復しすぎない");
    }

    [TestCase("easy")]
    [TestCase("normal")]
    [TestCase("hard")]
    public void FlickEntryAndRapidReturnRespectTheActualDirectionVectors(string difficulty)
    {
        var c = ChartLoader.LoadFromStreamingAssets(SongId, difficulty);
        foreach (var hand in new[] { "red", "blue" })
        {
            var ns = c.notes.Where(n => n.color == hand || n.color == "gold").ToArray();
            for (int i = 1; i < ns.Length; i++)
            {
                var a = ns[i-1]; var b = ns[i];
                var delta = new Vector2(b.x-a.x, b.y-a.y);
                double gap = b.TimeSeconds-a.TimeSeconds-a.lengthMs/1000;
                if (b.IsDirection)
                {
                    var bv = CutDirectionHelper.ToVector(CutDirectionHelper.Parse(b.direction));
                    Assert.GreaterOrEqual(delta.magnitude, .07999f);
                    Assert.GreaterOrEqual(Vector2.Dot(delta.normalized, bv), Mathf.Sqrt(.5f)-.0001f,
                        "横から入って縦へ切り直すフリックを避け、進入を45度以内にする");
                    if (a.IsDirection)
                    {
                        var av = CutDirectionHelper.ToVector(CutDirectionHelper.Parse(a.direction));
                        if (gap < 120.0/148-.0001)
                            Assert.LessOrEqual(Vector2.Dot(av,bv), -.4999f, "短い間隔は120度以上の折り返し");
                        double path = .5+(delta-.25f*(av+bv)).magnitude;
                        Assert.LessOrEqual(path/gap, 5.5001, "振り抜きと次の振り始めも移動量に含める");
                    }
                }
            }
        }
    }

    [Test]
    public void FirstSightNormalAllowsRecoveryAroundFlicks()
    {
        var chart=ChartLoader.LoadFromStreamingAssets(SongId,"normal");
        foreach(var hand in new[]{"blue","red"})
        {
            var ns=chart.notes.Where(n=>n.color==hand || n.color=="gold").ToArray();
            for(int i=1;i<ns.Length;i++)
                if(ns[i-1].IsDirection || ns[i].IsDirection)
                    Assert.GreaterOrEqual(ns[i].time-ns[i-1].time-ns[i-1].lengthMs,599.9f,
                        "通常譜面のフリックは構え直しの時間も確保する");
        }
    }

    [TestCase("normal")]
    [TestCase("hard")]
    public void FirstSightRollDoesNotRequireFastHandAlternation(string difficulty)
    {
        foreach(var n in ChartLoader.LoadFromStreamingAssets(SongId,difficulty).notes.Where(n=>n.IsLong))
            Assert.GreaterOrEqual(n.lengthMs/(n.count-1),399.9f,"一手でも入りやすい連続切り");
    }
}
