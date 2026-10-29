using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
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
            // 明示指定は GetFullPath で区切りが揃う（Windows では "/" が "\" になる）ので、期待値も揃えて比べる。
            Assert.AreEqual(Path.GetFullPath(launcher), PhoneSaberTriageReceiverLauncher.ResolveLauncherPath(null, _ => " " + launcher + " "));
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
            Assert.IsTrue(first.FlushAndWait(5000));
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
            var fourth = new PhoneSaberRunJournal(path);
            Assert.IsNull(fourth.Begin(watchdog));
            Assert.IsTrue(fourth.End(0, 5000));
            Assert.IsFalse(File.Exists(path + ".tmp"));
            // 保存不能な場所でも起動・終了を妨げない。
            var blocked = new PhoneSaberRunJournal(Path.Combine(path, "child"));
            Assert.DoesNotThrow(() => { blocked.Begin(); blocked.Note("quit", true); blocked.End(1, 5000); });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    static object JournalStorage(PhoneSaberRunJournal journal)
        => typeof(PhoneSaberRunJournal).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(journal);

    static object JournalState(object storage, string field)
        => storage.GetType().GetField(field).GetValue(storage);

    [Test]
    public void JournalBlockedIoKeepsOneLatestStateAndNotificationTimestamp()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-journal-burst-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "run.state");
        bool armed = false;
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var progressed = new ManualResetEventSlim())
        {
            var journal = new PhoneSaberRunJournal(path, () => { if (armed) { entered.Set(); release.Wait(); } });
            try
            {
                journal.Begin();
                armed = true;
                var caller = Task.Run(() => { journal.Note("hitch scene=Game"); progressed.Set(); });
                Assert.IsTrue(entered.Wait(5000));
                Assert.IsTrue(progressed.Wait(5000), "保存ゲートが閉じたまま次の処理へ進む");
                Assert.IsTrue(caller.Wait(5000));
                object storage = JournalStorage(journal);
                object writer = JournalState(storage, "writer");
                DateTime earliest = DateTime.UtcNow;
                for (int i = 0; i < 10000; i++) journal.Note("scene=Game sequence=" + i);
                DateTime latest = DateTime.UtcNow;
                object pending = JournalState(storage, "pending");
                Assert.IsNotNull(pending, "待機スロットは1個");
                string expected = (string)pending.GetType().GetField("text").GetValue(pending);
                StringAssert.EndsWith("state=scene=Game sequence=9999", expected);
                int at = expected.IndexOf(" state-utc=", StringComparison.Ordinal) + 11;
                DateTime timestamp = DateTime.Parse(expected.Substring(at, expected.IndexOf(" state=", at, StringComparison.Ordinal) - at),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                Assert.That(timestamp, Is.InRange(earliest, latest));
                Assert.AreSame(writer, JournalState(storage, "writer"));
                release.Set();
                Assert.IsTrue(journal.FlushAndWait(5000));
                Assert.AreEqual(expected, File.ReadAllText(path), "保存時刻でなく通知時刻を保持する");
                Assert.IsNull(JournalState(storage, "pending"));
                Assert.IsFalse(File.Exists(path + ".tmp"));
            }
            finally
            {
                release.Set();
                journal.End(0, 5000);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }

    [TestCase(0, -1)]
    [TestCase(1, -1)]
    [TestCase(0, 0)]
    [TestCase(1, 0)]
    [TestCase(0, 1)]
    [TestCase(1, 1)]
    public void JournalStartTimeoutAutomaticallyResumesAndCannotHideUncleanRun(int oldExit, int newExit)
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-journal-end-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "run.state");
        bool armed = false;
        using (var oldEntered = new ManualResetEventSlim())
        using (var oldRelease = new ManualResetEventSlim())
        using (var newEntered = new ManualResetEventSlim())
        using (var newRelease = new ManualResetEventSlim())
        {
            var first = new PhoneSaberRunJournal(path, () => { if (armed) { oldEntered.Set(); oldRelease.Wait(); } });
            var second = new PhoneSaberRunJournal(path, () => { newEntered.Set(); newRelease.Wait(); });
            PhoneSaberRunJournal diskRestart = null, third = null;
            try
            {
                first.Begin();
                string firstRun = File.ReadAllText(path);
                armed = true;
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                Assert.IsFalse(first.End(oldExit, 20));
                Assert.Less(elapsed.ElapsedMilliseconds, 1000, "終了待ちは期限付き");
                Assert.IsTrue(oldEntered.Wait(5000), "旧 run の終了保存を停止する");
                object storage = JournalStorage(first);
                object writer = JournalState(storage, "writer");
                StringAssert.Contains("journal-start-unavailable", second.Begin(timeoutMilliseconds: 0));
                Assert.AreEqual("PreviousWriterTimeout", second.LastError);
                string marker = File.ReadAllText(path + ".starting");
                StringAssert.Contains("state=journal-start-unavailable previous-writer-timeout", marker);
                Assert.AreNotEqual(firstRun.Split(' ')[0], marker.Split('\n')[1].Split(' ')[0]);
                for (int i = 0; i < 10000; i++) second.Note("new-run sequence=" + i);
                object pending = JournalState(storage, "pending");
                string latest = (string)pending.GetType().GetField("text").GetValue(pending);
                StringAssert.EndsWith("state=new-run sequence=9999", latest);
                Assert.AreSame(writer, JournalState(storage, "writer"));
                if (newExit >= 0)
                {
                    Assert.IsFalse(second.End(newExit, 0));
                    second.Note("ignored-after-end", true);
                }
                oldRelease.Set();
                // Begin や Flush を再呼出しせずに新 run の実保存へ進む。
                Assert.IsTrue(newEntered.Wait(5000), "旧 writer 復帰だけで新 run の保存を自動再開する");
                Assert.AreSame(writer, JournalState(storage, "writer"));
                string oldSaved = File.ReadAllText(path);
                StringAssert.StartsWith(oldExit == 0 ? "clean-quit\n" : "unclean-or-running\n", oldSaved);
                StringAssert.EndsWith("state=quit-callback exit-code=" + oldExit, oldSaved);
                Assert.AreEqual(marker, File.ReadAllText(path + ".starting"), "旧終了保存で新開始印を消さない");
                // この瞬間のディスクだけを別パスへ複写し、プロセス消失後の読取りを再現する。
                string restartPath = Path.Combine(directory, "restart.state");
                File.Copy(path, restartPath);
                File.Copy(path + ".starting", restartPath + ".starting");
                diskRestart = new PhoneSaberRunJournal(restartPath);
                string warning = diskRestart.Begin();
                StringAssert.Contains("no-clean-quit-observed", warning);
                StringAssert.Contains("journal-start-unavailable", warning);
                StringAssert.Contains(marker.Split('\n')[1].Split(' ')[0], warning);
                Assert.IsTrue(diskRestart.End(0, 5000));
                newRelease.Set();
                Assert.IsTrue(second.FlushAndWait(5000));
                string recovered = File.ReadAllText(path);
                StringAssert.Contains(marker.Split('\n')[1].Split(' ')[0], recovered, "新 run の ID を保持する");
                if (newExit < 0) Assert.AreEqual(latest, recovered, "通知時刻も保持する");
                else
                {
                    StringAssert.StartsWith(newExit == 0 ? "clean-quit\n" : "unclean-or-running\n", recovered);
                    StringAssert.EndsWith("state=quit-callback exit-code=" + newExit, recovered);
                }
                Assert.IsFalse(File.Exists(path + ".starting"));
                Assert.IsFalse(File.Exists(path + ".tmp"));
                first.Note("obsolete", true);
                Assert.IsFalse(first.End(0, 0));
                Assert.AreEqual(recovered, File.ReadAllText(path));
                // newExit=-1 は終了 callback なしで異常終了した run として検出する。
                third = new PhoneSaberRunJournal(path);
                string previous = third.Begin();
                if (newExit == 0) Assert.IsNull(previous);
                else
                {
                    StringAssert.Contains("no-clean-quit-observed", previous);
                    StringAssert.Contains(newExit < 0 ? "new-run sequence=9999" : "quit-callback exit-code=1", previous);
                }
                Assert.IsTrue(third.End(0, 5000));
            }
            finally
            {
                oldRelease.Set();
                newRelease.Set();
                first.End(1, 5000);
                second.End(1, 5000);
                diskRestart?.End(1, 5000);
                third?.End(1, 5000);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }

    [Test]
    public void JournalStartTimeoutAutomaticallySavesStartupWithoutFurtherRequests()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-journal-start-resume-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "run.state");
        bool armed = false;
        using (var oldEntered = new ManualResetEventSlim())
        using (var oldRelease = new ManualResetEventSlim())
        using (var resumed = new ManualResetEventSlim())
        {
            var first = new PhoneSaberRunJournal(path, () => { if (armed) { oldEntered.Set(); oldRelease.Wait(); } });
            var second = new PhoneSaberRunJournal(path, () => resumed.Set());
            try
            {
                first.Begin();
                armed = true;
                Assert.IsFalse(first.End(0, 0));
                Assert.IsTrue(oldEntered.Wait(5000));
                StringAssert.Contains("journal-start-unavailable", second.Begin(timeoutMilliseconds: 0));
                oldRelease.Set();
                Assert.IsTrue(resumed.Wait(5000), "新たな通知・保存要求なしでも開始状態を自動保存する");
                Assert.IsTrue(second.FlushAndWait(5000));
                StringAssert.StartsWith("unclean-or-running\n", File.ReadAllText(path));
                StringAssert.EndsWith("state=startup", File.ReadAllText(path));
                Assert.IsFalse(File.Exists(path + ".starting"));
            }
            finally
            {
                oldRelease.Set();
                first.End(1, 5000);
                second.End(1, 5000);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }

    [TestCase("")]
    [TestCase("unclean-or-running\nrun=interrupted-start state=journal-start-unavailable")]
    public void JournalIncompleteStartMarkerOverridesCleanQuitAndClearsAfterRecovery(string marker)
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-journal-marker-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "run.state");
        var journal = new PhoneSaberRunJournal(path);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "clean-quit\nold-run");
            File.WriteAllText(path + ".starting", marker);
            string watchdog = Path.Combine(directory, "last-exit");
            File.WriteAllText(watchdog, "exit-code=1 clean-quit=no");
            string previous = journal.Begin(watchdog);
            StringAssert.Contains("no-clean-quit-observed", previous);
            StringAssert.Contains("watchdog-last-observation=exit-code=1 clean-quit=no", previous);
            Assert.IsFalse(File.Exists(path + ".starting"));
            Assert.IsTrue(journal.End(0, 5000));
        }
        finally
        {
            journal.End(1, 5000);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public void JournalSaveFailureWaitsForRequestAndRecoversWithoutReplacingGoodState()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-journal-failure-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "run.state");
        int attempts = 0;
        var journal = new PhoneSaberRunJournal(path, () => Interlocked.Increment(ref attempts));
        try
        {
            journal.Begin();
            string startup = File.ReadAllText(path);
            Directory.CreateDirectory(path + ".tmp");
            journal.Note("blocked");
            Assert.IsFalse(journal.FlushAndWait(5000));
            Assert.IsNotEmpty(journal.LastError);
            int failedAttempts = Volatile.Read(ref attempts);
            Thread.Sleep(50);
            Assert.AreEqual(failedAttempts, Volatile.Read(ref attempts), "障害中に busy loop しない");
            Assert.AreEqual(startup, File.ReadAllText(path));
            Directory.Delete(path + ".tmp");
            Assert.IsTrue(journal.FlushAndWait(5000), "失敗した最後の状態を明示要求で再試行する");
            StringAssert.EndsWith("state=blocked", File.ReadAllText(path));
            Assert.IsEmpty(journal.LastError);
            Directory.CreateDirectory(path + ".tmp");
            journal.Note("older-failure");
            Assert.IsFalse(journal.FlushAndWait(5000));
            Directory.Delete(path + ".tmp");
            journal.Note("recovered-latest");
            Assert.IsTrue(journal.FlushAndWait(5000));
            StringAssert.EndsWith("state=recovered-latest", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(path + ".tmp")) Directory.Delete(path + ".tmp");
            journal.End(0, 5000);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public void JournalRestartDiscardsOldPendingStateAndReusesItsSingleWriter()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-journal-restart-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "run.state");
        var first = new PhoneSaberRunJournal(path);
        var second = new PhoneSaberRunJournal(path);
        try
        {
            first.Begin();
            Assert.IsTrue(first.FlushAndWait(5000));
            object storage = JournalStorage(first);
            object writer = JournalState(storage, "writer");
            lock (JournalState(storage, "gate"))
            {
                // writer が待機状態を取り出す前に、Domain Reload 無効時の再開始を再現する。
                first.Note("old-pending", true);
                StringAssert.Contains("state=startup", second.Begin());
                Assert.AreSame(storage, JournalStorage(second));
                Assert.AreSame(writer, JournalState(storage, "writer"));
                Assert.IsNull(JournalState(storage, "pending"));
            }
            Assert.IsTrue(second.FlushAndWait(5000));
            string startup = File.ReadAllText(path);
            StringAssert.StartsWith("unclean-or-running\n", startup);
            StringAssert.EndsWith("state=startup", startup);
            first.Note("old-focus");
            Assert.IsFalse(first.End(0, 0));
            second.Note("second-run");
            Assert.IsTrue(second.End(1, 5000));
            string ended = File.ReadAllText(path);
            Assert.AreEqual(startup.Split(' ')[0], ended.Split(' ')[0], "同じ新runの終了を保存する");
            StringAssert.EndsWith("state=quit-callback exit-code=1", ended);
        }
        finally
        {
            first.End(1, 5000);
            second.End(1, 5000);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    public void JournalFailedStartupAndEndCanRecoverOnExplicitRequestWithoutSpinning(int exitCode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-journal-unavailable-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "blocked", "run.state");
        int attempts = 0;
        var journal = new PhoneSaberRunJournal(path, () => Interlocked.Increment(ref attempts));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.GetDirectoryName(path), "not a directory");
            Assert.IsNull(journal.Begin());
            Assert.IsNotEmpty(journal.LastError);
            Assert.IsFalse(journal.FlushAndWait(5000));
            File.Delete(Path.GetDirectoryName(path));
            Assert.IsTrue(journal.FlushAndWait(5000));
            StringAssert.EndsWith("state=startup", File.ReadAllText(path));
            Directory.CreateDirectory(path + ".tmp");
            journal.Note("pending");
            Assert.IsFalse(journal.End(exitCode, 5000));
            object storage = JournalStorage(journal);
            Assert.IsNull(JournalState(storage, "writer"), "終了時の保存失敗でもwriterは停止する");
            int failedAttempts = Volatile.Read(ref attempts);
            journal.Note("ignored-after-end");
            Thread.Sleep(50);
            Assert.AreEqual(failedAttempts, Volatile.Read(ref attempts));
            Directory.Delete(path + ".tmp");
            Assert.IsTrue(journal.FlushAndWait(5000));
            string saved = File.ReadAllText(path);
            StringAssert.StartsWith(exitCode == 0 ? "clean-quit\n" : "unclean-or-running\n", saved);
            StringAssert.EndsWith("state=quit-callback exit-code=" + exitCode, saved);
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(path + ".tmp")) Directory.Delete(path + ".tmp");
            journal.End(1, 5000);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public void JournalStartupIsSynchronousAndStationEditorPlayerFilesAreIndependent()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-journal-paths-" + Guid.NewGuid().ToString("N"));
        var journals = new List<PhoneSaberRunJournal>();
        try
        {
            foreach (string station in new[] { "A", "B" })
            foreach (string mode in new[] { "editor", "player" })
            {
                string path = Path.Combine(directory, "run-" + station + "-" + mode + ".state");
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, "clean-quit\nold-run");
                File.WriteAllText(path + ".tmp", "stale tmp");
                var journal = new PhoneSaberRunJournal(path);
                journals.Add(journal);
                Assert.IsNull(journal.Begin());
                StringAssert.StartsWith("unclean-or-running\n", File.ReadAllText(path));
                StringAssert.EndsWith("state=startup", File.ReadAllText(path));
                Assert.IsFalse(File.Exists(path + ".tmp"));
                journal.Note("station=" + station + " mode=" + mode);
                Assert.IsTrue(journal.FlushAndWait(5000));
            }
            foreach (string path in Directory.GetFiles(directory))
            {
                string[] name = Path.GetFileNameWithoutExtension(path).Split('-');
                StringAssert.EndsWith("state=station=" + name[1] + " mode=" + name[2], File.ReadAllText(path));
            }
            Assert.AreEqual(4, Directory.GetFiles(directory).Length);
        }
        finally
        {
            foreach (var journal in journals) journal.End(0, 5000);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

}

