using NUnit.Framework;

public class MenuArrivalAndStatusTests
{
    [Test] public void GraceStartsOnTheFirstValidTickAndIgnoresLongFrames()
    {
        var guard = new SaberUIArrivalGuard();
        Assert.True(guard.Tick(.1f)); Assert.True(guard.CaptureTarget);
        Assert.True(guard.Tick(20)); Assert.False(guard.CaptureTarget);
        for (int i = 0; i < 4; i++) Assert.True(guard.Tick(.1f));
        Assert.False(guard.Tick(.1f));
        guard.Reset(); Assert.True(guard.Tick(.1f)); Assert.True(guard.CaptureTarget);
    }
    [Test] public void EitherHandEnablesTheStatusAndAnUnseenBlueHandIsVisible()
    {
        Assert.AreEqual("", MenuSaberStatus.Status(100, -1000, -1000));
        StringAssert.Contains("赤 ●", MenuSaberStatus.Status(100, 99.5, -1000));
        StringAssert.Contains("青 ○", MenuSaberStatus.Status(100, 99.5, -1000));
        StringAssert.Contains("赤 ○", MenuSaberStatus.Status(100, 99, 99.9));
        StringAssert.Contains("青 ●", MenuSaberStatus.Status(100, 99, 99.9));
    }
}
