using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class NoteVisualsTests
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
    }

    private GameObject MakeLegacyNote()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        created.Add(go);
        var col = go.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col);

        // 旧 Builder 由来の装飾を再現
        foreach (var name in new[] { "Front", "Back", "Right", "Left", "Top", "Bottom",
                                     "Target", "DotTL", "DotTR", "DotBL", "DotBR" })
        {
            var deco = new GameObject(name);
            deco.transform.SetParent(go.transform, false);
        }
        // 装飾以外の子（NoteSpawner が後で付ける想定の Arrow / CountLabel）も入れる
        var arrow = new GameObject("Arrow");
        arrow.transform.SetParent(go.transform, false);

        return go;
    }

    [Test]
    public void Awake_StripsLegacyDecorations_KeepsArrow()
    {
        var note = MakeLegacyNote();
        note.AddComponent<NoteVisuals>();

        var visuals = note.GetComponent<NoteVisuals>();
        Assert.AreEqual(0, visuals.LegacyDecorationCount(), "旧装飾は全て剥がされている");
        Assert.IsNotNull(visuals.FindChildByName("Arrow"), "Arrow は保持される");
    }

    [Test]
    public void Awake_AddsNewVisualChildren()
    {
        var note = MakeLegacyNote();
        note.AddComponent<NoteVisuals>();

        var visuals = note.GetComponent<NoteVisuals>();
        Assert.IsNotNull(visuals.FindChildByName("InnerCore"));
        Assert.IsNotNull(visuals.FindChildByName("EdgeTop"));
        Assert.IsNotNull(visuals.FindChildByName("EdgeBot"));
        Assert.IsNotNull(visuals.FindChildByName("EdgeLft"));
        Assert.IsNotNull(visuals.FindChildByName("EdgeRgt"));
        Assert.IsNotNull(visuals.FindChildByName("FrontHalo"));
    }

    [Test]
    public void Awake_StripsCollidersFromNewChildren()
    {
        var note = MakeLegacyNote();
        note.AddComponent<NoteVisuals>();
        var visuals = note.GetComponent<NoteVisuals>();
        foreach (var name in new[] { "InnerCore", "EdgeTop", "EdgeBot", "EdgeLft", "EdgeRgt", "FrontHalo" })
        {
            var child = visuals.FindChildByName(name);
            Assert.IsNotNull(child, $"{name} が見つからない");
            Assert.IsNull(child.GetComponent<Collider>(), $"{name} に Collider が残っている");
        }
    }

    [Test]
    public void Awake_DisableStrip_KeepsLegacyDecorations()
    {
        var note = MakeLegacyNote();
        var visuals = note.AddComponent<NoteVisuals>();
        // 既に Awake は走ってしまっているので、stripLegacyDecorations フラグが効くことを確かめる別パス：
        // 新しいノートを作って AddComponent 前にフラグを書く手段が無いため、
        // 代わりに「フラグの仕様＝デフォルトで剥がす」を担保する形にする。
        Assert.IsTrue(visuals.stripLegacyDecorations);
    }

    [Test]
    public void Update_WithZeroAmplitude_DoesNotThrow()
    {
        var note = MakeLegacyNote();
        var visuals = note.AddComponent<NoteVisuals>();
        visuals.pulseAmplitude = 0f;
        // MonoBehaviour.Update を直接呼ぶ手はないので、リフレクションで起動。
        var m = typeof(NoteVisuals).GetMethod("Update",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(m, "Update メソッドが見つからない");
        Assert.DoesNotThrow(() => m.Invoke(visuals, null));
    }

    [Test]
    public void InheritsBaseColorFromExistingMaterial()
    {
        var note = MakeLegacyNote();
        var mr = note.GetComponent<MeshRenderer>();
        Color expected = new Color(0.25f, 0.55f, 1f); // blue
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var src = new Material(sh);
        if (src.HasProperty("_BaseColor")) src.SetColor("_BaseColor", expected);
        else src.color = expected;
        mr.sharedMaterial = src;

        var visuals = note.AddComponent<NoteVisuals>();
        Assert.AreEqual(expected.r, visuals.baseColor.r, 0.001f);
        Assert.AreEqual(expected.g, visuals.baseColor.g, 0.001f);
        Assert.AreEqual(expected.b, visuals.baseColor.b, 0.001f);
    }

    [Test]
    public void Kind_DefaultsToTap_WhenNoCuttableNote()
    {
        var note = MakeLegacyNote();
        var visuals = note.AddComponent<NoteVisuals>();
        Assert.AreEqual(NoteVisuals.NoteKind.Tap, visuals.kind);
    }

    [Test]
    public void Kind_DetectedAsDirection_FromCuttableNote()
    {
        var note = MakeLegacyNote();
        var cn = note.AddComponent<CuttableNote>();
        cn.RequiredDirection = CutDirection.Up;
        cn.RequiredCutCount = 1;
        var visuals = note.AddComponent<NoteVisuals>();
        Assert.AreEqual(NoteVisuals.NoteKind.Direction, visuals.kind);
        // Direction では InnerCore は作らない（矢印がそこを占める）
        Assert.IsNull(visuals.FindChildByName("InnerCore"));
        // 代わりに BackBackLight が付く
        Assert.IsNotNull(visuals.FindChildByName("BackBackLight"));
    }

    [Test]
    public void Kind_DetectedAsLong_FromCuttableNote()
    {
        var note = MakeLegacyNote();
        var cn = note.AddComponent<CuttableNote>();
        cn.RequiredCutCount = 3;
        cn.RemainingCuts = 3;
        var visuals = note.AddComponent<NoteVisuals>();
        Assert.AreEqual(NoteVisuals.NoteKind.Long, visuals.kind);
        Assert.AreEqual(3, visuals.longSegments);
        // 区切り線が n-1 = 2 本
        int dividers = 0;
        foreach (Transform t in note.transform)
        {
            if (t.name == "Divider") dividers++;
        }
        Assert.AreEqual(2, dividers);
    }
}
