using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

public enum SwingDirection
{
    Unknown = 0,
    Left = 1,
    Right = 2,
    Up = 3,
    Down = 4,
}

public enum SaberSide
{
    Unknown = 0,
    Left = 1,
    Right = 2,
}

// Camera座標とは独立した、IMUの振り開始イベント。
// LocalReceiveTimeSecondsはUnity TimeではなくOS monotonic clock基準。
[Serializable]
public readonly struct SwingEvent
{
    public readonly ushort Sequence;
    public readonly SwingDirection Direction;
    public readonly SaberSide Side;
    public readonly float Strength;
    public readonly uint XiaoTimestampUs;
    public readonly long LocalReceiveTimestampTicks;
    public readonly double LocalReceiveTimeSeconds;
    public readonly double LocalhostTransportLatencyMs;
    public readonly double MainThreadHandoffLatencyMs;

    public SwingEvent(
        ushort sequence,
        SwingDirection direction,
        float strength,
        uint xiaoTimestampUs,
        long localReceiveTimestampTicks,
        double localhostTransportLatencyMs,
        double mainThreadHandoffLatencyMs = 0.0)
        : this(sequence, direction, SaberSide.Unknown, strength, xiaoTimestampUs,
            localReceiveTimestampTicks, localhostTransportLatencyMs, mainThreadHandoffLatencyMs)
    {
    }

    public SwingEvent(
        ushort sequence,
        SwingDirection direction,
        SaberSide side,
        float strength,
        uint xiaoTimestampUs,
        long localReceiveTimestampTicks,
        double localhostTransportLatencyMs,
        double mainThreadHandoffLatencyMs = 0.0)
    {
        Sequence = sequence;
        Direction = direction;
        Side = side;
        Strength = strength;
        XiaoTimestampUs = xiaoTimestampUs;
        LocalReceiveTimestampTicks = localReceiveTimestampTicks;
        LocalReceiveTimeSeconds = SwingMonotonicClock.ToSeconds(localReceiveTimestampTicks);
        LocalhostTransportLatencyMs = localhostTransportLatencyMs;
        MainThreadHandoffLatencyMs = mainThreadHandoffLatencyMs;
    }

    public SwingEvent WithMainThreadHandoff(long mainThreadTimestampTicks)
    {
        double milliseconds = SwingMonotonicClock.ElapsedMilliseconds(
            LocalReceiveTimestampTicks,
            mainThreadTimestampTicks);
        return new SwingEvent(
            Sequence,
            Direction,
            Side,
            Strength,
            XiaoTimestampUs,
            LocalReceiveTimestampTicks,
            LocalhostTransportLatencyMs,
            milliseconds);
    }
}

public static class SwingMonotonicClock
{
    public static long Timestamp => Stopwatch.GetTimestamp();

    public static double ToSeconds(long timestampTicks)
    {
        return timestampTicks / (double)Stopwatch.Frequency;
    }

    public static double ToNanoseconds(long timestampTicks)
    {
        return timestampTicks * (1_000_000_000.0 / Stopwatch.Frequency);
    }

    public static double ElapsedMilliseconds(long startTicks, long endTicks)
    {
        return (endTicks - startTicks) * (1000.0 / Stopwatch.Frequency);
    }
}

