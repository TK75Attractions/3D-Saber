import math
from pathlib import Path
import tempfile
import unittest

import predict_latency_eval as evaluation
from real_motion_extract import write_rows


STUBS = r'''
namespace UnityEngine {
    public static class Debug { public static void Log(object message) {} }
    public class MonoBehaviour { protected static void DontDestroyOnLoad(object obj) {} }
    public class GameObject { public GameObject(string name) {} public T AddComponent<T>() where T:new() => new T(); }
    public enum RuntimeInitializeLoadType { BeforeSceneLoad }
    public class RuntimeInitializeOnLoadMethodAttribute:System.Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType t) {} }
    public static class Mathf { public static int Clamp(int x,int lo,int hi)=>System.Math.Max(lo,System.Math.Min(x,hi)); }
    public static class PlayerPrefs {
        static System.Collections.Generic.Dictionary<string,int> values=new System.Collections.Generic.Dictionary<string,int>();
        public static int GetInt(string k,int v)=>values.TryGetValue(k,out var n)?n:v;
        public static void SetInt(string k,int v)=>values[k]=v;
        public static string GetString(string k,string v)=>v;
        public static void SetString(string k,string v) {}
        public static void Save() {}
    }
    public static class Time { public static double realtimeSinceStartupAsDouble; public static int frameCount; }
    public static class Application { public static event System.Action onBeforeRender; public static int targetFrameRate=120; public static string platform="stub"; public static void Render()=>onBeforeRender?.Invoke(); }
    public class BeforeRenderOrderAttribute:System.Attribute { public BeforeRenderOrderAttribute(int order) {} }
    public static class SystemInfo { public static string graphicsDeviceType="stub"; }
    public struct FrameTiming { public double gpuFrameTime; }
    public static class FrameTimingManager { public static bool IsFeatureEnabled()=>false; public static void CaptureFrameTimings() {} public static uint GetLatestTimings(uint n,FrameTiming[] values)=>0; }
    public struct RefreshRate { public double value=>60; }
    public struct Resolution { public RefreshRate refreshRateRatio; public int width,height; }
    public struct DisplayInfo { public string name; }
    public static class Screen { public static DisplayInfo mainWindowDisplayInfo; public static int width=1920,height=1080,fullScreenMode=1; public static Resolution currentResolution; }
    public static class QualitySettings { public static int vSyncCount,maxQueuedFrames=2; public static int GetQualityLevel()=>0; }
}
namespace UnityEngine.SceneManagement {
    public struct Scene { public string name=>"stub-scene"; public int handle=>1; }
    public static class SceneManager { public static Scene GetActiveScene()=>default; }
}
public class InputPoint {
    public static InputPoint Instance=new InputPoint();
    public int ReceivedPacketCount,ReceivedPacketCount2,PredictionHorizonMilliseconds=40;
    public bool PredictionAutomatic=true;
    public string PredictionLeadSource="fallback";
    public int PredictionLead(bool blue)=>PredictionHorizonMilliseconds;
    public string PredictionSource(bool blue)=>PredictionLeadSource;
    public long PredictionDraws,PredictionSuppressedDraws;
    public string RedCoordinateSender,BlueCoordinateSender,StationLabel;
    public double LastReceivedMonotonicTime,LastReceivedMonotonicTime2;
    public PhoneSaberMetrics ReadPhoneMetrics(string sender,double now)=>null;
    public PhoneSaberCoordinateTiming RedCoordinateTiming=new PhoneSaberCoordinateTiming(),BlueCoordinateTiming=new PhoneSaberCoordinateTiming();
}
public static class SwingMonotonicClock { public static long Timestamp=>(long)(UnityEngine.Time.realtimeSinceStartupAsDouble*1000000); public static double ToSeconds(long ticks)=>ticks/1000000.0; }
public static class PhoneSaberP2PBridgeProcess { public static bool AutoStartEnabled=false; }
public static class PhoneSaberPlayerSmoke { public static float ResolveSeconds(string[] args)=>0; }
public static class PhoneSaberEventLog { public static string Detail; public static void Record(string station,string colour,string kind,string detail) { Detail=detail; } }
'''