public class PhoneSaberEventLogTests
{
    // Windows で現行ファイルや世代を別のプロセスが開いたまま（Delete の共有なし）上限に達すると、
    // 世代交代が失敗する。失敗を追記ごとに再試行しても旧世代を1つも失わず、開放後は通常の交代に戻る。
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void SharingViolationPreservesEveryGenerationAcrossRetries(int lockedGeneration)
    {
        if (Path.DirectorySeparatorChar != '\\')
            Assert.Ignore("Windows のファイル共有違反を検証する");

        string folder = Path.Combine(Path.GetTempPath(), "phonesaber-rotation-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "events.log");
        var log = new PhoneSaberEventLog(path);
        try
        {
            using (var initial = new FileStream(path, FileMode.Create, FileAccess.Write))
                initial.SetLength(PhoneSaberEventLog.MaximumFileBytes);
            for (int i = 1; i < PhoneSaberEventLog.FileCount; i++)
                File.WriteAllText(PhoneSaberEventLog.GenerationPath(path, i), "generation-" + i);

            // 追記は許すが、削除・名前変更は許さない読み取り元を再現する。
            using (new FileStream(PhoneSaberEventLog.GenerationPath(path, lockedGeneration),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    log.Enqueue(DateTimeOffset.UtcNow, "B", "SYSTEM", "test", "attempt=" + attempt);
                    Assert.IsTrue(log.FlushAndWait(3000), log.LastError);
                }
                for (int i = 1; i < PhoneSaberEventLog.FileCount; i++)
                    Assert.AreEqual("generation-" + i, File.ReadAllText(PhoneSaberEventLog.GenerationPath(path, i)),
                        "generation " + i + " must survive a failed rotation");
                Assert.IsFalse(Directory.GetFiles(folder, "events.log.rotation-*").Length > 0, "retired file left behind");
            }

            // 読み取り元が閉じたら、次の追記で通常の世代交代に戻れる。
            log.Enqueue(DateTimeOffset.UtcNow, "B", "SYSTEM", "test", "recovered");
            Assert.IsTrue(log.FlushAndWait(3000), log.LastError);
            Assert.Less(new FileInfo(path).Length, PhoneSaberEventLog.MaximumFileBytes);
            // 交代できなかった間の4行は現行ファイルへ追記されたまま .1 へ移る（上限超過は許容）。
            Assert.GreaterOrEqual(new FileInfo(PhoneSaberEventLog.GenerationPath(path, 1)).Length, PhoneSaberEventLog.MaximumFileBytes);
            Assert.AreEqual("generation-1", File.ReadAllText(PhoneSaberEventLog.GenerationPath(path, 2)));
            Assert.AreEqual("generation-3", File.ReadAllText(PhoneSaberEventLog.GenerationPath(path, 4)));
        }
        finally
        {
            log.StopAndDrain(3000);
            Directory.Delete(folder, true);
        }
    }

    readonly List<PhoneSaberEventLog> logs = new List<PhoneSaberEventLog>();

    PhoneSaberEventLog NewLog(string path, Action beforeSave = null)
    {
        var log = new PhoneSaberEventLog(path, beforeSave);
        logs.Add(log);
        return log;
    }

    [TearDown]
    public void StopWriters()
    {
        foreach (var log in logs) log.StopAndDrain(5000);
        logs.Clear();
    }

    void Cleanup(string directory)
    {
        // 障害を残す試験も、再試行する writer を止めてから保存先を削除する。
        StopWriters();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    static object State(PhoneSaberEventLog log, string field)
        => typeof(PhoneSaberEventLog).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(log);

    [Test]
    public void BlockedSavingDoesNotBlockRequestsOrFollowingInputAndCoalescesRequests()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-writer-" + Guid.NewGuid().ToString("N"));
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var progressed = new ManualResetEventSlim())
        {
            var log = NewLog(Path.Combine(directory, "events.log"), () => { entered.Set(); release.Wait(); });
            try
            {
                log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "first", "");
                var caller = Task.Run(() => { log.Flush(); progressed.Set(); });
                Assert.IsTrue(entered.Wait(5000), "保存処理が同期ゲートへ到着する");
                Assert.IsTrue(progressed.Wait(5000), "writer 解放前に保存要求と入力相当処理が進む");
                var writer = State(log, "writer");
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100000; i++) log.Flush();
                long requestBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Assert.AreEqual(0, requestBytes, "要求ごとの Task/仕事を積まない");
                Assert.AreSame(writer, State(log, "writer"));
                Assert.AreEqual(true, State(log, "requested"));
                // ディスクが止まっていても赤青双方の enqueue が完了する。
                var red = Task.Run(() => { for (int i = 0; i < 200; i++) log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "test", "item=" + i); });
                var blue = Task.Run(() => { for (int i = 0; i < 200; i++) log.Enqueue(DateTimeOffset.UtcNow, "B", "BLUE", "test", "item=" + i); });
                Assert.IsTrue(Task.WaitAll(new[] { red, blue }, 5000));
                release.Set();
                Assert.IsTrue(log.StopAndDrain(5000));
                caller.GetAwaiter().GetResult();
                var lines = File.ReadAllLines(log.LogPath);
                Assert.AreEqual(401, lines.Length);
                foreach (string colour in new[] { "RED", "BLUE" })
                {
                    int expected = 0;
                    foreach (string line in lines)
                        if (line.Contains("colour=" + colour + " event=test"))
                            StringAssert.EndsWith("item=" + expected++, line);
                    Assert.AreEqual(200, expected);
                }
            }
            finally
            {
                release.Set();
                log.StopAndDrain(5000);
                Cleanup(directory);
            }
        }
    }

    [Test]
    public void StopDeadlineKeepsOwnershipAndReinitializationReusesBlockedWriter()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-stop-" + Guid.NewGuid().ToString("N"));
        var previous = PhoneSaberEventLog.Current;
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var log = NewLog(Path.Combine(directory, "events.log"), () => { entered.Set(); release.Wait(); });
            try
            {
                PhoneSaberEventLog.Current = log;
                var timestamp = DateTimeOffset.UtcNow;
                // Flush だけが Unity を読むため、この試験では Current を外して通知する。
                log.Enqueue(timestamp, "A", "SYSTEM", "session-start", "first");
                PhoneSaberEventLog.Current = null;
                log.Flush();
                Assert.IsTrue(entered.Wait(5000));
                var writer = State(log, "writer");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Assert.IsFalse(log.StopAndDrain(25));
                Assert.Less(watch.ElapsedMilliseconds, 1000, "無期限 join をしない");
                log.Enqueue(timestamp, "A", "RED", "closed", "not-accepted");
                PhoneSaberEventLog.Current = log;
                PhoneSaberEventLog.InitializeCurrent(log.LogPath);
                Assert.AreSame(log, PhoneSaberEventLog.Current);
                Assert.AreSame(writer, State(log, "writer"));
                log.Enqueue(timestamp.AddSeconds(1), "B", "SYSTEM", "session-start", "reinitialized");
                log.Enqueue(timestamp.AddSeconds(2), "B", "SYSTEM", "session-quit", "last");
                release.Set();
                Assert.IsTrue(log.StopAndDrain(5000));
                var lines = File.ReadAllLines(log.LogPath);
                Assert.AreEqual(3, lines.Length);
                StringAssert.Contains("event=session-quit", lines[2]);
                StringAssert.DoesNotContain("closed", File.ReadAllText(log.LogPath));
                Assert.IsNull(State(log, "writer"));
                // 正常停止したインスタンスも未保存行を捨てず、次回の所有者として再利用する。
                PhoneSaberEventLog.InitializeCurrent(log.LogPath);
                log.Enqueue(timestamp.AddSeconds(3), "A", "RED", "after-resume", "");
                Assert.IsTrue(log.StopAndDrain(5000));
                Assert.AreEqual(4, File.ReadAllLines(log.LogPath).Length);
            }
            finally
            {
                release.Set();
                log.StopAndDrain(5000);
                PhoneSaberEventLog.Current = previous;
                Cleanup(directory);
            }
        }
    }

    [Test]
    public void BlockedWriterKeepsOnly512QueuedLinesAndReportsExactOverflow()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-overflow-" + Guid.NewGuid().ToString("N"));
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var log = NewLog(Path.Combine(directory, "events.log"), () => { entered.Set(); release.Wait(); });
            try
            {
                log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "test", "initial");
                log.Flush();
                Assert.IsTrue(entered.Wait(5000));
                for (int i = 0; i < 2000; i++)
                {
                    log.Enqueue(DateTimeOffset.UtcNow, "B", "BLUE", "test", "item=" + i);
                    log.Flush();
                }
                Assert.AreEqual(512, ((Queue<string>)State(log, "pending")).Count);
                release.Set();
                Assert.IsTrue(log.StopAndDrain(5000));
                var lines = File.ReadAllLines(log.LogPath);
                Assert.AreEqual(513, lines.Length);
                StringAssert.Contains("event=queue-overflow dropped=1489", lines[0]);
                for (int i = 1; i < lines.Length; i++) StringAssert.EndsWith("item=" + (1487 + i), lines[i]);
            }
            finally
            {
                release.Set(); log.StopAndDrain(5000);
                Cleanup(directory);
            }
        }
    }

    [Test]
    public void StoppedFailedWriterRetainsRetryAndRecoversOnNextDrain()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-drain-" + Guid.NewGuid().ToString("N"));
        var log = NewLog(Path.Combine(directory, "events.log"));
        try
        {
            Directory.CreateDirectory(log.LogPath);
            var timestamp = DateTimeOffset.UtcNow;
            log.Enqueue(timestamp, "A", "SYSTEM", "session-start", "");
            log.Enqueue(timestamp.AddSeconds(1), "A", "SYSTEM", "session-quit", "");
            Assert.IsFalse(log.StopAndDrain(5000));
            Assert.IsNotEmpty(log.LastError);
            Assert.IsNull(State(log, "writer"));
            Directory.Delete(log.LogPath);
            Assert.IsTrue(log.StopAndDrain(5000));
            Assert.AreEqual("", log.LastError);
            Assert.AreEqual(2, File.ReadAllLines(log.LogPath).Length);
            Assert.AreEqual(File.ReadAllText(log.LogPath), File.ReadAllText(PhoneSaberEventLog.ArchivePath(log.LogPath, timestamp)));
        }
        finally { log.StopAndDrain(5000); Cleanup(directory); }
    }

    [Test]
    public void ActiveArchiveFailureRetriesOnlyTheArchiveCopy()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-archive-" + Guid.NewGuid().ToString("N"));
        var log = NewLog(Path.Combine(directory, "events.log"));
        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            log.Enqueue(timestamp, "A", "SYSTEM", "session-start", "");
            Assert.IsTrue(log.FlushAndWait(5000));
            string archive = PhoneSaberEventLog.ArchivePath(log.LogPath, timestamp);
            string start = File.ReadAllText(archive);
            File.Delete(archive);
            Directory.CreateDirectory(archive);
            log.Enqueue(timestamp.AddSeconds(1), "A", "RED", "test", "only-once");
            Assert.IsFalse(log.FlushAndWait(5000));
            Assert.AreEqual(2, File.ReadAllLines(log.LogPath).Length);
            Directory.Delete(archive);
            File.WriteAllText(archive, start);
            log.Enqueue(timestamp.AddSeconds(2), "A", "SYSTEM", "session-quit", "");
            Assert.IsTrue(log.StopAndDrain(5000));
            Assert.AreEqual(3, File.ReadAllLines(log.LogPath).Length);
            Assert.AreEqual(File.ReadAllText(log.LogPath), File.ReadAllText(archive));
        }
        finally { log.StopAndDrain(5000); Cleanup(directory); }
    }

    [Test]
    public void InputDisableDoesNotCloseTheSharedLog()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-disable-" + Guid.NewGuid().ToString("N"));
        var previous = PhoneSaberEventLog.Current;
        var input = PhoneSaberReceiverStateReadTests.CreateIdleInput();
