using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class TitleTargetProximityTests
{
    [Test]
    public void OnlyFreshNearbyColorsRespond()
    {
        Assert.AreEqual(SaberHand.Right, TitleTargetProximity.Choose(40, .2, 41, 0));
        Assert.AreEqual(SaberHand.Any, TitleTargetProximity.Choose(0, .20001, 0, 1));
        Assert.AreEqual(SaberHand.Left, TitleTargetProximity.Choose(0, .21, 30, .1));
        Assert.AreEqual(SaberHand.Left, TitleTargetProximity.Choose(20, .1, 10, .1));
        Assert.AreEqual(SaberHand.Right, TitleTargetProximity.Choose(10, .1, 10, .1));
        Assert.AreEqual(SaberHand.Any, TitleTargetProximity.Choose(0, -1, 0, double.PositiveInfinity));
    }

    [Test]
    public void WholeBladeAndRoundedCornerDistanceAreUsed()
    {
        Rect r = TitleTargetProximity.Outline;
        Assert.AreEqual(40, TitleTargetProximity.Distance(r, new Vector2(151, -200), new Vector2(151, 200)), .001);
        Assert.AreEqual(0, TitleTargetProximity.Distance(r, new Vector2(-500, 0), new Vector2(500, 0)), .001);
        Assert.Greater(TitleTargetProximity.Distance(r, new Vector2(141, 130), new Vector2(150, 140)), 40);
    }

    [Test]
    public void CornersMoveEightPixelsAndGlowReturnsInTwoTenths()
    {
        var root = new GameObject("Target", typeof(RectTransform));
        try
        {
            var glow = new GameObject("Glow", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            glow.transform.SetParent(root.transform, false);
            var edges = new RectTransform[8];
            var original = new Vector2[8];
            int i = 0;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int axis = 0; axis < 2; axis++)
            {
                edges[i] = new GameObject("TargetEdge", typeof(RectTransform)).GetComponent<RectTransform>();
                edges[i].SetParent(root.transform, false);
                original[i] = new Vector2(x * (axis == 0 ? 97.5f : 111), y * (axis == 1 ? 86.5f : 100));
                edges[i].anchoredPosition = original[i];
                i++;
            }
            var view = new TitleTargetProximity((RectTransform)root.transform, glow);
            view.Apply(SaberHand.Right, .075f);
            Assert.AreEqual(.09f, glow.color.a, .001);
            view.Apply(SaberHand.Right, .075f);
            for (i = 0; i < edges.Length; i++)
                Assert.Less(Vector2.Distance(edges[i].anchoredPosition, original[i]
                    - new Vector2(Mathf.Sign(original[i].x), Mathf.Sign(original[i].y)) * 8), .001);
            Assert.AreEqual(.18f, glow.color.a, .001);
            Assert.AreEqual(UISkinPalette.LogoRed.r, glow.color.r);
            view.Apply(SaberHand.Left, 0);
            Assert.AreEqual(UISkinPalette.LogoBlue.b, glow.color.b);
            view.Apply(SaberHand.Any, .1f);
            Assert.AreEqual(.09f, glow.color.a, .001);
            view.Apply(SaberHand.Any, .1f);
            Assert.AreEqual(0, glow.color.a, .001);
            for (i = 0; i < edges.Length; i++) Assert.AreEqual(original[i], edges[i].anchoredPosition);
        }
        finally { Object.DestroyImmediate(root); }
    }
}
