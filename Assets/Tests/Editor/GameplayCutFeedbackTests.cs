using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 成功カット・部分ミス・連打と所有元破棄の境界を実際の通知経路で確認する。
public class GameplayCutFeedbackTests
{
    readonly List<GameObject> created = new List<GameObject>();
    NoteSpawner owner;
    GameplayCutFeedback feedback;
    ScoreManager score;

    [SetUp]
    public void SetUp()
    {
        var go = new GameObject("CutFeedbackTestOwner");
        created.Add(go);
        owner = go.AddComponent<NoteSpawner>();
        feedback = GameplayCutFeedback.Create(owner);
        score = go.AddComponent<ScoreManager>(); score.Bind(owner);
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
        typeof(ScoreManager).GetMethod("HandleSpawned", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(score, new object[] { note });
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

    // ScoreManager を通さず、確定判定だけを通知する(方向降格後の最終判定と同じ入口)。
    void Judge(CuttableNote note, JudgmentTier tier)
    {
        typeof(CuttableNote).GetProperty("IsCut").SetValue(note, true);
        typeof(CuttableNote).GetMethod("NotifyJudgment", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(note, new object[] { tier, Vector3.zero, Vector3.right * 5f });
    }

    int VertexCountFor(JudgmentTier tier)
    {
        Judge(Note(), tier);
        int count = feedback.GetComponent<MeshFilter>().sharedMesh.vertexCount;
        feedback.ClearEffects();
        return count;
    }

    [Test]
    public void TieredAccent_PerfectIsRichest_GreatThenGood_BadAndMissDrawNothing()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        int perfect = VertexCountFor(JudgmentTier.Perfect);
        int great = VertexCountFor(JudgmentTier.Great);
        int good = VertexCountFor(JudgmentTier.Good);
        Assert.Greater(perfect, great, "Perfect は Great より多い形を描く");
        Assert.Greater(great, good, "Great は Good より多い形を描く");
        Assert.Greater(good, 0, "Good も演出を描く");
        Assert.LessOrEqual(perfect, GameplayCutFeedback.VerticesPerBurst, "1バーストの頂点上限を守る");
        Judge(Note(), JudgmentTier.Bad);
        Judge(Note(), JudgmentTier.Miss);
        Assert.AreEqual(0, feedback.ActiveCount, "Bad と Miss は演出を出さない");
        Assert.IsFalse(GameplayCutFeedback.Draws(JudgmentTier.Bad));
        Assert.IsFalse(GameplayCutFeedback.Draws(JudgmentTier.Miss));
    }

    [Test]
    public void ReducedEffects_DrawFewerShapesForEveryTier()
    {
        try
        {
            foreach (var tier in new[] { JudgmentTier.Perfect, JudgmentTier.Great, JudgmentTier.Good })
            {
                DisplaySettings.SetReducedEffectsForTest(false);
                int full = VertexCountFor(tier);
                DisplaySettings.SetReducedEffectsForTest(true);
                int low = VertexCountFor(tier);
                Assert.Greater(full, low, tier + " は LOW で形が減る");
                Assert.Greater(low, 0, tier + " は LOW でも消えない");
            }
        }
        finally { DisplaySettings.SetReducedEffectsForTest(false); }
    }

    [Test]
    public void MergedSimultaneousCutsKeepTheBetterTier()
    {
        Judge(Note(), JudgmentTier.Good);
        int goodOnly = feedback.GetComponent<MeshFilter>().sharedMesh.vertexCount;
        Judge(Note(), JudgmentTier.Perfect);
        Assert.AreEqual(1, feedback.ActiveCount, "同じ場所の同時切りは一つにまとめる");
        Assert.Greater(feedback.GetComponent<MeshFilter>().sharedMesh.vertexCount, goodOnly, "まとめた後は良い方の判定で描く");
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
        chart.notes.Add(new NoteData { time = 0, type = "tap" });
        owner.SetChart(chart);
        owner.Tick(0);
        Assert.NotNull(spawned);
        spawned.Cut(spawned.transform.position, Vector3.right * 8);
        // Spawnerが自分で生成・所有した表示コンポーネントを見る。
        var owned = (GameplayCutFeedback)typeof(NoteSpawner).GetField("cutFeedback", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(owner);
        Assert.AreEqual(1, owned.ActiveCount);
        owner.SetChart(new ChartData());
        Assert.AreEqual(0, owned.ActiveCount);
    }
}
