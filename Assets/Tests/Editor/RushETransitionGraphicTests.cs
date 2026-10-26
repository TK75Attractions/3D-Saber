using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

// 隠し譜面へ移る幕の四角。2026-10-26 に16×9(1マス120px)から64×36(1マス30px)へ細かくし、まん中の盤から広がる向きにした。
public class RushETransitionGraphicTests
{
    const int Columns = RushETransitionGraphic.Columns, Rows = RushETransitionGraphic.Rows;

    [Test]
    public void EveryTileCoversByTheRevealAndClearsBeforeTheCurtainIsRemoved()
    {
        for (int y = 0; y < Rows; y++) for (int x = 0; x < Columns; x++)
        {
            Assert.AreEqual(0, RushETransitionGraphic.TileScale(0, x, y));
            Assert.AreEqual(1, RushETransitionGraphic.TileScale(RushESecretState.RevealSeconds, x, y), "切り替えの瞬間は全面が幕の裏");
            Assert.AreEqual(1, RushETransitionGraphic.TileScale(.93f, x, y), "ほどき始めまで覆ったまま");
            Assert.AreEqual(0, RushETransitionGraphic.TileScale(RushESecretState.TransitionSeconds, x, y), "幕を外す前に消えきる");
        }
    }

    [Test]
    public void EachTileOnlyGrowsWhileCoveringAndOnlyShrinksWhileClearing()
    {
        for (int y = 0; y < Rows; y++) for (int x = 0; x < Columns; x++)
        {
            float last = 0;
            for (int frame = 0; frame <= 111; frame++)
            {
                float t = frame / 60f, scale = RushETransitionGraphic.TileScale(t, x, y);
                if (t < .94f) Assert.GreaterOrEqual(scale, last); else Assert.LessOrEqual(scale, last);
                last = scale;
            }
        }
    }

    [Test]
    public void TilesAreFineSquaresWithinTheUiVertexLimit()
    {
        Assert.AreEqual(1920f / Columns, 1080f / Rows, "正方形のマスで16:9の画面を埋める");
        Assert.LessOrEqual(1920f / Columns, 32, "1マスは30px前後(以前の120pxはあらすぎた)");
        Assert.Less(Columns * Rows * 4, 65000, "UI の1メッシュの頂点の上限より少ない");
    }

    [Test]
    public void TheWaveSpreadsFromTheCenterDiscAndClearsFromItFirst()
    {
        Vector2 disc = SongSelectSkin.DiscSlotPosition(0);
        int dx = Mathf.FloorToInt(disc.x / (1920f / Columns) + Columns / 2f), dy = Mathf.FloorToInt(disc.y / (1080f / Rows) + Rows / 2f);
        Assert.Less(RushETransitionGraphic.Reach(dx, dy), .03f, "盤の中心のマス");
        Assert.Greater(RushETransitionGraphic.TileScale(.12f, dx, dy), 0, "盤から伸び始める");
        Assert.Less(RushETransitionGraphic.TileScale(1.10f, dx, dy), 1, "ほどけるのも盤から");
        foreach (var corner in new[] { new Vector2Int(0, 0), new Vector2Int(Columns - 1, 0), new Vector2Int(0, Rows - 1), new Vector2Int(Columns - 1, Rows - 1) })
        {
            Assert.Greater(RushETransitionGraphic.Reach(corner.x, corner.y), .8f);
            Assert.AreEqual(0, RushETransitionGraphic.TileScale(.12f, corner.x, corner.y), "画面の角はまだ覆わない");
            Assert.AreEqual(1, RushETransitionGraphic.TileScale(1.10f, corner.x, corner.y), "画面の角はまだ覆ったまま");
        }
    }

    [Test]
    public void MeshCoversTheScreenAtTheRevealInThePaletteAndIsEmptyOutsideTheTransition()
    {
        var go = new GameObject("RushETileTransition", typeof(RectTransform));
        try
        {
            var curtain = go.AddComponent<RushETransitionGraphic>();
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(1920, 1080);
            curtain.SetPalette(RushESecretState.ElDoradoG);
            Assert.AreEqual(Columns * Rows * 4, Vertices(curtain, RushESecretState.RevealSeconds, out var colors), "全部のマスを描く");
            Color32 deep = RushESecretState.ElDoradoG.TileDeep, light = RushESecretState.ElDoradoG.TileLight;
            foreach (var c in colors)
            {
                Assert.AreEqual(255, c.a);
                Assert.That((int)c.r, Is.InRange(Mathf.Min(deep.r, light.r) - 1, Mathf.Max(deep.r, light.r) + 1), "覆った面はその譜面の暗い色と明るい色の間");
                Assert.That((int)c.g, Is.InRange(Mathf.Min(deep.g, light.g) - 1, Mathf.Max(deep.g, light.g) + 1));
                Assert.That((int)c.b, Is.InRange(Mathf.Min(deep.b, light.b) - 1, Mathf.Max(deep.b, light.b) + 1));
            }
            int partway = Vertices(curtain, .3f, out _);
            Assert.Greater(partway, 0); Assert.Less(partway, Columns * Rows * 4, "途中は盤の近くだけ");
            Assert.AreEqual(0, Vertices(curtain, -1, out _));
            Assert.AreEqual(0, Vertices(curtain, RushESecretState.TransitionSeconds, out _));
        }
        finally { Object.DestroyImmediate(go); }
    }

    static int Vertices(RushETransitionGraphic curtain, float elapsed, out List<Color32> colors)
    {
        curtain.Show(elapsed);
        using (var vh = new VertexHelper())
        {
            typeof(RushETransitionGraphic).GetMethod("OnPopulateMesh", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Invoke(curtain, new object[] { vh });
            colors = new List<Color32>(); var vertex = new UIVertex();
            for (int i = 0; i < vh.currentVertCount; i++) { vh.PopulateUIVertex(ref vertex, i); colors.Add(vertex.color); }
            return vh.currentVertCount;
        }
    }
}
