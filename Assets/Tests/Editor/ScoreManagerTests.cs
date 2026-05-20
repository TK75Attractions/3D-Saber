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
    public void RegisterHit_GreatGoodBad_AllCountAsHit_ButBadBreaksCombo()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Great);
        s.RegisterHit(JudgmentTier.Good);
        s.RegisterHit(JudgmentTier.Bad);
        Assert.AreEqual(3, s.HitCount, "Bad もヒット数には入る");
        Assert.AreEqual(1, s.GreatCount);
        Assert.AreEqual(1, s.GoodCount);
        Assert.AreEqual(1, s.BadCount);
        Assert.AreEqual(0, s.Combo, "Bad でコンボは切れる");
        Assert.AreEqual(2, s.MaxCombo, "MaxCombo は Bad 前の値を維持");
        Object.DestroyImmediate(s.gameObject);
    }

    [Test]
    public void RegisterHit_Bad_StillAddsScore_ButResetsCombo()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Perfect); // Score 300, Combo 1
        s.RegisterHit(JudgmentTier.Perfect); // Score +310 = 610, Combo 2
        int scoreBeforeBad = s.Score;
        s.RegisterHit(JudgmentTier.Bad);     // Combo 0 でリセット、Bad の基礎点 50
        Assert.AreEqual(0, s.Combo, "Bad でコンボは0に");
        Assert.AreEqual(scoreBeforeBad + 50, s.Score, "Bad は基礎点(50)を加算、コンボ倍率は0");
        Assert.AreEqual(1, s.BadCount);
        Object.DestroyImmediate(s.gameObject);
    }

    [Test]
    public void RegisterHit_GoodKeepsCombo()
    {
        var s = Make();
        s.RegisterHit(JudgmentTier.Perfect);
        s.RegisterHit(JudgmentTier.Good); // 切れない
        s.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(3, s.Combo, "Good ではコンボが継続");
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
