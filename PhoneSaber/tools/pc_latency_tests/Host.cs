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
    public static class Time { public static double realtimeSinceStartupAsDouble; public static int frameCount; }
    public static class Application { public static event Action onBeforeRender; public static int targetFrameRate = -1; public static string platform = "host"; public static void Render() => onBeforeRender?.Invoke(); }
    public struct Rate { public double value; }
    public struct Resolution { public Rate refreshRateRatio; }
    public static class Screen { public static int width=1920, height=1080, fullScreenMode=1; public static Resolution currentResolution = new Resolution {refreshRateRatio=new Rate {value=60}}; }
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
    public static InputPoint Instance;
    public int ReceivedPacketCount, ReceivedPacketCount2;
#if AFTER
    public PhoneSaberCoordinateTiming RedCoordinateTiming = new PhoneSaberCoordinateTiming(), BlueCoordinateTiming = new PhoneSaberCoordinateTiming();
    // 予測の perf 項目（別ジョブで追加）。既定は OFF。
    public bool PredictionAutomatic;
    public int PredictionHorizonMilliseconds;
    public string PredictionLeadSource = "manual";
    public long PredictionDraws, PredictionSuppressedDraws;
    public string RedCoordinateSender, BlueCoordinateSender, StationLabel;
    public double LastReceivedMonotonicTime, LastReceivedMonotonicTime2;
    public PhoneSaberMetricsStore Metrics = new PhoneSaberMetricsStore();
    public PhoneSaberMetrics ReadPhoneMetrics(string sender, double now) => Metrics.Read(sender, now);
#endif
}
public static class PhoneSaberPlayerSmoke { public static float ResolveSeconds(string[] args) => 0; }
public static class PhoneSaberP2PBridgeProcess { public static bool AutoStartEnabled; }
public static class PhoneSaberStation { public static string Read() => "TEST"; }
public static class PhoneSaberEventLog
{
    public static readonly List<string> Lines = new List<string>();
    public static readonly List<string> Kinds = new List<string>();
    public static void Record(string station, string colour, string kind, string detail) { Lines.Add(detail); Kinds.Add(colour + ":" + kind); }
}
public static class SwingMonotonicClock { public static long Timestamp => (long)(UnityEngine.Time.realtimeSinceStartupAsDouble * 1000000); public static double ToSeconds(long ticks) => ticks / 1000000.0; }
