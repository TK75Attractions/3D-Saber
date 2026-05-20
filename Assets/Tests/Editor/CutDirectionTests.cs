using NUnit.Framework;
using UnityEngine;

public class CutDirectionTests
{
    [Test]
    public void Parse_KnownStrings()
    {
        Assert.AreEqual(CutDirection.Up, CutDirectionHelper.Parse("up"));
        Assert.AreEqual(CutDirection.Down, CutDirectionHelper.Parse("Down"));
        Assert.AreEqual(CutDirection.UpLeft, CutDirectionHelper.Parse("up-left"));
        Assert.AreEqual(CutDirection.UpRight, CutDirectionHelper.Parse("UP_RIGHT"));
        Assert.AreEqual(CutDirection.None, CutDirectionHelper.Parse("none"));
        Assert.AreEqual(CutDirection.None, CutDirectionHelper.Parse(""));
        Assert.AreEqual(CutDirection.None, CutDirectionHelper.Parse(null));
        Assert.AreEqual(CutDirection.None, CutDirectionHelper.Parse("garbage"));
    }

    [Test]
    public void Matches_NoneAlwaysMatches()
    {
        Assert.IsTrue(CutDirectionHelper.Matches(CutDirection.None, new Vector2(1, 0)));
        Assert.IsTrue(CutDirectionHelper.Matches(CutDirection.None, Vector2.zero));
    }

    [Test]
    public void Matches_RightDirection()
    {
        Assert.IsTrue(CutDirectionHelper.Matches(CutDirection.Right, new Vector2(5, 0)));
        Assert.IsTrue(CutDirectionHelper.Matches(CutDirection.Right, new Vector2(3, 1))); // ほぼ右
        Assert.IsFalse(CutDirectionHelper.Matches(CutDirection.Right, new Vector2(-1, 0)));
        Assert.IsFalse(CutDirectionHelper.Matches(CutDirection.Right, new Vector2(0, 1)));
    }

    [Test]
    public void Matches_DiagonalDirection()
    {
        Assert.IsTrue(CutDirectionHelper.Matches(CutDirection.UpRight, new Vector2(1, 1)));
        Assert.IsFalse(CutDirectionHelper.Matches(CutDirection.UpRight, new Vector2(-1, 1)));
    }

    [Test]
    public void Matches_ZeroVelocityFails()
    {
        Assert.IsFalse(CutDirectionHelper.Matches(CutDirection.Right, Vector2.zero));
    }

    [Test]
    public void Matches_StrictTolerance_45DegOff_Fails()
    {
        // 45度ずれた切り（Right 要求に対して UpRight 方向の振り）はもはや通らない
        Assert.IsFalse(CutDirectionHelper.Matches(CutDirection.Right, new Vector2(1, 1)));
    }

    [Test]
    public void Matches_StrictTolerance_NearlyCorrect_Passes()
    {
        // 約 18°ずれは許容範囲内
        Assert.IsTrue(CutDirectionHelper.Matches(CutDirection.Right, new Vector2(3, 1)));
    }

    // -- ShouldRejectOpposite --

    [Test]
    public void ShouldRejectOpposite_None_NeverRejects()
    {
        Assert.IsFalse(CutDirectionHelper.ShouldRejectOpposite(CutDirection.None, new Vector2(-1, 0), CutDirection.None));
    }

    [Test]
    public void ShouldRejectOpposite_OppositeVelocity_Rejects()
    {
        // 右要求に対して左方向（180°）
        Assert.IsTrue(CutDirectionHelper.ShouldRejectOpposite(CutDirection.Right, new Vector2(-1, 0), CutDirection.None));
    }

    [Test]
    public void ShouldRejectOpposite_Perpendicular_DoesNotReject()
    {
        // 右要求に対して上方向（90°）→ 横方向、降格カットで通すべき
        Assert.IsFalse(CutDirectionHelper.ShouldRejectOpposite(CutDirection.Right, new Vector2(0, 1), CutDirection.None));
        Assert.IsFalse(CutDirectionHelper.ShouldRejectOpposite(CutDirection.Right, new Vector2(0, -1), CutDirection.None));
    }

    [Test]
    public void ShouldRejectOpposite_SlightlyOff_DoesNotReject()
    {
        // 右要求に対して斜め右上、もちろん通す
        Assert.IsFalse(CutDirectionHelper.ShouldRejectOpposite(CutDirection.Right, new Vector2(1, 1), CutDirection.None));
    }

    [Test]
    public void ShouldRejectOpposite_ImuRescuesOpposite()
    {
        // 速度は左だが IMU が「右」と検知 → レスキューで通す
        Assert.IsFalse(CutDirectionHelper.ShouldRejectOpposite(CutDirection.Right, new Vector2(-1, 0), CutDirection.Right));
    }

    [Test]
    public void ShouldRejectOpposite_ZeroVelocity_DoesNotReject()
    {
        // 速度ゼロは方向判定不能 → 拒否しない（誤拒否回避）
        Assert.IsFalse(CutDirectionHelper.ShouldRejectOpposite(CutDirection.Right, Vector2.zero, CutDirection.None));
    }
}
