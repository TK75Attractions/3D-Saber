using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 保存した全15譜面を実際の生成・判定・採点へ通す。プレイヤーの成績には保存しない。
public class FiveSongPlayabilityTests
{
    [Serializable] sealed class CutRow { public int index; public string hand; public double[] timesMs; public string[] directions; }
    [Serializable] sealed class CutPlan { public CutRow[] notes; }
    static IEnumerable<TestCaseData> Charts
    {
        get
        {
            foreach(string song in FiveSongPackTests.Ids)
                foreach(string difficulty in new[]{"easy","normal","hard"})
                    yield return new TestCaseData(song,difficulty);
        }
    }

    [TestCaseSource(nameof(Charts))]
    public void FlickAccentsStaySparseAndNeverReachTenConsecutiveNotes(string song,string difficulty)
    {
        var chart=ChartLoader.LoadFromStreamingAssets(song,difficulty);
        var plan=LoadPlan(song,difficulty);
        var notes=chart.notes.OrderBy(n=>n.time).ToArray();
        double limit=difficulty=="easy"?0:difficulty=="normal"?.12:.22;
        Assert.LessOrEqual(notes.Count(n=>n.IsDirection)/(double)notes.Length,limit);
        Assert.LessOrEqual(LongestFlickRun(notes),4,"全体の連続数");
        foreach(var required in new[]{SaberHand.Left,SaberHand.Right})
        {
            var hand=chart.notes.Where((n,i)=>HandFor(n,plan.notes[i])==required).OrderBy(n=>n.time).ToArray();
            Assert.LessOrEqual(LongestFlickRun(hand),3,"逆手のタップだけで片手の連続数をリセットしない");
            for(int i=0;i<hand.Length-1;i++)
                if(hand[i].IsLong)
                    Assert.Greater(hand[i+1].time,hand[i].time+hand[i].lengthMs,"ロング占有中の同手ノーツ");
        }
        int run=0;
        foreach(var group in notes.GroupBy(n=>n.time))
        {
            int arrows=group.Count(n=>n.IsDirection);
            run=arrows>0?run+arrows:0;
            Assert.Less(run,10,"同時打ちの並び順によらない連続数");
        }
        for(int i=0;i+16<=notes.Length;i++)
            Assert.LessOrEqual(notes.Skip(i).Take(16).Count(n=>n.IsDirection),(int)(16*limit),"局所的なフリック過多");
    }

    static CutPlan LoadPlan(string song,string difficulty) => JsonUtility.FromJson<CutPlan>(File.ReadAllText(
        Path.Combine("Tools","ChartDesign","FiveSongs20261014",song,difficulty+"-cut-plan.json")));

    static SaberHand HandFor(NoteData note,CutRow cut)
    {
        // 金は色指定がないので設計時の担当を使う。赤青は位置に関係なく色で決める。
        var hand=SaberHandHelper.FromColor(note.color);
        return hand==SaberHand.Any?SaberHandHelper.FromColor(cut.hand):hand;
    }

    [TestCaseSource(nameof(Charts))]
    public void ConventionalLayoutsRemainTheMainStructure(string song,string difficulty)
    {
        var chart=ChartLoader.LoadFromStreamingAssets(song,difficulty);
        var plan=LoadPlan(song,difficulty);
        // 横配置が主軸であることだけを保証する。特殊配置の最低数は設けない。
        int ordinary=chart.notes.Where((n,i)=>
            Math.Abs(n.y)<=.6f && (HandFor(n,plan.notes[i])==SaberHand.Left?n.x<-.5f:n.x>.5f)).Count();
        Assert.Greater(ordinary,chart.notes.Count/2,"通常配置より技のほうが多くなっている");
    }

    static int LongestFlickRun(IEnumerable<NoteData> notes)
    {
        int run=0,max=0;
        foreach(var note in notes)
        {
            run=note.IsDirection?run+1:0;max=Math.Max(max,run);
        }
        return max;
    }

    [TestCaseSource(nameof(Charts))]
    public void EveryPlannedCutCanCompleteWithCorrectHandAndDirection(string song,string difficulty)
    {
        var randomState=UnityEngine.Random.state;
        var root=new GameObject("FiveSongPlayability");
        try
        {
            var spawner=root.AddComponent<NoteSpawner>();
            var score=root.AddComponent<ScoreManager>();score.Bind(spawner);
            spawner.noteRoot=root.transform;spawner.buildTimingCues=false;spawner.simultaneousGuideEnabled=false;
            var prefab=new GameObject("Note",typeof(CuttableNote));prefab.transform.SetParent(root.transform);
            prefab.GetComponent<CuttableNote>().shatterDebrisCount=0;
            spawner.notePrefab=spawner.notePrefabRed=spawner.notePrefabBlue=prefab;
            var spawned=new Dictionary<int,CuttableNote>();int index=0;
            spawner.OnNoteSpawned+=n=>
            {
                spawned[index++]=n;
                if(n.countLabel!=null)n.countLabel.transform.SetParent(root.transform,true);
            };
            var chart=ChartLoader.LoadFromStreamingAssets(song,difficulty);
            var plan=LoadPlan(song,difficulty);
            Assert.AreEqual(chart.notes.Count,plan.notes.Length);
            var cuts=plan.notes.SelectMany(row=>row.timesMs.Select((time,k)=>(row.index,time,direction:row.directions[k]))).OrderBy(c=>c.time).ToArray();
            spawner.SetChart(chart);
            foreach(var cut in cuts)
            {
                double seconds=cut.time/1000.0;
                spawner.Tick(seconds);
                Assert.IsTrue(spawned.TryGetValue(cut.index,out var note),"生成漏れ / "+cut.index);
                Assert.IsNotNull(note,"カット前に退場 / "+cut.index);
                var hand=HandFor(chart.notes[cut.index],plan.notes[cut.index]);
                Assert.AreEqual(SaberHandHelper.FromColor(chart.notes[cut.index].color),note.RequiredHand);
                Assert.AreNotEqual(SaberHand.Any,hand,"カット計画には担当手を記録する");
                var direction=CutDirectionHelper.ToVector(CutDirectionHelper.Parse(cut.direction)).normalized;
                Assert.IsTrue(note.TryCutWithCameraTiming(note.transform.position,new Vector3(direction.x,direction.y,0)*12,
                    hand,seconds,.866f),song+" / "+difficulty+" / index="+cut.index+" at "+seconds);
            }
            spawner.Tick(cuts.Last().time/1000.0+5);
            Assert.AreEqual(chart.notes.Count,score.PerfectCount);
            Assert.AreEqual(0,score.MissCount);
            Assert.AreEqual(chart.notes.Count,score.MaxCombo);
        }
        finally{UnityEngine.Object.DestroyImmediate(root);UnityEngine.Random.state=randomState;}
    }
}
