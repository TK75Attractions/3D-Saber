using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 実行中の生成・切断・ミス・曲送りで、矢印の材質だけが残らないことを確認する。
public class ArrowMaterialLifetimePlayTests
{
    Scene previousScene, testScene;
    readonly List<Material> observed = new List<Material>();

    [SetUp]
    public void SetUp()
    {
        previousScene = SceneManager.GetActiveScene();
        testScene = SceneManager.CreateScene("ArrowMaterialLifetime_" + Guid.NewGuid().ToString("N"));
        SceneManager.SetActiveScene(testScene);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        SceneManager.SetActiveScene(previousScene);
        if (testScene.IsValid() && testScene.isLoaded) yield return SceneManager.UnloadSceneAsync(testScene);
        // 修正前の再現テストでも、残った材質を後のテストへ持ち越さない。
        foreach (var material in observed) if (material != null) Object.Destroy(material);
        observed.Clear();
        yield return null;
    }

    [UnityTest]
    public IEnumerator GameCutReleasesArrowMaterials()
    {
        NoteSpawner spawner = MakeSpawner(out CuttableNote note);
        Material[] materials = Observe(note.transform.Find("Arrow"));
        note.Cut(note.transform.position, Vector3.up * 5);
        yield return null;
        yield return null;
        Assert.True(note == null, "本体のスライスでノーツが消えること");
        AssertReleased(materials);
        Assert.NotNull(spawner);
    }

    [UnityTest]
    public IEnumerator MissKeepsArrowUntilDespawnThenReleasesMaterials()
    {
        NoteSpawner spawner = MakeSpawner(out CuttableNote note);
        Material[] materials = Observe(note.transform.Find("Arrow"));
        spawner.Tick(1);
        Assert.True(note.IsMissed);
        Assert.True(materials.All(material => material != null), "流れ去る途中の矢印は表示できること");
        spawner.Tick(4);
        yield return null;
        yield return null;
        Assert.True(note == null);
        AssertReleased(materials);
    }

    [UnityTest]
    public IEnumerator ChartResetReleasesArrowMaterials()
    {
        NoteSpawner spawner = MakeSpawner(out CuttableNote note);
        Material[] materials = Observe(note.transform.Find("Arrow"));
        spawner.SetChart(new ChartData());
        yield return null;
        yield return null;
        Assert.AreEqual(0, spawner.AliveCount);
        AssertReleased(materials);
    }

    [UnityTest]
    public IEnumerator RemovingOneArrowPreservesTheOtherArrowAndSharedBodyMaterial()
    {
        var first = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var second = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Material sharedBody = first.GetComponent<Renderer>().sharedMaterial;
        second.GetComponent<Renderer>().sharedMaterial = sharedBody;
        NoteSpawner.BuildArrow(first.transform, CutDirection.Left);
        NoteSpawner.BuildArrow(second.transform, CutDirection.Right);
        Material[] removed = Observe(first.transform.Find("Arrow"));
        Material[] surviving = Observe(second.transform.Find("Arrow"));
        Object.Destroy(first.transform.Find("Arrow").gameObject);
        yield return null;
        yield return null;
        AssertReleased(removed);
        Assert.True(surviving.All(material => material != null));
        Assert.True(sharedBody != null);
        Assert.AreSame(sharedBody, first.GetComponent<Renderer>().sharedMaterial);
    }

    [UnityTest]
    public IEnumerator RepeatedSelectionShotsReleaseEachOldArrow()
    {
        var camera = new GameObject("NavigationCamera").AddComponent<Camera>();
        camera.transform.position = new Vector3(0, 0, -10);
        var nav = new GameObject("Navigation").AddComponent<SongSelectSlashNav>();
        nav.enabled = false;
        nav.Init(null, camera);
        Material[] downMaterials = Observe(nav.DownNote.transform.Find("Arrow"));
        for (int index = 0; index < 8; index++)
        {
            CuttableNote note = nav.UpNote;
            Material[] materials = Observe(note.transform.Find("Arrow"));
            Assert.True(nav.TryShoot(true));
            yield return null;
            yield return null;
            AssertReleased(materials);
            Assert.True(downMaterials.All(material => material != null), "逆側の矢印は再利用できること");
            nav.Tick(3);
            Assert.NotNull(nav.UpNote);
            Assert.False(nav.UpNote.IsCut);
        }
    }

    [UnityTest]
    public IEnumerator SceneExitReleasesArrowMaterials()
    {
        var note = new GameObject("ArrowHost");
        NoteSpawner.BuildArrow(note.transform, CutDirection.Down);
        Material[] materials = Observe(note.transform.Find("Arrow"));
        SceneManager.SetActiveScene(previousScene);
        yield return SceneManager.UnloadSceneAsync(testScene);
        yield return null;
        AssertReleased(materials);
    }

    NoteSpawner MakeSpawner(out CuttableNote note)
    {
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.name = "TestNotePrefab";
        prefab.transform.position = Vector3.one * 1000;
        var spawner = new GameObject("Spawner").AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.reuseNotes = false;
        spawner.buildTimingCues = false;
        var chart = new ChartData();
        chart.notes.Add(new NoteData { time = 0, color = "red", direction = "up", count = 1 });
        CuttableNote spawned = null;
        spawner.OnNoteSpawned += value => spawned = value;
        spawner.SetChart(chart);
        spawner.Tick(0);
        note = spawned;
        Assert.NotNull(note);
        return spawner;
    }

    Material[] Observe(Transform arrow)
    {
        Assert.NotNull(arrow);
        Material[] materials = arrow.GetComponentsInChildren<Renderer>().Select(renderer => renderer.sharedMaterial).ToArray();
        Assert.AreEqual(2, materials.Length);
        Assert.True(materials.All(material => material != null));
        observed.AddRange(materials);
        return materials;
    }

    static void AssertReleased(Material[] materials)
    {
        Assert.AreEqual(0, materials.Count(material => material != null), "消えた矢印の材質を残さない");
    }
}
