using NUnit.Framework;

// Unity の時計や描画を使わず、遅延テストの切替・計測・失敗判定を検証する。
public class PhoneSaberLatencyProbeTests
{
    [Test]
    public void MeasuresFromSwitchToFirstMovedPacket()
    {
        var loop = new PhoneSaberLatencyLoop();
        loop.Reset(0);
        loop.Tick(0.5, 0.49, -0.5f, true);
        Assert.IsFalse(loop.Waiting, "1秒経つまでは切り替えない");
        loop.Tick(1.0, 0.99, -0.5f, true);
        Assert.IsTrue(loop.Waiting);
        Assert.AreEqual(1, loop.Side);
        loop.Tick(1.03, 1.02, -0.45f, true);
        Assert.IsTrue(loop.Waiting, "閾値未満の揺れは切替として数えない");
        loop.Tick(1.10, 1.085, 0.5f, true);
        Assert.IsFalse(loop.Waiting);
        Assert.AreEqual(1, loop.SamplesMs.Count);
        Assert.AreEqual(85.0, loop.SamplesMs[0], 1e-6);
    }

    [Test]
    public void IgnoresPacketsReceivedBeforeTheSwitch()
    {
        var loop = new PhoneSaberLatencyLoop();
        loop.Reset(0);
        loop.Tick(1.0, 0.99, -0.5f, true);
        Assert.IsTrue(loop.Waiting);
        // 切替前に受信していた古い値（同じ受信時刻）は新しいサンプルにならない。
        loop.Tick(1.05, 0.99, 0.5f, true);
        Assert.AreEqual(0, loop.SamplesMs.Count);
    }

    [Test]
    public void TimesOutWithoutMovementAndDoesNotSwitchWithoutReception()
    {
        var loop = new PhoneSaberLatencyLoop();
        loop.Reset(0);
        loop.Tick(1.0, 0.5, -0.5f, true);
        Assert.IsFalse(loop.Waiting, "0.2秒以上受信がなければ切り替えない");
        loop.Tick(1.1, 1.09, -0.5f, true);
        Assert.IsTrue(loop.Waiting);
        loop.Tick(2.7, 1.09, -0.5f, true);
        Assert.IsFalse(loop.Waiting);
        Assert.AreEqual(1, loop.Misses);
        StringAssert.Contains("失敗 1 回", loop.Summary());
    }
}
