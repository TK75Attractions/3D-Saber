using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class GoldNoteSfxTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
        foreach (var n in Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (n != null) Object.DestroyImmediate(n.gameObject);
        }
    }

    [Test]
    public void BuildShingClip_ProducesNormalizedMetallicClip()
    {
        var clip = GoldNoteSfx.BuildShingClip("test_shing", 2093f, 0.85f, 0.09f, 7f, 12345);
        Assert.IsNotNull(clip);
        Assert.Greater(clip.samples, 1000, "十分な長さがある");

        var data = new float[clip.samples];
        clip.GetData(data, 0);
        float peak = 0f;
        double energy = 0.0;
        foreach (float v in data)
        {
            peak = Mathf.Max(peak, Mathf.Abs(v));
            energy += v * v;
        }
        Assert.LessOrEqual(peak, 0.95f, "正規化されクリップしない");
        Assert.Greater(peak, 0.5f, "ピークは正規化値(0.9)付近");
        Assert.Greater(energy, 1.0, "無音ではない");
    }

    [Test]
    public void BuildShingClip_IsDeterministicForSameSeed()
    {
        var a = GoldNoteSfx.BuildShingClip("a", 2093f, 0.4f, 0.09f, 7f, 777);
        var b = GoldNoteSfx.BuildShingClip("b", 2093f, 0.4f, 0.09f, 7f, 777);
        var da = new float[a.samples];
        var db = new float[b.samples];
        a.GetData(da, 0);
        b.GetData(db, 0);
        Assert.AreEqual(da.Length, db.Length);
        for (int i = 0; i < da.Length; i += 997)
        {
            Assert.AreEqual(da[i], db[i], 1e-6f, "シード固定で再現可能(調整の回帰確認ができる)");
        }
    }

    [Test]
    public void Spawner_DetectsGoldFromChartColor()
    {
        var sGo = new GameObject("spawner");
        created.Add(sGo);
        var spawner = sGo.AddComponent<NoteSpawner>();
        var prefab = new GameObject("notePrefab");
        prefab.AddComponent<CuttableNote>();
        created.Add(prefab);
        spawner.notePrefab = prefab;
        spawner.approachTime = 2.0f;
        spawner.spawnZ = 20f;
        spawner.judgeZ = 0f;
        spawner.judgeWindow = 0.2f;
        spawner.missGrace = 0.05f;
        spawner.despawnAfterMissSeconds = 0.1f;

        var captured = new List<CuttableNote>();
        spawner.OnNoteSpawned += n => captured.Add(n);

        var chart = new ChartData { bpm = 100f };
        chart.notes.Add(new NoteData { time = 1000, x = 0, y = 0, type = "tap", color = "gold" });
        // 2つ目は先読み境界(songTime + approachTime >= eff は spawn される)の外に置く
        chart.notes.Add(new NoteData { time = 2100, x = 0, y = 0, type = "tap", color = "red" });
        spawner.SetChart(chart);

        spawner.Tick(0.0); // 先読み内
        Assert.AreEqual(1, captured.Count);
        Assert.IsTrue(captured[0].IsGold, "color=gold は IsGold=true");

        spawner.Tick(1.0);
        Assert.AreEqual(2, captured.Count);
        Assert.IsFalse(captured[1].IsGold, "color=red は IsGold=false");
    }

    [Test]
    public void GoldSfx_OnlyAttachesToGoldNotes()
    {
        var go = new GameObject("goldSfx", typeof(AudioSource), typeof(GoldNoteSfx));
        created.Add(go);
        var sfx = go.GetComponent<GoldNoteSfx>();

        var sGo = new GameObject("spawner");
        created.Add(sGo);
        var spawner = sGo.AddComponent<NoteSpawner>();
        var prefab = new GameObject("notePrefab");
        prefab.AddComponent<CuttableNote>();
        created.Add(prefab);
        spawner.notePrefab = prefab;
        spawner.approachTime = 2.0f;
        spawner.spawnZ = 20f;
        spawner.judgeZ = 0f;

        sfx.Bind(spawner);

        var chart = new ChartData { bpm = 100f };
        chart.notes.Add(new NoteData { time = 1000, x = 0, y = 0, type = "tap", color = "gold" });
        chart.notes.Add(new NoteData { time = 1100, x = 0, y = 0, type = "tap", color = "red" });
        spawner.SetChart(chart);
        spawner.Tick(0.0);

        var notes = Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        int goldCount = 0;
        foreach (var n in notes) if (n.IsGold) goldCount++;
        Assert.AreEqual(1, goldCount, "1つだけ Gold");
    }
}
