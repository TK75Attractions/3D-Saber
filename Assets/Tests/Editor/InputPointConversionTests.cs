using NUnit.Framework;
using UnityEngine;

// InputPoint の座標変換純関数の検証。
// 「棒2だけ direct モードでピクセル→正規化変換が抜けていて画面隅に張り付く」バグの回帰防止。
// 現在は棒1・棒2とも CanonicalizePoint / Normalized01 を通るため、対称性は構造的に保証される。
public class InputPointConversionTests
{
    const float W = 1920f;
    const float H = 1080f;

    // ---- direct world mapping モード(本番構成: EnsureInputPoint が有効化) ----

    [Test]
    public void Canonicalize_Direct_PixelInput_IsNormalized()
    {
        // これが落ちていたのが棒2バグ。ピクセル(960,540)=画面中央 → 正規化(0,0)
        Vector2 center = InputPoint.CanonicalizePoint(960f, 540f, W, H, true);
        Assert.AreEqual(0f, center.x, 1e-4f);
        Assert.AreEqual(0f, center.y, 1e-4f);

        Vector2 corner = InputPoint.CanonicalizePoint(1920f, 1080f, W, H, true);
        Assert.AreEqual(1f, corner.x, 1e-4f);
        Assert.AreEqual(1f, corner.y, 1e-4f);

        Vector2 origin = InputPoint.CanonicalizePoint(0f, 0f, W, H, true);
        Assert.AreEqual(0f, origin.x, 1e-4f, "(0,0)は正規化扱い(|v|<=1.5)でそのまま");
    }

    [Test]
    public void Canonicalize_Direct_NormalizedInput_PassesThrough()
    {
        Vector2 v = InputPoint.CanonicalizePoint(0.5f, -0.75f, W, H, true);
        Assert.AreEqual(0.5f, v.x, 1e-4f);
        Assert.AreEqual(-0.75f, v.y, 1e-4f);
    }

    // ---- legacy モード(boardRect 経由の旧チェーン) ----

    [Test]
    public void Canonicalize_Legacy_NormalizedInput_BecomesPixels()
    {
        Vector2 v = InputPoint.CanonicalizePoint(0f, 0f, W, H, false);
        Assert.AreEqual(960f, v.x, 1e-2f);
        Assert.AreEqual(540f, v.y, 1e-2f);

        Vector2 max = InputPoint.CanonicalizePoint(1f, 1f, W, H, false);
        Assert.AreEqual(1920f, max.x, 1e-2f);
        Assert.AreEqual(1080f, max.y, 1e-2f);
    }

    [Test]
    public void Canonicalize_Legacy_PixelInput_PassesThrough()
    {
        Vector2 v = InputPoint.CanonicalizePoint(462f, 891f, W, H, false);
        Assert.AreEqual(462f, v.x, 1e-3f);
        Assert.AreEqual(891f, v.y, 1e-3f);
    }

    // ---- 判別は「点単位」(片軸だけ小さくてもピクセル点として扱う) ----

    [Test]
    public void Canonicalize_MixedMagnitudePoint_IsTreatedAsPixels()
    {
        // x=0.5 だが y=891 → この点はピクセル座標とみなす(棒1の従来仕様を踏襲)
        Vector2 v = InputPoint.CanonicalizePoint(0.5f, 891f, W, H, true);
        Assert.AreEqual((0.5f / W) * 2f - 1f, v.x, 1e-4f);
        Assert.AreEqual((891f / H) * 2f - 1f, v.y, 1e-4f);
    }

    // ---- NormalizedPosition(0..1) ----

    [Test]
    public void Normalized01_BothInputKinds()
    {
        Vector2 fromNorm = InputPoint.Normalized01(-1f, 1f, W, H);
        Assert.AreEqual(0f, fromNorm.x, 1e-4f);
        Assert.AreEqual(1f, fromNorm.y, 1e-4f);

        Vector2 fromPixel = InputPoint.Normalized01(960f, 270f, W, H);
        Assert.AreEqual(0.5f, fromPixel.x, 1e-4f);
        Assert.AreEqual(0.25f, fromPixel.y, 1e-4f);
    }
}
