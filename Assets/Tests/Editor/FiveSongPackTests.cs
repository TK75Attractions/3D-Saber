using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Saber.ChartEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

public class FiveSongPackTests
{
    public static readonly string[] Ids = { "ManicSonic", "escapade", "logic", "BenkyouRemaster", "Aobasou" };
    static readonly string[] Titles = { "Manic Sonic", "escapade", "logic!!!", "勉強（Remaster）", "青葉騒 feat.初音ミク" };
    static readonly string[] Composers = { "浜Bay", "ぴくちゃ", "爆裂P", "爆裂P", "キョーカスイゲツ。" };
    static readonly double[] Durations = { 149.451938, 109.944036, 206.759184, 138.762449, 138.857052 };
    // logic の 238→240拍(96BPM)は、到達の一打のあとの半拍を吸収する区間(10/21、旧表は 240拍から曲の拍と半拍ずれていた)。
    static readonly float[][] Beats = {
        new float[] {0,171,199,379,395,403}, new float[] {0},
        new float[] {0,141,227.5f,229,235,236,238,240,400},
        new float[] {0,80,114,280,304},new float[] {0} };
    static readonly float[][] Tempos = {
        new float[] {180,160,180,60,180,200},new float[] {190},
        new float[] {160,120,100,80,100,120,96,120,160},
        new float[] {160,100,160,70,360},new float[] {140} };
    static string Folder(string id) => Path.Combine(Application.streamingAssetsPath,"Songs",id);
    [Serializable] sealed class Source { public string audioSha256; }