SMOKE = r'''
using System;
using UnityEngine;
class Program {
    static void Require(bool ok,string message) { if(!ok) throw new Exception(message); }
    static PhoneSaberInputStats Stats(double value, double age=.01) {
        var samples=new double[20]; Array.Fill(samples,value);
        return new PhoneSaberInputStats(true,60,age,"",true,oneWayDelay:new PhoneSaberTimingStats(samples));
    }
    static void Main(string[] args) {
        Require(PhoneSaberPredictionSettings.Load("new")==0&&!PhoneSaberPredictionSettings.LoadAutomatic("new"),"default off");
        PhoneSaberPredictionSettings.SaveAutomatic("A",true);
        Require(PhoneSaberPredictionSettings.LoadAutomatic("A")&&!PhoneSaberPredictionSettings.LoadAutomatic("B"),"station isolation");
        Require(PhoneSaberPredictionSettings.AutomaticLead(Stats(32),true,out var used)==32&&used,"measured");
        foreach(var stats in new[] { default(PhoneSaberInputStats), Stats(-1), Stats(32,.2), Stats(double.NaN), Stats(double.PositiveInfinity) })
            Require(PhoneSaberPredictionSettings.AutomaticLead(stats,true,out used)==0&&!used,"fallback");
        Require(PhoneSaberPredictionSettings.AutomaticLead(Stats(32),false,out used)==0&&!used,"unsynchronized");
        Require(PhoneSaberPredictionSettings.AutomaticLead(Stats(135),true,out used)==60&&used,"cap");
        var log=new PhoneSaberPerfLog();
        var update=typeof(PhoneSaberPerfLog).GetMethod("Update",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
        Time.realtimeSinceStartupAsDouble=0;update.Invoke(log,null);
        InputPoint.Instance.PredictionDraws=600;InputPoint.Instance.PredictionSuppressedDraws=240;
        Time.realtimeSinceStartupAsDouble=10;update.Invoke(log,null);
        foreach(var text in new[] {"prediction-mode=auto","prediction-lead-ms=40","prediction-source=fallback","prediction-draws=600","prediction-suppressed-draws=240"})
            Require(PhoneSaberEventLog.Detail.Contains(text),"perf missing "+text);
        Time.realtimeSinceStartupAsDouble=20;update.Invoke(log,null);
        Require(PhoneSaberEventLog.Detail.Contains("prediction-draws=0")&&PhoneSaberEventLog.Detail.Contains("prediction-suppressed-draws=0"),"window delta");
        PhoneSaberEndpointPredictor p=default;
        for(int i=0;i<1000;i++) { p.AddSample(i/60.0,new Vector2(i*.01f,0),new Vector2(i*.01f,1));p.Predict(i/60.0,40,out _,out _); }
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=1000;i<301000;i++) { p.AddSample(i/60.0,new Vector2(i*.01f,0),new Vector2(i*.01f,1));p.Predict(i/60.0,40,out _,out _); }
        Require(GC.GetAllocatedBytesForCurrentThread()==before,"prediction allocation");
        Console.WriteLine("Actual C#9/stub: auto settings, perf window fields PASS; predictor 300000 frames = 0 B");
    }
}
'''


class ForecastTests(unittest.TestCase):
    def test_actual_csharp_settings_perf_and_zero_frame_allocation(self):
        import check_endpoint_predictor_parity as parity
        root = parity.ROOT / 'Assets/Scripts/Managers/Inputsystem/Input'
        discovery = (root / 'PhoneSaberDiscoveryResponder.cs').read_text()
        sources = {name: (root / name).read_text() for name in
                   ('PhoneSaberPredictionSettings.cs', 'PhoneSaberInputStats.cs', 'PhoneSaberPerfLog.cs', 'PhoneSaberLatencyEstimate.cs')}
        sources['Stubs.cs'] = STUBS
        sources['Station.cs'] = 'using System;\n' + discovery[discovery.index('public static class PhoneSaberStation'):]
        self.assertIn('300000 frames = 0 B', parity.check_csharp(extra_sources=sources, program=SMOKE))

    def test_future_samples_do_not_change_a_past_forecast(self):
        for kind in ('current', 'cv', 'gh', 'acceleration', 'adaptive', 'guarded'):
            predictor = evaluation.LegacyPredictor() if kind == 'current' else evaluation.Candidate(kind)
            for i in range(3):
                predictor.add(i / 60, (i / 60, 0, i / 60, 1))
            first = predictor.predict(2 / 60, 40)
            self.assertEqual(first, predictor.predict(2 / 60, 40), kind)

    def test_shared_regression_uses_the_reference_and_actual_csharp(self):
        import check_endpoint_predictor_parity as parity
        self.assertEqual(240, parity.check_python())
        self.assertIn('PASS (240 predictions', parity.check_csharp())

    def test_hold_overshoot_is_zero_even_when_future_endpoint_order_changes(self):
        rows = []
        for i in range(60):
            x = .1 * math.sin(i / 10)
            endpoints = (x, 0, x + .5, 0) if i < 20 else (x + .5, 0, x, 0)
            rows.append(dict(session='synthetic', frame=i, time_s=i/60, color='red',
                             detected=True, predicted=False, width=11, height=6,
                             time_source='captureTimeSeconds', label='synthetic',
                             **dict(zip(('x1', 'y1', 'x2', 'y2'), endpoints))))
        # 3点未満の窓や欠測を補間せず、既知フラグと60Hz分類も確認。
        rows[30]['detected'] = False
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'data.csv'
            write_rows(path, rows)
            report = evaluation.evaluate(path, horizons=(20, 40, 120), kinds=('hold', 'guarded'))
        self.assertEqual({'60fps': 59}, report['cadence'])
        self.assertEqual({'60fps': 2}, report['segments'])
        for row in report['reversal_overshoot']:
            if row['predictor'] == 'hold' and row['n']:
                self.assertEqual(0, row['max'])
        for row in report['results']:
            if row['group'] == 'all':
                self.assertEqual({20: 110, 40: 106, 120: 86}[row['lead_ms']], row['n'])
        self.assertTrue(any(r['group'] == 'after_dropout' and r['n'] for r in report['results']))

    def test_truth_segmentation_retains_single_points_for_causal_history(self):
        from real_motion_eval import load_segments
        rows = []
        for i in range(3):
            rows.append(dict(session='synthetic', frame=i, time_s=i/30, color='red',
                             detected=i != 1, predicted=False, width=11, height=6,
                             time_source='captureTimeSeconds', label='synthetic', x1=i, y1=0, x2=i+1, y2=0))
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'data.csv'
            write_rows(path, rows)
            self.assertEqual([], load_segments(path)[0])
            segments, _ = load_segments(path, minimum_samples=1)
            self.assertEqual([1, 1], [len(s[2]) for s in segments])


if __name__ == '__main__':
    unittest.main()
