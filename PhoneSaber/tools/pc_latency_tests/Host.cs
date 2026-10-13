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
    public static InputPoint Instance;
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

public static class PhoneSaberEndpointPredictor { public const int MaximumHorizonMilliseconds = 60; public const double MaximumGapSeconds = .1; }
