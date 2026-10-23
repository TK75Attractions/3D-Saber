#if PHONESABER_FRAME_HOST
using System;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;

public static class FrameClock
{
    public static double realtimeSinceStartupAsDouble => 1;
    public static float realtimeSinceStartup => 1;
}
public static class FrameScreen { public static int width => 1920; public static int height => 1080; }

// 座標取込みhostでは、画面・PlayerPrefsを読む推定入口だけを欠測に固定する。
public static class FramePredictionPerfLog
{
    public static PhoneSaberLatencyEstimate Estimate(InputPoint input, bool blue, double now, PhoneSaberTimingStats timing) =>
        new PhoneSaberLatencyEstimate(null, timing, null, 0, now, 60, 0);
}

// Editor は起動しない。実 InputPoint の座標取込みと送信元監視を Unity 同梱 Mono で実行する。
class Frames
{
    static int failed, failureMask;

    static void CheckRegression(Action test, int mask, string message)
    {
        try { test(); }
        catch (AssertionException e)
        {
            // lock 待ち以外の準備失敗や例外を旧実装の回帰再現として数えない。
            if (!e.Message.Contains(message)) throw;
            Console.WriteLine("FAIL " + mask + ": " + e.Message);
            failed++;
            failureMask |= mask;
        }
    }

    static int Main(string[] args)
    {
#if PHONESABER_CALIBRATION_HOST
        RunFixture(typeof(PhoneSaberAcknowledgementTests));
        RunFixture(typeof(PhoneSaberPositionCalibrationTests));
        Console.WriteLine("Calibration host: Unity lifecycle / PlayerPrefs / JSON substituted; actual ACK / mapping / InputPoint methods executed");
        return 0;
#else
        Console.WriteLine(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + " / " +
            System.Runtime.InteropServices.RuntimeInformation.OSDescription + " / " +
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        var tests = new PhoneSaberReceiverStateReadTests();
        // 旧版比較の入力には無視範囲がないため、この回帰は現行入力で実行する。
        if (args.Length == 0 || args[0] == "0")
        {
            foreach (bool ignoredBlue in new[] { false, true })
                foreach (bool published in new[] { false, true })
                    tests.IgnoredPacketDoesNotPublishSenderOrResetCoordinateTiming(ignoredBlue, published);
            Console.WriteLine("Ignored packet source regression: 4 PASS");
        }
        foreach (string gate in new[] { "lockObj", "lockObj2" })
        {
            CheckRegression(() => tests.IdleCoordinatesDoNotWaitForReceiverLock(gate),
                gate == "lockObj" ? 1 : 2, "新着のない色の受信 lock で main thread を止めない");
        }
        tests.IdleCoordinatesAllocateZeroBytes();
        tests.BothColoursConsumePacketsAndAllocateZeroBytesBetweenPackets();
        tests.PendingDiagnosticIsDrainedEvenWithoutNewCoordinates("pendingFreezeReceiveLog1");
        tests.PendingDiagnosticIsDrainedEvenWithoutNewCoordinates("pendingFreezeReceiveLog2");
        bool checkPrediction = args.Length < 2 || args[1] != "skip-prediction";
        if (checkPrediction)
            foreach (bool secondStick in new[] { false, true })
                CheckRegression(() => tests.AutomaticPredictionExpiresBeforeTheNextPeriodicRefresh(secondStick),
                    secondStick ? 16 : 8, "受信時計の期限切れを次の定期更新まで持ち越さない");
        var statsTests = new PhoneSaberInputStatsTests();
        CheckRegression(statsTests.NoConflictPollingDoesNotWaitForStatisticsWriter, 4,
            "競合のない frame は受信統計の lock を待たない");
        statsTests.PollingStillExpiresConflictsAfterResetAndRebind();
        RunFixture(typeof(PhoneSaberInputStatsTests));
        RunFixture(typeof(PhoneSaberSenderGateTests));
        var input = PhoneSaberReceiverStateReadTests.CreateIdleInput();
        Action read = PhoneSaberReceiverStateReadTests.BindIdleRead(input);
        bool zero = Measure("idle-input", i => read());
        Action red = PhoneSaberReceiverStateReadTests.BindPacket(input, false);
        Action blue = PhoneSaberReceiverStateReadTests.BindPacket(input, true);
        zero &= Measure("dual-60Hz-at-1920fps", i =>
        {
            if (i % 32 == 0) { red(); blue(); }
            read();
        });
        var statistics = new PhoneSaberPacketStatistics();
        statistics.Record(0, "phone", true);
        zero &= Measure("sender-poll-no-conflict", i => statistics.PollSenderGate(0.5));
        PhoneSaberReceiverStateReadTests.DestroyIdleInput(input);
        Console.WriteLine($"Frame regression: {(checkPrediction ? 10 : 8) - failed} PASS / {failed} FAIL");
        int expected = args.Length >= 1 ? int.Parse(args[0]) : 0;
        return failureMask == expected && zero ? 0 : 1;
#endif
    }

    static void RunFixture(Type type)
    {
        int passed = 0;
        foreach (var method in type.GetMethods())
        {
            if (method.Name == "NoConflictPollingDoesNotWaitForStatisticsWriter") continue; // 前後比較は上で行う。
            var cases = method.GetCustomAttributes(typeof(TestCaseAttribute), false);
            if (cases.Length == 0 && !method.IsDefined(typeof(TestAttribute), false)) continue;
            if (cases.Length == 0) cases = new object[] { null };
            foreach (var item in cases)
            {
                var target = Activator.CreateInstance(type);
                var args = item == null ? new object[0] : ((TestCaseAttribute)item).Arguments;
                var parameters = method.GetParameters();
                for (int i = 0; i < args.Length; i++)
                    if (args[i] != null) args[i] = Convert.ChangeType(args[i],
                        Nullable.GetUnderlyingType(parameters[i].ParameterType) ?? parameters[i].ParameterType,
                        System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    foreach (var setup in type.GetMethods())
                        if (setup.IsDefined(typeof(SetUpAttribute), false)) setup.Invoke(target, null);
                    method.Invoke(target, args);
                    passed++;
                }
                finally
                {
                    foreach (var teardown in type.GetMethods())
                        if (teardown.IsDefined(typeof(TearDownAttribute), false)) teardown.Invoke(target, null);
                }
            }
        }
        Console.WriteLine(type.Name + ": " + passed + " PASS");
    }

    static bool Measure(string label, Action<int> step)
    {
        const int count = 1000000, repetitions = 21;
        for (int i = 0; i < 10000; i++) step(i);
        var times = new double[repetitions];
        long allocated = 0;
        for (int run = 0; run < repetitions; run++)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++) step(i);
            times[run] = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / count;
            allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
        }
        Array.Sort(times);
        Console.WriteLine(FormattableString.Invariant(
            $"{label} warmup=10000 frames={count} repetitions={repetitions} median-ns={times[repetitions / 2]:F2} p95-ns={times[(int)Math.Ceiling(repetitions * .95) - 1]:F2} allocated-bytes={allocated}"));
        return allocated == 0;
    }
}

