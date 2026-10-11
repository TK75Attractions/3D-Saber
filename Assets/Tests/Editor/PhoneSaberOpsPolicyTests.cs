using System;
using System.IO;
using NUnit.Framework;

// ハートビートと P2P 再試行の純粋ロジック。Player / Editor や本物の bridge は起動しない。
public class PhoneSaberOpsPolicyTests
{
    [Test]
    public void HeartbeatRequiresExplicitArgument()
    {
        Assert.IsNull(PhoneSaberHeartbeatWriter.ResolvePath(new[] { "game" }));
        Assert.IsNull(PhoneSaberHeartbeatWriter.ResolvePath(new[] { "game", "-phonesaberHeartbeat" }));
        Assert.AreEqual("/a b/heartbeat", PhoneSaberHeartbeatWriter.ResolvePath(
            new[] { "game", "-phonesaberHeartbeat", "/a b/heartbeat" }));
    }

    [Test]
    public void HeartbeatWritesOncePerSecondAndDoesNotThrowOnBadPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "phonesaber-heartbeat-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var writer = new PhoneSaberHeartbeatWriter(path))
            {
                writer.Tick(0);
                Assert.IsTrue(File.Exists(path));
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
                DateTime old = File.GetLastWriteTimeUtc(path);
                writer.Tick(0.99);
                Assert.AreEqual(old, File.GetLastWriteTimeUtc(path));
                writer.Tick(1);
                Assert.Greater(File.GetLastWriteTimeUtc(path), old);
            }
            using (var writer = new PhoneSaberHeartbeatWriter(path + "/missing/file"))
                Assert.DoesNotThrow(() => { writer.Tick(0); writer.Tick(1); });
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void HeartbeatSteadyWritesDoNotAllocate()
    {
        string path = Path.Combine(Path.GetTempPath(), "phonesaber-heartbeat-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var writer = new PhoneSaberHeartbeatWriter(path))
            {
                // stream の初期化と JIT を済ませてから、書込みがある定常処理を測る。
                for (int i = 0; i < 100; i++) writer.Tick(i);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 100; i < 1100; i++) writer.Tick(i);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.AreEqual(0, allocated);
            }
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void RetryBackoffIsBoundedAndResetsAfterOneMinute()
    {
        var policy = new PhoneSaberBridgeRetryPolicy();
        double now = 0;
        foreach (double delay in new[] { 2.0, 5.0, 15.0, 60.0, 60.0 })
        {
            Assert.IsTrue(policy.CanStart(now));
            policy.Started(now);
            now += 1;
            policy.Failed(now);
            Assert.IsFalse(policy.CanStart(now + delay - 0.001));
            now += delay;
            Assert.IsTrue(policy.CanStart(now));
        }
        policy.Started(now);
        now += 60;
        policy.Failed(now);
        Assert.IsFalse(policy.CanStart(now + 1.999));
        Assert.IsTrue(policy.CanStart(now + 2));
    }

    [Test]
    public void FailedStartAndShortRunsDoNotResetBackoff()
    {
        var policy = new PhoneSaberBridgeRetryPolicy();
        policy.Failed(0);
        Assert.IsFalse(policy.CanStart(1.999));
        Assert.IsTrue(policy.CanStart(2));
        policy.Started(2);
        policy.Failed(61.999);
        Assert.IsFalse(policy.CanStart(66.998));
        Assert.IsTrue(policy.CanStart(66.999));
        policy.Reset();
        Assert.IsTrue(policy.CanStart(0));
    }
}
