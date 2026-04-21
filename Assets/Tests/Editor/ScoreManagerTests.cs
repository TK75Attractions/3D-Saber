using NUnit.Framework;
using UnityEngine;

public class ScoreManagerTests
{
    private ScoreManager Make()
    {
        var go = new GameObject("score");
        return go.AddComponent<ScoreManager>();
    }

    [Test]
    public void RegisterHit_Perfect_IncreasesScoreAndCombo()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(1, s.HitCount);
        Assert.AreEqual(1, s.Combo);
        Assert.AreEqual(300, s.Score);
        Assert.AreEqual(1, s.MaxCombo);
        Assert.AreEqual(1, s.PerfectCount);
        Object.DestroyImmediate(s.gameObject);
    }

    [Test]
    public void RegisterHit_ComboBonusAccumulates()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Perfect);  // 300 + 0
        s.RegisterHit(JudgmentTier.Perfect);  // 300 + 10
        s.RegisterHit(JudgmentTier.Perfect);  // 300 + 20
        Assert.AreEqual(930, s.Score);
        Assert.AreEqual(3, s.Combo);
        Assert.AreEqual(3, s.MaxCombo);
        Object.DestroyImmediate(s.gameObject);
    }

    [Test]
    public void RegisterHit_GreatGoodBad_AllCountAsHit()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Great);
        s.RegisterHit(JudgmentTier.Good);
        s.RegisterHit(JudgmentTier.Bad);
        Assert.AreEqual(3, s.HitCount);
        Assert.AreEqual(1, s.GreatCount);
        Assert.AreEqual(1, s.GoodCount);
        Assert.AreEqual(1, s.BadCount);
        Assert.AreEqual(3, s.Combo);
        Object.DestroyImmediate(s.gameObject);
    }

    [Test]
    public void RegisterHit_MissTier_BreaksCombo()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterHit(JudgmentTier.Miss);
        Assert.AreEqual(0, s.Combo);
        Assert.AreEqual(1, s.MissCount);
        Assert.AreEqual(1, s.HitCount);
        Object.DestroyImmediate(s.gameObject);
    }

    [Test]
    public void RegisterMiss_BreaksComboAndKeepsMaxCombo()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterMiss();
        Assert.AreEqual(0, s.Combo);
        Assert.AreEqual(2, s.MaxCombo);
        Assert.AreEqual(1, s.MissCount);
        Object.DestroyImmediate(s.gameObject);
    }

    [Test]
    public void Reset_ClearsAllState()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Great);
        s.RegisterMiss();
        s.Reset();
        Assert.AreEqual(0, s.Score);
        Assert.AreEqual(0, s.Combo);
        Assert.AreEqual(0, s.MaxCombo);
        Assert.AreEqual(0, s.HitCount);
        Assert.AreEqual(0, s.MissCount);
        Assert.AreEqual(0, s.PerfectCount);
        Assert.AreEqual(0, s.GreatCount);
        Object.DestroyImmediate(s.gameObject);
    }

    [Test]
    public void OnJudgment_FiresWithTier()
    {
        var s = Make();
        JudgmentTier received = JudgmentTier.Miss;
        int awarded = -1;
        s.OnJudgment += (t, a) => { received = t; awarded = a; };
        s.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(JudgmentTier.Perfect, received);
        Assert.AreEqual(300, awarded);
        Object.DestroyImmediate(s.gameObject);
    }
}