#if PHONESABER_CALIBRATION_HOST
// native objectを作らず既存のNUnit全件を実行する。保存はメモリ内で隔離する。
public sealed class CalibrationHostObject : IDisposable
{
    public CalibrationHostObject(string name) { }
    public T AddComponent<T>() where T : class
    {
        var input = PhoneSaberReceiverStateReadTests.CreateIdleInput();
        typeof(InputPoint).GetField("<PositionCapture>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(input, new PhoneSaberPositionCapture());
        return (T)(object)input;
    }
    public void Dispose() { }
}

public static class CalibrationHostPrefs
{
    static readonly System.Collections.Generic.Dictionary<string, string> values = new System.Collections.Generic.Dictionary<string, string>();
    public static bool HasKey(string key) => values.ContainsKey(key);
    public static string GetString(string key, string fallback = "") => values.TryGetValue(key, out var value) ? value : fallback;
    public static int GetInt(string key, int fallback = 0) => int.TryParse(GetString(key), out int value) ? value : fallback;
    public static void SetString(string key, string value) => values[key] = value;
    public static void SetInt(string key, int value) => SetString(key, value.ToString());
    public static void DeleteKey(string key) => values.Remove(key);
    public static void Save() { }
}

public static class CalibrationHostJson
{
    public static string ToJson(object value)
    {
        using (var stream = new System.IO.MemoryStream())
        {
            new System.Runtime.Serialization.Json.DataContractJsonSerializer(value.GetType()).WriteObject(stream, value);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
    }
    public static T FromJson<T>(string value)
    {
        if (string.IsNullOrEmpty(value)) return default;
        try
        {
            using (var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(value)))
                return (T)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        catch (System.Runtime.Serialization.SerializationException e) { throw new ArgumentException("Invalid JSON", e); }
    }
}
#endif

#else
// 実Playerの代用ではない。PerfLogの窓境界と純粋な集計だけをhostで検証する。
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public class MonoBehaviour { protected static void DontDestroyOnLoad(object o) { } }
    public class GameObject { public GameObject(string name) { } public T AddComponent<T>() where T:new() => new T(); }
    public enum RuntimeInitializeLoadType { BeforeSceneLoad }
    public class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType t) { } }
    public class BeforeRenderOrderAttribute : Attribute { public BeforeRenderOrderAttribute(int order) { } }
    public static class Mathf { public static int Clamp(int v,int lo,int hi) => System.Math.Max(lo,System.Math.Min(v,hi)); }
    public static class PlayerPrefs {
        static readonly Dictionary<string,string> values = new Dictionary<string,string>();
        public static string GetString(string k,string d) => values.TryGetValue(k,out var v)?v:d;
        public static void SetString(string k,string v) => values[k]=v;
        public static int GetInt(string k,int d) => int.TryParse(GetString(k,""),out int v)?v:d;
        public static void SetInt(string k,int v) => SetString(k,v.ToString());
        public static void DeleteKey(string k) => values.Remove(k);
        public static void Save() { }
    }
    public static class Debug { public static void Log(object message) {} }
    public static class Time { public static double realtimeSinceStartupAsDouble; public static int frameCount; }
    public static class Application { public static event Action onBeforeRender; public static int targetFrameRate = -1; public static string platform = "host"; public static void Render() => onBeforeRender?.Invoke(); }
    public struct Rate { public double value; }
    public struct Resolution { public Rate refreshRateRatio; public int width, height; }
    public struct DisplayInfo { public string name; }
    public static class Screen { public static DisplayInfo mainWindowDisplayInfo = new DisplayInfo {name="host"}; public static int width=1920, height=1080, fullScreenMode=1; public static Resolution currentResolution = new Resolution {refreshRateRatio=new Rate {value=60}}; }
    public static class QualitySettings { public static int vSyncCount=1, maxQueuedFrames=2; public static int GetQualityLevel() => 0; }
    public static class SystemInfo { public static string graphicsDeviceType = "host"; }
    public struct FrameTiming { public double gpuFrameTime; }
    public static class FrameTimingManager { public static bool IsFeatureEnabled() => false; public static void CaptureFrameTimings() { } public static uint GetLatestTimings(uint n, FrameTiming[] values) => 0; }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene { public string name; public int handle; }
    public static class SceneManager { public static Scene Active = new Scene {name="Menu",handle=1}; public static Scene GetActiveScene() => Active; }
}
public class InputPoint
{
    public static InputPoint Instance { get; set; }
    public int ReceivedPacketCount, ReceivedPacketCount2;
#if AFTER
    public PhoneSaberCoordinateTiming RedCoordinateTiming = new PhoneSaberCoordinateTiming(), BlueCoordinateTiming = new PhoneSaberCoordinateTiming();
    // 予測の perf 項目（別ジョブで追加）。既定は OFF。
    public bool PredictionAutomatic;
    public int PredictionHorizonMilliseconds;
    public string PredictionLeadSource = "manual";
    public int PredictionLead(bool blue) => PredictionHorizonMilliseconds;
    public string PredictionSource(bool blue) => PredictionLeadSource;
    public long PredictionDraws, PredictionSuppressedDraws;
    public string RedCoordinateSender, BlueCoordinateSender, StationLabel;
    public double LastReceivedMonotonicTime, LastReceivedMonotonicTime2;
    public PhoneSaberMetricsStore Metrics = new PhoneSaberMetricsStore();
    public PhoneSaberMetrics ReadPhoneMetrics(string sender, double now) => Metrics.Read(sender, now);
#endif
}
public static class PhoneSaberPlayerSmoke { public static float ResolveSeconds(string[] args) => 0; }
public static class PhoneSaberP2PBridgeProcess { public static bool AutoStartEnabled; }
public static class PhoneSaberStation { public static string Read() => "TEST"; public static string Normalize(string s) => PhoneSaberStationPolicy(s);
    static string PhoneSaberStationPolicy(string s) { s=(s??"").Trim(); return s.Length<=16?s:""; } }
public static class PhoneSaberEventLog
{
    public static readonly List<string> Lines = new List<string>();
    public static readonly List<string> Kinds = new List<string>();
    public static void Record(string station, string colour, string kind, string detail) { Lines.Add(detail); Kinds.Add(colour + ":" + kind); }
}
public static class SwingMonotonicClock { public static long Timestamp => (long)(UnityEngine.Time.realtimeSinceStartupAsDouble * 1000000); public static double ToSeconds(long ticks) => ticks / 1000000.0; }

public static class PhoneSaberEndpointPredictor { public const int MaximumHorizonMilliseconds = 120; public const int MaximumManualHorizonMilliseconds = 60; public const double MaximumGapSeconds = .1; }

#endif
