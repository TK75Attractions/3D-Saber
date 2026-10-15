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
    public void Spawner_LinksLongAndTapWithSameStartTime()
    {
        // 異種ノーツ(ロング×タップ)でも始点(time)が同じなら連結する
        var spawner = MakeSpawner();
        var chart = new ChartData { bpm = 100f };
        chart.notes.Add(new NoteData { time = 1000f, x = -1.5f, y = 0f, type = "long", color = "blue", count = 3 });
        chart.notes.Add(new NoteData { time = 1000f, x = 1.5f, y = 0f, type = "tap", color = "red" });
        spawner.SetChart(chart);
        spawner.Tick(0.0);

        var links = Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsSortMode.None);
        Assert.AreEqual(1, links.Length, "ロング×タップの同時ペアにも連結線");
        Assert.IsTrue(links[0].Refresh());
        bool hasLong = links[0].noteA.RequiredCutCount > 1 || links[0].noteB.RequiredCutCount > 1;
        Assert.IsTrue(hasLong, "片方はロングノーツ");
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

        Vector3 a = link.Line.GetPosition(0), b = link.Line.GetPosition(1);
        Assert.Greater(a.x, -2f, "線は左ノーツの輪郭から始まる");
        Assert.Less(b.x, 2f, "線は右ノーツの輪郭で終わる");
        Assert.AreEqual(5f, a.z); Assert.AreEqual(5f, b.z);
        Assert.AreEqual(0f, a.y + a.x * .5f, .0001f);
        Assert.AreEqual(0f, b.y + b.x * .5f, .0001f);
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

    private CuttableNote VisualNote(SaberHand hand, float x)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        created.Add(go);
        go.transform.position = Vector3.right * x;
        var note = go.AddComponent<CuttableNote>();
        note.RequiredHand = hand;
        var visuals = go.AddComponent<NoteVisuals>();
        visuals.Initialize();
        visuals.ResetForReuse();
        return note;
    }

    private static Color Emission(CuttableNote note)
    {
        return note.GetComponent<MeshRenderer>().sharedMaterial.GetColor("_EmissionColor");
    }

    private static void UpdateVisuals(NoteVisuals visuals)
    {
        typeof(NoteVisuals).GetMethod("Update", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic).Invoke(visuals, null);
    }

    [Test]
    public void Link_GentlyLightsOnlyItsNotesAndPreservesTimingHighlight()
    {
        var a = VisualNote(SaberHand.Left, -2);
        var b = VisualNote(SaberHand.Right, 2);
        var single = VisualNote(SaberHand.Left, 4);
        Color baseA = Emission(a), baseB = Emission(b), baseSingle = Emission(single);
        Color bodyColor = a.GetComponent<MeshRenderer>().sharedMaterial.GetColor("_BaseColor");
        var link = SimultaneousNoteLink.Create(a, b, null);
        float ratio = Emission(a).maxColorComponent / baseA.maxColorComponent;
        Assert.That(ratio, Is.InRange(1.1f, 1.35f), "同時押しは控えめな発光に留める");
        Assert.AreEqual(ratio, Emission(b).maxColorComponent / baseB.maxColorComponent, .001f);
        Assert.AreEqual(baseSingle, Emission(single), "単独ノーツへ波及しない");
        Assert.AreEqual(bodyColor, a.GetComponent<MeshRenderer>().sharedMaterial.GetColor("_BaseColor"));
        var visuals = a.GetComponent<NoteVisuals>();
        visuals.SetEmissionBoost(1.4f);
        UpdateVisuals(visuals);
        Assert.AreEqual(baseA.maxColorComponent * ratio * 1.4f, Emission(a).maxColorComponent, .001f);
        Object.DestroyImmediate(link.gameObject);
        Assert.AreEqual(baseA.maxColorComponent * 1.4f, Emission(a).maxColorComponent, .001f);
        Assert.AreEqual(baseB, Emission(b));
    }

    [Test]
    public void MultipleLinks_DoNotStackGlowAndReleaseOnlyAfterLastLink()
    {
        var a = VisualNote(SaberHand.Left, -2);
        var b = VisualNote(SaberHand.Right, 2);
        var c = VisualNote(SaberHand.Left, 4);
        Color baseline = Emission(a);
        var ab = SimultaneousNoteLink.Create(a, b, null);
        Color paired = Emission(a);
        var ac = SimultaneousNoteLink.Create(a, c, null);
        Assert.AreEqual(paired, Emission(a), "三個以上でも光量を重ねない");
        ab.gameObject.SetActive(false);
        Object.DestroyImmediate(ab.gameObject);
        Assert.AreEqual(paired, Emission(a), "無効化と破棄で二重に解除しない");
        c.MarkMiss();
        Color dim = Emission(c);
        Assert.IsFalse(ac.Refresh());
        UpdateVisuals(c.GetComponent<NoteVisuals>());
        Assert.AreEqual(dim, Emission(c), "ミスした本体を再点灯しない");
        Assert.AreEqual(baseline, Emission(a));
    }

    [Test]
    public void Reuse_ClearsOldGlowAndOldLinkCannotRemoveNewPairsGlow()
    {
        var a = VisualNote(SaberHand.Left, -2);
        var b = VisualNote(SaberHand.Right, 2);
        var c = VisualNote(SaberHand.Right, 4);
        Color baseline = Emission(a);
        var oldLink = SimultaneousNoteLink.Create(a, b, null);
        typeof(CuttableNote).GetMethod("ResetForSpawn", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic).Invoke(a, null);
        a.GetComponent<NoteVisuals>().ResetForReuse();
        Assert.AreEqual(baseline, Emission(a), "単独ノーツへの再利用で発光を持ち越さない");
        var newLink = SimultaneousNoteLink.Create(a, c, null);
        Color paired = Emission(a);
        Assert.IsFalse(oldLink.Refresh());
        Object.DestroyImmediate(oldLink.gameObject);
        Assert.AreEqual(paired, Emission(a), "古いリンクの遅延破棄は新しい組へ影響しない");
        Object.DestroyImmediate(newLink.gameObject);
        Assert.AreEqual(baseline, Emission(a));
    }

    [Test]
    public void Link_IsBrighterThanBarLineAndUsesReadableWidth()
    {
        Assert.Greater(SimultaneousNoteLink.DefaultAlpha, GameStageSkin.BarLineAlpha);

        var spawner = MakeSpawner();
        spawner.SetChart(TwoNoteChart(1000f, 1000f));
        spawner.Tick(0.0);
        var link = Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsSortMode.None)[0];
        // LineRenderer の頂点色は8bit量子化されるため許容を広めに取る。
        Assert.AreEqual(SimultaneousNoteLink.DefaultAlpha, link.Line.startColor.a, 0.005f);
        Assert.Greater(link.Line.startColor.a, GameStageSkin.BarLineAlpha, "同時押しを小節線より明るく示す");
        Assert.AreEqual(SimultaneousNoteLink.DefaultWidth, link.Line.startWidth, .0001f);
        Assert.AreEqual(SimultaneousNoteLink.DefaultWidth, link.Line.endWidth, .0001f);
        Assert.AreEqual(1f, link.Line.startColor.r, 1e-4f, "白である");
    }
}