    [TestCaseSource(nameof(Ids))]
    public void SongMetadataAndAllDifficultiesAreAvailable(string id)
    {
        int index = Array.IndexOf(Ids,id);
        CollectionAssert.Contains(SongSelectController.EnumerateSongIds(),id);
        Assert.AreEqual(Titles[index], SongSelectController.DisplaySongTitle(id));
        Assert.AreEqual(Composers[index],SongDisplayInfo.Get(id).Composer);
        string folder=Folder(id);
        Assert.AreEqual(File.ReadAllText(Path.Combine(folder,"chart_normal.json")),File.ReadAllText(Path.Combine(folder,"chart.json")));
        string audio=Directory.GetFiles(folder,"audio.*").Single(p=>!p.EndsWith(".meta"));
        var source=JsonUtility.FromJson<Source>(File.ReadAllText(Path.Combine(folder,"stage.json")));
        using(var sha=SHA256.Create())
            Assert.AreEqual(source.audioSha256,BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(audio))).Replace("-","").ToLowerInvariant());
        var timeline=StagePerformanceTimeline.Load(id);
        Assert.IsFalse(timeline.hideBarLines);
        Assert.IsNotEmpty(timeline.sections);
        int previous=0;
        foreach(string difficulty in SongSelectController.StandardDifficulties)
        {
            var chart=ChartLoader.LoadFromStreamingAssets(id,difficulty);
            Assert.Greater(chart.notes.Count,previous);previous=chart.notes.Count;
            Assert.That(chart.displayLevel,Is.InRange(1,10));
            // 54A: 冒頭の単発を維持し、106.714秒に2回切りを1個置く。
            if(id=="Aobasou" && difficulty.Equals("normal",StringComparison.OrdinalIgnoreCase))
            {
                Assert.AreEqual(1,chart.notes.Count(n=>n.IsLong));
                var hold=chart.notes.Single(n=>n.IsLong);
                Assert.AreEqual(106714.2857,hold.time,.03);
                Assert.AreEqual(2,hold.count);
                Assert.AreEqual(1285.7143,hold.lengthMs,.03);
                var opening=chart.notes.Where(n=>
                    Math.Abs((double)n.time+chart.offsetMs)<=.03).ToArray();
                Assert.AreEqual(1,opening.Length);
                Assert.AreEqual("red",opening[0].color);
                Assert.AreEqual("tap",opening[0].type);
                Assert.AreEqual("none",opening[0].direction);
                Assert.AreEqual(1,opening[0].count);
                Assert.AreEqual(0,opening[0].lengthMs);
                Assert.AreEqual(.9,opening[0].x,.00001);
                Assert.AreEqual(-.6,opening[0].y,.00001);
            }
            else
                Assert.IsTrue(chart.notes.Any(n=>n.IsLong));
            // 下位譜面は表拍・速度条件を優先し、金同時切りを義務付けない。
            if(difficulty.Equals("hard",StringComparison.OrdinalIgnoreCase))
                Assert.IsTrue(chart.notes.GroupBy(n=>n.time).Any(g=>g.Count()==2 && g.All(n=>n.color=="gold")));
            Assert.IsTrue(chart.notes.Any(n=>SongPreviewWindow.Intersects(chart,n,SongSelectChartPreview.ResolveWindow(id,timeline,Durations[index]),0)));
            Assert.IsTrue(chart.notes.Any(n=>n.IsDirection));
        }
    }

    [TestCaseSource(nameof(Ids))]
    public void SuppliedTempoMeterAndNoteTimesSurviveEditing(string id)
    {
        int index=Array.IndexOf(Ids,id);
        foreach(string difficulty in SongSelectController.StandardDifficulties)
        {
            string json=File.ReadAllText(Path.Combine(Folder(id),"chart_"+difficulty+".json"));
            var chart=ChartLoader.Parse(json);
            var document=SaberChartUtility.Clone(SaberChartUtility.FromJson(json));
            var edited=ChartLoader.Parse(SaberChartUtility.ToJson(document));
            var grid=new SaberChartGrid(document.bpm,document.beatZeroMs,document.tempoMap);
            var clock=new ChartTempoMap(chart.bpm,chart.beatZeroMs,chart.editorTempoMap);
            var selection=new SongSelectBeatTimeline(new[]{chart});
            Assert.AreEqual(index==2?8:4,chart.timeSignatures[0].numerator);
            Assert.AreEqual(index==2?8:4,chart.timeSignatures[0].denominator);
            for(int j=0;j<Beats[index].Length;j++)
            {
                float b=Beats[index][j];
                Assert.AreEqual(Tempos[index][j],grid.TempoAt(b+.001f),.02f,id+" / beat "+b);
                Assert.AreEqual(grid.TimeAt(b),clock.TimeAt(b),.025);
                Assert.AreEqual(clock.TimeAt(b)/1000.0,selection.TimeAt(b),.0001);
                if(j>0)Assert.AreEqual(Tempos[index][j-1],grid.TempoAt(b-.001f),.02f);
            }
            Assert.AreEqual(chart.notes.Count,edited.notes.Count);
            for(int n=0;n<chart.notes.Count;n++)
            {
                var note=chart.notes[n];
                Assert.AreEqual(note.time,edited.notes[n].time,.001);
                Assert.AreEqual(note.lengthMs,edited.notes[n].lengthMs,.001);
                Assert.AreEqual(note.time,clock.TimeAt(note.beat),.04);
                Assert.AreEqual(note.time,grid.TimeAt(note.beat),.04);
                Assert.AreEqual(note.beat,clock.BeatAt(note.time),.001);
                Assert.That(note.time,Is.GreaterThanOrEqualTo(0));
                Assert.Less((note.time+note.lengthMs)/1000.0,Durations[index]);
                Assert.That(Math.Abs(note.x),Is.LessThanOrEqualTo(2.5));
                Assert.That(Math.Abs(note.y),Is.LessThanOrEqualTo(1.5));
            }
        }
    }

    [Test]
    public void TempoClockHandlesFallbacksAndInvalidAnchors()
    {
        var fallback=new ChartTempoMap(120,25,null);
        Assert.AreEqual(2025,fallback.TimeAt(4));Assert.AreEqual(-1,fallback.BeatAt(-475));
        var map=new ChartTempoMap(120,0,new[]{null,new ChartTempoPoint{beat=0,timeMs=25},
            new ChartTempoPoint{beat=0,timeMs=75},new ChartTempoPoint{beat=4,timeMs=2025},
            new ChartTempoPoint{beat=8,timeMs=1025},new ChartTempoPoint{beat=8,timeMs=6025},
            new ChartTempoPoint{beat=float.NaN,timeMs=500}});
        Assert.AreEqual(3,map.Points.Count);Assert.AreEqual(10025,map.TimeAt(12));
        Assert.AreEqual(12,map.BeatAt(10025));
    }

    [Test]
    public void BarLinesUseTempoChangeInsideARestAndApplyOffsetOnlyOnce()
    {
        var obj=new GameObject("tempo-guide-test");
        try
        {
            var spawner=obj.AddComponent<BarLineSpawner>();
            var chart=new ChartData{bpm=120,beatZeroMs=25,offsetMs=100,
                editorTempoMap=new List<ChartTempoPoint>{new ChartTempoPoint{beat=0,timeMs=25},
                    new ChartTempoPoint{beat=3,timeMs=1525},new ChartTempoPoint{beat=7,timeMs=5525}},
                notes=new List<NoteData>{new NoteData{beat=8,time=6525}}};
            spawner.SetChart(chart,.2);
            var field=typeof(BarLineSpawner).GetField("barTimes",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            var times=(List<double>)field.GetValue(spawner);
            Assert.AreEqual(.325,times[0],.000001);Assert.AreEqual(2.825,times[1],.000001);
            Assert.AreEqual(6.825,times[2],.000001);
        }
        finally{UnityEngine.Object.DestroyImmediate(obj);}
    }

    [UnityTest]
    public IEnumerator GameDecoderLoadsAllFiveOriginalAudioFiles()
    {
        string output=Path.GetFullPath("Library/FiveSongAudioChecks");Directory.CreateDirectory(output);
        for(int i=0;i<Ids.Length;i++)
        {
            string path=Directory.GetFiles(Folder(Ids[i]),"audio.*").Single(p=>!p.EndsWith(".meta"));
            using(var request=UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri,path.EndsWith(".wav")?AudioType.WAV:AudioType.MPEG))
            {
                yield return request.SendWebRequest();
                Assert.AreEqual(UnityWebRequest.Result.Success,request.result,request.error);
                var clip=DownloadHandlerAudioClip.GetContent(request);Assert.IsNotNull(clip);
                try
                {
                    Assert.AreEqual(2,clip.channels);
                    Assert.That((double)clip.samples/clip.frequency,Is.InRange(Durations[i]-.1,Durations[i]+2));
                    // VBR MP3の追加長が実音ではなく無音の末尾であることも確認する。
                    int sourceEnd=(int)Math.Ceiling(Durations[i]*clip.frequency);
                    if(clip.samples>sourceEnd)
                    {
                        var padding=new float[(clip.samples-sourceEnd)*clip.channels];
                        Assert.IsTrue(clip.GetData(padding,sourceEnd));
                        Assert.Less(padding.Max(v=>Mathf.Abs(v)),.0001f,Ids[i]+" / 追加末尾は無音");
                    }
                    // 実際のUnityデコード波形を保存し、音源解析側と相互相関で時計を照合する。
                    using(var stream=new BinaryWriter(File.Create(Path.Combine(output,Ids[i]+".bin"))))
                    {
                        stream.Write(clip.frequency);stream.Write(clip.channels);
                        foreach(int second in new[]{10,60,100})
                        {
                            var samples=new float[clip.frequency*clip.channels];
                            Assert.IsTrue(clip.GetData(samples,clip.frequency*second));
                            Assert.Greater(samples.Max(v=>Mathf.Abs(v)),.005f);
                            stream.Write(second);stream.Write(samples.Length);
                            foreach(float sample in samples)stream.Write(sample);
                        }
                    }
                }
                finally{UnityEngine.Object.DestroyImmediate(clip);}
            }
        }
    }
}
