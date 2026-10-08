using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
#if !PHONESABER_ALLOC_BENCH
using NUnit.Framework;
using System.Reflection;
using UnityEngine;
#endif

// harness と EditMode が同じ旧実装・入力集合を使い、受理結果と浮動小数点のビットを比較する。
public static class PhoneSaberParserTestCorpus
{
    public static bool LegacyParse(byte[] data, out bool isStick, out float a, out float b,
        out float c, out float d, out double? sentEpoch)
    {
        isStick = false;
        a = b = c = d = 0f;
        string originalMessage = Encoding.UTF8.GetString(data).Trim();
        sentEpoch = LegacyEpoch(originalMessage);
        int prefixLength = originalMessage.StartsWith("ts=", StringComparison.Ordinal) ? 3 :
            originalMessage.StartsWith("timestamp=", StringComparison.Ordinal) ? 10 : 0;
        string message = originalMessage;
        if (prefixLength != 0)
        {
            int separator = message.IndexOf(';', prefixLength);
            if (separator > prefixLength) message = message.Substring(separator + 1);
        }
        string[] parts = message.Split(',');
        if (parts.Length != 2 && parts.Length != 4) return false;
        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) ||
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return false;
        isStick = parts.Length == 4;
        if (isStick &&
            (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c) ||
             !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out d))) return false;
        return true;
    }

    static double? LegacyEpoch(string message)
    {
        if (message == null || !message.StartsWith("ts=", StringComparison.Ordinal)) return null;
        int separator = message.IndexOf(';', 3);
        if (separator <= 3 || !double.TryParse(message.Substring(3, separator - 3),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double epoch) ||
            double.IsNaN(epoch) || double.IsInfinity(epoch) || epoch <= 0) return null;
        return epoch;
    }

    public static IEnumerable<byte[]> Packets()
    {
        string[] numbers = { "0", "-0", "+0", "1", "-1", "+1.25", ".5", "5.", "1e-30",
            "-1E+30", "3.4028234663852886e38", "3.4028235677973366e38", "1e999", "-1e999",
            "1e-999", "1.401298464324817e-45", "7.006492321624085e-46", "16777217",
            "1.000000059604644775390625", "1.000000178813934326171875", "NaN", "nan", "+NaN",
            "Infinity", "infinity", "+Infinity", "-Infinity", "", " ", "1,000", "1e", "--1",
            "+ 1", "1x", "1\0", "1\0\0", "1\0x", "0x10", "１２", "\u00a01\u00a0",
            "\t-1.25\r\n", "\u20031.5\u2003" };
        string[] prefixes = { "", "ts=1000;", "timestamp=1000;", "ts=;", "ts=oops;", "ts=NaN;",
            "ts=Infinity;", "ts=-1;", "ts=0;", "ts=1e999;", "ts=1e-999;", "ts=+1.25e3;",
            "ts= 1000 ;", "ts=1,25;", "ts=1000\0;", "ts=1000", "TS=1000;", "ts=1;;" };
        foreach (string prefix in prefixes)
            foreach (string number in numbers)
                foreach (string payload in new[] { number + ",2", "1," + number, number + ",2,3,4",
                    "1,2," + number + ",4", "1,2,3," + number })
                {
                    yield return Encoding.UTF8.GetBytes(prefix + payload);
                    yield return Encoding.UTF8.GetBytes("\u2003 \t" + prefix + payload + "\r\n\u00a0");
                }
        foreach (string payload in new[] { "", ",", ",,,", "1", "1,2,3", "1,2,3,4,5", "1,2,",
            "1,2,3,4;", "ts=1;", "ts=1;1,2;garbage", "1,2\0", "1,2\0\0", "1,2\0x",
            new string('0', 65000) + "1,2", "ts=" + new string('0', 64000) + "1;1,2,3,4" })
            yield return Encoding.UTF8.GetBytes(payload);
        var random = new System.Random(0x5005);
        for (int i = 0; i < 20000; i++)
        {
            float value = BitConverter.Int32BitsToSingle(random.Next() ^ (i % 2 == 0 ? int.MinValue : 0));
            string number = value.ToString(i % 2 == 0 ? "R" : "E9", CultureInfo.InvariantCulture);
            string payload = prefixes[i % prefixes.Length] + number + ", -0.0, +.25, 1.0e-4";
            if (i % 3 == 0) payload += "garbage";
            yield return Encoding.UTF8.GetBytes(payload);
        }
        // 不正 UTF-8 も、旧 decoder の置換結果と同じになることを確認する。
        for (int i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(0, 80)];
            random.NextBytes(bytes);
            yield return bytes;
        }
    }

    public static void Compare(PhoneSaberPacketParser parser, byte[] data)
    {
        bool before = LegacyParse(data, out bool oldStick, out float oldA, out float oldB,
            out float oldC, out float oldD, out double? oldEpoch);
        bool after = parser.TryParse(data, out bool stick, out float a, out float b,
            out float c, out float d, out double? epoch);
        bool equal = before == after && oldEpoch.HasValue == epoch.HasValue &&
            (!oldEpoch.HasValue || BitConverter.DoubleToInt64Bits(oldEpoch.Value) == BitConverter.DoubleToInt64Bits(epoch.Value));
        if (before && after)
            equal &= oldStick == stick && Bits(oldA) == Bits(a) && Bits(oldB) == Bits(b) &&
                Bits(oldC) == Bits(c) && Bits(oldD) == Bits(d);
        if (!equal) throw new InvalidOperationException("Parser mismatch: " + BitConverter.ToString(data));
    }

    static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
}

