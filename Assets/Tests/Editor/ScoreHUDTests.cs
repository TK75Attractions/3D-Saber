using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class ScoreHUDTests
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

    private ScoreHUD MakeHud()
    {
        var go = new GameObject("hud");
        created.Add(go);
        return go.AddComponent<ScoreHUD>();
    }

    [Test]
    public void ColorByComboTier_WhiteUnder10()
    {
        var hud = MakeHud();
        var c = hud.ColorByComboTier(0);
        Assert.That(c.r, Is.GreaterThan(0.8f));
        Assert.That(c.g, Is.GreaterThan(0.8f));
    }

    [Test]
    public void ColorByComboTier_CyanAt10()
    {
        var hud = MakeHud();
        var c = hud.ColorByComboTier(10);
        // シアン = R 低、G 高、B 高
        Assert.That(c.r, Is.LessThan(0.5f));
        Assert.That(c.g, Is.GreaterThan(0.8f));
        Assert.That(c.b, Is.GreaterThan(0.8f));
    }

    [Test]
    public void ColorByComboTier_YellowAt30()
    {
        var hud = MakeHud();
        var c = hud.ColorByComboTier(30);
        // 黄 = R 高、G 高、B 低
        Assert.That(c.r, Is.GreaterThan(0.8f));
        Assert.That(c.g, Is.GreaterThan(0.8f));
        Assert.That(c.b, Is.LessThan(0.5f));
    }

    [Test]
    public void ColorByComboTier_OrangeAt60()
    {
        var hud = MakeHud();
        var c = hud.ColorByComboTier(60);
        Assert.That(c.r, Is.GreaterThan(0.8f));
        Assert.That(c.g, Is.LessThan(0.7f));
        Assert.That(c.g, Is.GreaterThan(0.3f));
        Assert.That(c.b, Is.LessThan(0.5f));
    }

    [Test]
    public void ColorByComboTier_MagentaAt100()
    {
        var hud = MakeHud();
        var c = hud.ColorByComboTier(100);
        // マゼンタ = R 高、G 低、B 中〜高
        Assert.That(c.r, Is.GreaterThan(0.8f));
        Assert.That(c.g, Is.LessThan(0.5f));
        Assert.That(c.b, Is.GreaterThan(0.6f));
    }

    [Test]
    public void ColorByComboTier_TiersAreMonotonicByCount()
    {
        var hud = MakeHud();
        var c9 = hud.ColorByComboTier(9);
        var c10 = hud.ColorByComboTier(10);
        var c29 = hud.ColorByComboTier(29);
        var c30 = hud.ColorByComboTier(30);
        // 段階で確実に色が変わっている
        Assert.That(c9, Is.Not.EqualTo(c10));
        Assert.That(c29, Is.Not.EqualTo(c30));
    }
}
