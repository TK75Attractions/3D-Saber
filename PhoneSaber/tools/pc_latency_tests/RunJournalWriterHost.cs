using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// 保存処理を stub にせず、直前ゲートと完了計測だけを一時ソースに挿入する。
static class RunJournalWriterHost
{
    const int Samples = 80;
    static int delay, saves;
    static long ioAllocated, ioBytesStart;

    public static void BeforeIo()
    {
        ioBytesStart = GC.GetAllocatedBytesForCurrentThread();
        if (delay > 0) Thread.Sleep(delay);
    }

    public static void AfterIo()
    {
        ioAllocated += GC.GetAllocatedBytesForCurrentThread() - ioBytesStart;
        Interlocked.Increment(ref saves);
    }

    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine("Runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Console.WriteLine("OS: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        try
        {
            GateProof(args[0]);
            foreach (int stall in new[] { 0, 50, 200 }) Benchmark(args[0], stall);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    static void GateProof(string root)
    {
        string path = Path.Combine(root, "gate.state");
        bool armed = false;
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var continued = new ManualResetEventSlim())
        {
            var journal = new PhoneSaberRunJournal(path, () => { if (armed) { entered.Set(); release.Wait(); } });
            journal.Begin();
            armed = true;
            var caller = Task.Run(() => { journal.Note("hitch scene=Game"); continued.Set(); });
            try
            {
                Check(entered.Wait(5000), "保存ゲートへ到達しない");
#if BEFORE
                Check(!continued.IsSet && !caller.IsCompleted, "旧Noteの同期停止を再現できない");
                Console.WriteLine("GATE before PASS: I/O gate closed / Note caller and following frame work blocked");
#else
                Check(continued.Wait(5000), "I/O解放前にNote後の処理が進まない");
                object writer = Writer(journal);
                for (int i = 0; i < 10000; i++) journal.Note("burst=" + i);
                Check(Pending(journal) == 1, "待機は最新状態1件のみ");
                Check(ReferenceEquals(writer, Writer(journal)), "通知ごとにwriterが増えている");
                Console.WriteLine("GATE after PASS: I/O gate closed / following frame work progressed; 10000 notes / pending=1 / same writer");
#endif
            }
            finally { release.Set(); Check(caller.Wait(5000), "caller timeout"); }
#if !BEFORE
            Check(journal.FlushAndWait(5000), "gate drain failed");
            Check(File.ReadAllText(path).EndsWith("state=burst=9999", StringComparison.Ordinal), "最後の通知が保存されない");
#endif
            journal.End(0);
            Check(File.ReadAllText(path).StartsWith("clean-quit\n", StringComparison.Ordinal), "clean-quit未保存");
        }
    }

#if !BEFORE
    static object Storage(PhoneSaberRunJournal journal)
        => typeof(PhoneSaberRunJournal).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(journal);
    static object Writer(PhoneSaberRunJournal journal)
    {
        object storage = Storage(journal);
        return storage.GetType().GetField("writer").GetValue(storage);
    }
    static int Pending(PhoneSaberRunJournal journal)
    {
        object storage = Storage(journal);
        lock (storage.GetType().GetField("gate").GetValue(storage))
            return storage.GetType().GetField("pending").GetValue(storage) == null ? 0 : 1;
    }
#endif

    static void Benchmark(string root, int stall)
    {
        string path = Path.Combine(root, "stall-" + stall + ".state");
        delay = 0;
        var journal = new PhoneSaberRunJournal(path, BeforeIo);
        long coldStart = Stopwatch.GetTimestamp();
        journal.Begin();
        double cold = Milliseconds(Stopwatch.GetTimestamp() - coldStart);
        // Noteの初回JITとwriter起動も計測窓から分離する。
        for (int i = 0; i < 8; i++)
        {
            journal.Note("warmup=" + i);
#if !BEFORE
            Check(journal.FlushAndWait(5000), "warmup drain failed");
#endif
        }
        delay = stall;
        saves = 0;
        ioAllocated = 0;
        var samples = new double[Samples];
        long callerBytes = 0;
        int maxPending = 0;
        for (int i = 0; i < Samples; i++)
        {
            string state = (i % 4 == 0 ? "hitch scene=Game" : i % 4 == 1 ? "scene=Result" :
                i % 4 == 2 ? "focus=False" : "pause=True") + " sequence=" + i;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            journal.Note(state);
            // Noteの直後へ進む呼出側時間。保存完了待ちは計測外に置く。
            samples[i] = Milliseconds(Stopwatch.GetTimestamp() - start);
            callerBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
#if !BEFORE
            maxPending = Math.Max(maxPending, Pending(journal));
            Check(journal.FlushAndWait(5000), "sample drain failed");
#endif
            Check(File.ReadAllText(path).EndsWith("state=" + state, StringComparison.Ordinal), "通知列の保存内容不一致");
        }
        Check(saves == Samples, "旧新の保存回数不一致");
        int measuredSaves = saves;
        long measuredIoBytes = ioAllocated;
        string final = File.ReadAllText(path);
        Check(final.EndsWith("state=pause=True sequence=79", StringComparison.Ordinal), "最終状態不一致");
        delay = 0;
        journal.End(0);
        Check(File.ReadAllText(path).StartsWith("clean-quit\n", StringComparison.Ordinal), "終了印未保存");
        Check(!File.Exists(path + ".tmp"), ".tmpが残った");
        Array.Sort(samples);
        Console.WriteLine($"BENCH stall={stall}ms n={Samples} caller-ms p50={samples[39]:F6} p95={samples[75]:F6} max={samples[79]:F6}" +
            $" startup-ms={cold:F6} caller-bytes={callerBytes} io-bytes={measuredIoBytes} max-pending={maxPending}" +
            $" saved={measuredSaves} final=pause=True_sequence=79 end=clean-quit");
#if !BEFORE
        Check(samples[75] < 25, "新版の通知が注入I/O遅延で停止している");
#endif
    }

    static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
