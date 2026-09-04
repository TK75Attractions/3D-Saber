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

    // ---- 刃のクランプ(中点を範囲内に収め、端点は平行移動) ----

    [Test]
    public void ClampBladeKeepingLength_MidInside_Unchanged()
    {
        var (a, b) = SaberInputBridge.ClampBladeKeepingLength(
            new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector2(-5f, -3f), new Vector2(5f, 3f));
        Assert.AreEqual(new Vector3(-1f, 0f, 0f), a);
        Assert.AreEqual(new Vector3(1f, 0f, 0f), b);
    }

    [Test]
    public void ClampBladeKeepingLength_MidAtEdge_ReachesEdge_AndKeepsLength()
    {
        // 中点 x=5(右端)・刃長 2 の水平ブレード。
        // 旧実装(端点を個別クランプ)は右端点だけ止まって中点が 4.5 に戻り、ポインタが右端に届かなかった。
        var (a, b) = SaberInputBridge.ClampBladeKeepingLength(
            new Vector3(4f, 0f, 0f), new Vector3(6f, 0f, 0f), new Vector2(-5f, -3f), new Vector2(5f, 3f));
        Vector3 mid = (a + b) * 0.5f;
        Assert.AreEqual(5f, mid.x, 1e-4f, "中点は端まで届く");
        Assert.AreEqual(2f, Vector3.Distance(a, b), 1e-4f, "刃長は変わらない");
    }

    [Test]
    public void ClampBladeKeepingLength_MidOutside_ShiftsBothEndsEqually()
    {
        var (a, b) = SaberInputBridge.ClampBladeKeepingLength(
            new Vector3(6f, 4f, 0f), new Vector3(8f, 4f, 0f), new Vector2(-5f, -3f), new Vector2(5f, 3f));
        Vector3 mid = (a + b) * 0.5f;
        Assert.AreEqual(5f, mid.x, 1e-4f);
        Assert.AreEqual(3f, mid.y, 1e-4f);
        Assert.AreEqual(4f, a.x, 1e-4f, "左端点も同じ量だけ戻る");
        Assert.AreEqual(6f, b.x, 1e-4f);
        Assert.AreEqual(3f, a.y, 1e-4f);
    }
}