#if !PHONESABER_ALLOC_BENCH
public class PhoneSaberPacketParserTests
{
    [Test]
    public void ByteParserMatchesOldParserBitForBit()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var parser = new PhoneSaberPacketParser();
            foreach (byte[] packet in PhoneSaberParserTestCorpus.Packets())
                PhoneSaberParserTestCorpus.Compare(parser, packet);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Test]
    public void SteadyParserStatisticsAndEventPollingDoNotAllocate()
    {
        var parser = new PhoneSaberPacketParser();
        byte[] packet = Encoding.UTF8.GetBytes("ts=1791234567.123456;-0.125,0.5,0.75,-1.0");
        var stats = new PhoneSaberPacketStatistics();
        var events = new PhoneSaberConnectionEvents(0, true, (kind, detail) => { });
        var log = new PhoneSaberEventLog("unused-empty-log");
        // JIT・比較器初期化・sender-change のログは計測前に済ませる。
        for (int i = 0; i < 1000; i++) Step(i / 60.0, parser, packet, stats, events, log);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1000; i < 11000; i++) Step(i / 60.0, parser, packet, stats, events, log);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0, allocated);
    }

    [Test]
    public void HiddenOverlayUpdateDoesNotAllocateOrFormatDisplayText()
    {
        var previousLog = PhoneSaberEventLog.Current;
        var gameObject = new GameObject("PhoneSaber hidden overlay allocation test");
        try
        {
            PhoneSaberEventLog.Current = new PhoneSaberEventLog("unused-empty-log");
            var overlay = gameObject.AddComponent<PhoneSaberOperatorOverlay>();
            var updateMethod = typeof(PhoneSaberOperatorOverlay).GetMethod("Update",
                BindingFlags.Instance | BindingFlags.NonPublic);
            // Invoke の割り当ては計測に混ぜず、実際の Update を呼ぶ。
            var update = (Action)Delegate.CreateDelegate(typeof(Action), overlay, updateMethod);
            for (int i = 0; i < 1000; i++) update();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) update();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, allocated);
            foreach (string field in new[] { "redText", "blueText", "services" })
                Assert.IsNull(typeof(PhoneSaberOperatorOverlay).GetField(field,
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay));
        }
        finally
        {
            PhoneSaberEventLog.Current = previousLog;
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    static void Step(double now, PhoneSaberPacketParser parser, byte[] packet,
        PhoneSaberPacketStatistics stats, PhoneSaberConnectionEvents events, PhoneSaberEventLog log)
    {
        bool parsed = parser.TryParse(packet, out _, out _, out _, out _, out _, out double? epoch);
        stats.Record(now, "127.0.0.1", parsed, epoch + 0.01, epoch);
        stats.Read(now);
        events.Packet(now, "127.0.0.1");
        events.Poll(now);
        log.Flush();
    }
}
#endif
