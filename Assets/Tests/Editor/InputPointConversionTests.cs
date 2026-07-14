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

    // ---- 感度(中央基準で移動量を増幅。既定2倍) ----

    [Test]
    public void Sensitivity_Direct_DoublesAroundCenter()
    {
        // 中心0基準: (0.25, -0.3) → 2倍 → (0.5, -0.6)
        Vector2 v = InputPoint.ApplySensitivity(new Vector2(0.25f, -0.3f), 2f, true, W, H);
        Assert.AreEqual(0.5f, v.x, 1e-4f);
        Assert.AreEqual(-0.6f, v.y, 1e-4f);

        // 中央は動かない
        Vector2 c = InputPoint.ApplySensitivity(Vector2.zero, 2f, true, W, H);
        Assert.AreEqual(0f, c.x, 1e-4f);
        Assert.AreEqual(0f, c.y, 1e-4f);
    }

    [Test]
    public void Sensitivity_Direct_ClampsAtEdge()
    {
        // 2倍で ±1 を超える分は画面端でクランプ(画面外へ飛ばさない)
        Vector2 v = InputPoint.ApplySensitivity(new Vector2(0.8f, -0.9f), 2f, true, W, H);
        Assert.AreEqual(1f, v.x, 1e-4f);
        Assert.AreEqual(-1f, v.y, 1e-4f);
    }

    [Test]
    public void Sensitivity_One_IsIdentity()
    {
        Vector2 v = InputPoint.ApplySensitivity(new Vector2(0.37f, -0.62f), 1f, true, W, H);
        Assert.AreEqual(0.37f, v.x, 1e-4f);
        Assert.AreEqual(-0.62f, v.y, 1e-4f);

        Vector2 p = InputPoint.ApplySensitivity(new Vector2(462f, 891f), 1f, false, W, H);
        Assert.AreEqual(462f, p.x, 1e-3f);
        Assert.AreEqual(891f, p.y, 1e-3f);
    }

    [Test]
    public void Sensitivity_Legacy_ScalesAroundPixelCenter()
    {
        // legacy はピクセル中心 (960, 540) 基準。(1200, 270) → (960+240*2, 540-270*2) = (1440, 0)
        Vector2 v = InputPoint.ApplySensitivity(new Vector2(1200f, 270f), 2f, false, W, H);
        Assert.AreEqual(1440f, v.x, 1e-2f);
        Assert.AreEqual(0f, v.y, 1e-2f);

        // 画面外はクランプ
        Vector2 e = InputPoint.ApplySensitivity(new Vector2(1800f, 1000f), 2f, false, W, H);
        Assert.AreEqual(1920f, e.x, 1e-2f);
        Assert.AreEqual(1080f, e.y, 1e-2f);
    }

    [Test]
    public void Sensitivity01_ScalesAroundHalf()
    {
        // NormalizedPosition(0..1)は 0.5 中心。(0.6, 0.25) → (0.7, 0.0)
        Vector2 v = InputPoint.ApplySensitivity01(new Vector2(0.6f, 0.25f), 2f);
        Assert.AreEqual(0.7f, v.x, 1e-4f);
        Assert.AreEqual(0f, v.y, 1e-4f);

        // クランプ確認
        Vector2 e = InputPoint.ApplySensitivity01(new Vector2(0.9f, 0.05f), 2f);
        Assert.AreEqual(1f, e.x, 1e-4f);
        Assert.AreEqual(0f, e.y, 1e-4f);
    }

    [Test]
    public void Sensitivity_StickEndpoints_TranslateWithMidKeepingLength()
    {
        // 端点は「中点の移動量」で平行移動する仕様 → 棒の長さが変わらないことを検証。
        // mid=(0.3,0), 棒は水平で長さ0.4 (endA=0.1, endB=0.5)
        Vector2 mid = new Vector2(0.3f, 0f);
        Vector2 endA = new Vector2(0.1f, 0f);
        Vector2 endB = new Vector2(0.5f, 0f);

        Vector2 sensMid = InputPoint.ApplySensitivity(mid, 2f, true, W, H);
        Vector2 delta = sensMid - mid;
        Vector2 movedA = endA + delta;
        Vector2 movedB = endB + delta;

        // 中点は2倍の位置へ
        Assert.AreEqual(0.6f, sensMid.x, 1e-4f);
        // 平行移動後も棒長は 0.4 のまま(端点ごとにスケールすると 0.8 になってしまう)
        Assert.AreEqual(0.4f, Vector2.Distance(movedA, movedB), 1e-4f);
        // 移動後の端点の中点 = 感度適用後の中点
        Vector2 movedMid = (movedA + movedB) * 0.5f;
        Assert.AreEqual(sensMid.x, movedMid.x, 1e-4f);
        Assert.AreEqual(sensMid.y, movedMid.y, 1e-4f);
    }
}
