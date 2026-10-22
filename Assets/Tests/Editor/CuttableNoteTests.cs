using NUnit.Framework;
using UnityEngine;

public class CuttableNoteTests
{
    private CuttableNote Make()
    {
        var go = new GameObject("note");
        return go.AddComponent<CuttableNote>();
    }

    [Test]
    public void Cut_FlipsState_AndFiresEventOnce()
    {
        var n = Make();
        int fired = 0;
        n.OnCut += (_, __, ___) => fired++;
        n.Cut(Vector3.zero, Vector3.right);
        n.Cut(Vector3.zero, Vector3.right);
        Assert.IsTrue(n.IsCut);
        Assert.AreEqual(1, fired);
        Object.DestroyImmediate(n.gameObject);
    }

    [Test]
    public void MarkMiss_FlipsState_AndFiresEventOnce()
    {
        var n = Make();
        int fired = 0;
        n.OnMiss += _ => fired++;
        n.MarkMiss();
        n.MarkMiss();
        Assert.IsTrue(n.IsMissed);
        Assert.AreEqual(1, fired);
        Object.DestroyImmediate(n.gameObject);
    }

    [Test]
    public void Cut_AfterMiss_DoesNothing()
    {
        var n = Make();
        n.MarkMiss();
        n.Cut(Vector3.zero, Vector3.right);
        Assert.IsFalse(n.IsCut);
        Assert.IsTrue(n.IsMissed);
        Object.DestroyImmediate(n.gameObject);
    }

    [Test]
    public void Cut_Long_FiresPartialCutPerCall_ThenOnCutOnce()
    {
        var n = Make();
        n.RequiredCutCount = 3;
        n.RemainingCuts = 3;
        var indices = new System.Collections.Generic.List<int>();
        int totals = 0;
        int onCutFired = 0;
        n.OnPartialCut += (_, idx, total) => { indices.Add(idx); totals = total; };
        n.OnCut += (_, __, ___) => onCutFired++;

        n.Cut(Vector3.zero, Vector3.right);
        n.Cut(Vector3.zero, Vector3.right);
        n.Cut(Vector3.zero, Vector3.right);
        n.Cut(Vector3.zero, Vector3.right); // 4回目：完了済みで何も起きない

        Assert.AreEqual(new[] { 0, 1, 2 }, indices.ToArray());
        Assert.AreEqual(3, totals);
        Assert.AreEqual(1, onCutFired);
        Assert.IsTrue(n.IsCut);
        Object.DestroyImmediate(n.gameObject);
    }

    [Test]
    public void Cut_Tap_FiresPartialCutOnce()
    {
        var n = Make();
        int partial = 0;
        n.OnPartialCut += (_, __, ___) => partial++;
        n.Cut(Vector3.zero, Vector3.right);
        Assert.AreEqual(1, partial);
        Object.DestroyImmediate(n.gameObject);
    }

    [Test]
    public void Cut_OppositeDirection_DoesNotAdvanceState()
    {
        var n = Make();
        n.RequiredDirection = CutDirection.Right;
        n.RequiredCutCount = 1;
        n.RemainingCuts = 1;
        int onCut = 0, partial = 0;
        n.OnCut += (_, __, ___) => onCut++;
        n.OnPartialCut += (_, __, ___) => partial++;

        // 左方向（要求の真逆）で切ろうとする → 反応しない
        n.Cut(Vector3.zero, Vector3.left);
        Assert.IsFalse(n.IsCut, "逆方向では IsCut にならない");
        Assert.IsFalse(n.IsMissed);
        Assert.AreEqual(0, onCut);
        Assert.AreEqual(0, partial);
        Assert.AreEqual(1, n.RemainingCuts, "RemainingCuts は減らない");
        Object.DestroyImmediate(n.gameObject);
    }

    [Test]
    public void Cut_PerpendicularDirection_DoesNotCutUntilTheRightSwing()
    {
        var n = Make();
        n.RequiredDirection = CutDirection.Right;
        n.RequiredCutCount = 1;
        n.RemainingCuts = 1;
        int onCut = 0;
        n.OnCut += (_, __, ___) => onCut++;

        // 上方向（90°ズレ）→ 2026-10-22 から1段下げで切らず、その振りでは何も起きない
        n.Cut(Vector3.zero, Vector3.up);
        Assert.IsFalse(n.IsCut);
        Assert.IsFalse(n.IsMissed);
        Assert.AreEqual(0, onCut);
        // 向きの合う次の振りで切れる
        n.Cut(Vector3.zero, Vector3.right);
        Assert.IsTrue(n.IsCut);
        Assert.AreEqual(1, onCut);
        Object.DestroyImmediate(n.gameObject);
    }

    [Test]
    public void Cut_OppositeOnLongNote_DoesNotConsumeCut()
    {
        var n = Make();
        n.RequiredDirection = CutDirection.Right;
        n.RequiredCutCount = 3;
        n.RemainingCuts = 3;

        n.Cut(Vector3.zero, Vector3.right); // OK 1回目
        Assert.AreEqual(2, n.RemainingCuts);

        n.Cut(Vector3.zero, Vector3.left);  // 逆方向 → 消費しない
        Assert.AreEqual(2, n.RemainingCuts);

        n.Cut(Vector3.zero, Vector3.right); // OK 2回目
        Assert.AreEqual(1, n.RemainingCuts);
        Object.DestroyImmediate(n.gameObject);
    }

    [Test]
    public void Cut_DirectionVisualOnly_AcceptsAnyDirection()
    {
        // メニューのナビノーツ用: 矢印は見た目だけで、逆方向のスイングでも切れる
        var n = Make();
        n.RequiredDirection = CutDirection.Up;
        n.DirectionVisualOnly = true;
        int onCut = 0;
        n.OnCut += (_, __, ___) => onCut++;

        n.Cut(Vector3.zero, Vector3.down); // 真逆
        Assert.IsTrue(n.IsCut, "方向が見た目だけなら逆方向でも切れる");
        Assert.AreEqual(1, onCut);
        Object.DestroyImmediate(n.gameObject);
    }

    [Test]
    public void Cut_DirectionVisualOnly_Off_CutsOnlyInTheArrowDirection()
    {
        // 既定(false)では、逆方向も横振りも切らない(本編の規則)
        var n = Make();
        n.RequiredDirection = CutDirection.Up;
        Assert.IsFalse(n.DirectionVisualOnly, "DirectionVisualOnly の既定は false");
        n.Cut(Vector3.zero, Vector3.down);
        Assert.IsFalse(n.IsCut, "逆方向は切らない");
        n.Cut(Vector3.zero, Vector3.right);
        Assert.IsFalse(n.IsCut, "横振りも切らない");
        n.Cut(Vector3.zero, Vector3.up);
        Assert.IsTrue(n.IsCut, "矢印の向きの振りで切れる");
        Object.DestroyImmediate(n.gameObject);
    }
}
