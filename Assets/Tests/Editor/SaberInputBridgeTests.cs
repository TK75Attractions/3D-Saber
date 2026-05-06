using NUnit.Framework;
using UnityEngine;

public class SaberInputBridgeTests
{
    private SaberInputBridge Make()
    {
        var go = new GameObject("bridge");
        var b = go.AddComponent<SaberInputBridge>();
        b.pixelsToWorld = 0.01f;
        b.fixedZ = 0f;
        b.clampToBounds = true;
        b.minBounds = new Vector2(-5f, -3f);
        b.maxBounds = new Vector2( 5f,  3f);
        return b;
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var b in Object.FindObjectsByType<SaberInputBridge>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (b != null) Object.DestroyImmediate(b.gameObject);
        }
    }

    [Test]
    public void ComputeWorld_AppliesScale()
    {
        var b = Make();
        Vector3 w = b.ComputeWorld(new Vector2(200f, 100f));
        Assert.AreEqual(2f, w.x, 0.001f);
        Assert.AreEqual(1f, w.y, 0.001f);
        Assert.AreEqual(0f, w.z, 0.001f);
    }

    [Test]
    public void ComputeWorld_ClampsToMaxBounds()
    {
        var b = Make();
        Vector3 w = b.ComputeWorld(new Vector2(99999f, 99999f));
        Assert.AreEqual(5f, w.x, 0.001f);
        Assert.AreEqual(3f, w.y, 0.001f);
    }

    [Test]
    public void ComputeWorld_ClampsToMinBounds()
    {
        var b = Make();
        Vector3 w = b.ComputeWorld(new Vector2(-99999f, -99999f));
        Assert.AreEqual(-5f, w.x, 0.001f);
        Assert.AreEqual(-3f, w.y, 0.001f);
    }

    [Test]
    public void ComputeWorld_FixedZIsApplied()
    {
        var b = Make();
        b.fixedZ = 2.5f;
        Vector3 w = b.ComputeWorld(Vector2.zero);
        Assert.AreEqual(2.5f, w.z, 0.001f);
    }
}