#if PHONESABER_FRAME_HOST
        typeof(InputPoint).GetField("networkLifecycleLock", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, new object());
        typeof(InputPoint).GetField("networkShutdown", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, true);
#endif
        var log = NewLog(Path.Combine(directory, "events.log"));
        try
        {
            PhoneSaberEventLog.Current = log;
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "before-disable", "");
            typeof(InputPoint).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, null);
            log.Enqueue(DateTimeOffset.UtcNow, "B", "BLUE", "after-disable", "");
            Assert.IsTrue(log.StopAndDrain(5000));
            var lines = File.ReadAllLines(log.LogPath);
            Assert.AreEqual(2, lines.Length);
            StringAssert.Contains("event=after-disable", lines[1]);
        }
        finally
        {
            PhoneSaberReceiverStateReadTests.DestroyIdleInput(input);
            PhoneSaberEventLog.Current = previous;
            Cleanup(directory);
        }
    }

    [TestCase("A", false, false)]
    [TestCase("A", true, false)]
    [TestCase("B", false, false)]
    [TestCase("B", true, false)]
    [TestCase("A", false, true)]
    [TestCase("A", true, true)]
    [TestCase("B", false, true)]
    [TestCase("B", true, true)]
    public void QuitDuringLatencyMeasurementSavesFinalResultsOnceBeforeClosing(string station, bool disableFirst, bool noSamples)
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-probe-quit-" + Guid.NewGuid().ToString("N"));
        var previous = PhoneSaberEventLog.Current;
        var previousJournal = PhoneSaberRunJournal.Current;
        bool previousMeasuring = PhoneSaberDisplaySync.Measuring;
        var log = NewLog(Path.Combine(directory, "events.log"));
