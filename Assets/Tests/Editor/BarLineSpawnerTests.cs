using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class BarLineSpawnerTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
        // 残ったバーラインインスタンスも片付け
        foreach (var s in Object.FindObjectsByType<BarLineSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (s != null) Object.DestroyImmediate(s.gameObject);
        }
    }

    private GameObject MakePrefab()
    {
        var go = new GameObject("BarLinePrefab");
        created.Add(go);
        return go;
    }

    private (BarLineSpawner sp, GameObject prefab) MakeSpawner(float bpm, params float[] noteTimesMs)
    {
        var sGo = new GameObject("bar");
        created.Add(sGo);
        var sp = sGo.AddComponent<BarLineSpawner>();
        var pf = MakePrefab();
        sp.barLinePrefab = pf;
        sp.approachTime = 2.0f;
        sp.spawnZ = 20f;
        sp.judgeZ = 0f;
        sp.beatsPerBar = 4;
        sp.accentEvery = 0;

        var chart = new ChartData { bpm = bpm };
        foreach (var t in noteTimesMs) chart.notes.Add(new NoteData { time = t });
        sp.SetChart(chart);
        return (sp, pf);
    }

    [Test]
    public void Tick_SpawnsBarsWithinLeadTime()
    {
        // BPM 120 → barInterval=2s。approachTime=2 で songTime=0 のとき
        // [0, 2.0] 内にある 0.0 と 2.0 の2本がスポーンされる。
        var (sp, _) = MakeSpawner(120f, 8000f);
        sp.Tick(0.0);
        Assert.AreEqual(2, sp.NextIndex);
    }

    [Test]
    public void Tick_BarsSpawnedAtBPMInterval()
    {
        // BPM 60 → barInterval=4s。8 秒譜面 → endTime=12s。
        var (sp, _) = MakeSpawner(60f, 8000f);
        sp.Tick(0.0);     // 0+2=2、bar 0 のみ届く
        Assert.AreEqual(1, sp.NextIndex);
        sp.Tick(2.5);     // 2.5+2=4.5、bar 4 が追加
        Assert.AreEqual(2, sp.NextIndex);
        sp.Tick(6.5);     // 6.5+2=8.5、bar 8 が追加
        Assert.AreEqual(3, sp.NextIndex);
    }

    [Test]
    public void Tick_StopsAfterEndTime()
    {
        // 4秒譜面 → endTime=8s、BPM 120 → 0,2,4,6,8 の5本
        var (sp, _) = MakeSpawner(120f, 4000f);
        for (int t = 0; t < 30; t++) sp.Tick(t * 0.5);
        Assert.AreEqual(5, sp.NextIndex);
    }

    [Test]
    public void Tick_DespawnsAfterPassingJudge()
    {
        var (sp, _) = MakeSpawner(120f, 8000f);
        sp.despawnAfterSeconds = 0.1f;
        sp.Tick(0.0);
        Assert.Greater(sp.AliveCount, 0);

        // endTime(=12) より十分先まで進める：全 bar が despawn 範囲を超える
        sp.Tick(20.0);
        Assert.AreEqual(0, sp.AliveCount);
    }

    [Test]
    public void SetChart_ZeroBpm_DoesNotSpawn()
    {
        var (sp, _) = MakeSpawner(0f, 4000f);
        sp.Tick(0.0);
        sp.Tick(5.0);
        Assert.AreEqual(0, sp.NextIndex);
        Assert.AreEqual(0, sp.AliveCount);
    }
}
