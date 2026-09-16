using NUnit.Framework;

public class SwingEventStreamTests
{
    [TestCase("left", SwingDirection.Left)]
    [TestCase("right", SwingDirection.Right)]
    [TestCase("up", SwingDirection.Up)]
    [TestCase("down", SwingDirection.Down)]
    public void PacketParse_ParsesDirections(string name, SwingDirection expected)
    {
        Assert.IsTrue(SwingPacketParser.TryParse(
            $"SWING:123,{name},0.82,12345678",
            SwingMonotonicClock.Timestamp,
            out SwingEvent swing));
        Assert.AreEqual((ushort)123, swing.Sequence);
        Assert.AreEqual(expected, swing.Direction);
        Assert.AreEqual(0.82f, swing.Strength, 0.0001f);
        Assert.AreEqual(12345678u, swing.XiaoTimestampUs);
    }

    [Test]
    public void PacketParse_NormalizesFirmwareStrength()
    {
        Assert.IsTrue(SwingPacketParser.TryParse(
            "SWING:1,left,800,12",
            SwingMonotonicClock.Timestamp,
            out SwingEvent swing));
        Assert.AreEqual(0.8f, swing.Strength, 0.0001f);
    }

    [TestCase("")]
    [TestCase("IMU:1,2,3")]
    [TestCase("SWING:no,left,0.8,1")]
    [TestCase("SWING:1,diagonal,0.8,1")]
    [TestCase("SWING:1,left,broken,1")]
    public void PacketParse_RejectsInvalidPacket(string packet)
    {
        Assert.IsFalse(SwingPacketParser.TryParse(
            packet,
            SwingMonotonicClock.Timestamp,
            out _));
    }

    [Test]
    public void SequenceTracker_DropsDuplicateAndOutOfOrder()
    {
        var tracker = new SwingSequenceTracker();
        Assert.AreEqual(SwingSequenceResult.Accepted, tracker.Observe(10));
        Assert.AreEqual(SwingSequenceResult.Duplicate, tracker.Observe(10));
        Assert.AreEqual(SwingSequenceResult.OutOfOrder, tracker.Observe(9));
        Assert.AreEqual(1, tracker.AcceptedEvents);
        Assert.AreEqual(1, tracker.DuplicateEvents);
        Assert.AreEqual(1, tracker.OutOfOrderEvents);
    }

    [Test]
    public void SequenceTracker_DoesNotWaitForPacketLoss()
    {
        var tracker = new SwingSequenceTracker();
        Assert.AreEqual(SwingSequenceResult.Accepted, tracker.Observe(101));
        Assert.AreEqual(SwingSequenceResult.Accepted, tracker.Observe(102));
        Assert.AreEqual(SwingSequenceResult.Accepted, tracker.Observe(104));
        Assert.AreEqual(3, tracker.AcceptedEvents);
        Assert.AreEqual(1, tracker.MissingEvents);
    }

    [Test]
    public void SequenceTracker_HandlesWrapAndSenderRestart()
    {
        var tracker = new SwingSequenceTracker();
        Assert.AreEqual(SwingSequenceResult.Accepted, tracker.Observe(65535));
        Assert.AreEqual(SwingSequenceResult.Accepted, tracker.Observe(0));
        tracker.ResetSession();
        Assert.AreEqual(SwingSequenceResult.Accepted, tracker.Observe(0));
    }

    [Test]
    public void StaleEvent_UsesLocalReceiveClock()
    {
        long received = SwingMonotonicClock.Timestamp;
        var swing = new SwingEvent(1, SwingDirection.Left, 0.8f, 10, received, double.NaN);
        long later = received + (long)(0.2 * System.Diagnostics.Stopwatch.Frequency);
        Assert.IsTrue(SwingEventTiming.IsStale(swing, later, 0.15));
        Assert.IsFalse(SwingEventTiming.IsStale(swing, later, 0.25));
    }

    [Test]
    public void MainThreadHandoff_PreservesOriginalReceiveTimestamp()
    {
        long received = System.Diagnostics.Stopwatch.Frequency * 10;
        var original = new SwingEvent(1, SwingDirection.Left, 0.8f, 10, received, double.NaN);
        long later = received + System.Diagnostics.Stopwatch.Frequency / 10;
        var delivered = original.WithMainThreadHandoff(later);
        Assert.AreEqual(received, delivered.LocalReceiveTimestampTicks);
        Assert.AreEqual(original.LocalReceiveTimeSeconds, delivered.LocalReceiveTimeSeconds);
        Assert.AreEqual(100.0, delivered.MainThreadHandoffLatencyMs, 0.001);
    }
}
