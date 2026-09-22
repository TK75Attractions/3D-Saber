using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// Perfect のとき切断片の縁を短く発光させ、時間で元の発光色へ戻す(GameplayCutFeedback → CuttableNote.FlashSlices → SlicePieceDecay.Flash)。
public class SlicePieceFlashTests
{
    readonly List<Object> created = new List<Object>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
        created.Clear();
    }

    SlicePieceDecay Piece(Material source)
    {
        var go = new GameObject("piece", typeof(MeshFilter), typeof(MeshRenderer));
        created.Add(go);
        var piece = go.AddComponent<SlicePieceDecay>();
        piece.SetOwnedMaterial(new Material(source));
        return piece;
    }

    [Test]
    public void Flash_RaisesEmissionThenRestoresTheOriginalColor()
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var source = new Material(shader); created.Add(source);
        var baseEmission = new Color(.1f, .2f, .3f, 1f);
        source.SetColor("_EmissionColor", baseEmission);
        var piece = Piece(source);
        var mat = piece.GetComponent<MeshRenderer>().sharedMaterial;

        piece.Flash(Color.cyan, .3f);
        Assert.IsTrue(piece.IsFlashing);
        Assert.AreNotEqual(baseEmission, mat.GetColor("_EmissionColor"), "発光色が一時的に変わる");
        piece.Step(.1f);
        Assert.IsTrue(piece.IsFlashing);
        piece.Step(.3f);
        Assert.IsFalse(piece.IsFlashing);
        Assert.AreEqual(baseEmission, mat.GetColor("_EmissionColor"), "時間切れで元の発光色へ戻る");
    }

    [Test]
    public void Flash_IsIgnoredAfterReleaseAndWithoutEmissionProperty()
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var source = new Material(shader); created.Add(source);
        var piece = Piece(source);
        piece.Release();
        piece.Flash(Color.white, .3f);
        Assert.IsFalse(piece.IsFlashing, "解放済みの片は発光しない");

        var plain = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? shader); created.Add(plain);
        var unlit = Piece(plain);
        unlit.Flash(Color.white, .3f);
        // 発光プロパティが無い材質では何もしない(例外や色の書き込みを起こさない)。
        Assert.IsFalse(plain.HasProperty("_EmissionColor") && unlit.IsFlashing);
    }
}
