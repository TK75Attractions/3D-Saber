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
        double limit=difficulty=="easy"?.05:difficulty=="normal"?.12:.22;
        if(difficulty!="hard") Assert.GreaterOrEqual(notes.Count(n=>n.IsDirection),1);
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
        {
            int arrows=notes.Skip(i).Take(16).Count(n=>n.IsDirection);
            // 採用した作り直しの指定窓だけ、矢印2個を固定する。
            bool reviewedWindow=
                (song=="ManicSonic" && difficulty=="normal" && i==126) ||
                (song=="BenkyouRemaster" && difficulty=="normal" && i>=130 && i<=134) ||
                (song=="Aobasou" && difficulty=="easy" && i>=78 && i<=84) ||
                (song=="Aobasou" && difficulty=="normal" && i==144) ||
                (song=="logic" && difficulty=="normal" &&
                    ((i>=41 && i<=48) || i==73 || (i>=143 && i<=149)));
            if(reviewedWindow)
                Assert.AreEqual(2,arrows,"作り直しで確認した16ノーツの窓");
            else
                Assert.LessOrEqual(arrows,Math.Max(1,(int)(16*limit)),"局所的なフリック過多");
        }
    }

    static CutPlan LoadPlan(string song,string difficulty)
    {
        var plan=JsonUtility.FromJson<CutPlan>(File.ReadAllText(
            difficulty=="hard" ? Path.Combine("Tools","ChartDesign","FiveSongs20261014",song,difficulty+"-cut-plan.json")
            : Path.Combine("Tools","ChartDesign","Beginner20261021","plans",song,difficulty+".json")));
        if(difficulty!="hard") return plan;

        if(song=="escapade")
        {
            Assert.AreEqual(295,plan.notes.Length,"53Aの補正前計画");
            plan.notes[151].timesMs=new[]{62978.0};
            plan.notes[152].timesMs=new[]{63283.0};
            plan.notes[225].timesMs=new[]{83374.0,84074.0};
            plan.notes[226].timesMs=new[]{83374.0,84074.0};
            plan.notes[258].timesMs=new[]{93492.0,93842.0,94192.0};
            plan.notes[258].directions=new[]{"down","up","down"};
            var removed=new[]{224,256,259,276};
            plan.notes=plan.notes.Where(r=>!removed.Contains(r.index)).ToArray();
            Assert.AreEqual(291,plan.notes.Length);
        }
        if(song=="logic")
        {
            Assert.AreEqual(425,plan.notes.Length,"68Aの補正前計画");
            plan.notes[100].timesMs=new[]{42537.0};
            plan.notes[195].timesMs=new[]{81092.0};
            plan.notes=plan.notes.Where(r=>r.index!=226).ToArray();
            Assert.AreEqual(424,plan.notes.Length);
        }
        for(int i=0;i<plan.notes.Length;i++) plan.notes[i].index=i;
        return plan;
    }

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
        // Hardの高低差は通常の運動として許可する。交差の最低数は設けない。
        int ordinary=chart.notes.Where((n,i)=>
            (difficulty=="hard" || Math.Abs(n.y)<=.6f) && (HandFor(n,plan.notes[i])==SaberHand.Left?n.x<-.5f:n.x>.5f)).Count();
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
            // ロングは1回切るごとに判定1つ・コンボ +1(2026-10-20)。全部 Perfect ならノーツ分の満点 90000。
            Assert.AreEqual(cuts.Length,score.PerfectCount);
            Assert.AreEqual(0,score.MissCount);
            Assert.AreEqual(cuts.Length,score.MaxCombo);
            Assert.AreEqual(ScoreManager.NoteScore,score.Score);
        }
        finally{UnityEngine.Object.DestroyImmediate(root);UnityEngine.Random.state=randomState;}
    }
}
