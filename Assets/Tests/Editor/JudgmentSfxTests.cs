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
    public void ClipFor_UnassignedTiersRemainPlayable()
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

    [Test]
    public void ClipFor_DefaultHitsUseBundledCutButMissDoesNot()
    {
        var go = new GameObject("sfx", typeof(AudioSource));
        var sfx = go.AddComponent<JudgmentSfx>();
        var cut = Resources.Load<AudioClip>("Audio/SFX/Saber_NoteCut");
        Assert.IsNotNull(cut, "通常カット音がビルドに含まれる場所から読める");
        Assert.AreEqual(48000, cut.frequency);
        Assert.AreEqual(1, cut.channels);
        Assert.AreEqual(0.22f, cut.length, 0.001f);
        foreach (var tier in new[] { JudgmentTier.Perfect, JudgmentTier.Great, JudgmentTier.Good, JudgmentTier.Bad })
            Assert.AreSame(cut, sfx.ClipFor(tier));
        Assert.AreNotSame(cut, sfx.ClipFor(JudgmentTier.Miss));
    }
}
