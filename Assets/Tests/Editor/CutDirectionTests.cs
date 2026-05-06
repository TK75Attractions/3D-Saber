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
}
