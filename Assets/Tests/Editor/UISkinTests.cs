using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class UISkinTests
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

    private Canvas MakeCanvas()
    {
        var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
        created.Add(go);
        return go.GetComponent<Canvas>();
    }

    [Test]
    public void Backdrop_Ensure_AddsBackdropAsFirstSibling()
    {
        var canvas = MakeCanvas();
        // 既存 UI を1個入れて、backdrop が前に挿入されることを確認
        var ui = new GameObject("ExistingUI", typeof(RectTransform));
        ui.transform.SetParent(canvas.transform, false);

        var backdrop = CyberBackdrop.Ensure(canvas);
        Assert.IsNotNull(backdrop);
        Assert.AreEqual(0, backdrop.transform.GetSiblingIndex(),
            "backdrop は Canvas の最初の子であるべき");
    }

    [Test]
    public void Backdrop_Ensure_IsIdempotent()
    {
        var canvas = MakeCanvas();
        var a = CyberBackdrop.Ensure(canvas);
        var b = CyberBackdrop.Ensure(canvas);
        Assert.AreSame(a, b);
        // 子の中に CyberBackdrop が1つだけ
        var found = canvas.GetComponentsInChildren<CyberBackdrop>(true);
        Assert.AreEqual(1, found.Length);
    }

    [Test]
    public void Backdrop_Build_CreatesGradientAndScanLinesAndGlow()
    {
        var canvas = MakeCanvas();
        var backdrop = CyberBackdrop.Ensure(canvas);
        Assert.IsNotNull(backdrop.transform.Find("Gradient"));
        Assert.IsNotNull(backdrop.transform.Find("ScanLines"));
        Assert.IsNotNull(backdrop.transform.Find("GlowLine"));
    }

    [Test]
    public void TitleSceneSkin_FindTextByContent_ReturnsMatch()
    {
        var canvas = MakeCanvas();
        var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(canvas.transform, false);
        var t = go.GetComponent<Text>();
        t.text = "3D SABER";

        var found = TitleSceneSkin.FindTextByContent(canvas, "3D SABER");
        Assert.AreSame(t, found);
    }

    [Test]
    public void TitleSceneSkin_FindTextByContent_NotFound_ReturnsNull()
    {
        var canvas = MakeCanvas();
        var found = TitleSceneSkin.FindTextByContent(canvas, "Nothing");
        Assert.IsNull(found);
    }
}
