using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class SlicePieceDecayTests
{
    private readonly List<GameObject> created = new List<GameObject>();
    private readonly List<Material> materials = new List<Material>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
        foreach (var m in materials)
        {
            if (m != null) Object.DestroyImmediate(m);
        }
        materials.Clear();
    }

    private Material MakeMat()
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(sh);
        materials.Add(m);
        return m;
    }

    [Test]
    public void SetOwnedMaterial_AssignsToRendererAndDestroysOnGo()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        created.Add(go);
        Object.DestroyImmediate(go.GetComponent<BoxCollider>());
        var mr = go.GetComponent<MeshRenderer>();

        var parentMat = MakeMat();
        var clone = new Material(parentMat); // 親由来を複製

        var decay = go.AddComponent<SlicePieceDecay>();
        decay.SetOwnedMaterial(clone);
        Assert.AreSame(clone, mr.sharedMaterial);

        // 親由来 Material を破棄しても、clone はピース側が保持しているので生きている
        Object.DestroyImmediate(parentMat);
        materials.Remove(parentMat);
        Assert.IsNotNull(mr.sharedMaterial); // マゼンタ化していない
        Assert.IsNotNull(clone);

        // GameObject 破棄時はピースの clone も破棄される（OnDestroy で）。
        // EditMode では DestroyImmediate で OnDestroy が呼ばれないため、リフレクションで起動する。
        var onDestroy = typeof(SlicePieceDecay).GetMethod("OnDestroy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(onDestroy, "OnDestroy メソッドが見つからない");
        onDestroy.Invoke(decay, null);
        Assert.IsTrue(clone == null, "OnDestroy で clone が破棄されているはず");
        materials.Remove(clone); // 既に破棄済み
        Object.DestroyImmediate(go);
        created.Remove(go);
    }
}
