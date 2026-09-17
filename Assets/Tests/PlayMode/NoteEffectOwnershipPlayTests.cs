using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// 実際のCut経路とUnityの破棄を通し、見えなくなった演出の生成資源が残らないことを確認する。
public class NoteEffectOwnershipPlayTests
{
    readonly List<GameObject> objects = new List<GameObject>();
    readonly List<Mesh> slicedMeshes = new List<Mesh>();
    readonly List<Material> ownedMaterials = new List<Material>();
    Mesh sharedCube;
    Random.State randomState;

    [SetUp] public void Setup() { randomState = Random.state; }

    CuttableNote Note(string label, int cuts = 1)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = "OwnershipProbe-" + label;
        objects.Add(go); sharedCube = go.GetComponent<MeshFilter>().sharedMesh;
        var note = go.AddComponent<CuttableNote>();
        note.RequiredCutCount = note.RemainingCuts = cuts;
        note.pieceLife = .06f; note.pieceFadeStart = .03f;
        return note;
    }

    void CapturePieces(string label)
    {
        foreach (var piece in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsSortMode.None))
        {
            if (!piece.name.StartsWith("OwnershipProbe-" + label)) continue;
            objects.Add(piece.gameObject);
            var mesh = piece.GetComponent<MeshFilter>().sharedMesh;
            if (piece.name.EndsWith("_piece"))
            {
                Assert.AreNotSame(sharedCube, mesh);
                Assert.AreSame(mesh, piece.GetComponent<MeshCollider>().sharedMesh);
                slicedMeshes.Add(mesh);
            }
            else Assert.AreSame(sharedCube, mesh, "小さな破片は共有Cubeを使い続ける");
            ownedMaterials.Add(piece.GetComponent<MeshRenderer>().sharedMaterial);
        }
    }

    [UnityTearDown]
    public IEnumerator Cleanup()
    {
        foreach (var go in objects) if (go != null) Object.Destroy(go);
        yield return null; yield return null;
        // 修正前の再現実行でも、テストが残した資源を次のテストへ持ち越さない。
        foreach (var mesh in slicedMeshes) if (mesh != null) Object.Destroy(mesh);
        foreach (var material in ownedMaterials) if (material != null) Object.Destroy(material);
        objects.Clear(); slicedMeshes.Clear(); ownedMaterials.Clear(); Random.state = randomState;
        yield return null;
    }

    [UnityTest]
    public IEnumerator RepeatedCutsReleaseAllSliceMeshesAfterLifetime()
    {
        for (int i = 0; i < 16; i++)
        {
            string label = "Tap" + i + "-";
            var note = Note(label); note.Cut(Vector3.zero, Vector3.right * 5);
            CapturePieces(label);
        }
        Assert.AreEqual(32, slicedMeshes.Count);
        Assert.IsTrue(slicedMeshes.All(mesh => mesh != null && mesh.vertexCount > 0));
        yield return new WaitForSeconds(.15f); yield return null; yield return null;
        int remaining = slicedMeshes.Count(mesh => mesh != null);
        TestContext.Progress.WriteLine("16 cuts / 32 slice meshes: remaining after lifetime = " + remaining);
        Assert.AreEqual(0, remaining);
        Assert.IsTrue(ownedMaterials.All(material => material == null));
        Assert.IsTrue(sharedCube != null && sharedCube.vertexCount > 0);
    }

    [UnityTest]
    public IEnumerator LongNoteCracksDisappearWithNoteAndSharedDebrisMeshSurvives()
    {
        var note = Note("Long", 3);
        note.Cut(Vector3.zero, Vector3.right * 5);
        note.Cut(Vector3.zero, Vector3.right * 5);
        var cracks = note.GetComponentsInChildren<MeshRenderer>().Where(r => r.name == "Crack").Select(r => r.sharedMaterial).ToArray();
        Assert.AreEqual(2, cracks.Length); Assert.IsTrue(cracks.All(m => m != null)); ownedMaterials.AddRange(cracks);
        note.Cut(Vector3.zero, Vector3.right * 5); CapturePieces("Long");
        Assert.AreEqual(2, slicedMeshes.Count);
        yield return new WaitForSeconds(.15f); yield return null; yield return null;
        int remaining = cracks.Count(m => m != null);
        TestContext.Progress.WriteLine("Long note: remaining crack materials = " + remaining);
        Assert.AreEqual(0, remaining);
        Assert.IsTrue(slicedMeshes.All(mesh => mesh == null));
        Assert.IsTrue(sharedCube != null && sharedCube.vertexCount > 0);
    }

    [UnityTest]
    public IEnumerator SceneExitReleasesSlicesBeforeTheirLifetimeEnds()
    {
        var previous = SceneManager.GetActiveScene();
        var scene = SceneManager.CreateScene("OwnershipProbeScene");
        try
        {
            SceneManager.SetActiveScene(scene);
            var note = Note("Exit"); note.pieceLife = 30;
            note.Cut(Vector3.zero, Vector3.right * 5); CapturePieces("Exit");
        }
        finally { SceneManager.SetActiveScene(previous); }
        Assert.AreEqual(2, slicedMeshes.Count);
        yield return SceneManager.UnloadSceneAsync(scene);
        yield return null; yield return null;
        Assert.IsTrue(slicedMeshes.All(mesh => mesh == null));
        Assert.IsTrue(ownedMaterials.All(material => material == null));
        Assert.IsTrue(sharedCube != null && sharedCube.vertexCount > 0);
    }
}
