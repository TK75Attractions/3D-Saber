using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class SaberRigTests
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
        // SaberRig が生成した2本目も回収する
        foreach (var j in Object.FindObjectsByType<SaberCutJudge>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (j != null) Object.DestroyImmediate(j.gameObject);
        }
    }

    private SaberCutJudge MakePrimary()
    {
        var go = new GameObject("Saber");
        created.Add(go);
        var tracker = go.AddComponent<SaberTracker>();
        var bridge = go.AddComponent<SaberInputBridge>();
        var judge = go.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.bladeProvider = bridge;
        judge.bladeRadius = 0.26f;
        judge.noteHitRadiusXY = 0.45f;
        judge.minCutSpeed = 3.0f;
        return judge;
    }

    [Test]
    public void EnsureSecondSaber_CreatesOppositeHand()
    {
        var primary = MakePrimary();
        var second = SaberRig.EnsureSecondSaber(primary, SaberHand.Right);

        Assert.IsNotNull(second, "2本目が生成される");
        Assert.AreEqual(SaberHand.Right, primary.hand, "棒1に指定の手が入る");
        Assert.AreEqual(SaberHand.Left, second.hand, "棒2は逆の手");
        Assert.IsNotNull(second.saber, "Tracker が配線されている");
        Assert.IsNotNull(second.bladeProvider, "Bridge が配線されている");
        Assert.AreEqual(2, second.bladeProvider.stickIndex, "棒2(port5006)を読む");
        Assert.IsFalse(second.bladeProvider.fallbackToMouse, "マウスフォールバックは棒1のみ");
    }

    [Test]
    public void EnsureSecondSaber_CopiesJudgeTuning()
    {
        var primary = MakePrimary();
        var second = SaberRig.EnsureSecondSaber(primary, SaberHand.Right);
        Assert.AreEqual(primary.bladeRadius, second.bladeRadius, 0.0001f);
        Assert.AreEqual(primary.noteHitRadiusXY, second.noteHitRadiusXY, 0.0001f);
        Assert.AreEqual(primary.minCutSpeed, second.minCutSpeed, 0.0001f);
    }

    [Test]
    public void EnsureSecondSaber_IsIdempotent()
    {
        var primary = MakePrimary();
        var a = SaberRig.EnsureSecondSaber(primary, SaberHand.Right);
        var b = SaberRig.EnsureSecondSaber(primary, SaberHand.Right);
        Assert.AreSame(a, b, "二重生成しない");
    }

    [Test]
    public void EnsureSecondSaber_AddsTrailsToBothSabers()
    {
        var primary = MakePrimary();
        var second = SaberRig.EnsureSecondSaber(primary, SaberHand.Right);
        Assert.IsNotNull(primary.GetComponent<TrailRenderer>(), "1本目にトレイル");
        Assert.IsNotNull(second.GetComponent<TrailRenderer>(), "2本目にトレイル");
    }

    [Test]
    public void EnsureSecondSaber_NullPrimary_ReturnsNull()
    {
        Assert.IsNull(SaberRig.EnsureSecondSaber(null, SaberHand.Right));
    }

    [Test]
    public void CopyTuning_NullSafe()
    {
        var primary = MakePrimary();
        Assert.DoesNotThrow(() => SaberRig.CopyTuning(primary, null));
        Assert.DoesNotThrow(() => SaberRig.CopyTuning(null, primary));
    }
}
