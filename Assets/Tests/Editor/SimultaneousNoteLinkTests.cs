using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 同時押しノーツの連結線(SimultaneousNoteLink)の検証。
public class SimultaneousNoteLinkTests
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
        foreach (var link in Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (link != null) Object.DestroyImmediate(link.gameObject);
        }
        foreach (var n in Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (n != null) Object.DestroyImmediate(n.gameObject);
        }
    }

    private NoteSpawner MakeSpawner()
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
        return spawner;
    }

    private static ChartData TwoNoteChart(float timeA, float timeB)
    {
        var chart = new ChartData { bpm = 100f };
        chart.notes.Add(new NoteData { time = timeA, x = -1.5f, y = 0f, type = "tap", color = "blue" });
        chart.notes.Add(new NoteData { time = timeB, x = 1.5f, y = 0f, type = "tap", color = "red" });
        return chart;
    }

    [Test]
    public void Spawner_CreatesLinkForSimultaneousNotes()
    {
        var spawner = MakeSpawner();
        spawner.SetChart(TwoNoteChart(1000f, 1000f));
        spawner.Tick(0.0);

        var links = Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsSortMode.None);
        Assert.AreEqual(1, links.Length, "同時刻ペアに連結線が1本");
        Assert.IsNotNull(links[0].Line, "LineRenderer が生成されている");
        Assert.IsTrue(links[0].Refresh(), "両ノーツ生存中は生きている");
    }

    [Test]
    public void Spawner_NoLinkForDifferentTimes()
    {
        var spawner = MakeSpawner();
        spawner.SetChart(TwoNoteChart(1000f, 1200f));
        spawner.Tick(0.0);

        Assert.AreEqual(0, Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsSortMode.None).Length,
            "200ms差は同時扱いしない");
    }

    [Test]
    public void Spawner_DisabledFlag_SuppressesLinks()
    {
        var spawner = MakeSpawner();
        spawner.simultaneousGuideEnabled = false;
        spawner.SetChart(TwoNoteChart(1000f, 1000f));
        spawner.Tick(0.0);

        Assert.AreEqual(0, Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsSortMode.None).Length);
    }

    [Test]
    public void Link_FollowsNotePositions()
    {
        var spawner = MakeSpawner();
        spawner.SetChart(TwoNoteChart(1000f, 1000f));
        spawner.Tick(0.0);
        var link = Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsSortMode.None)[0];

        link.noteA.transform.position = new Vector3(-2f, 1f, 5f);
        link.noteB.transform.position = new Vector3(2f, -1f, 5f);
        Assert.IsTrue(link.Refresh());

        Assert.AreEqual(new Vector3(-2f, 1f, 5f), link.Line.GetPosition(0));
        Assert.AreEqual(new Vector3(2f, -1f, 5f), link.Line.GetPosition(1));
    }

    [Test]
    public void Link_DiesWhenEitherNoteIsCut()
    {
        var spawner = MakeSpawner();
        spawner.SetChart(TwoNoteChart(1000f, 1000f));
        spawner.Tick(0.0);
        var link = Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsSortMode.None)[0];

        link.noteA.IsJudgeable = true;
        link.noteA.Cut(Vector3.zero, new Vector3(10f, 0f, 0f), CutDirection.None, SaberHand.Any);

        Assert.IsFalse(link.Refresh(), "片方がカットされたら消える");
    }

    [Test]
    public void Link_IsFainterThanBarLine()
    {
        // 要件: 小節線(GameStageSkin.BarLineAlpha)より薄い白
        Assert.Less(SimultaneousNoteLink.DefaultAlpha, GameStageSkin.BarLineAlpha);

        var spawner = MakeSpawner();
        spawner.SetChart(TwoNoteChart(1000f, 1000f));
        spawner.Tick(0.0);
        var link = Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsSortMode.None)[0];
        // LineRenderer の頂点色は8bit量子化される(0.095→約0.0941)ため許容を広めに取る
        Assert.AreEqual(SimultaneousNoteLink.DefaultAlpha, link.Line.startColor.a, 0.005f);
        Assert.Less(link.Line.startColor.a, GameStageSkin.BarLineAlpha, "実測アルファも小節線より薄い");
        Assert.AreEqual(1f, link.Line.startColor.r, 1e-4f, "白である");
    }
}
