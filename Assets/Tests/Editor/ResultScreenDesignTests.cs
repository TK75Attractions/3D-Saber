using NUnit.Framework;
using UnityEngine;

// リザルト画面(デザインハンドオフ 6a 準拠版)の純関数の検証。
public class ResultScreenDesignTests
{
    // ---- 曲名整形 ----

    [Test]
    public void SongIdToDisplayTitle_SplitsCamelCaseAndUppercases()
    {
        Assert.AreEqual("EL DORADO", ResultSkin.SongIdToDisplayTitle("ElDorado"));
    }

    [Test]
    public void SongIdToDisplayTitle_TreatsSeparatorsAsSpaces()
    {
        Assert.AreEqual("NEO TOKYO RUSH", ResultSkin.SongIdToDisplayTitle("neo_tokyo-rush"));
    }

    [Test]
    public void SongIdToDisplayTitle_AllCapsStaysJoined()
    {
        Assert.AreEqual("ELDORADO", ResultSkin.SongIdToDisplayTitle("ELDORADO"));
    }

    [Test]
    public void SongIdToDisplayTitle_EmptyAndNullAreSafe()
    {
        Assert.AreEqual("", ResultSkin.SongIdToDisplayTitle(""));
        Assert.AreEqual("", ResultSkin.SongIdToDisplayTitle(null));
    }

    // ---- 判定分布バー ----

    [Test]
    public void DistributionWidths_ProportionalAndSumsToTotal()
    {
        float[] w = ResultSkin.DistributionWidths(new[] { 3, 1 }, 400f);
        Assert.AreEqual(300f, w[0], 0.01f);
        Assert.AreEqual(100f, w[1], 0.01f);
    }

    [Test]
    public void DistributionWidths_AllZeroCounts_GivesZeroWidths()
    {
        float[] w = ResultSkin.DistributionWidths(new[] { 0, 0, 0 }, 400f);
        foreach (float x in w) Assert.AreEqual(0f, x, 1e-4f);
    }

    [Test]
    public void DistributionWidths_NullCounts_GivesEmpty()
    {
        Assert.AreEqual(0, ResultSkin.DistributionWidths(null, 400f).Length);
    }

    // ---- ランク色マップ ----

    [Test]
    public void RankAccentColor_MatchesHandoffSpec()
    {
        Assert.AreEqual(UISkinPalette.Cyan, ResultSkin.RankAccentColor("S+"));
        Assert.AreEqual(UISkinPalette.LogoBlue, ResultSkin.RankAccentColor("S"));
        Assert.AreEqual(UISkinPalette.LogoRed, ResultSkin.RankAccentColor("A"));
        Assert.AreEqual(UISkinPalette.LogoGreen, ResultSkin.RankAccentColor("B"));
        Assert.AreEqual(UISkinPalette.NoteFlick, ResultSkin.RankAccentColor("C"));
    }

    // ---- 六角形バッジの頂点 ----

    [Test]
    public void HexPoints_MatchesClipPathPolygon()
    {
        // polygon(50% 0, 100% 25%, 100% 75%, 50% 100%, 0 75%, 0 25%) 相当(中心原点)
        Vector2[] p = ResultHexBadgeGraphic.HexPoints(400f, 440f);
        Assert.AreEqual(6, p.Length);
        Assert.AreEqual(new Vector2(0f, 220f), p[0]);
        Assert.AreEqual(new Vector2(200f, 110f), p[1]);
        Assert.AreEqual(new Vector2(200f, -110f), p[2]);
        Assert.AreEqual(new Vector2(0f, -220f), p[3]);
        Assert.AreEqual(new Vector2(-200f, -110f), p[4]);
        Assert.AreEqual(new Vector2(-200f, 110f), p[5]);
    }

    // ---- 床グリッド ----

    [Test]
    public void FloorGrid_FadeAlpha_TransparentAtTopOpaqueBelowMask()
    {
        Assert.AreEqual(0f, ResultFloorGridGraphic.FadeAlpha(1f), 1e-4f, "上端は透明");
        Assert.AreEqual(1f, ResultFloorGridGraphic.FadeAlpha(0f), 1e-4f, "下端は不透明");
        Assert.AreEqual(1f, ResultFloorGridGraphic.FadeAlpha(0.45f), 1e-3f, "マスク境界(55%)で不透明");
    }

    [Test]
    public void FloorGrid_HorizontalLines_CompressTowardHorizon()
    {
        // 下(手前)ほど間隔が広く、上(地平線)ほど密になる
        float u0 = ResultFloorGridGraphic.HorizontalLineU(0, 10, 2.2f);
        float u1 = ResultFloorGridGraphic.HorizontalLineU(1, 10, 2.2f);
        float u9 = ResultFloorGridGraphic.HorizontalLineU(9, 10, 2.2f);
        float u10 = ResultFloorGridGraphic.HorizontalLineU(10, 10, 2.2f);
        Assert.AreEqual(0f, u0, 1e-4f);
        Assert.AreEqual(1f, u10, 1e-4f);
        Assert.Greater(u1 - u0, u10 - u9, "手前の間隔 > 地平線際の間隔");
    }
}
