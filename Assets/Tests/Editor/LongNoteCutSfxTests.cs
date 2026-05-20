using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class LongNoteCutSfxTests
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
    }

    private LongNoteCutSfx Make()
    {
        var go = new GameObject("sfx", typeof(AudioSource), typeof(LongNoteCutSfx));
        created.Add(go);
        return go.GetComponent<LongNoteCutSfx>();
    }

    [Test]
    public void FrequencyFor_AscendsWithCutIndex_Pentatonic()
    {
        var sfx = Make();
        sfx.baseFrequency = 440f;
        sfx.pitchPerCutSemitones = 0f; // pentatonic

        float f0 = sfx.FrequencyFor(0);
        float f1 = sfx.FrequencyFor(1);
        float f2 = sfx.FrequencyFor(2);
        float f3 = sfx.FrequencyFor(3);

        Assert.AreEqual(440f, f0, 0.5f);
        Assert.Less(f0, f1);
        Assert.Less(f1, f2);
        Assert.Less(f2, f3);
        // 3 番目は 1 オクターブ上（+12 半音）= 880Hz
        Assert.AreEqual(880f, f3, 1f);
    }

    [Test]
    public void FrequencyFor_AscendsWithCutIndex_Chromatic()
    {
        var sfx = Make();
        sfx.baseFrequency = 440f;
        sfx.pitchPerCutSemitones = 2f; // 半音 2 つずつ

        float f0 = sfx.FrequencyFor(0);
        float f1 = sfx.FrequencyFor(1);
        // f1/f0 = 2^(2/12)
        float ratio = f1 / f0;
        Assert.AreEqual(Mathf.Pow(2f, 2f / 12f), ratio, 0.001f);
    }
}
