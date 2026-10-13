using NUnit.Framework;

public class PhoneSaberTrackingProbeTests
{
    [Test]
    public void ArgumentIsOffByDefaultAndClampsTheDuration()
    {
        Assert.AreEqual(0f, PhoneSaberTrackingProbe.ResolveSeconds(new[] { "app" }));
        Assert.AreEqual(30f, PhoneSaberTrackingProbe.ResolveSeconds(new[] { "app", PhoneSaberTrackingProbe.Argument, "30" }));
        Assert.AreEqual(PhoneSaberTrackingProbe.DefaultSeconds,
            PhoneSaberTrackingProbe.ResolveSeconds(new[] { "app", PhoneSaberTrackingProbe.Argument }));
        Assert.AreEqual(PhoneSaberTrackingProbe.DefaultSeconds,
            PhoneSaberTrackingProbe.ResolveSeconds(new[] { "app", PhoneSaberTrackingProbe.Argument, "5" }));
    }

    [Test]
    public void TargetStaysOnScreenAndNeverJumpsBetweenSegments()
    {
        double previous = PhoneSaberTrackingProbe.TargetX(0);
        Assert.AreEqual(0.5, previous, 1e-9);
        for (int i = 1; i <= 240 * 130; i++)
        {
            double x = PhoneSaberTrackingProbe.TargetX(i / 240.0);
            Assert.That(x, Is.InRange(0.5 - PhoneSaberTrackingProbe.Amplitude - 1e-9, 0.5 + PhoneSaberTrackingProbe.Amplitude + 1e-9));
            // 最速 2.5 Hz でも 1/240 秒の移動は振幅×2π×2.5/240 以下。区間の境目で飛ばない。
            Assert.Less(System.Math.Abs(x - previous), PhoneSaberTrackingProbe.Amplitude * 2 * System.Math.PI * 2.5 / 240 + 1e-6);
            previous = x;
        }
        // 停止区間（3つ目）では動かない。
        Assert.AreEqual(PhoneSaberTrackingProbe.TargetX(17), PhoneSaberTrackingProbe.TargetX(23), 1e-12);
    }
}
