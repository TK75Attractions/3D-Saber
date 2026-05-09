using NUnit.Framework;
using UnityEngine;

public class JudgmentSfxTests
{
    [TearDown]
    public void Cleanup()
    {
        foreach (var s in Object.FindObjectsByType<JudgmentSfx>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (s != null) Object.DestroyImmediate(s.gameObject);
        }
    }

    [Test]
    public void Beep_ReturnsValidClip()
    {
        AudioClip clip = JudgmentSfx.Beep(440f, 0.1f);
        Assert.IsNotNull(clip);
        Assert.AreEqual(1, clip.channels);
        Assert.Greater(clip.samples, 0);
    }

    [Test]
    public void Buzz_ReturnsValidClip()
    {
        AudioClip clip = JudgmentSfx.Buzz(110f, 0.1f);
        Assert.IsNotNull(clip);
        Assert.Greater(clip.samples, 0);
    }

    [Test]
    public void ClipFor_FallsBackToGeneratedBeep()
    {
        var go = new GameObject("sfx", typeof(AudioSource));
        var sfx = go.AddComponent<JudgmentSfx>();
        // クリップ未指定でも null を返さない
        Assert.IsNotNull(sfx.ClipFor(JudgmentTier.Perfect));
        Assert.IsNotNull(sfx.ClipFor(JudgmentTier.Great));
        Assert.IsNotNull(sfx.ClipFor(JudgmentTier.Good));
        Assert.IsNotNull(sfx.ClipFor(JudgmentTier.Bad));
        Assert.IsNotNull(sfx.ClipFor(JudgmentTier.Miss));
    }

    [Test]
    public void ClipFor_PrefersAssignedClip()
    {
        var go = new GameObject("sfx", typeof(AudioSource));
        var sfx = go.AddComponent<JudgmentSfx>();
        sfx.perfectClip = JudgmentSfx.Beep(1000f, 0.05f);
        Assert.AreSame(sfx.perfectClip, sfx.ClipFor(JudgmentTier.Perfect));
    }
}