#if PHONESABER_FRAME_HOST
        // native object の生成・検索と画面設定のqueryだけをhostへ置換する。
        var overlay = (PhoneSaberOperatorOverlay)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PhoneSaberOperatorOverlay));
        var probe = (PhoneSaberLatencyProbe)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PhoneSaberLatencyProbe));
        SetPrivate(probe, "loop", new PhoneSaberLatencyLoop());
        foreach (string field in new[] { "blockSamples", "blockPresent", "blockC2s", "blockNetwork", "blockFps" })
            SetPrivate(probe, field, new List<double>());
        EventLogLifecycleHost.Probes = new[] { probe };
        EventLogLifecycleHost.Station = station;
#else
        var root = new UnityEngine.GameObject("計測途中の終了ログ");
        root.SetActive(false);
        var overlay = root.AddComponent<PhoneSaberOperatorOverlay>();
        var probe = root.AddComponent<PhoneSaberLatencyProbe>();
#endif
        try
        {
            PhoneSaberEventLog.Current = log;
            PhoneSaberRunJournal.Current = null;
            PhoneSaberDisplaySync.Measuring = true;
            SetPrivate(overlay, "fallbackStation", station);
            SetPrivate(probe, "active", true);
            SetPrivate(probe, "blockConfiguration", "shutdown-fixture");
            // 初回サンプル前と、1ブロック完了後の途中ブロックの両方を残す。
            SetPrivate(probe, "blockIndex", noSamples ? 0 : 1);
            if (!noSamples)
                ((List<double>)typeof(PhoneSaberLatencyProbe).GetField("blockSamples", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(probe)).AddRange(new[] { 100.0, 120.0, 140.0 });
            var startedAt = DateTimeOffset.UtcNow;
            log.Enqueue(startedAt, station, "SYSTEM", "session-start", "shutdown-fixture");
            if (disableFirst) InvokeLifecycle(probe, "OnDisable");
            InvokeLifecycle(overlay, "OnApplicationQuit");
            // quit より後の Unity callback でも二重記録や破棄がないことを確認する。
            InvokeLifecycle(probe, "OnDisable");
            InvokeLifecycle(probe, "OnDestroy");
            Assert.AreEqual(false, State(log, "accepting"));
            Assert.IsTrue(log.StopAndDrain(5000));
            string content = File.ReadAllText(log.LogPath);
            Assert.AreEqual(content, File.ReadAllText(PhoneSaberEventLog.ArchivePath(log.LogPath, startedAt)));
            var lines = File.ReadAllLines(log.LogPath);
            Assert.AreEqual(5, lines.Length, "開始、途中結果3行、終了を本体・archiveへ一度ずつ保存する");
            string[] events = { "session-start", "latency-block", "display-measured", "latency-loop-end", "session-quit" };
            for (int i = 0; i < events.Length; i++) StringAssert.Contains(" event=" + events[i] + " ", lines[i]);
            StringAssert.Contains("n=" + (noSamples ? 0 : 3), lines[1]);
            StringAssert.Contains("station=" + station, lines[4]);
            Assert.IsFalse(PhoneSaberDisplaySync.Measuring);
        }
        finally
        {
#if PHONESABER_FRAME_HOST
            EventLogLifecycleHost.Probes = Array.Empty<PhoneSaberLatencyProbe>();
#else
            UnityEngine.Object.DestroyImmediate(root);
#endif
            PhoneSaberRunJournal.Current = previousJournal;
            PhoneSaberDisplaySync.Measuring = previousMeasuring;
            PhoneSaberEventLog.Current = previous;
            Cleanup(directory);
        }
    }

    static void SetPrivate(object target, string field, object value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

    static void InvokeLifecycle(object target, string method)
        => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);

    [Test]
    public void EmptyPollingDoesNotCreateFilesAndFlushResumesAfterDrain()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            var savedAtBatchStart = new List<int>();
            var log = NewLog(path, () => savedAtBatchStart.Add(File.Exists(path) ? File.ReadAllLines(path).Length : 0));
            for (int i = 0; i < 100; i++) log.FlushAndWait(5000);
            Assert.IsFalse(Directory.Exists(directory));
            Assert.IsNull(State(log, "writer"), "空pollではthreadも作らない");
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "first", "");
            log.FlushAndWait(5000);
            for (int i = 0; i < 100; i++) log.FlushAndWait(5000);
            // 1バッチ128件の上限を超えても writer が残りを保存する。
            for (int i = 0; i < 200; i++)
                log.Enqueue(DateTimeOffset.UtcNow, "A", "BLUE", "next", "item=" + i);
            log.FlushAndWait(5000);
            Assert.AreEqual(201, File.ReadAllLines(path).Length, "writer が128行ずつ複数バッチを保存する");
            CollectionAssert.AreEqual(new[] { 0, 1, 129 }, savedAtBatchStart, "保存バッチの128行上限も維持する");
            log.FlushAndWait(5000);
            Assert.AreEqual(201, File.ReadAllLines(path).Length);
            Assert.AreEqual("", log.LastError);
        }
        finally { Cleanup(directory); }
    }

    [Test]
    public void ConcurrentEnqueueAndPollingKeepEveryLineInOrder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            var log = NewLog(path);
            var writer = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                    log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "test", "item=" + i);
            });
            while (!writer.IsCompleted) { log.Flush(); Thread.Yield(); }
            writer.GetAwaiter().GetResult();
            for (int i = 0; i < 3; i++) log.FlushAndWait(5000);
            string[] lines = File.ReadAllLines(path);
            Assert.AreEqual(200, lines.Length);
            for (int i = 0; i < lines.Length; i++)
                StringAssert.EndsWith("item=" + i, lines[i]);
            Assert.AreEqual("", log.LastError);
        }
        finally { Cleanup(directory); }
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
            var log = NewLog(Path.Combine(directory, "events.log"));
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
            log.FlushAndWait(5000);
            var lines = File.ReadAllLines(log.LogPath);
            Assert.AreEqual(5, lines.Length);
            StringAssert.Contains("event=idle", lines[0]);
            StringAssert.Contains("event=judge-timing kept", lines[1]);
            StringAssert.Contains("event=input-silent kept", lines[2]);
            Assert.AreEqual(PhoneSaberEventLog.Format(timestamp, "A", "SYSTEM", "perf", "scene=Game fps=60.0").TrimEnd(), lines[3]);
            Assert.AreEqual(PhoneSaberEventLog.Format(timestamp, "A", "BLUE", "latency-estimate", "status=estimate").TrimEnd(), lines[4]);
        }
        finally { Cleanup(directory); }
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
            var log = NewLog(path);
            log.Enqueue(timestamp, "A", "SYSTEM", "session-start", "first");
            log.FlushAndWait(5000);
            string first = PhoneSaberEventLog.ArchivePath(path, timestamp);
            Assert.AreEqual(File.ReadAllText(path), File.ReadAllText(first), "開始時点から複写する");
            using (var file = new FileStream(path, FileMode.Open)) file.SetLength(PhoneSaberEventLog.MaximumFileBytes);
            log.Enqueue(timestamp.AddSeconds(1), "A", "SYSTEM", "judge-timing", "kept-through-rotation");
            log.Enqueue(timestamp.AddSeconds(2), "A", "SYSTEM", "session-quit", "exit-code=0");
            log.Enqueue(timestamp.AddSeconds(3), "A", "SYSTEM", "after-quit", "not-in-first");
            log.FlushAndWait(5000);
            Assert.AreEqual(3, File.ReadAllLines(first).Length);
            StringAssert.Contains("event=session-quit", File.ReadAllText(first));
            StringAssert.DoesNotContain("after-quit", File.ReadAllText(first));
            // 同じ logger でも、新しい開始で前セッションを確定する。
            log.Enqueue(timestamp.AddSeconds(4), "A", "SYSTEM", "session-start", "second");
            log.Enqueue(timestamp.AddSeconds(5), "A", "SYSTEM", "session-start", "third");
            log.FlushAndWait(5000);
            Assert.AreEqual(1, File.ReadAllLines(PhoneSaberEventLog.ArchivePath(path, timestamp.AddSeconds(4))).Length);
            Assert.AreEqual(1, File.ReadAllLines(PhoneSaberEventLog.ArchivePath(path, timestamp.AddSeconds(5))).Length);
            // プロセス再起動を模擬。同じ秒の archive も上書きしない。
            var restarted = NewLog(path);
            restarted.Enqueue(timestamp.AddSeconds(5), "A", "SYSTEM", "session-start", "fourth");
            restarted.FlushAndWait(5000);
            StringAssert.Contains("third", File.ReadAllText(PhoneSaberEventLog.ArchivePath(path, timestamp.AddSeconds(5))));
            StringAssert.Contains("fourth", File.ReadAllText(PhoneSaberEventLog.ArchivePath(path, timestamp.AddSeconds(6))));
            Assert.AreEqual("", restarted.LastError);
        }
        finally { Cleanup(directory); }
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
            var log = NewLog(path);
            log.Enqueue(now, "A", "SYSTEM", "session-start", "new");
            log.FlushAndWait(5000);
            Assert.AreEqual("", log.LastError);
            Assert.IsFalse(File.Exists(old));
            Assert.AreEqual("kept", File.ReadAllText(recent));
            Assert.AreEqual("kept", File.ReadAllText(unrelated));
            Assert.IsTrue(File.Exists(PhoneSaberEventLog.ArchivePath(path, now)));
        }
        finally { Cleanup(directory); }
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
            var log = NewLog(path);
            log.Enqueue(timestamp, "A", "SYSTEM", "session-start", "first");
            log.Enqueue(timestamp, "A", "SYSTEM", "session-quit", "last");
            log.FlushAndWait(5000); log.FlushAndWait(5000);
            Assert.IsNotEmpty(log.LastError);
            Assert.AreEqual(1, File.ReadAllLines(path).Length);
            File.Delete(blocked);
            log.FlushAndWait(5000);
            Assert.AreEqual("", log.LastError);
            Assert.AreEqual(File.ReadAllText(path), File.ReadAllText(PhoneSaberEventLog.ArchivePath(path, timestamp)));
            Assert.AreEqual(2, File.ReadAllLines(path).Length);
        }
        finally { Cleanup(directory); }
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
            var log = NewLog(path);
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "receiver-start", "first");
            log.FlushAndWait(5000);
            Assert.AreEqual("", log.LastError);
            Assert.AreEqual(5, Directory.GetFiles(directory).Length);
            Assert.AreEqual("generation=3", File.ReadAllText(path + ".4"));
            Assert.AreEqual(PhoneSaberEventLog.MaximumFileBytes, new FileInfo(path + ".1").Length);
            var nextSession = NewLog(path);
            nextSession.Enqueue(DateTimeOffset.UtcNow, "B", "BLUE", "receiver-start", "second");
            nextSession.FlushAndWait(5000);
            StringAssert.Contains("first", File.ReadAllText(path));
            StringAssert.Contains("second", File.ReadAllText(path));
        }
        finally { Cleanup(directory); }
    }

    [Test]
    public void QueueIsBoundedAndUnwritablePathNeverThrows()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            var log = NewLog(path);
            for (int i = 0; i < 2000; i++) log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "test", "item=" + i);
            for (int i = 0; i < 5; i++) log.FlushAndWait(5000);
            string contents = File.ReadAllText(path);
            StringAssert.Contains("event=queue-overflow dropped=1488", contents);
            Assert.AreEqual(PhoneSaberEventLog.QueueCapacity + 1, contents.Split('\n').Length - 1);
            File.Delete(path);
            Directory.CreateDirectory(path); // ファイル名をディレクトリにして書込失敗を再現。
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "test", "");
            Assert.DoesNotThrow(() => log.FlushAndWait(5000));
            Assert.IsNotEmpty(log.LastError);
        }
        finally { Cleanup(directory); }
    }

    [Test]
    public void FailedWriteKeepsTheLineAndWritesItInOrderAfterRecovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phonesaber-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.log");
        try
        {
            var log = NewLog(path);
            Directory.CreateDirectory(path); // ファイル名をディレクトリにして書込失敗を再現。
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "receiver-failed", "item=0");
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "receiver-restart", "item=1");
            Assert.DoesNotThrow(() => log.FlushAndWait(5000));
            Assert.IsNotEmpty(log.LastError);
            Assert.DoesNotThrow(() => log.FlushAndWait(5000));
            // 書込先が回復したら、失敗中の行も失わずに元の順序で書く。
            Directory.Delete(path);
            log.FlushAndWait(5000);
            Assert.AreEqual("", log.LastError);
            string[] lines = File.ReadAllLines(path);
            Assert.AreEqual(2, lines.Length);
            StringAssert.EndsWith("item=0", lines[0]);
            StringAssert.EndsWith("item=1", lines[1]);
            log.FlushAndWait(5000);
            Assert.AreEqual(2, File.ReadAllLines(path).Length, "成功後に同じ行を二重に書かない");
        }
        finally { Cleanup(directory); }
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
            var log = NewLog(path);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "test", "item=" + attempt);
                log.FlushAndWait(5000);
                StringAssert.StartsWith("rotate ", log.LastError);
                for (int generation = 2; generation < PhoneSaberEventLog.FileCount; generation++)
                    Assert.AreEqual("generation=" + generation,
                        File.ReadAllText(PhoneSaberEventLog.GenerationPath(path, generation)));
            }
            // 障害が解消したら、保持した世代と現行ログを通常どおり交代する。
            Directory.Delete(path + ".1", true);
            log.Enqueue(DateTimeOffset.UtcNow, "A", "RED", "test", "recovered");
            log.FlushAndWait(5000);
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
        finally { Cleanup(directory); }
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
            var log = NewLog(path);
            log.Enqueue(DateTimeOffset.UtcNow, "A", "BLUE", "receiver-start", "after-blocked-rotation");
            Assert.DoesNotThrow(() => log.FlushAndWait(5000));
            StringAssert.StartsWith("rotate ", log.LastError);
            Assert.Greater(new FileInfo(path).Length, PhoneSaberEventLog.MaximumFileBytes);
            using (var reader = new StreamReader(path))
            {
                reader.BaseStream.Seek(PhoneSaberEventLog.MaximumFileBytes, SeekOrigin.Begin);
                StringAssert.Contains("after-blocked-rotation", reader.ReadToEnd());
            }
        }
        finally { Cleanup(directory); }
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
