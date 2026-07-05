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
    public void Build_Creates3FloorLanes()
    {
        // Neon Focus:レーンガイドは両端+中央の 3 本に削減(縦線ノイズを減らす)
        var fr = FloorRenderer.Ensure();
        created.Add(fr.gameObject);
        int laneCount = 0;
        foreach (Transform t in fr.transform)
        {
            if (t.name.StartsWith("FloorLane_")) laneCount++;
        }
        Assert.AreEqual(3, laneCount, "x=-3 / 0 / +3 の 3 本（床）");
    }

    [Test]
    public void Build_NoCeilingByDefault()
    {
        // Neon Focus:天井グリッドは視覚ノイズのため既定 OFF
        var fr = FloorRenderer.Ensure();
        created.Add(fr.gameObject);
        foreach (Transform t in fr.transform)
        {
            Assert.IsFalse(t.name.StartsWith("CeilLane_"), $"天井レーンが生成されている: {t.name}");
            Assert.IsFalse(t.name.StartsWith("CeilDepth_"), $"天井奥行き線が生成されている: {t.name}");
        }
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
