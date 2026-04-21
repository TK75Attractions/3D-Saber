using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class NoteSpawnerTests
{
    private readonly List<GameObject> createdObjects = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in createdObjects)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        createdObjects.Clear();
        foreach (var n in Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (n != null) Object.DestroyImmediate(n.gameObject);
        }
    }

    private GameObject MakePrefab()
    {
        var go = new GameObject("notePrefab");
        go.AddComponent<CuttableNote>();
        // 非アクティブにすることで Instantiate 後にインスタンスだけがアクティブ化される設計にしたいが、
        // CuttableNote 側で Instantiate 時に SetActive しないのでここではアクティブのまま残す。
        // 代わりにテスト側は OnNoteSpawned で捕捉する。
        createdObjects.Add(go);
        return go;
    }

    private (NoteSpawner spawner, GameObject prefab, List<CuttableNote> spawned) MakeSpawner()
    {
        var sGo = new GameObject("spawner");
        createdObjects.Add(sGo);
        var sp = sGo.AddComponent<NoteSpawner>();
        var pf = MakePrefab();
        sp.notePrefab = pf;
        sp.approachTime = 2.0f;
        sp.spawnZ = 20f;
        sp.judgeZ = 0f;
        sp.judgeWindow = 0.15f;
        sp.missGrace = 0.05f;
        var spawned = new List<CuttableNote>();
        sp.OnNoteSpawned += n => spawned.Add(n);
        return (sp, pf, spawned);
    }

    private ChartData MakeChart(params float[] timesMs)
    {
        var c = new ChartData { bpm = 120f };
        foreach (var t in timesMs)
        {
            c.notes.Add(new NoteData { time = t, x = 0, y = 0, type = "tap" });
        }
        return c;
    }

    [Test]
    public void Tick_SpawnsNoteOnceWithinLeadTime()
    {
        var (sp, _, _) = MakeSpawner();
        sp.SetChart(MakeChart(3000f));
        sp.Tick(0.0);
        Assert.AreEqual(0, sp.AliveCount, "まだ approachTime(2s) より先");
        sp.Tick(1.0);
        Assert.AreEqual(1, sp.AliveCount, "1秒時点で 3-1=2 なので生成されるべき");
        sp.Tick(1.1);
        Assert.AreEqual(1, sp.AliveCount, "重複生成しない");
    }

    [Test]
    public void Tick_SetsJudgeableInsideWindow()
    {
        var (sp, _, spawned) = MakeSpawner();
        sp.SetChart(MakeChart(1000f));
        sp.Tick(0.0);
        Assert.AreEqual(1, spawned.Count);
        sp.Tick(1.0);
        Assert.IsTrue(spawned[0].IsJudgeable);
    }

    [Test]
    public void Tick_MissesNoteAfterGraceWindow()
    {
        var (sp, _, _) = MakeSpawner();
        int missed = 0;
        sp.OnNoteMissed += _ => missed++;
        sp.SetChart(MakeChart(1000f));
        sp.Tick(0.0);
        sp.Tick(1.0);
        sp.Tick(1.3);
        Assert.AreEqual(1, missed);
        Assert.AreEqual(0, sp.AliveCount);
    }

    [Test]
    public void Speed_MatchesSpawnRange()
    {
        var (sp, _, _) = MakeSpawner();
        Assert.AreEqual(10f, sp.Speed, 0.001f);
    }

    [Test]
    public void Tick_MovesNoteTowardJudgeZOverTime()
    {
        var (sp, _, spawned) = MakeSpawner();
        sp.SetChart(MakeChart(1000f));
        sp.Tick(0.0);
        Assert.AreEqual(1, spawned.Count);
        float zAt0 = spawned[0].transform.position.z;
        sp.Tick(0.5);
        float zAt05 = spawned[0].transform.position.z;
        Assert.Less(zAt05, zAt0, "時間が進むと Z は判定ラインに近づく（減少する）");
    }
}
