using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// XY 距離ベースの新判定で、CuttableNote.Cut が実際に発火することを確認。
public class SaberCutSmokeTest
{
    [UnityTest]
    public IEnumerator SaberTrajectoryCutsNote_SingleFrame()
    {
        var note = GameObject.CreatePrimitive(PrimitiveType.Cube);
        note.name = "TestNote";
        note.transform.position = Vector3.zero;
        var cuttable = note.AddComponent<CuttableNote>();
        cuttable.IsJudgeable = true;

        var saberGo = new GameObject("SmokeSaber");
        var tracker = saberGo.AddComponent<SaberTracker>();
        var judge = saberGo.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.bladeRadius = 0.4f;
        judge.minCutSpeed = 3f;
        judge.maxCutDistance = 5f;
        judge.noteHitRadiusXY = 0.5f;

        yield return null;

        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(2f, 0f, 0f), 0.1f);
        int cuts = judge.TryCut();

        Assert.AreEqual(1, cuts, "横切りで 1 個切れる");
        Assert.IsTrue(cuttable.IsCut);

        Object.Destroy(saberGo);
        yield return null;
    }

    [UnityTest]
    public IEnumerator SlowSaberDoesNotCut()
    {
        var note = GameObject.CreatePrimitive(PrimitiveType.Cube);
        note.transform.position = Vector3.zero;
        var cuttable = note.AddComponent<CuttableNote>();
        cuttable.IsJudgeable = true;

        var saberGo = new GameObject("SlowSaber");
        var tracker = saberGo.AddComponent<SaberTracker>();
        var judge = saberGo.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.minCutSpeed = 3f;
        judge.maxCutDistance = 5f;
        judge.noteHitRadiusXY = 0.5f;

        yield return null;

        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(-1f, 0f, 0f), 1f);
        int cuts = judge.TryCut();

        Assert.AreEqual(0, cuts);
        Assert.IsFalse(cuttable.IsCut);

        Object.Destroy(saberGo);
        Object.Destroy(note);
        yield return null;
    }

    [UnityTest]
    public IEnumerator NonJudgeableNote_NotCut()
    {
        var note = GameObject.CreatePrimitive(PrimitiveType.Cube);
        note.transform.position = Vector3.zero;
        var cuttable = note.AddComponent<CuttableNote>();
        cuttable.IsJudgeable = false;

        var saberGo = new GameObject("WindowSaber");
        var tracker = saberGo.AddComponent<SaberTracker>();
        var judge = saberGo.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.minCutSpeed = 3f;
        judge.maxCutDistance = 5f;
        judge.noteHitRadiusXY = 0.5f;

        yield return null;

        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(2f, 0f, 0f), 0.1f);
        int cuts = judge.TryCut();

        Assert.AreEqual(0, cuts);

        Object.Destroy(saberGo);
        Object.Destroy(note);
        yield return null;
    }

    [UnityTest]
    public IEnumerator MeshIsActuallySliced_TwoPiecesSpawn()
    {
        var note = GameObject.CreatePrimitive(PrimitiveType.Cube);
        note.name = "SliceTestNote";
        note.transform.position = Vector3.zero;
        Object.DestroyImmediate(note.GetComponent<BoxCollider>());
        var cuttable = note.AddComponent<CuttableNote>();
        cuttable.IsJudgeable = true;

        var saberGo = new GameObject("SliceSaber");
        var tracker = saberGo.AddComponent<SaberTracker>();
        var judge = saberGo.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.bladeRadius = 0.4f;
        judge.minCutSpeed = 3f;
        judge.maxCutDistance = 5f;
        judge.noteHitRadiusXY = 0.5f;

        yield return null;

        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(2f, 0f, 0f), 0.1f);
        judge.TryCut();

        yield return null;

        var pieces = Object.FindObjectsByType<SlicePieceDecay>(FindObjectsSortMode.None);
        Assert.AreEqual(2, pieces.Length, "スライス後に 2 つの物理片が生成される");

        foreach (var p in pieces) Object.Destroy(p.gameObject);
        Object.Destroy(saberGo);
        yield return null;
    }
}
