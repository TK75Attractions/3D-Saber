using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class BarLineSpawnerTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go == null) continue;
            // EditModeではAwakeを経ないため、所有資源の終了処理を明示的に呼ぶ。
            var sp = go.GetComponent<BarLineSpawner>();
            if (sp != null) typeof(BarLineSpawner).GetMethod("OnDestroy",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(sp, null);
            Object.DestroyImmediate(go);
        }
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

    private BarLineSpawner MakeVisibleSpawner()
    {
        var (sp, prefab) = MakeSpawner(120f, 8000f);
        var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
        line.name = "Line";
        line.transform.SetParent(prefab.transform, false);
        line.GetComponent<MeshRenderer>().sharedMaterial = null;
        sp.root = sp.transform;
        sp.overrideVisual = true;
        sp.lineAlpha = .12f;
        return sp;
    }

    [Test]
    public void VisualOverrideSuppliesATransparentMaterialWhenPrefabHasNone()
    {
        var sp = MakeVisibleSpawner();
        sp.Tick(0);
        var material = sp.GetComponentInChildren<MeshRenderer>().sharedMaterial;
        Assert.NotNull(material, "素材なしの小節線がエラーピンクにならない");
        Assert.AreEqual("Universal Render Pipeline/Unlit", material.shader.name);
        Assert.AreEqual(.12f, material.GetColor("_BaseColor").a, .0001f);
        Assert.AreEqual(1, material.GetFloat("_Surface"));
        Assert.AreEqual(0, material.GetInt("_ZWrite"));
        Assert.AreEqual((int)UnityEngine.Rendering.BlendMode.SrcAlpha, material.GetInt("_SrcBlend"));
        Assert.IsNull(sp.barLinePrefab.GetComponentInChildren<MeshRenderer>().sharedMaterial, "共有プレハブは書き換えない");
    }

    [Test]
    public void BarsShareTheirStyleAndAccentDoesNotChangeNormalBars()
    {
        var sp = MakeVisibleSpawner();
        sp.accentEvery = 2;
        sp.despawnAfterSeconds = 10;
        sp.Tick(2);
        var bars = sp.GetComponentsInChildren<MeshRenderer>();
        Assert.AreEqual(3, bars.Length);
        Assert.AreSame(bars[0].sharedMaterial, bars[2].sharedMaterial);
        Assert.AreNotSame(bars[0].sharedMaterial, bars[1].sharedMaterial);
        Assert.AreEqual(.12f, bars[1].sharedMaterial.GetColor("_BaseColor").a, .0001f);
        Assert.Greater(bars[0].sharedMaterial.GetColor("_BaseColor").a, .12f);
    }

    [Test]
    public void ChartResetReusesOwnedStyles()
    {
        var sp = MakeVisibleSpawner();
        sp.accentEvery = 2;
        sp.Tick(0);
        var bars = sp.GetComponentsInChildren<MeshRenderer>();
        Material accent = bars[0].sharedMaterial;
        sp.SetChart(new ChartData { bpm = 120 });
        sp.Tick(0);
        Assert.AreSame(accent, sp.GetComponentInChildren<MeshRenderer>().sharedMaterial);
    }

    [Test]
    public void WithoutOverrideThePrefabMaterialIsPreserved()
    {
        var sp = MakeVisibleSpawner();
        sp.overrideVisual = false;
        var original = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        try
        {
            original.SetColor("_BaseColor", Color.green);
            sp.barLinePrefab.GetComponentInChildren<MeshRenderer>().sharedMaterial = original;
            sp.Tick(0);
            Assert.AreSame(original, sp.GetComponentInChildren<MeshRenderer>().sharedMaterial);
            Assert.AreEqual(Color.green, original.GetColor("_BaseColor"));
        }
        finally { Object.DestroyImmediate(original); }
    }
}
