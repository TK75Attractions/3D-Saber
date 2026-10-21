using NUnit.Framework;
using UnityEngine;

// 音の出口のリミッター(仕様面の点検 11)。普段の音は1ビットも変えず、重なって閾値を超える所だけを下げる。
public class MasterLimiterTests
{
    static readonly double Release = MasterLimiter.ReleaseCoefficient(48000);

    [Test]
    public void QuietSoundPassesThroughUnchanged()
    {
        var rng = new System.Random(7);
        var data = new float[4800 * 2];
        for (int i = 0; i < data.Length; i++) data[i] = (float)(rng.NextDouble() * 1.6 - .8);
        var before = (float[])data.Clone();
        double gain = MasterLimiter.Process(data, 2, 1, Release);
        Assert.AreEqual(1.0, gain);
        CollectionAssert.AreEqual(before, data);
    }

    [Test]
    public void OverlappingPeaksNeverPassTheThreshold()
    {
        var data = new float[4800 * 2];
        for (int i = 0; i < data.Length; i++) data[i] = 1.6f * Mathf.Sin(i * .37f) + (i % 97 == 0 ? .9f : 0f);
        MasterLimiter.Process(data, 2, 1, Release);
        foreach (float v in data) Assert.LessOrEqual(Mathf.Abs(v), MasterLimiter.Threshold + 1e-6f);
    }

    [Test]
    public void BothChannelsShareOneGainSoTheBalanceStays()
    {
        var data = new[] { 1.2f, .3f, -1.5f, .6f };
        MasterLimiter.Process(data, 2, 1, Release);
        Assert.AreEqual(.25f, data[1] / data[0], 1e-5f);
        Assert.AreEqual(-.4f, data[3] / data[2], 1e-5f);
        Assert.LessOrEqual(Mathf.Abs(data[2]), MasterLimiter.Threshold + 1e-6f);
    }

    [Test]
    public void GainComesBackToExactlyOneAfterTheLoudPart()
    {
        var loud = new float[480 * 2];
        for (int i = 0; i < loud.Length; i++) loud[i] = 1.4f;
        double gain = MasterLimiter.Process(loud, 2, 1, Release);
        Assert.Less(gain, .7);
        // 150ms の時定数で戻るので、静かな音が2秒続けば元の大きさにそろい、そのあとの値は変わらない。
        var quiet = new float[96000 * 2];
        for (int i = 0; i < quiet.Length; i++) quiet[i] = .2f;
        gain = MasterLimiter.Process(quiet, 2, gain, Release);
        Assert.AreEqual(1.0, gain);
        Assert.AreEqual(.2f, quiet[quiet.Length - 1]);
        Assert.Less(quiet[0], .2f, "大きな音の直後は少し下がったまま戻っていく");
    }

    [Test]
    public void BrokenInputsDoNotThrowOrStick()
    {
        Assert.AreEqual(.5, MasterLimiter.Process(null, 2, .5, Release));
        Assert.AreEqual(.5, MasterLimiter.Process(new float[4], 0, .5, Release));
        var data = new[] { .1f, .1f };
        Assert.AreEqual(1.0, MasterLimiter.Process(data, 2, double.NaN, Release), "壊れた倍率は 1 からやり直す");
        Assert.Greater(MasterLimiter.ReleaseCoefficient(0), 0.0);
        var broken = new[] { float.NaN, .2f, float.PositiveInfinity, -.3f };
        Assert.AreEqual(1.0, MasterLimiter.Process(broken, 2, 1, Release), "NaN・無限大は 0 にして倍率を汚さない");
        CollectionAssert.AreEqual(new[] { 0f, .2f, 0f, -.3f }, broken);
    }

    [Test]
    public void AttachesOnceToEachListener()
    {
        var go = new GameObject("ListenerForLimiterTest", typeof(AudioListener));
        try
        {
            MasterLimiter.AttachToListeners();
            MasterLimiter.AttachToListeners();
            Assert.AreEqual(1, go.GetComponents<MasterLimiter>().Length);
        }
        finally { Object.DestroyImmediate(go); }
    }
}
