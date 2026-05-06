using NUnit.Framework;
using UnityEngine;

public class CutDirectionHintTests
{
    [Test]
    public void MatchesWithHint_VelocityMatch_OK()
    {
        Assert.IsTrue(CutDirectionHelper.MatchesWithHint(
            CutDirection.Right, new Vector2(5, 0), CutDirection.None));
    }

    [Test]
    public void MatchesWithHint_VelocityFails_HintMatches_OK()
    {
        // 速度は左向き、ヒントは Right → ヒントで救済
        Assert.IsTrue(CutDirectionHelper.MatchesWithHint(
            CutDirection.Right, new Vector2(-5, 0), CutDirection.Right));
    }

    [Test]
    public void MatchesWithHint_NeitherMatches_Fails()
    {
        Assert.IsFalse(CutDirectionHelper.MatchesWithHint(
            CutDirection.Right, new Vector2(-5, 0), CutDirection.Left));
    }

    [Test]
    public void MatchesWithHint_NoneRequired_AlwaysOK()
    {
        Assert.IsTrue(CutDirectionHelper.MatchesWithHint(
            CutDirection.None, Vector2.zero, CutDirection.None));
    }

    [Test]
    public void Note_CutWithImuHint_WrongVelocityStillCorrectIfHintMatches()
    {
        var go = new GameObject("note");
        var n = go.AddComponent<CuttableNote>();
        n.RequiredDirection = CutDirection.Up;
        // 速度は右向き（不一致）、IMU は Up（一致）
        n.Cut(Vector3.zero, new Vector3(5f, 0f, 0f), CutDirection.Up);
        Assert.IsTrue(n.LastCutCorrectDirection);
        Object.DestroyImmediate(go);
    }
}
