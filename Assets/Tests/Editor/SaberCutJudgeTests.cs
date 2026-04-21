using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class SaberCutJudgeTests
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
        foreach (var n in Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (n != null) Object.DestroyImmediate(n.gameObject);
        }
    }

    private (SaberCutJudge judge, SaberTracker tracker) MakeRig()
    {
        var saberGo = new GameObject("saber");
        created.Add(saberGo);
        var tracker = saberGo.AddComponent<SaberTracker>();
        var judge = saberGo.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.minCutSpeed = 3.0f;
        judge.bladeRadius = 0.2f;
        judge.maxCutDistance = 5.0f;
        judge.noteHitRadiusXY = 0.3f;
        return (judge, tracker);
    }

    private CuttableNote MakeNote(Vector3 pos, bool judgeable = true)
    {
        var go = new GameObject("note");
        created.Add(go);
        go.transform.position = pos;
        var n = go.AddComponent<CuttableNote>();
        n.IsJudgeable = judgeable;
        return n;
    }

    [Test]
    public void TryCut_WithoutPreviousFrame_ReturnsZero()
    {
        var (judge, _) = MakeRig();
        Assert.AreEqual(0, judge.TryCut());
    }

    [Test]
    public void TryCut_BelowMinSpeed_Skips()
    {
        var (judge, tracker) = MakeRig();
        tracker.ResetTo(Vector3.zero);
        tracker.Tick(new Vector3(0.01f, 0, 0), 1f);
        Assert.AreEqual(0, judge.TryCut());
    }

    [Test]
    public void TryCut_ExceedsMaxDistance_Skips()
    {
        var (judge, tracker) = MakeRig();
        tracker.ResetTo(Vector3.zero);
        // maxCutDistance=5 を超える 10
        tracker.Tick(new Vector3(10f, 0, 0), 0.1f);
        MakeNote(new Vector3(2f, 0f, 0f));
        Assert.AreEqual(0, judge.TryCut());
    }

    [Test]
    public void SingleFramePassThrough_CutsNote()
    {
        var (judge, tracker) = MakeRig();
        MakeNote(new Vector3(0f, 0f, 0f));
        // 左 → 右 を一気に通過、終点は十分遠い
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(2f, 0f, 0f), 0.1f); // Speed=40, dist=4
        int cuts = judge.TryCut();
        Assert.AreEqual(1, cuts);
    }

    [Test]
    public void EnterButNotExit_DoesNotCut()
    {
        var (judge, tracker) = MakeRig();
        var note = MakeNote(new Vector3(0f, 0f, 0f));
        // 左から note 中心まで入るだけ
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(0f, 0f, 0f), 0.1f); // Speed=20, 終点が note 中心 → まだ出ていない
        int cuts = judge.TryCut();
        Assert.AreEqual(0, cuts, "入ったが出ていないので切らない");
        Assert.IsFalse(note.IsCut);
        Assert.Greater(judge.PendingCount, 0, "保留中として残る");
    }

    [Test]
    public void EnterThenExitAcrossFrames_CutsOnExit()
    {
        var (judge, tracker) = MakeRig();
        var note = MakeNote(new Vector3(0f, 0f, 0f));
        // フレーム1：入る（まだ出ない）
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(0f, 0f, 0f), 0.1f);
        Assert.AreEqual(0, judge.TryCut());
        // フレーム2：通り抜ける
        tracker.Tick(new Vector3(2f, 0f, 0f), 0.1f);
        int cuts = judge.TryCut();
        Assert.AreEqual(1, cuts);
        Assert.IsTrue(note.IsCut);
    }

    [Test]
    public void NonJudgeableNote_Ignored()
    {
        var (judge, tracker) = MakeRig();
        MakeNote(Vector3.zero, judgeable: false);
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(2f, 0f, 0f), 0.1f);
        Assert.AreEqual(0, judge.TryCut());
    }

    [Test]
    public void ZCoordinate_Ignored()
    {
        var (judge, tracker) = MakeRig();
        // ノーツは Z=+5、セーバーは Z=0 で動く。XY 判定のみなので切れるはず。
        MakeNote(new Vector3(0f, 0f, 5f));
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(2f, 0f, 0f), 0.1f);
        int cuts = judge.TryCut();
        Assert.AreEqual(1, cuts);
    }

    [Test]
    public void DistPointToSegment_ComputesPerpendicularDistance()
    {
        float d = SaberCutJudge.DistPointToSegment(
            new Vector2(0f, 3f),
            new Vector2(-5f, 0f), new Vector2(5f, 0f),
            out Vector2 closest);
        Assert.AreEqual(3f, d, 0.001f);
        Assert.AreEqual(0f, closest.x, 0.001f);
        Assert.AreEqual(0f, closest.y, 0.001f);
    }
}
