using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 保存した全15譜面を実際の生成・判定・採点へ通す。プレイヤーの成績には保存しない。
public class FiveSongPlayabilityTests
{
    [Serializable] sealed class CutRow { public int index; public double[] timesMs; public string[] directions; }
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
            var plan=JsonUtility.FromJson<CutPlan>(File.ReadAllText(Path.Combine("Tools","ChartDesign","FiveSongs20261014",song,difficulty+"-cut-plan.json")));
            Assert.AreEqual(chart.notes.Count,plan.notes.Length);
            var cuts=plan.notes.SelectMany(row=>row.timesMs.Select((time,k)=>(row.index,time,direction:row.directions[k]))).OrderBy(c=>c.time).ToArray();
            spawner.SetChart(chart);
            foreach(var cut in cuts)
            {
                double seconds=cut.time/1000.0;
                spawner.Tick(seconds);
                Assert.IsTrue(spawned.TryGetValue(cut.index,out var note),"生成漏れ / "+cut.index);
                Assert.IsNotNull(note,"カット前に退場 / "+cut.index);
                var hand=chart.notes[cut.index].x>0?SaberHand.Right:SaberHand.Left;
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
