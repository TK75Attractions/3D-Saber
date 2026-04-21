using NUnit.Framework;
using UnityEngine;

public class SaberTrackerTests
{
    private SaberTracker MakeTracker()
    {
        var go = new GameObject("saber");
        return go.AddComponent<SaberTracker>();
    }

    [Test]
    public void Tick_ComputesVelocityAndSpeed()
    {
        var t = MakeTracker();
        t.ResetTo(new Vector3(0, 0, 0));
        t.Tick(new Vector3(1, 0, 0), 0.5f);

        Assert.AreEqual(new Vector3(1, 0, 0), t.CurrentPosition);
        Assert.AreEqual(new Vector3(0, 0, 0), t.PreviousPosition);
        Assert.AreEqual(2f, t.Velocity.x, 0.0001f);
        Assert.AreEqual(2f, t.Speed, 0.0001f);
        Assert.IsTrue(t.HasPrevious);

        Object.DestroyImmediate(t.gameObject);
    }

    [Test]
    public void Tick_WithZeroDelta_DoesNotDivideByZero()
    {
        var t = MakeTracker();
        t.ResetTo(Vector3.zero);
        t.Tick(new Vector3(10, 0, 0), 0f);
        Assert.IsFalse(float.IsNaN(t.Speed));
        Assert.IsFalse(float.IsInfinity(t.Speed));
        Object.DestroyImmediate(t.gameObject);
    }

    [Test]
    public void ResetTo_ClearsPreviousFlag()
    {
        var t = MakeTracker();
        t.Tick(new Vector3(1, 0, 0), 0.1f);
        Assert.IsTrue(t.HasPrevious);
        t.ResetTo(Vector3.zero);
        Assert.IsFalse(t.HasPrevious);
        Assert.AreEqual(0f, t.Speed);
        Object.DestroyImmediate(t.gameObject);
    }

    [Test]
    public void Tick_AccumulatesHistoryAcrossFrames()
    {
        var t = MakeTracker();
        t.ResetTo(Vector3.zero);
        t.Tick(new Vector3(1, 0, 0), 1f);
        t.Tick(new Vector3(3, 0, 0), 1f);
        Assert.AreEqual(new Vector3(1, 0, 0), t.PreviousPosition);
        Assert.AreEqual(new Vector3(3, 0, 0), t.CurrentPosition);
        Assert.AreEqual(2f, t.Velocity.x, 0.0001f);
        Object.DestroyImmediate(t.gameObject);
    }
}
