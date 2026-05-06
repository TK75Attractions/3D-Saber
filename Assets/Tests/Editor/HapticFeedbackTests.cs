using NUnit.Framework;
using UnityEngine;

public class HapticFeedbackTests
{
    [Test]
    public void DurationFor_DescendsByTier()
    {
        var go = new GameObject("h");
        var h = go.AddComponent<HapticFeedback>();
        Assert.Greater(h.DurationFor(JudgmentTier.Perfect), h.DurationFor(JudgmentTier.Bad));
        Assert.AreEqual(0f, h.DurationFor(JudgmentTier.Miss));
        Object.DestroyImmediate(go);
    }
}
