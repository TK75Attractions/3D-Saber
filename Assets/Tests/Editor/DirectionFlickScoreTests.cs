using NUnit.Framework;
using UnityEngine;

// HandleCut の private を直接叩けないので、ScoreManager の DowngradeTier と
// CuttableNote の振る舞いを通して "誤方向で1段階下がる" 挙動を検証する。
public class DirectionFlickScoreTests
{
    [Test]
    public void DowngradeTier_ShiftsByOne()
    {
        Assert.AreEqual(JudgmentTier.Great, ScoreManager.DowngradeTier(JudgmentTier.Perfect));
        Assert.AreEqual(JudgmentTier.Good, ScoreManager.DowngradeTier(JudgmentTier.Great));
        Assert.AreEqual(JudgmentTier.Bad, ScoreManager.DowngradeTier(JudgmentTier.Good));
        Assert.AreEqual(JudgmentTier.Miss, ScoreManager.DowngradeTier(JudgmentTier.Bad));
        Assert.AreEqual(JudgmentTier.Miss, ScoreManager.DowngradeTier(JudgmentTier.Miss));
    }

    [Test]
    public void RegisterHit_PerfectAfterDowngrade_IsGreat()
    {
        var go = new GameObject("score");
        var s = go.AddComponent<ScoreManager>();
        // 直接 Great を登録（誤方向時の最終 tier 想定）
        s.RegisterHit(JudgmentTier.Great);
        Assert.AreEqual(1, s.GreatCount);
        Assert.AreEqual(0, s.PerfectCount);
        Object.DestroyImmediate(go);
    }
}
