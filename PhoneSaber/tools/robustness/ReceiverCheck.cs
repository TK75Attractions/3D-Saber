using System;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using System.Text;

class ReceiverCheck
{
    static int Main(string[] args)
    {
        int seed = int.Parse(args[0]);
        double seconds = double.Parse(args[1], CultureInfo.InvariantCulture);
        var random = new Random(seed);
        var clock = Stopwatch.StartNew();
        var parser = new PhoneSaberPacketParser();
        foreach (var packet in PhoneSaberParserTestCorpus.Packets()) PhoneSaberParserTestCorpus.Compare(parser, packet);
        var stats = new PhoneSaberPacketStatistics();
        stats.Reset(0);
        var gate = new PhoneSaberSenderGate();
        PhoneSaberSenderGate.Enforce = false;
        int silent = 0, back = 0;
        bool expectedSilent = false;
        double lastEventPacket = 0;
        var events = new PhoneSaberConnectionEvents(0, true, (kind, detail) => {
            if (kind == "input-silent") silent++;
            if (kind == "input-back") back++;
        });
        var arrivals = new Queue<double>();
        double now = 0;
        int count = 0, conflictLocks = 0;
        string[] numbers = { "0", "-0", ".5", "1e999", "NaN", "", "\u2003-3\u00a0", "1\0", "1e-5" };
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            byte[] bytes;
            if (count % 3 == 0)
            {
                bytes = new byte[random.Next(0, count % 101 == 0 ? 65508 : 256)];
                random.NextBytes(bytes);
            }
            else
            {
                string prefix = new[] { "", "ts=1000;", "timestamp=bad;", "ts=;" }[random.Next(4)];
                var text = new StringBuilder(prefix);
                int fields = random.Next(1, 7);
                for (int i = 0; i < fields; i++)
                {
                    if (i > 0) text.Append(',');
                    text.Append(numbers[random.Next(numbers.Length)]);
                }
                bytes = Encoding.UTF8.GetBytes(text.ToString());
            }
            PhoneSaberParserTestCorpus.Compare(parser, bytes);
            now += random.NextDouble() * (count % 31 == 0 ? 2 : .04);
            string sender = count % 17 == 0 ? "127.0.0.1" : "10.0.0." + random.Next(1, 50);
            bool parsed = random.Next(4) != 0;
            if (!gate.Accept(sender, now) || !stats.Record(now, sender, parsed, 1000.02, parsed ? 1000 : (double?)null))
                throw new Exception("detect-only rejected seed=" + seed + " step=" + count);
            arrivals.Enqueue(now);
            while (arrivals.Count > 0 && arrivals.Peek() <= now - 1) arrivals.Dequeue();
            var snapshot = stats.Read(now);
            if (gate.LockedSender != "") conflictLocks++;
            if (snapshot.PacketsPerSecond != arrivals.Count || snapshot.SenderRejectedPackets != 0 ||
                snapshot.PayloadParsed != parsed || snapshot.SenderIP != sender ||
                snapshot.ArrivalGaps.Count > PhoneSaberPacketStatistics.MaximumTimingSamples)
                throw new Exception("statistics invariant seed=" + seed + " step=" + count);
            // Poll だけでも ownership を公称時間内に解放する。
            if (count % 1000 == 999)
            {
                now += PhoneSaberSenderGate.SilenceSeconds + .000001;
                gate.Poll(now);
                stats.PollSenderGate(now);
                if (gate.LockedSender != "" || stats.Read(now).LockedSender != "") throw new Exception("ownership not released");
            }
            if (now - lastEventPacket > 1) expectedSilent = true;
            events.Poll(now);
            if (count % 5 != 0)
            {
                int oldBack = back;
                events.Packet(now, sender);
                if (back - oldBack != (expectedSilent ? 1 : 0)) throw new Exception("input-back count");
                expectedSilent = false;
                lastEventPacket = now;
            }
            if (silent < back || silent - back > 1) throw new Exception("silence/back unbalanced");
            count++;
        }
        if (conflictLocks == 0) throw new Exception("no conflict exercised");
        stats.Read(now + 6);
        if (stats.Read(now + 6).PacketsPerSecond != 0 || stats.Read(now + 6).ArrivalGaps.Count != 0)
            throw new Exception("window not expired");
        Console.WriteLine("PASS seed=" + seed + " iterations=" + count + " seconds=" + clock.Elapsed.TotalSeconds + " conflictLocks=" + conflictLocks);
        return 0;
    }
}
