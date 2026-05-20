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
        sp.despawnAfterMissSeconds = 0.1f;
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
    public void Tick_MissesNoteAfterGraceWindow_KeepsFlowing()
    {
        var (sp, _, _) = MakeSpawner();
        int missed = 0;
        sp.OnNoteMissed += _ => missed++;
        sp.SetChart(MakeChart(1000f));
        sp.Tick(0.0);
        sp.Tick(1.0);
        sp.Tick(1.3); // 判定窓 + grace を超える → miss 発火だがノーツは残る
        Assert.AreEqual(1, missed);
        Assert.AreEqual(1, sp.AliveCount, "miss でも即消えず後ろに流す");

        // despawnAfterMiss(0.1) を超えると回収される
        sp.Tick(2.0);
        Assert.AreEqual(0, sp.AliveCount);
        Assert.AreEqual(1, missed, "miss は1回だけ発火");
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

    [Test]
    public void LateWindowFor_LongNote_ScalesByCount()
    {
        var (sp, _, _) = MakeSpawner();
        sp.judgeWindow = 0.23f;
        sp.secondsPerLongCut = 0.7f;

        var tapNote = new GameObject("tap").AddComponent<CuttableNote>();
        tapNote.RequiredCutCount = 1;
        Assert.AreEqual(0.23f, sp.LateWindowFor(tapNote), 0.001f);

        var longNote = new GameObject("long").AddComponent<CuttableNote>();
        longNote.RequiredCutCount = 4;
        Assert.AreEqual(0.23f + 3 * 0.7f, sp.LateWindowFor(longNote), 0.001f);

        var bigLong = new GameObject("big").AddComponent<CuttableNote>();
        bigLong.RequiredCutCount = 50;
        Assert.AreEqual(0.23f + 49 * 0.7f, sp.LateWindowFor(bigLong), 0.001f);

        Object.DestroyImmediate(tapNote.gameObject);
        Object.DestroyImmediate(longNote.gameObject);
        Object.DestroyImmediate(bigLong.gameObject);
    }

    [Test]
    public void ComputeNoteZ_TapMovesAtBaseSpeed()
    {
        var (sp, _, _) = MakeSpawner();
        var tap = new GameObject("tap").AddComponent<CuttableNote>();
        tap.RequiredCutCount = 1;
        float speed = sp.Speed;
        // dt=1 → z = judgeZ + speed*1
        Assert.AreEqual(sp.judgeZ + speed * 1f, sp.ComputeNoteZ(tap, 1.0, speed), 0.001f);
        // dt=-1（過ぎた後） → z = judgeZ + speed*-1
        Assert.AreEqual(sp.judgeZ - speed, sp.ComputeNoteZ(tap, -1.0, speed), 0.001f);
        Object.DestroyImmediate(tap.gameObject);
    }

    [Test]
    public void ComputeNoteZ_LongNoteLingersAfterHitTime()
    {
        var (sp, _, _) = MakeSpawner();
        sp.secondsPerLongCut = 0.7f;
        sp.longLingerDriftZ = 1.0f;
        var longNote = new GameObject("long").AddComponent<CuttableNote>();
        longNote.RequiredCutCount = 4; // lingerDuration = 3 * 0.7 = 2.1s
        float speed = sp.Speed;

        // 接近フェーズ（dt > 0）はタップと同じ
        Assert.AreEqual(sp.judgeZ + speed * 1f, sp.ComputeNoteZ(longNote, 1.0, speed), 0.001f);
        // HitTime 直後 → ほぼ judgeZ
        Assert.That(sp.ComputeNoteZ(longNote, -0.01, speed),
            Is.EqualTo(sp.judgeZ).Within(0.05f));
        // 滞留中（-dt = lingerDuration / 2 ≒ 1.05）→ -driftZ * 0.5
        float midLingerZ = sp.ComputeNoteZ(longNote, -1.05, speed);
        Assert.That(midLingerZ, Is.EqualTo(sp.judgeZ - 0.5f).Within(0.05f));
        // 滞留終了直前（-dt = 2.1）→ z ≈ judgeZ - driftZ
        Assert.That(sp.ComputeNoteZ(longNote, -2.1, speed),
            Is.EqualTo(sp.judgeZ - 1.0f).Within(0.05f));
        // 滞留終了後（-dt = 3.1、afterLinger=1）→ 通常速度で後退
        Assert.That(sp.ComputeNoteZ(longNote, -3.1, speed),
            Is.EqualTo(sp.judgeZ - 1.0f - speed * 1.0f).Within(0.05f));
        Object.DestroyImmediate(longNote.gameObject);
    }

    [Test]
    public void SpawnLongNote_AppliesZScaleCap()
    {
        var (sp, _, spawned) = MakeSpawner();
        sp.longMaxVisualZScale = 6f;

        var chart = new ChartData { bpm = 100f };
        // count=50 のロング
        chart.notes.Add(new NoteData { time = 1000, x = 0, y = 0, type = "long", count = 50 });
        sp.SetChart(chart);
        sp.Tick(0.0);

        Assert.AreEqual(1, spawned.Count);
        // 元の prefab scale.z=1 想定。count=50 でも longMaxVisualZScale=6 でキャップ。
        float zScale = spawned[0].transform.localScale.z;
        Assert.That(zScale, Is.LessThanOrEqualTo(6.1f),
            "count=50 でも視野を埋め尽くさないよう Z スケールはキャップされる");
    }

    [Test]
    public void OffsetMs_ShiftsHitTime()
    {
        var (sp, _, spawned) = MakeSpawner();
        // 500ms 後ろにずらす（譜面が早すぎる場合）
        var chart = new ChartData { bpm = 100f, offsetMs = 500f };
        chart.notes.Add(new NoteData { time = 1000, x = 0, y = 0, type = "tap" });
        sp.SetChart(chart);

        // 元の TimeSeconds は 1.0、オフセット 0.5 を加えて実効 1.5s
        // approachTime=2.0s なので、songTime=-0.5 から先読み可能
        sp.Tick(-0.5);
        Assert.AreEqual(1, spawned.Count, "オフセット込みで approachTime 内に入っているので生成される");
        Assert.AreEqual(1.5, spawned[0].HitTime, 0.001, "HitTime にオフセットが反映される");
    }

    [Test]
    public void ExtraOffsetSeconds_ShiftsHitTime()
    {
        var (sp, _, spawned) = MakeSpawner();
        var chart = new ChartData { bpm = 100f, offsetMs = 100f };
        chart.notes.Add(new NoteData { time = 1000, x = 0, y = 0, type = "tap" });
        sp.SetExtraOffsetSeconds(0.2); // 追加で 200ms ずらす
        sp.SetChart(chart);

        sp.Tick(0.0);
        Assert.AreEqual(1, spawned.Count);
        // 1.0 + 0.1 (chart) + 0.2 (extra) = 1.3
        Assert.AreEqual(1.3, spawned[0].HitTime, 0.001);
        Assert.AreEqual(0.3, sp.TotalOffsetSeconds, 0.001);
    }

    [Test]
    public void EffectiveTime_ReturnsTimeWithOffset()
    {
        var (sp, _, _) = MakeSpawner();
        var chart = new ChartData { bpm = 100f, offsetMs = 250f };
        sp.SetChart(chart);
        var nd = new NoteData { time = 2000 };
        Assert.AreEqual(2.25, sp.EffectiveTime(nd), 0.001);
    }
}
