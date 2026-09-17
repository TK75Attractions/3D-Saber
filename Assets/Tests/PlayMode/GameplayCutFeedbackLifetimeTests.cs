using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class GameplayCutFeedbackLifetimeTests
{
    readonly List<GameObject> created = new List<GameObject>();

    [UnityTearDown]
    public IEnumerator Cleanup()
    {
        foreach (var go in created) if (go != null) Object.Destroy(go);
        created.Clear();
        yield return null;
    }

    NoteSpawner CreateSpawner(out List<CuttableNote> notes)
    {
        var root = new GameObject("FeedbackLifetimeOwner");
        var prefab = new GameObject("FeedbackLifetimePrefab");
        created.Add(root); created.Add(prefab);
        prefab.AddComponent<CuttableNote>();
        var owner = root.AddComponent<NoteSpawner>();
        owner.notePrefab = prefab;
        var captured = new List<CuttableNote>();
        owner.OnNoteSpawned += note => { captured.Add(note); created.Add(note.gameObject); };
        var chart = new ChartData();
        chart.notes.Add(new NoteData { time = 1000, type = "tap" });
        chart.notes.Add(new NoteData { time = 1500, type = "tap", x = 2 });
        owner.SetChart(chart);
        owner.Tick(1);
        notes = captured;
        return owner;
    }

    [UnityTest]
    public IEnumerator DestroyingOnlySpawnerReleasesOwnedVisualResources()
    {
        var owner = CreateSpawner(out _);
        yield return null;
        var effect = owner.GetComponentInChildren<GameplayCutFeedback>();
        var mesh = effect.GetComponent<MeshFilter>().sharedMesh;
        var material = effect.GetComponent<MeshRenderer>().sharedMaterial;
        Object.Destroy(owner);
        yield return null;
        yield return null;
        Assert.True(effect == null, "所有元コンポーネントだけの破棄でも表示を残さない");
        Assert.True(mesh == null);
        Assert.True(material == null);
    }

    [UnityTest]
    public IEnumerator DisablingSpawnerClearsEffectsAndReenablingKeepsLiveNotesConnected()
    {
        var owner = CreateSpawner(out var notes);
        yield return null;
        var effect = owner.GetComponentInChildren<GameplayCutFeedback>();
        notes[0].Cut(Vector3.zero, Vector3.right * 8);
        Assert.AreEqual(1, effect.ActiveCount);
        owner.enabled = false;
        Assert.AreEqual(0, effect.ActiveCount);
        owner.enabled = true;
        notes[1].Cut(notes[1].transform.position, Vector3.up * 8);
        Assert.AreEqual(1, effect.ActiveCount);
        effect.Tick(.3f);
        Assert.AreEqual(0, effect.ActiveCount);
    }
}
