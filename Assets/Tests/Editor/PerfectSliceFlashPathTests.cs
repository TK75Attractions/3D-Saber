using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// 実際の切断の流れ(Cut → OnCut → ScoreManager → OnJudged → GameplayCutFeedback → 2片の生成)を通して、
// Perfect の切断片の縁発光が本当に出ることを確かめる。判定の通知は2片を作る前に来るため、以前は何も起きなかった。
public class PerfectSliceFlashPathTests
{
    readonly List<Object> objects = new List<Object>();
    readonly List<CuttableNote> notes = new List<CuttableNote>();
    NoteSpawner spawner;
    ScoreManager score;
    SongPlayer clock;

    [SetUp]
    public void Setup()
    {
        var root = new GameObject("SliceFlashPathTest"); objects.Add(root);
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube); objects.Add(prefab);
        prefab.name = "SliceFlashNotePrefab";
        Object.DestroyImmediate(prefab.GetComponent<Collider>());
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader); objects.Add(material);
        material.SetColor("_EmissionColor", new Color(.1f, .1f, .1f, 1f));
        prefab.GetComponent<MeshRenderer>().sharedMaterial = material;
        prefab.AddComponent<CuttableNote>();
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.buildTimingCues = false;
        spawner.OnNoteSpawned += n => { notes.Add(n); objects.Add(n.gameObject); };
        clock = root.AddComponent<SongPlayer>();
        score = root.AddComponent<ScoreManager>(); score.songPlayer = clock; score.Bind(spawner);
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var piece in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (piece != null) Object.DestroyImmediate(piece.gameObject);
        foreach (var item in objects) if (item != null) Object.DestroyImmediate(item);
        objects.Clear(); notes.Clear();
    }

    void SpawnAndCut(double error)
    {
        var chart = new ChartData();
        chart.notes.Add(new NoteData { time = 1000, type = "tap", color = "blue", count = 1, x = -1 });
        spawner.SetChart(chart); spawner.Tick(1);
        Assert.AreEqual(1, notes.Count);
        typeof(SongPlayer).GetField("scheduled", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(clock, true);
        typeof(SongPlayer).GetField("clockSynchronized", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(clock, true);
        typeof(SongPlayer).GetField("startDspTime", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(clock, AudioSettings.dspTime - notes[0].HitTime - error);
        notes[0].Cut(notes[0].transform.position, Vector3.up * 8, CutDirection.None, notes[0].RequiredHand);
    }

    static int FlashingPieces()
    {
        int n = 0;
        foreach (var piece in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (piece.IsFlashing) n++;
        return n;
    }

    [Test]
    public void PerfectCutFlashesBothSlicesThroughTheRealJudgmentPath()
    {
        SpawnAndCut(0);
        Assert.AreEqual(JudgmentTier.Perfect, score.LastTier);
        Assert.AreEqual(2, FlashingPieces(), "Perfect の2片が両方とも縁発光する");
    }

    [Test]
    public void GreatCutDoesNotFlashSlices()
    {
        SpawnAndCut(.10);
        Assert.AreEqual(JudgmentTier.Great, score.LastTier);
        Assert.AreEqual(0, FlashingPieces(), "縁発光は Perfect だけ");
    }

    [Test]
    public void PendingFlashIsNotCarriedToAnUncutNote()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube); objects.Add(go);
        var note = go.AddComponent<CuttableNote>();
        note.FlashSlices(Color.white, .3f);
        Assert.AreEqual(0, FlashingPieces(), "切れていないノーツには予約しない");
    }
}
