using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;

// Unity API・ソケットを使わず、運営ログの形式・容量・接続遷移を確認する。
public class PhoneSaberEventLogTests
{
    [Test]
    public void EmptyPollingDoesNotCreateFilesAndFlushResumesAfterDrain()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            var log = new PhoneSaberEventLog(path);
            for (int i = 0; i < 100; i++) log.Flush();
            Assert.IsFalse(Directory.Exists(directory));
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "first", "");
            log.Flush();
            for (int i = 0; i < 100; i++) log.Flush();
            // 1回128件の上限を超えても残りが次の Flush に届く。
            for (int i = 0; i < 200; i++)
                log.Enqueue(DateTimeOffset.UtcNow, "A", "BLUE", "next", "item=" + i);
            log.Flush();
            Assert.AreEqual(129, File.ReadAllLines(path).Length);
            log.Flush();
            Assert.AreEqual(201, File.ReadAllLines(path).Length);
            Assert.AreEqual("", log.LastError);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public void ConcurrentEnqueueAndPollingKeepEveryLineInOrder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            var log = new PhoneSaberEventLog(path);
            var writer = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                    log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "test", "item=" + i);
            });
            while (!writer.IsCompleted) { log.Flush(); System.Threading.Thread.Yield(); }
            writer.GetAwaiter().GetResult();
            for (int i = 0; i < 3; i++) log.Flush();
            string[] lines = File.ReadAllLines(path);
            Assert.AreEqual(200, lines.Length);
            for (int i = 0; i < lines.Length; i++)
                StringAssert.EndsWith("item=" + i, lines[i]);
            Assert.AreEqual("", log.LastError);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public void FormatUsesUtcAndOneBoundedLineRegardlessOfCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var time = new DateTimeOffset(2026, 10, 7, 12, 34, 56, 789, TimeSpan.FromHours(9));
            var line = PhoneSaberEventLog.Format(time, "A", "RED", "receiver-failed", "error\r\nnext\tline");
            Assert.AreEqual("2026-10-07T03:34:56.789Z station=A colour=RED event=receiver-failed error  next line\n", line);
            StringAssert.Contains("station=none", PhoneSaberEventLog.Format(time, null, null, null, null));
            var bounded = PhoneSaberEventLog.Format(time, "B", "BLUE", "error", new string('熱', 20000));
            Assert.Less(Encoding.UTF8.GetByteCount(bounded), PhoneSaberEventLog.MaximumFileBytes);
            Assert.AreEqual(1, bounded.Split('\n').Length - 1);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestCase(0, 100, 100, false)]
    [TestCase(90, 10, 100, false)]
    [TestCase(90, 11, 100, true)]
    [TestCase(100, 1, 100, true)]
    [TestCase(101, 1, 100, true)]
    public void RotationUsesIncomingByteCountAndKeepsFiveFiles(long current, int incoming, long limit, bool expected)
    {
        Assert.AreEqual(expected, PhoneSaberEventLog.ShouldRotate(current, incoming, limit));
        Assert.AreEqual(5, PhoneSaberEventLog.FileCount);
        Assert.AreEqual("events.log", PhoneSaberEventLog.GenerationPath("events.log", 0));
        Assert.AreEqual("events.log.4", PhoneSaberEventLog.GenerationPath("events.log", 4));
    }

    [Test]
    public void SilenceAndRecoveryAreIndependentAndIncludeGapsBetweenPolls()
    {
        var events = new List<string>();
        var red = new PhoneSaberConnectionEvents(10, true, (kind, detail) => events.Add(kind + " " + detail));
        var blueEvents = new List<string>();
        var blue = new PhoneSaberConnectionEvents(10, true, (kind, detail) => blueEvents.Add(kind));
        red.Poll(11); Assert.IsEmpty(events);
        red.Poll(11.01); red.Poll(12);
        Assert.AreEqual(1, events.Count);
        StringAssert.StartsWith("input-silent", events[0]);
        red.Packet(12, "127.0.0.1");
        StringAssert.Contains("route-to=P2P bridge", events[1]);
        StringAssert.StartsWith("input-back", events[2]);
        red.Packet(12.5, "127.0.0.1");
        Assert.AreEqual(3, events.Count);
        red.Packet(14, "192.168.1.2");
        StringAssert.StartsWith("input-silent", events[3]);
        StringAssert.Contains("route-from=P2P bridge route-to=LAN", events[4]);
        StringAssert.StartsWith("input-back", events[5]);
        blue.Poll(14); Assert.AreEqual(new[] { "input-silent" }, blueEvents);
    }

    [Test]
    public void RotationDropsOldestAndAppendResumesAcrossSessions()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            Directory.CreateDirectory(directory);
            for (int generation = 0; generation < 5; generation++)
                File.WriteAllText(PhoneSaberEventLog.GenerationPath(path, generation), "generation=" + generation);
            using (var file = new FileStream(path, FileMode.Open)) file.SetLength(PhoneSaberEventLog.MaximumFileBytes);
            var log = new PhoneSaberEventLog(path);
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "receiver-start", "first");
            log.Flush();
            Assert.AreEqual("", log.LastError);
            Assert.AreEqual(5, Directory.GetFiles(directory).Length);
            Assert.AreEqual("generation=3", File.ReadAllText(path + ".4"));
            Assert.AreEqual(PhoneSaberEventLog.MaximumFileBytes, new FileInfo(path + ".1").Length);
            var nextSession = new PhoneSaberEventLog(path);
            nextSession.Enqueue(DateTimeOffset.UtcNow, "B", "BLUE", "receiver-start", "second");
            nextSession.Flush();
            StringAssert.Contains("first", File.ReadAllText(path));
            StringAssert.Contains("second", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public void QueueIsBoundedAndUnwritablePathNeverThrows()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            var log = new PhoneSaberEventLog(path);
            for (int i = 0; i < 2000; i++) log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "test", "item=" + i);
            for (int i = 0; i < 5; i++) log.Flush();
            string contents = File.ReadAllText(path);
            StringAssert.Contains("event=queue-overflow dropped=1488", contents);
            Assert.AreEqual(PhoneSaberEventLog.QueueCapacity + 1, contents.Split('\n').Length - 1);
            File.Delete(path);
            Directory.CreateDirectory(path); // ファイル名をディレクトリにして書込失敗を再現。
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "test", "");
            Assert.DoesNotThrow(log.Flush);
            Assert.IsNotEmpty(log.LastError);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
