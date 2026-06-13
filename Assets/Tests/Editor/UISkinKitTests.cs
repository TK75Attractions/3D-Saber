using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class UISkinKitTests
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

    private Transform MakeCanvasRoot()
    {
        var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
        created.Add(go);
        return go.transform;
    }

    // ---- スプライト生成 ----

    [Test]
    public void RoundedRect_IsCachedSingleton()
    {
        var a = UISkinKit.RoundedRect();
        var b = UISkinKit.RoundedRect();
        Assert.IsNotNull(a);
        Assert.AreSame(a, b, "2回目以降はキャッシュを返す");
    }

    [Test]
    public void RoundedRect_HasNineSliceBorder()
    {
        var sp = UISkinKit.RoundedRect();
        Assert.Greater(sp.border.x, 0f, "9スライス境界が設定されている");
        Assert.Greater(sp.border.y, 0f);
    }

    [Test]
    public void RoundedFrame_IsDistinctFromFill()
    {
        Assert.AreNotSame(UISkinKit.RoundedRect(), UISkinKit.RoundedFrame());
    }

    [Test]
    public void SoftGlow_And_Vignette_AreCreated()
    {
        Assert.IsNotNull(UISkinKit.SoftGlow());
        Assert.IsNotNull(UISkinKit.Vignette());
        Assert.AreSame(UISkinKit.SoftGlow(), UISkinKit.SoftGlow());
    }

    // ---- ロゴフォント ----

    [Test]
    public void LogoFontAsset_LoadsFromResources_AndCaches()
    {
        var a = UISkinKit.LogoFontAsset();
        Assert.IsNotNull(a, "Resources/Fonts/ChakraPetch-BoldItalic から TMP フォントが生成される");
        Assert.AreSame(a, UISkinKit.LogoFontAsset(), "2回目以降はキャッシュを返す");
    }

    [Test]
    public void ApplyLogoGradient_SetsVertexGradientAndOutline()
    {
        var root = MakeCanvasRoot();
        var t = UISkinKit.MakeTMP(root, "LogoTest", "BEAT", 100f, Color.red,
            TMPro.TextAlignmentOptions.Center, Vector2.zero, new Vector2(400f, 120f));
        TitleSceneSkin.ApplyLogoGradient(t, Color.red);

        Assert.IsTrue(t.enableVertexGradient);
        Assert.AreNotEqual(t.colorGradient.topLeft, t.colorGradient.bottomLeft,
            "上下で色が変わるグラデーション");
        Assert.Greater(t.outlineWidth, 0f, "暗い縁取りが入る");
    }

    // ---- ASCII 判定(ロゴフォント適用可否) ----

    [Test]
    public void IsAsciiOnly_DetectsNonAscii()
    {
        Assert.IsTrue(UISkinKit.IsAsciiOnly("ElDorado"));
        Assert.IsTrue(UISkinKit.IsAsciiOnly("Mix-01 (feat. X)"));
        Assert.IsTrue(UISkinKit.IsAsciiOnly(""));
        Assert.IsTrue(UISkinKit.IsAsciiOnly(null));
        Assert.IsFalse(UISkinKit.IsAsciiOnly("日本語の曲名"));
        Assert.IsFalse(UISkinKit.IsAsciiOnly("Mixé"));
    }

    [Test]
    public void MakeNeonButton_AsciiLabel_UsesLogoFont()
    {
        var root = MakeCanvasRoot();
        var parts = UISkinKit.MakeNeonButton(root, "TestBtn", "START",
            Vector2.zero, new Vector2(200f, 60f), Color.cyan, null);
        var logo = UISkinKit.LogoFontAsset();
        if (logo != null)
        {
            Assert.AreSame(logo, parts.label.font, "ASCII ラベルはロゴフォントで描画される");
        }
        else
        {
            Assert.IsNotNull(parts.label.font, "フォールバック時も既定フォントが入る");
        }
    }

    // ---- ボタンファクトリ ----

    [Test]
    public void MakeNeonButton_BuildsAllParts()
    {
        var root = MakeCanvasRoot();
        bool clicked = false;
        var parts = UISkinKit.MakeNeonButton(root, "TestBtn", "PLAY",
            Vector2.zero, new Vector2(200f, 60f), Color.cyan, () => clicked = true);

        Assert.IsNotNull(parts.button);
        Assert.IsNotNull(parts.fill);
        Assert.IsNotNull(parts.frame);
        Assert.IsNotNull(parts.glow);
        Assert.IsNotNull(parts.label);
        Assert.AreEqual("PLAY", parts.label.text);
        Assert.IsNotNull(parts.button.GetComponent<UIHoverEffect>(), "ホバー演出が付く");
        Assert.AreEqual(0f, parts.glow.color.a, 0.001f, "グローは初期非表示");

        parts.button.onClick.Invoke();
        Assert.IsTrue(clicked, "onClick が配線されている");
    }

    [Test]
    public void MakeNeonButton_FillUsesSlicedRoundedSprite()
    {
        var root = MakeCanvasRoot();
        var parts = UISkinKit.MakeNeonButton(root, "TestBtn", "GO",
            Vector2.zero, new Vector2(100f, 40f), Color.magenta, null);
        Assert.AreSame(UISkinKit.RoundedRect(), parts.fill.sprite);
        Assert.AreEqual(Image.Type.Sliced, parts.fill.type);
    }

    [Test]
    public void RestyleButton_DisablesLegacyText_AndCopiesLabel()
    {
        var root = MakeCanvasRoot();
        // 旧スタイルのボタン(Image + Button + 子の legacy Text)を再現
        var btnGo = new GameObject("LegacyBtn", typeof(RectTransform), typeof(Image), typeof(Button));
        btnGo.transform.SetParent(root, false);
        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
        labelGo.transform.SetParent(btnGo.transform, false);
        labelGo.GetComponent<Text>().text = "QUIT";

        var parts = UISkinKit.RestyleButton(btnGo.GetComponent<Button>(), Color.magenta);

        Assert.IsFalse(labelGo.activeSelf, "旧 legacy Text は無効化される");
        Assert.AreEqual("QUIT", parts.label.text, "ラベル文字列は引き継がれる");
        Assert.AreSame(btnGo.GetComponent<Button>(), parts.button, "Button コンポーネントは同一のまま");
    }

    [Test]
    public void RestyleButton_RemovesLegacyOutline()
    {
        var root = MakeCanvasRoot();
        var btnGo = new GameObject("LegacyBtn", typeof(RectTransform), typeof(Image), typeof(Button));
        btnGo.transform.SetParent(root, false);
        btnGo.AddComponent<Outline>();

        UISkinKit.RestyleButton(btnGo.GetComponent<Button>(), Color.cyan);

        Assert.IsNull(btnGo.GetComponent<Outline>(), "旧 Outline 枠は撤去される");
    }
}
