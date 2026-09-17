using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 成功カット・部分ミス・連打と所有元破棄の境界を実際の通知経路で確認する。
public class GameplayCutFeedbackTests
{
    readonly List<GameObject> created = new List<GameObject>();
    NoteSpawner owner;
    GameplayCutFeedback feedback;

    [SetUp]
    public void SetUp()
    {
        var go = new GameObject("CutFeedbackTestOwner");
        created.Add(go);
        owner = go.AddComponent<NoteSpawner>();
        feedback = GameplayCutFeedback.Create(owner);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
    }

    CuttableNote Note(float x = 0, int cuts = 1)
    {
        var go = new GameObject("CutFeedbackTestNote");
        created.Add(go);
        go.transform.position = new Vector3(x, 0, 0);
        var note = go.AddComponent<CuttableNote>();
        note.RequiredCutCount = note.RemainingCuts = cuts;
        feedback.Track(note);
        return note;
    }

    [Test]
    public void FinalCutDrawsOnceAndExpiresWithoutChangingJudgment()
    {
        var note = Note();
        feedback.Track(note);
        note.Cut(Vector3.zero, Vector3.right * 8);
        Assert.AreEqual(1, feedback.ActiveCount);
        var renderer = feedback.GetComponent<MeshRenderer>();
        Assert.NotNull(renderer);
        Assert.True(renderer.enabled);
        Assert.True(renderer.sharedMaterial.shader.isSupported);
        Assert.Greater(feedback.GetComponent<MeshFilter>().sharedMesh.vertexCount, 0);
        feedback.Tick(.25f);
        Assert.AreEqual(0, feedback.ActiveCount);
        Assert.False(renderer.enabled);
    }

    [Test]
    public void PartialLongMissNeverFlashesAsACompletedCut()
    {
        var note = Note(0, 3);
        note.Cut(Vector3.zero, Vector3.up * 8);
        Assert.AreEqual(0, feedback.ActiveCount);
        note.MarkMiss();
        Assert.True(note.IsMissed);
        Assert.AreEqual(0, feedback.ActiveCount);
    }

    [Test]
    public void LongCompletionProducesOnlyOneFinalAccent()
    {
        var note = Note(0, 3);
        note.Cut(Vector3.zero, Vector3.up * 8);
        note.Cut(Vector3.zero, Vector3.down * 8);
        Assert.AreEqual(0, feedback.ActiveCount);
        note.Cut(Vector3.zero, Vector3.up * 8);
        Assert.AreEqual(1, feedback.ActiveCount);
    }

    [Test]
    public void DenseCutsHaveABoundedMeshAndLifetime()
    {
        for (int i = 0; i < 40; i++)
            Note(i).Cut(new Vector3(i, 0, 0), Vector3.right * 8);
        Assert.AreEqual(GameplayCutFeedback.MaxBursts, feedback.ActiveCount);
        Assert.LessOrEqual(feedback.GetComponent<MeshFilter>().sharedMesh.vertexCount,
            GameplayCutFeedback.MaxBursts * GameplayCutFeedback.VerticesPerBurst);
        feedback.Tick(1);
        Assert.AreEqual(0, feedback.ActiveCount);
    }

    [Test]
    public void SimultaneousCutsAtSamePositionDoNotStackBrightness()
    {
        Note().Cut(Vector3.zero, Vector3.right * 8);
        Note().Cut(Vector3.zero, Vector3.up * 8);
        Assert.AreEqual(1, feedback.ActiveCount);
    }

    [Test]
    public void ResetDetachesOldNotesAndClearsTheScreen()
    {
        var old = Note(2);
        Note().Cut(Vector3.zero, Vector3.right * 8);
        feedback.ResetState();
        old.Cut(new Vector3(2, 0, 0), Vector3.right * 8);
        Assert.AreEqual(0, feedback.ActiveCount);
        Assert.False(feedback.GetComponent<MeshRenderer>().enabled);
    }

    [Test]
    public void SpawnerWiresCutsAndChartReplacementClearsEffects()
    {
        var prefab = new GameObject("CutFeedbackTestPrefab");
        created.Add(prefab);
        prefab.AddComponent<CuttableNote>();
        owner.notePrefab = prefab;
        CuttableNote spawned = null;
        owner.OnNoteSpawned += note => { spawned = note; created.Add(note.gameObject); };
        var chart = new ChartData();
        chart.notes.Add(new NoteData { time = 1000, type = "tap" });
        owner.SetChart(chart);
        owner.Tick(1);
        Assert.NotNull(spawned);
        spawned.Cut(spawned.transform.position, Vector3.right * 8);
        // Spawnerが自分で生成・所有した表示コンポーネントを見る。
        var owned = (GameplayCutFeedback)typeof(NoteSpawner).GetField("cutFeedback", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(owner);
        Assert.AreEqual(1, owned.ActiveCount);
        owner.SetChart(new ChartData());
        Assert.AreEqual(0, owned.ActiveCount);
    }
}
