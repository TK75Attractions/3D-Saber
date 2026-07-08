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
    public void Backdrop_DefaultsToMinimal_GradientGlowVignetteOnly()
    {
        // 既定はミニマル:グラデ + 中央グロー + ビネットのみ。テンプレ要素は生成しない。
        var canvas = MakeCanvas();
        var backdrop = CyberBackdrop.Ensure(canvas);
        Assert.AreEqual(BackdropStyle.Minimal, backdrop.style);
        Assert.IsNotNull(backdrop.transform.Find("Gradient"), "グラデはある");
        Assert.IsNotNull(backdrop.transform.Find("CenterGlow"), "中央グローはある");
        Assert.IsNotNull(backdrop.transform.Find("Vignette"), "ビネットはある");
        // 「デモ感」の主因だったテンプレ要素が無いこと
        Assert.IsNull(backdrop.transform.Find("ScanLines"), "スキャンラインは無い");
        Assert.IsNull(backdrop.transform.Find("GlowLine"), "動く光線は無い");
        Assert.IsNull(backdrop.transform.Find("HorizonGrid"), "床グリッドは無い");
        Assert.IsNull(backdrop.transform.Find("Beams"), "斜め光線は無い");
        Assert.IsNull(backdrop.transform.Find("Particles"), "浮遊ドットは無い");
    }

    [Test]
    public void Backdrop_CyberStyle_CreatesTemplateElements()
    {
        // Cyber は明示オプトインで従来の盛りだくさん構成
        var canvas = MakeCanvas();
        var backdrop = CyberBackdrop.Ensure(canvas, BackdropStyle.Cyber);
        Assert.AreEqual(BackdropStyle.Cyber, backdrop.style);
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
