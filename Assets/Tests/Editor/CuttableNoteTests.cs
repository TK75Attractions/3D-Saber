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
}
