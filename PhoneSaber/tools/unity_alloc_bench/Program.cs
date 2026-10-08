using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class Program
{
    const int Count = 100000;
    static readonly byte[] Packet = Encoding.UTF8.GetBytes("ts=1791234567.123456;-0.125,0.5,0.75,-1.0");
    static double sink;

    static void Main()
    {
        Console.WriteLine("Runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Console.WriteLine("OS: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        var parser = new PhoneSaberPacketParser();
        int cases = 0;
        foreach (var packet in PhoneSaberParserTestCorpus.Packets())
        {
            PhoneSaberParserTestCorpus.Compare(parser, packet);
            cases++;
        }
        CultureInfo.CurrentCulture = previous;
        Console.WriteLine("Parser differential: PASS, " + cases + " packets (acceptance, endpoint bits, epoch bits)");
        CheckStatistics();
        Measure("Parser before", i =>
        {
            PhoneSaberParserTestCorpus.LegacyParse(Packet, out _, out float a, out _, out _, out _, out double? epoch);
            sink = a + epoch.GetValueOrDefault();
        });
        Measure("Parser after", i =>
        {
            parser.TryParse(Packet, out _, out float a, out _, out _, out _, out double? epoch);
            sink = a + epoch.GetValueOrDefault();
        });
        var unicode = Encoding.UTF8.GetBytes("\u2003ts=1000;1,2,3,4\u00a0");
        Measure("UTF-8 compatibility parser after", i =>
        {
            parser.TryParse(unicode, out _, out float a, out _, out _, out _, out _);
            sink = a;
        });
        var oldStats = new Legacy.PhoneSaberPacketStatistics();
        Measure("Statistics Record+Read before", i =>
        {
            double now = i / 60.0;
            oldStats.Record(now, "127.0.0.1", true, 1000.01, 1000);
            sink = oldStats.Read(now).ArrivalGaps.P95Ms;
        });
        var stats = new PhoneSaberPacketStatistics();
        Measure("Statistics Record+Read after", i =>
        {
            double now = i / 60.0;
            stats.Record(now, "127.0.0.1", true, 1000.01, 1000);
            sink = stats.Read(now).ArrivalGaps.P95Ms;
        });
        var events = new PhoneSaberConnectionEvents(0, true, (kind, detail) => { });
        var log = new PhoneSaberEventLog("unused-empty-log");
        Measure("Event Packet+Poll+empty Flush (unchanged)", i =>
        {
            events.Packet(i / 60.0, "127.0.0.1");
            events.Poll(i / 60.0);
            log.Flush();
        });
        MeasureSockets();
        Console.WriteLine("Sink: " + sink.ToString("R", CultureInfo.InvariantCulture));
    }

    static void Measure(string label, Action<int> step)
    {
        for (int i = 0; i < 1000; i++) step(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1000; i < Count + 1000; i++) step(i);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine(label + ": " + bytes + " bytes / " + Count + " packets");
        if (label.EndsWith("after", StringComparison.Ordinal) && bytes != 0)
            throw new InvalidOperationException("Steady managed path allocated: " + label);
    }

    static void CheckStatistics()
    {
        var oldStats = new Legacy.PhoneSaberPacketStatistics();
        var stats = new PhoneSaberPacketStatistics();
        for (int i = 0; i < 4096; i++)
        {
            oldStats.Record(i / 10000.0, "127.0.0.1", true, 1000.01, 1000);
            stats.Record(i / 10000.0, "127.0.0.1", true, 1000.01, 1000);
        }
        var cappedBefore = oldStats.Read(0.5);
        var cappedAfter = stats.Read(0.5);
        CheckTiming(cappedBefore.ArrivalGaps, cappedAfter.ArrivalGaps);
        CheckTiming(cappedBefore.OneWayDelay, cappedAfter.OneWayDelay);
        if (cappedAfter.ArrivalGaps.Count != PhoneSaberPacketStatistics.MaximumTimingSamples ||
            cappedAfter.OneWayDelay.Count != PhoneSaberPacketStatistics.MaximumTimingSamples)
            throw new InvalidOperationException("Timing sample cap mismatch");
        var random = new Random(5006);
        double now = 0;
        for (int i = 0; i < 10000; i++)
        {
            if (i % 997 == 0) { oldStats.Reset(now); stats.Reset(now); }
            now += i % 503 == 0 ? -1 : i % 499 == 0 ? 6 : random.NextDouble() / 1000.0;
            string ip = i % 307 == 0 ? "192.168.1.2" : "127.0.0.1";
            bool parsed = i % 5 != 0;
            double? sent = i % 7 == 0 ? (double?)null : 1000;
            double receive = 1000 + (random.NextDouble() - 0.5);
            oldStats.Record(now, ip, parsed, receive, sent);
            stats.Record(now, ip, parsed, receive, sent);
            double readAt = now + (i % 401 == 0 ? 5.0 : 0);
            var a = oldStats.Read(readAt);
            var b = stats.Read(readAt);
            if (a.HasPacket != b.HasPacket || a.PacketsPerSecond != b.PacketsPerSecond ||
                Bits(a.SecondsSinceLastPacket) != Bits(b.SecondsSinceLastPacket) ||
                a.SenderIP != b.SenderIP || a.PayloadParsed != b.PayloadParsed)
                throw new InvalidOperationException("Statistics mismatch");
            CheckTiming(a.ArrivalGaps, b.ArrivalGaps);
            CheckTiming(a.OneWayDelay, b.OneWayDelay);
        }
        Console.WriteLine("Statistics differential: PASS, 10000 Record+Read snapshots");
    }

    static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);
    static void CheckTiming(Legacy.PhoneSaberTimingStats a, PhoneSaberTimingStats b)
    {
        if (a.Count != b.Count || Bits(a.MedianMs) != Bits(b.MedianMs) || Bits(a.P95Ms) != Bits(b.P95Ms) ||
            Bits(a.MaxMs) != Bits(b.MaxMs) || Bits(a.MinMs) != Bits(b.MinMs))
            throw new InvalidOperationException("Timing bits mismatch");
    }

    static void MeasureSockets()
    {
        // SendTo の割り当てを除外し、receive thread 上の割り当てだけを数える。
        using var transmitter = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var oldReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        oldReceiver.Client.ReceiveTimeout = 2000;
        var destination = oldReceiver.Client.LocalEndPoint;
        IPEndPoint oldEndpoint = new IPEndPoint(IPAddress.Any, 0);
        long oldBytes = 0;
        for (int i = 0; i < Count + 1000; i++)
        {
            transmitter.SendTo(Packet, destination);
            long before = GC.GetAllocatedBytesForCurrentThread();
            byte[] data = oldReceiver.Receive(ref oldEndpoint);
            string ip1 = oldEndpoint.Address.ToString();
            string ip2 = oldEndpoint.Address.ToString();
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            if (i >= 1000) oldBytes += delta;
            sink = data.Length + ip1.Length + ip2.Length;
        }
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.ReceiveTimeout = 2000;
        destination = receiver.LocalEndPoint;
        EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
        IPAddress lastAddress = null;
        string cachedIP = "";
        var buffer = new byte[PhoneSaberPacketParser.MaximumDatagramBytes];
        long newBytes = 0;
        for (int i = 0; i < Count + 1000; i++)
        {
            transmitter.SendTo(Packet, destination);
            long before = GC.GetAllocatedBytesForCurrentThread();
            int length = receiver.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref endpoint);
            IPAddress address = ((IPEndPoint)endpoint).Address;
            if (!address.Equals(lastAddress)) { lastAddress = address; cachedIP = address.ToString(); }
            string ip = cachedIP;
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            if (i >= 1000) newBytes += delta;
            if (ip != "127.0.0.1" || length != Packet.Length)
                throw new InvalidOperationException("ReceiveFrom endpoint mismatch");
            sink = length + ip.Length;
        }
        Console.WriteLine("UDP receive + sender before: " + oldBytes + " bytes / " + Count + " packets");
        Console.WriteLine("UDP receive + sender after: " + newBytes + " bytes / " + Count + " packets (runtime socket internals included)");
    }
}
