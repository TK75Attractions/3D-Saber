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
}