public static class SwingPacketParser
{
    public static bool TryParse(string message, long receiveTimestampTicks, out SwingEvent swing)
    {
        swing = default;
        if (string.IsNullOrWhiteSpace(message) ||
            !message.StartsWith("SWING:", StringComparison.Ordinal))
        {
            return false;
        }

        string[] values = message.Substring(6).Split(',');
        if (values.Length < 4 || values.Length > 6) return false;
        int offset = 0;
        SaberSide side = SaberSide.Unknown;
        if (values.Length >= 5 && TryParseSide(values[0], out SaberSide parsedSide))
        {
            side = parsedSide;
            offset = 1;
        }
        if (values.Length - offset != 4 && values.Length - offset != 5) return false;

        if (!ushort.TryParse(values[offset], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort sequence) ||
            !TryParseDirection(values[offset + 1], out SwingDirection direction) ||
            !float.TryParse(values[offset + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float strength) ||
            !uint.TryParse(values[offset + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint xiaoTimestampUs))
        {
            return false;
        }

        // 実bridgeの0..1000とVirtual IMUの0..1を受理し、Unity内部は0..1に揃える。
        if (strength > 1.0f)
        {
            strength /= 1000.0f;
        }
        strength = Math.Clamp(strength, 0.0f, 1.0f);

        double localhostLatencyMs = double.NaN;
        if (values.Length - offset == 5 &&
            long.TryParse(values[offset + 4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long senderMonotonicNs))
        {
            double receiveMonotonicNs = SwingMonotonicClock.ToNanoseconds(receiveTimestampTicks);
            double candidateMs = (receiveMonotonicNs - senderMonotonicNs) / 1_000_000.0;
            // 任意fieldは同じMacのmonotonic clockを両processが使う場合だけ有効。
            if (candidateMs >= -1.0 && candidateMs < 60_000.0)
            {
                localhostLatencyMs = Math.Max(0.0, candidateMs);
            }
        }

        swing = new SwingEvent(
            sequence,
            direction,
            side,
            strength,
            xiaoTimestampUs,
            receiveTimestampTicks,
            localhostLatencyMs);
        return true;
    }

    public static bool TryParseSide(string value, out SaberSide side)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "left": side = SaberSide.Left; return true;
            case "right": side = SaberSide.Right; return true;
            case "unknown": side = SaberSide.Unknown; return true;
            default: side = SaberSide.Unknown; return false;
        }
    }

    public static bool TryParseDirection(string value, out SwingDirection direction)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "left": direction = SwingDirection.Left; return true;
            case "right": direction = SwingDirection.Right; return true;
            case "up": direction = SwingDirection.Up; return true;
            case "down": direction = SwingDirection.Down; return true;
            case "unknown": direction = SwingDirection.Unknown; return true;
            default: direction = SwingDirection.Unknown; return false;
        }
    }
}

public enum SwingSequenceResult
{
    Accepted,
    Duplicate,
    OutOfOrder,
}

// UDP受信スレッドだけから呼ぶ。欠番は数えるが待たず、次の新しいeventを即受理する。
public sealed class SwingSequenceTracker
{
    private bool hasLastSequence;
    private ushort lastSequence;
    private int receivedEvents;
    private int acceptedEvents;
    private int duplicateEvents;
    private int outOfOrderEvents;
    private int missingEvents;

    public int ReceivedEvents => Volatile.Read(ref receivedEvents);
    public int AcceptedEvents => Volatile.Read(ref acceptedEvents);
    public int DuplicateEvents => Volatile.Read(ref duplicateEvents);
    public int OutOfOrderEvents => Volatile.Read(ref outOfOrderEvents);
    public int MissingEvents => Volatile.Read(ref missingEvents);

    public SwingSequenceResult Observe(ushort sequence)
    {
        Interlocked.Increment(ref receivedEvents);
        if (!hasLastSequence)
        {
            hasLastSequence = true;
            lastSequence = sequence;
            Interlocked.Increment(ref acceptedEvents);
            return SwingSequenceResult.Accepted;
        }

        ushort forwardDistance = (ushort)(sequence - lastSequence);
        if (forwardDistance == 0)
        {
            Interlocked.Increment(ref duplicateEvents);
            return SwingSequenceResult.Duplicate;
        }
        if (forwardDistance >= 0x8000)
        {
            Interlocked.Increment(ref outOfOrderEvents);
            return SwingSequenceResult.OutOfOrder;
        }

        if (forwardDistance > 1)
        {
            Interlocked.Add(ref missingEvents, forwardDistance - 1);
        }
        lastSequence = sequence;
        Interlocked.Increment(ref acceptedEvents);
        return SwingSequenceResult.Accepted;
    }

    public void ResetSession()
    {
        hasLastSequence = false;
    }
}

public static class SwingEventTiming
{
    public static bool IsStale(in SwingEvent swing, long nowTicks, double maximumAgeSeconds)
    {
        if (maximumAgeSeconds <= 0.0)
        {
            return false;
        }
        return SwingMonotonicClock.ElapsedMilliseconds(
            swing.LocalReceiveTimestampTicks,
            nowTicks) > maximumAgeSeconds * 1000.0;
    }
}
