using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class MemoryNoteVeilPlayTests
{
    Scene previous, scene;
    bool projector;
    NoteSpawner spawner;
    CuttableNote current;

    [SetUp]
    public void Setup()
    {
        previous = SceneManager.GetActiveScene();
        scene = SceneManager.CreateScene("MemoryNoteVeilPlayTests");
        SceneManager.SetActiveScene(scene);
        projector = DisplaySettings.ProjectorMode;
        DisplaySettings.SetProjectorModeForTest(false);

        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.name = "MemoryVeilPrefab";
        prefab.SetActive(false);
        spawner = new GameObject("MemoryVeilSpawner").AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.MemoryVeilMode = true;
        spawner.OnNoteSpawned += note => current = note;
        var score = new GameObject("MemoryVeilScore").AddComponent<ScoreManager>();
        score.Bind(spawner);
    }

    [UnityTearDown]
    public IEnumerator Cleanup()
    {
        DisplaySettings.SetProjectorModeForTest(projector);
        SceneManager.SetActiveScene(previous);
        if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        yield return null;
    }

    void Spawn(float veil)
    {
        var chart = new ChartData();
        chart.notes.Add(new NoteData {
            time = 1000, color = "blue", direction = "up", count = 3, veil = veil
        });
        spawner.SetChart(chart);
        spawner.Tick(1);
    }

    void Cut()
    {
        current.Cut(current.transform.position, Vector3.up * 8f, CutDirection.Up, SaberHand.Left);
    }

    [UnityTest]
    public IEnumerator NearlyInvisibleNoteReturnsToAnOrdinaryNote()
        => CheckReuse(.95f);

    [UnityTest]
    public IEnumerator CompletelyInvisibleNoteReturnsToAnOrdinaryNote()
        => CheckReuse(1f);

    IEnumerator CheckReuse(float veil)
    {
        Spawn(veil);
        yield return null;
        var first = current;
        var body = first.GetComponent<MeshRenderer>().sharedMaterial;
        var arrow = first.transform.Find("Arrow");
        Assert.AreEqual(veil, first.Veil);
        Assert.IsFalse(first.countLabel.gameObject.activeInHierarchy);
        Assert.IsFalse(arrow.gameObject.activeSelf);
        Assert.IsFalse(first.TimingCue.GhostRoot.activeInHierarchy);
        Assert.IsFalse(first.TimingCue.ApproachRoot.activeInHierarchy);

        Cut();
        Assert.AreEqual(2, first.RemainingCuts);
        foreach (Transform child in first.transform)
            if (child.name == "Crack") Assert.IsFalse(child.gameObject.activeSelf);
        Cut();
        Cut();
        Assert.IsTrue(first.IsCut);

        var feedback = spawner.GetComponentInChildren<GameplayCutFeedback>();
        Assert.AreEqual(1, feedback.ActiveCount);
        var feedbackRenderer = feedback.GetComponent<MeshRenderer>();
        Assert.IsTrue(feedbackRenderer.enabled);
        Assert.AreEqual(MemoryVeilLayers.AboveSortingOrder, feedbackRenderer.sortingOrder);
        Assert.AreEqual(MemoryVeilLayers.AboveRenderQueue + 5, feedbackRenderer.sharedMaterial.renderQueue);

        var pieces = spawner.FragmentPool.GetComponentsInChildren<SlicePieceDecay>();
        Assert.AreEqual(8, pieces.Length);
        float expectedAlpha = .85f * (1f - veil);
        foreach (var piece in pieces)
        {
            var renderer = piece.GetComponent<MeshRenderer>();
            Assert.AreEqual(expectedAlpha, renderer.sharedMaterial.GetColor("_BaseColor").a, 1e-6f);
            Assert.AreEqual(veil < 1f, renderer.enabled);
        }
        first.FlashSlices(Color.white, .3f);
        spawner.Tick(1.01);

        int created = spawner.CreatedNoteCount;
        Spawn(0f);
        Assert.AreSame(first, current);
        Assert.AreEqual(created, spawner.CreatedNoteCount);
        Assert.AreEqual(0f, current.Veil);
        Assert.AreEqual(0f, current.GetComponent<NoteVisuals>().Veil);
        Assert.AreSame(body, current.GetComponent<MeshRenderer>().sharedMaterial);
        Assert.AreEqual(.85f, body.GetColor("_BaseColor").a, 1e-6f);
        Assert.IsTrue(current.GetComponent<MeshRenderer>().enabled);
        Assert.IsTrue(current.transform.Find("EdgeRails").GetComponent<MeshRenderer>().enabled);
        Assert.IsTrue(arrow.gameObject.activeInHierarchy);
        Assert.IsTrue(current.countLabel.gameObject.activeInHierarchy);
        Assert.AreEqual(1f, current.countLabel.alpha);
        Assert.AreEqual("3", current.countLabel.text);
        Assert.IsTrue(current.TimingCue.GhostRoot.activeInHierarchy);
        Assert.IsTrue(current.TimingCue.ApproachRoot.activeInHierarchy);

        // 本体が普通に戻っても、既に飛んでいる破片の材質は薄いまま独立している。
        foreach (var piece in pieces)
        {
            var renderer = piece.GetComponent<MeshRenderer>();
            Assert.AreEqual(expectedAlpha, renderer.sharedMaterial.GetColor("_BaseColor").a, 1e-6f);
            piece.Step(.8f);
            Assert.LessOrEqual(renderer.sharedMaterial.GetColor("_BaseColor").a, expectedAlpha + 1e-6f);
        }

        Cut();
        current.TickCracks(.3f);
        int cracks = 0;
        foreach (Transform child in current.transform)
        {
            if (child.name != "Crack" || !child.gameObject.activeSelf) continue;
            cracks++;
            Assert.AreEqual(1f, child.GetComponent<MeshRenderer>().sharedMaterial.GetColor("_BaseColor").a);
        }
        Assert.AreEqual(1, cracks);
        yield return null;
    }
}
