using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Globalization;

// 同じ保存処理の直前・直後を一時ソースで計測する。ゲームや認識のコードは実行しない。
static class EventLogWriterHost
{
    static int delay, ioCompleted;
    static long ioStart, ioBytesStart, ioBytes, ioTicks;
    const int Samples = 80, LinesPerSample = 4;
    static readonly DateTimeOffset Timestamp = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    public static void BeforeIo()
    {
        ioBytesStart = GC.GetAllocatedBytesForCurrentThread();
        ioStart = Stopwatch.GetTimestamp();
        if (delay > 0) Thread.Sleep(delay);
    }

    public static void AfterIo()
    {
        ioTicks += Stopwatch.GetTimestamp() - ioStart;
        ioBytes += GC.GetAllocatedBytesForCurrentThread() - ioBytesStart;
        Interlocked.Increment(ref ioCompleted);
    }

    static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine("Runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Console.WriteLine("OS: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        try
        {
            GateProof(args[0]);
            Benchmark(args[0], 0);
            Benchmark(args[0], 50);
            Benchmark(args[0], 200);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    static void GateProof(string directory)
    {
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var continued = new ManualResetEventSlim())
        {
            var log = new PhoneSaberEventLog(Path.Combine(directory, "gate.log"), () => { entered.Set(); release.Wait(); });
            log.Enqueue(Timestamp, "A", "RED", "test", "gate");
            var caller = Task.Run(() => { log.Flush(); continued.Set(); });
            try
            {
                Check(entered.Wait(5000), "保存処理がゲートに到達しない");
#if BEFORE
                // Task の待機状態も確認する。解除前には旧 Flush から先へ進めない。
                Check(!continued.IsSet && !caller.IsCompleted, "旧経路で待機を再現できない");
                Console.WriteLine("GATE before: caller waiting / following input blocked while I/O gate closed PASS");
#else
                Check(continued.Wait(5000), "I/O解放前に入力相当処理が進まない");
                Console.WriteLine("GATE after: request returned / following input progressed while I/O gate closed PASS");
#endif
            }
            finally { release.Set(); Check(caller.Wait(5000), "caller timeout"); }
#if !BEFORE
            Check(log.StopAndDrain(5000), "gate drain failed");
#endif
            Check(File.ReadAllLines(log.LogPath).Length == 1, "gate 保存件数");
        }
    }

    static void Benchmark(string root, int stall)
    {
        string directory = Path.Combine(root, "stall-" + stall);
        string path = Path.Combine(directory, "events.log");
        delay = 0;
        var log = new PhoneSaberEventLog(path, BeforeIo);
        // 初回JIT・thread生成を別記し、定常要求の分布から除く。
        log.Enqueue(Timestamp, "A", "SYSTEM", "session-start", "benchmark");
        long cold = Stopwatch.GetTimestamp();
        log.Flush();
        double coldMs = Milliseconds(Stopwatch.GetTimestamp() - cold);
#if !BEFORE
        Check(log.FlushAndWait(5000), "warmup drain failed");
#endif
        delay = stall;
        ioTicks = ioBytes = 0;
        ioCompleted = 0;
        var samples = new double[Samples];
        long callerBytes = 0;
        int continued = 0, steps = 0;
        for (int i = 0; i < Samples; i++)
        {
            for (int j = 0; j < LinesPerSample; j++)
                log.Enqueue(Timestamp.AddMilliseconds(i * LinesPerSample + j + 1),
                    j % 2 == 0 ? "A" : "B", j % 2 == 0 ? "RED" : "BLUE", "test", "item=" + (i * LinesPerSample + j));
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            log.Flush();
            steps++; // 呼出元の次の入力／描画相当処理。
            samples[i] = Milliseconds(Stopwatch.GetTimestamp() - start);
            callerBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (Volatile.Read(ref ioCompleted) <= i) continued++;
#if !BEFORE
            // 各試料の保存完了を計測区間の外で待ち、旧/新でバッチ件数と保存I/Oを揃える。
            Check(log.FlushAndWait(5000), "sample drain failed");
#endif
        }
        delay = 0;
        log.Enqueue(Timestamp.AddSeconds(1), "A", "SYSTEM", "session-quit", "benchmark");
#if BEFORE
        log.Flush();
#else
        Check(log.StopAndDrain(5000), "final drain failed");
#endif
        Check(ioCompleted == Samples + 1, "旧/新の保存バッチ数不一致");
        string content = File.ReadAllText(path);
        string archive = PhoneSaberEventLog.ArchivePath(path, Timestamp);
        Check(content == File.ReadAllText(archive), "本体/archive不一致");
        Check(File.ReadAllLines(path).Length == Samples * LinesPerSample + 2, "保存行数不一致");
        // 全行を期待値と照合し、旧/新とも同一データであることを検証する。
        var expected = new System.Text.StringBuilder(PhoneSaberEventLog.Format(Timestamp, "A", "SYSTEM", "session-start", "benchmark"));
        for (int i = 0; i < Samples * LinesPerSample; i++)
            expected.Append(PhoneSaberEventLog.Format(Timestamp.AddMilliseconds(i + 1), i % 2 == 0 ? "A" : "B",
                i % 2 == 0 ? "RED" : "BLUE", "test", "item=" + i));
        expected.Append(PhoneSaberEventLog.Format(Timestamp.AddSeconds(1), "A", "SYSTEM", "session-quit", "benchmark"));
        Check(content == expected.ToString(), "保存順序/内容不一致");
        Array.Sort(samples);
        using (var hash = SHA256.Create())
        {
            string sha = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
            Console.WriteLine($"BENCH stall={stall}ms n={Samples} caller-ms p50={samples[39]:F6} p95={samples[75]:F6} max={samples[79]:F6} cold={coldMs:F6}" +
                $" continued-before-io-complete={continued}/{steps} caller-bytes={callerBytes} io-thread-bytes={ioBytes} io-ms={Milliseconds(ioTicks):F3}" +
                $" saved={Samples * LinesPerSample + 2} rolling=archive=expected sha256={sha}");
        }
    }

    static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}

// Unityオブジェクトと設定の参照だけを代役にする。保存処理・時計・行形式は実ソース。
public sealed class InputPoint
{
    public static InputPoint Instance => null;
    public double LastReceivedMonotonicTime => double.NegativeInfinity;
    public double LastReceivedMonotonicTime2 => double.NegativeInfinity;
}
public static class PhoneSaberStation { public static string Read() => "A"; }
