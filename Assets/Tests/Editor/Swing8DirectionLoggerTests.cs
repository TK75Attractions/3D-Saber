using NUnit.Framework;
using UnityEngine;

public class Swing8DirectionLoggerTests
{
    [Test]
    public void Get8DirectionIndex_RightIsZero()
    {
        Assert.AreEqual(0, Swing8DirectionLogger.Get8DirectionIndex(new Vector2(1, 0)));
    }

    [Test]
    public void Get8DirectionIndex_UpIsTwo()
    {
        Assert.AreEqual(2, Swing8DirectionLogger.Get8DirectionIndex(new Vector2(0, 1)));
    }

    [Test]
    public void Get8DirectionIndex_DiagonalUpRightIsOne()
    {
        Assert.AreEqual(1, Swing8DirectionLogger.Get8DirectionIndex(new Vector2(1, 1)));
    }

    [Test]
    public void FromSwing8Index_MatchesEnum()
    {
        Assert.AreEqual(CutDirection.Right, CutDirectionHelper.FromSwing8Index(0));
        Assert.AreEqual(CutDirection.UpRight, CutDirectionHelper.FromSwing8Index(1));
        Assert.AreEqual(CutDirection.Up, CutDirectionHelper.FromSwing8Index(2));
        Assert.AreEqual(CutDirection.DownRight, CutDirectionHelper.FromSwing8Index(7));
        Assert.AreEqual(CutDirection.None, CutDirectionHelper.FromSwing8Index(99));
    }

    [Test]
    public void TryGetLatest_NoInstance_ReturnsFalse()
    {
        // 直前テストで Instance が残っていないことを確認しつつ既定挙動を検証。
        foreach (var l in Object.FindObjectsByType<Swing8DirectionLogger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Object.DestroyImmediate(l.gameObject);
        }
        bool ok = Swing8DirectionLogger.TryGetLatest(out CutDirection d, out float t);
        Assert.IsFalse(ok);
        Assert.AreEqual(CutDirection.None, d);
    }
}
