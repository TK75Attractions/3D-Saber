using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;

// Unity API・ソケットを使わず、運営ログの形式・容量・接続遷移を確認する。
public class PhoneSaberDiagnosticsTests
{
    [Test]
    public void LauncherResolvesEditorPlayerAndExplicitPathsWithoutCwd()
    {
        string root = Path.Combine(Path.GetTempPath(), "診断 worktree " + Guid.NewGuid().ToString("N"));
        string launcher = Path.Combine(root, "PhoneSaber", PhoneSaberTriageReceiverLauncher.LauncherRelativePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(launcher));
            File.WriteAllText(launcher, "fixture");
            foreach (string relative in new[] { "Assets", "Builds/Mac/3D-Saber.app/Contents", "Builds/Mac/3D-Saber.app/Contents/Resources/Data" })
                Assert.AreEqual(launcher, PhoneSaberTriageReceiverLauncher.ResolveLauncherPath(Path.Combine(root, relative), _ => null));
            Assert.AreEqual(launcher, PhoneSaberTriageReceiverLauncher.ResolveLauncherPath(null, _ => " " + launcher + " "));
            Assert.IsNull(PhoneSaberTriageReceiverLauncher.ResolveLauncherPath(null, _ => null));
            File.Delete(launcher);
            Assert.IsNull(PhoneSaberTriageReceiverLauncher.ResolveLauncherPath(Path.Combine(root, "Builds/Mac/3D-Saber.app/Contents"), _ => null));
            StringAssert.Contains("--no-codex", PhoneSaberTriageReceiverLauncher.BuildShellCommand(launcher));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public void UncleanRunRetainsLastStateAndWatchdogObservationButCleanQuitDoesNotWarn()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-run-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "run.state");
        try
        {
            var first = new PhoneSaberRunJournal(path);
            Assert.IsNull(first.Begin());
            first.Note("pause=True scene=Game");
            string watchdog = Path.Combine(directory, "last-exit");
            File.WriteAllText(watchdog, "exit-code=1 clean-quit=no");
            var second = new PhoneSaberRunJournal(path);
            string previous = second.Begin(watchdog);
            StringAssert.Contains("cause=unknown", previous);
            StringAssert.Contains("pause=True scene=Game", previous);
            StringAssert.Contains("exit-code=1 clean-quit=no", previous);
            second.End(1);
            second.Note("focus=False");
            var third = new PhoneSaberRunJournal(path);
            StringAssert.Contains("quit-callback exit-code=1", third.Begin());
            third.End(0);
            third.Note("focus=False");
            Assert.IsNull(new PhoneSaberRunJournal(path).Begin(watchdog));
            Assert.IsFalse(File.Exists(path + ".tmp"));
            // 保存不能な場所でも起動・終了を妨げない。
            var blocked = new PhoneSaberRunJournal(Path.Combine(path, "child"));
            Assert.DoesNotThrow(() => { blocked.Begin(); blocked.Note("quit", true); });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

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
    public void RetentionBudgetCoversSevenDaysAtObservedRateIncludingTheRollingCopy()
    {
        const long mib = 1024 * 1024;
        Assert.GreaterOrEqual(PhoneSaberEventLog.MaximumFileBytes, 2 * mib);
        Assert.GreaterOrEqual(PhoneSaberEventLog.MaximumArchiveBytes, 7 * 24 * mib);
        Assert.LessOrEqual(PhoneSaberEventLog.MaximumArchiveBytes +
            PhoneSaberEventLog.MaximumFileBytes * PhoneSaberEventLog.FileCount, 200000000L);
    }

    [Test]
    public void ActivityEntersIdleOnceAtSixtySecondsAndEitherColourResumesImmediately()
    {
        var activity = new PhoneSaberLogActivity(10);
        Assert.IsFalse(activity.Update(69.999, double.NegativeInfinity, double.NegativeInfinity));
        Assert.IsFalse(activity.IsIdle);
        Assert.IsTrue(activity.Update(70, double.NegativeInfinity, double.NegativeInfinity));
        Assert.IsTrue(activity.IsIdle);
        Assert.IsFalse(activity.Update(100, double.NegativeInfinity, double.NegativeInfinity));
        Assert.IsFalse(activity.Update(101, 101, double.NegativeInfinity));
        Assert.IsFalse(activity.IsIdle);
        Assert.IsTrue(activity.Update(161, 101, double.NegativeInfinity));
        Assert.IsFalse(activity.Update(162, 101, 162));
        Assert.IsFalse(activity.IsIdle);
        // 受信停止時に InputPoint の時刻がリセットされても、最後の座標から60秒までは保持する。
        Assert.IsFalse(activity.Update(221.999, double.NegativeInfinity, double.NegativeInfinity));
        Assert.IsTrue(activity.Update(222, double.NegativeInfinity, double.NegativeInfinity));
        Assert.IsTrue(PhoneSaberLogActivity.IsPeriodic("perf"));
        Assert.IsTrue(PhoneSaberLogActivity.IsPeriodic("latency-estimate"));
        Assert.IsFalse(PhoneSaberLogActivity.IsPeriodic("judge-timing"));
    }

    [Test]
    public void IdleSuppressesOnlyPeriodicLinesAndKeepsTheirOriginalFormatAfterRecovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new PhoneSaberEventLog(Path.Combine(directory, "events.log"));
            var timestamp = DateTimeOffset.UtcNow;
            // コンストラクタの実時計より十分先の、注入した単調時刻で検証する。
            double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp) + 100;
            log.ObserveCoordinates(now, double.NegativeInfinity, double.NegativeInfinity, timestamp, "A");
            log.ObserveCoordinates(now + 10, double.NegativeInfinity, double.NegativeInfinity, timestamp, "A");
            log.Enqueue(timestamp, "A", "SYSTEM", "perf", "suppressed");
            log.Enqueue(timestamp, "A", "RED", "latency-estimate", "suppressed");
            log.Enqueue(timestamp, "A", "SYSTEM", "judge-timing", "kept");
            log.Enqueue(timestamp, "A", "RED", "input-silent", "kept");
            log.ObserveCoordinates(now + 11, double.NegativeInfinity, now + 11, timestamp, "A");
            log.Enqueue(timestamp, "A", "SYSTEM", "perf", "scene=Game fps=60.0");
            log.Enqueue(timestamp, "A", "BLUE", "latency-estimate", "status=estimate");
            log.Flush();
            var lines = File.ReadAllLines(log.LogPath);
            Assert.AreEqual(5, lines.Length);
            StringAssert.Contains("event=idle", lines[0]);
            StringAssert.Contains("event=judge-timing kept", lines[1]);
            StringAssert.Contains("event=input-silent kept", lines[2]);
            Assert.AreEqual(PhoneSaberEventLog.Format(timestamp, "A", "SYSTEM", "perf", "scene=Game fps=60.0").TrimEnd(), lines[3]);
            Assert.AreEqual(PhoneSaberEventLog.Format(timestamp, "A", "BLUE", "latency-estimate", "status=estimate").TrimEnd(), lines[4]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public void ArchiveNamesUseUtcAndPruningHasStrictFourteenDayBoundary()
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 34, 56, TimeSpan.Zero);
        string path = PhoneSaberEventLog.ArchivePath("logs/events.log", now.ToOffset(TimeSpan.FromHours(9)));
        Assert.AreEqual(Path.Combine("logs", "archive", "events-20261021-123456.log"), path);
        Assert.IsFalse(PhoneSaberEventLog.ShouldPruneArchive(path, now.AddDays(14)));
        Assert.IsTrue(PhoneSaberEventLog.ShouldPruneArchive(path, now.AddDays(14).AddSeconds(1)));
        Assert.IsFalse(PhoneSaberEventLog.ShouldPruneArchive(path, now.AddDays(13)));
        foreach (string invalid in new[] { "events.log", "notes-20261001-000000.log", "events-20261301-000000.log", "events-20261001-000000.log.bak" })
            Assert.IsFalse(PhoneSaberEventLog.ShouldPruneArchive(invalid, now));
    }

    [Test]
    public void ArchiveBudgetPrunesOldestFinishedSessionsButProtectsSevenDaysAndActiveFile()
    {
        var now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        string old = PhoneSaberEventLog.ArchivePath("logs/events.log", now.AddDays(-15));
        string middle = PhoneSaberEventLog.ArchivePath("logs/events.log", now.AddDays(-8));
        string recent = PhoneSaberEventLog.ArchivePath("logs/events.log", now.AddDays(-7));
        string active = PhoneSaberEventLog.ArchivePath("logs/events.log", now.AddDays(-20));
        long size = PhoneSaberEventLog.MaximumArchiveBytes / 2;
        var files = new[] { (middle, size, now.AddDays(-8)), (recent, size, now.AddDays(-7)),
            (old, size, now.AddDays(-15)), (active, size, now.AddDays(-20)), ("unrelated.log", size, now.AddDays(-30)) };
        CollectionAssert.AreEqual(new[] { old, middle }, PhoneSaberEventLog.ArchivesToPrune(files, now, active));
        // 容量超過があっても直近7日と書込中の1件は削除しない。
        CollectionAssert.IsEmpty(PhoneSaberEventLog.ArchivesToPrune(new[] {
            (recent, size * 3, now.AddDays(-7)), (active, size * 3, now.AddDays(-20)) }, now, active));
        CollectionAssert.AreEqual(new[] { old }, PhoneSaberEventLog.ArchivesToPrune(new[] {
            (old, 1L, now.AddDays(-15)), (middle, 1L, now.AddDays(-8)) }, now, null));
        // 開始が古くても、直近に書かれたセッションの末尾を失わない。
        CollectionAssert.IsEmpty(PhoneSaberEventLog.ArchivesToPrune(new[] { (old, 1L, now) }, now, null));
    }

    [Test]
    public void ArchivesContainEachSessionAcrossRotationQuitAndRestartWithoutQuit()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            var log = new PhoneSaberEventLog(path);
            log.Enqueue(timestamp, "A", "SYSTEM", "session-start", "first");
            log.Flush();
            string first = PhoneSaberEventLog.ArchivePath(path, timestamp);
            Assert.AreEqual(File.ReadAllText(path), File.ReadAllText(first), "開始時点から複写する");
            using (var file = new FileStream(path, FileMode.Open)) file.SetLength(PhoneSaberEventLog.MaximumFileBytes);
            log.Enqueue(timestamp.AddSeconds(1), "A", "SYSTEM", "judge-timing", "kept-through-rotation");
            log.Enqueue(timestamp.AddSeconds(2), "A", "SYSTEM", "session-quit", "exit-code=0");
            log.Enqueue(timestamp.AddSeconds(3), "A", "SYSTEM", "after-quit", "not-in-first");
            log.Flush();
            Assert.AreEqual(3, File.ReadAllLines(first).Length);
            StringAssert.Contains("event=session-quit", File.ReadAllText(first));
            StringAssert.DoesNotContain("after-quit", File.ReadAllText(first));
            // 同じ logger でも、新しい開始で前セッションを確定する。
            log.Enqueue(timestamp.AddSeconds(4), "A", "SYSTEM", "session-start", "second");
            log.Enqueue(timestamp.AddSeconds(5), "A", "SYSTEM", "session-start", "third");
            log.Flush();
            Assert.AreEqual(1, File.ReadAllLines(PhoneSaberEventLog.ArchivePath(path, timestamp.AddSeconds(4))).Length);
            Assert.AreEqual(1, File.ReadAllLines(PhoneSaberEventLog.ArchivePath(path, timestamp.AddSeconds(5))).Length);
            // プロセス再起動を模擬。同じ秒の archive も上書きしない。
            var restarted = new PhoneSaberEventLog(path);
            restarted.Enqueue(timestamp.AddSeconds(5), "A", "SYSTEM", "session-start", "fourth");
            restarted.Flush();
            StringAssert.Contains("third", File.ReadAllText(PhoneSaberEventLog.ArchivePath(path, timestamp.AddSeconds(5))));
            StringAssert.Contains("fourth", File.ReadAllText(PhoneSaberEventLog.ArchivePath(path, timestamp.AddSeconds(6))));
            Assert.AreEqual("", restarted.LastError);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public void SessionStartPrunesExpiredArchivesAndLeavesOtherFilesAlone()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            var now = DateTimeOffset.UtcNow;
            string old = PhoneSaberEventLog.ArchivePath(path, now.AddDays(-15));
            string recent = PhoneSaberEventLog.ArchivePath(path, now.AddDays(-13));
            Directory.CreateDirectory(Path.GetDirectoryName(old));
            File.WriteAllText(old, "expired");
            File.SetLastWriteTimeUtc(old, now.AddDays(-15).UtcDateTime);
            File.WriteAllText(recent, "kept");
            File.SetLastWriteTimeUtc(recent, now.AddDays(-13).UtcDateTime);
            string unrelated = Path.Combine(Path.GetDirectoryName(old), "notes.log");
            File.WriteAllText(unrelated, "kept");
            var log = new PhoneSaberEventLog(path);
            log.Enqueue(now, "A", "SYSTEM", "session-start", "new");
            log.Flush();
            Assert.AreEqual("", log.LastError);
            Assert.IsFalse(File.Exists(old));
            Assert.AreEqual("kept", File.ReadAllText(recent));
            Assert.AreEqual("kept", File.ReadAllText(unrelated));
            Assert.IsTrue(File.Exists(PhoneSaberEventLog.ArchivePath(path, now)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public void FailedArchiveWriteRetriesWithoutDuplicatingTheRollingLine()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            Directory.CreateDirectory(directory);
            string blocked = Path.Combine(directory, "archive");
            File.WriteAllText(blocked, "block archive directory");
            var timestamp = DateTimeOffset.UtcNow;
            var log = new PhoneSaberEventLog(path);
            log.Enqueue(timestamp, "A", "SYSTEM", "session-start", "first");
            log.Enqueue(timestamp, "A", "SYSTEM", "session-quit", "last");
            log.Flush(); log.Flush();
            Assert.IsNotEmpty(log.LastError);
            Assert.AreEqual(1, File.ReadAllLines(path).Length);
            File.Delete(blocked);
            log.Flush();
            Assert.AreEqual("", log.LastError);
            Assert.AreEqual(File.ReadAllText(path), File.ReadAllText(PhoneSaberEventLog.ArchivePath(path, timestamp)));
            Assert.AreEqual(2, File.ReadAllLines(path).Length);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public void SilenceAndRecoveryAreIndependentAndIncludeGapsBetweenPolls()
    {
        var events = new List<string>();
        var red = new PhoneSaberConnectionEvents(10, true, (kind, detail) => events.Add(kind + " " + detail), () => 0);
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

    [TestCase(0.249, false)]
    [TestCase(0.25, true)]
    [TestCase(0.5, true)]
    [TestCase(1.0, true)]
    [TestCase(1.001, false)]
    public void ShortGapUsesCompletedPacketIntervalWithoutChangingSilence(double gap, bool expected)
    {
        var events = new List<string>();
        var connection = new PhoneSaberConnectionEvents(0, true,
            (kind, detail) => events.Add(kind + " " + detail), () => 1);
        connection.Packet(0, "iphone");
        events.Clear();
        connection.Poll(gap); connection.Poll(gap);
        connection.Packet(gap, "iphone");
        if (gap > 1)
            CollectionAssert.AreEqual(new[] { "input-silent gap-seconds=1.001", "input-back sender=iphone" }, events);
        else if (expected)
            CollectionAssert.AreEqual(new[] { "input-gap gap-seconds=" + gap.ToString("F3", CultureInfo.InvariantCulture) }, events);
        else Assert.IsEmpty(events);
        connection.Poll(gap + 0.1);
        connection.Packet(gap + 0.1, "iphone");
        Assert.AreEqual(gap > 1 ? 2 : expected ? 1 : 0, events.Count, "同じ途絶は再記録しない");
    }

    [Test]
    public void ShortGapsArePerColourAndRequirePacketsInTheSameGameScene()
    {
        int scene = 0;
        var redEvents = new List<string>();
        var blueEvents = new List<string>();
        var red = new PhoneSaberConnectionEvents(0, true, (kind, detail) => redEvents.Add(kind), () => scene);
        var blue = new PhoneSaberConnectionEvents(0, true, (kind, detail) => blueEvents.Add(kind), () => scene);
        red.Packet(0.5, "iphone"); // 起動から初回受信までは対象外。
        red.Packet(1, "iphone"); // メニュー中。
        scene = 1;
        red.Packet(1.5, "iphone"); // 曲への入場を跨ぐ。
        blue.Packet(1.5, "iphone");
        redEvents.Clear(); blueEvents.Clear();
        red.Packet(1.75, "iphone");
        CollectionAssert.AreEqual(new[] { "input-gap" }, redEvents);
        Assert.IsEmpty(blueEvents);
        blue.Packet(2, "iphone");
        CollectionAssert.AreEqual(new[] { "input-gap" }, blueEvents);
        scene = 0;
        red.Packet(2.25, "iphone"); // 退出後。
        scene = 2;
        blue.Packet(2.5, "iphone"); // 別の Game scene への遷移。
        Assert.AreEqual(1, redEvents.Count);
        Assert.AreEqual(1, blueEvents.Count);
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

    [Test]
    public void FailedWriteKeepsTheLineAndWritesItInOrderAfterRecovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            var log = new PhoneSaberEventLog(path);
            Directory.CreateDirectory(path); // ファイル名をディレクトリにして書込失敗を再現。
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "receiver-failed", "item=0");
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "receiver-restart", "item=1");
            Assert.DoesNotThrow(log.Flush);
            Assert.IsNotEmpty(log.LastError);
            Assert.DoesNotThrow(log.Flush);
            // 書込先が回復したら、失敗中の行も失わずに元の順序で書く。
            Directory.Delete(path);
            log.Flush();
            Assert.AreEqual("", log.LastError);
            string[] lines = File.ReadAllLines(path);
            Assert.AreEqual(2, lines.Length);
            StringAssert.EndsWith("item=0", lines[0]);
            StringAssert.EndsWith("item=1", lines[1]);
            log.Flush();
            Assert.AreEqual(2, File.ReadAllLines(path).Length, "成功後に同じ行を二重に書かない");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public void RepeatedBlockedRotationPreservesExistingGenerations()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            Directory.CreateDirectory(directory);
            using (var file = new FileStream(path, FileMode.Create)) file.SetLength(PhoneSaberEventLog.MaximumFileBytes);
            for (int generation = 2; generation < PhoneSaberEventLog.FileCount; generation++)
                File.WriteAllText(PhoneSaberEventLog.GenerationPath(path, generation), "generation=" + generation);
            // 現行ファイルの移動先が塞がれていても、既存の世代を先に削除・移動しない。
            Directory.CreateDirectory(path + ".1");
            File.WriteAllText(Path.Combine(path + ".1", "keep"), "x");
            var log = new PhoneSaberEventLog(path);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "test", "item=" + attempt);
                log.Flush();
                StringAssert.StartsWith("rotate ", log.LastError);
                for (int generation = 2; generation < PhoneSaberEventLog.FileCount; generation++)
                    Assert.AreEqual("generation=" + generation,
                        File.ReadAllText(PhoneSaberEventLog.GenerationPath(path, generation)));
            }
            // 障害が解消したら、保持した世代と現行ログを通常どおり交代する。
            Directory.Delete(path + ".1", true);
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "test", "recovered");
            log.Flush();
            Assert.AreEqual("", log.LastError);
            Assert.AreEqual("generation=2", File.ReadAllText(path + ".3"));
            Assert.AreEqual("generation=3", File.ReadAllText(path + ".4"));
            using (var reader = new StreamReader(path + ".1"))
            {
                reader.BaseStream.Seek(PhoneSaberEventLog.MaximumFileBytes, SeekOrigin.Begin);
                for (int attempt = 0; attempt < 3; attempt++) StringAssert.EndsWith("item=" + attempt, reader.ReadLine());
            }
            StringAssert.Contains("recovered", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public void BlockedRotationKeepsAppendingAndReportsTheError()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "old");
            using (var file = new FileStream(path, FileMode.Open)) file.SetLength(PhoneSaberEventLog.MaximumFileBytes);
            // 世代1の場所を空でないディレクトリにして、Windows で古い世代を開かれた時と同じく移動を失敗させる。
            string blocked = PhoneSaberEventLog.GenerationPath(path, 1);
            Directory.CreateDirectory(blocked);
            File.WriteAllText(Path.Combine(blocked, "keep"), "x");
            var log = new PhoneSaberEventLog(path);
            log.Enqueue(DateTimeOffset.UtcNow, "A", "BLUE", "receiver-start", "after-blocked-rotation");
            Assert.DoesNotThrow(log.Flush);
            StringAssert.StartsWith("rotate ", log.LastError);
            Assert.Greater(new FileInfo(path).Length, PhoneSaberEventLog.MaximumFileBytes);
            using (var reader = new StreamReader(path))
            {
                reader.BaseStream.Seek(PhoneSaberEventLog.MaximumFileBytes, SeekOrigin.Begin);
                StringAssert.Contains("after-blocked-rotation", reader.ReadToEnd());
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public void FinalSuppressedRouteChangeIsLoggedEvenWithoutAnotherChange()
    {
        var events = new List<string>();
        var red = new PhoneSaberConnectionEvents(0, true, (kind, detail) => events.Add(kind + " " + detail));
        red.Packet(0, "192.168.1.2");
        red.Packet(0.2, "127.0.0.1");
        for (int i = 3; i <= 10; i++) red.Packet(i / 10.0, "127.0.0.1");
        red.Poll(1);
        red.Poll(1.1);
        Assert.AreEqual(2, events.Count, "最後の経路切替も1秒後に一度だけ記録する");
        StringAssert.Contains("from=192.168.1.2 to=127.0.0.1", events[1]);
        StringAssert.Contains("route-to=P2P bridge suppressed=1", events[1]);
    }

    [Test]
    public void AlternatingSendersAreLoggedAtMostOncePerSecond()
    {
        var events = new List<string>();
        var red = new PhoneSaberConnectionEvents(0, false, (kind, detail) => events.Add(kind + " " + detail));
        // 2台が同じ port へ 60Hz で交互に送る 3 秒間。
        for (int i = 0; i < 180; i++) red.Packet(0.1 + i / 60.0, i % 2 == 0 ? "192.168.1.2" : "192.168.1.3");
        var changes = events.FindAll(line => line.StartsWith("sender-change"));
        Assert.LessOrEqual(changes.Count, 4, "毎 packet ではなく1秒に1行まで");
        Assert.GreaterOrEqual(changes.Count, 3);
        StringAssert.DoesNotContain("suppressed=", changes[0]);
        StringAssert.Contains("suppressed=59", changes[1]);
    }
}
