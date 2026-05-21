using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class FloorRendererTests
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
        // FloorRenderer 静的に存在する場合もクリーンアップ
        var existing = Object.FindFirstObjectByType<FloorRenderer>();
        if (existing != null) Object.DestroyImmediate(existing.gameObject);
    }

    [Test]
    public void Ensure_CreatesFloorRenderer()
    {
        var fr = FloorRenderer.Ensure();
        Assert.IsNotNull(fr);
        created.Add(fr.gameObject);
    }

    [Test]
    public void Ensure_IsIdempotent()
    {
        var a = FloorRenderer.Ensure();
        var b = FloorRenderer.Ensure();
        Assert.AreSame(a, b);
        created.Add(a.gameObject);
    }

    [Test]
    public void Build_CreatesBaseFloor()
    {
        var fr = FloorRenderer.Ensure();
        created.Add(fr.gameObject);
        var floor = fr.transform.Find("FloorBase");
        Assert.IsNotNull(floor, "FloorBase が存在する");
    }

    [Test]
    public void Build_Creates7FloorLanes()
    {
        var fr = FloorRenderer.Ensure();
        created.Add(fr.gameObject);
        int laneCount = 0;
        foreach (Transform t in fr.transform)
        {
            if (t.name.StartsWith("FloorLane_")) laneCount++;
        }
        Assert.AreEqual(7, laneCount, "x=-3..+3 の 7 本（床）");
    }

    [Test]
    public void Build_Creates7CeilingLanes()
    {
        var fr = FloorRenderer.Ensure();
        created.Add(fr.gameObject);
        int ceilCount = 0;
        foreach (Transform t in fr.transform)
        {
            if (t.name.StartsWith("CeilLane_")) ceilCount++;
        }
        Assert.AreEqual(7, ceilCount, "天井にも 7 レーン");
    }

    [Test]
    public void Build_CreatesFloorDepthLines()
    {
        var fr = FloorRenderer.Ensure();
        created.Add(fr.gameObject);
        int depthCount = 0;
        foreach (Transform t in fr.transform)
        {
            if (t.name.StartsWith("FloorDepth_")) depthCount++;
        }
        Assert.Greater(depthCount, 5, "床の奥行きラインが複数本");
    }

    [Test]
    public void Build_CreatesCeilingDepthLines()
    {
        var fr = FloorRenderer.Ensure();
        created.Add(fr.gameObject);
        int depthCount = 0;
        foreach (Transform t in fr.transform)
        {
            if (t.name.StartsWith("CeilDepth_")) depthCount++;
        }
        Assert.Greater(depthCount, 5, "天井の奥行きラインが複数本");
    }

    [Test]
    public void Build_StripsCollidersFromAllParts()
    {
        var fr = FloorRenderer.Ensure();
        created.Add(fr.gameObject);
        foreach (var col in fr.GetComponentsInChildren<Collider>(true))
        {
            Assert.Fail($"unexpected collider on {col.name}");
        }
    }
}
