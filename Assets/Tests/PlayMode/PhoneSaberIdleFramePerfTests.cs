using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 通常の回帰テストからは除外。環境変数と testFilter で同じ条件の前後測定を行う。
public class PhoneSaberIdleFramePerfTests
{
    [UnityTest]
    public IEnumerator HiddenControlsAndIdleInput_FrameCost()
    {
        if (Environment.GetEnvironmentVariable("PHONESABER_FRAME_BENCHMARK") != "1")
            Assert.Ignore("PHONESABER_FRAME_BENCHMARK=1 で明示的に測定する");

        const int frames = 600;
        int oldRate = Application.targetFrameRate;
        int oldVsync = QualitySettings.vSyncCount;
        var inputObject = new GameObject("IdleFramePerfInput");
        inputObject.SetActive(false);
        var input = inputObject.AddComponent<InputPoint>();
        input.port = FreePort();
        do { input.port2 = FreePort(); } while (input.port2 == input.port);
        input.useImuFallback = false;
        inputObject.SetActive(true);
        var saberObject = new GameObject("IdleFramePerfSaber");
        var saber = saberObject.AddComponent<SaberInputBridge>();
        saber.fallbackToMouse = false;
        var saberObject2 = new GameObject("IdleFramePerfSaber2");
        var saber2 = saberObject2.AddComponent<SaberInputBridge>();
        saber2.stickIndex = 2;
        saber2.fallbackToMouse = false;
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;
        try
        {
            for (int i = 0; i < 120; i++) yield return null;
            Assert.IsTrue(input != null && input.isActiveAndEnabled);
            Assert.AreSame(input, InputPoint.Instance, "他のテストの受信機が残らない単独の testFilter で測定する");
            var overlay = Object.FindFirstObjectByType<PhoneSaberOperatorOverlay>();
            var probe = Object.FindFirstObjectByType<PhoneSaberLatencyProbe>();
            Assert.IsNotNull(overlay);
            Assert.IsNotNull(probe);
            Assert.IsFalse((bool)typeof(PhoneSaberOperatorOverlay)
                .GetField("visible", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(overlay));
            Assert.IsFalse((bool)typeof(PhoneSaberLatencyProbe)
                .GetField("active", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(probe));
            using var main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
            using var scripts = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Update.ScriptRunBehaviourUpdate", 1);
            using var gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            Assert.IsTrue(main.Valid, "Main Thread recorder");
            Assert.IsTrue(gc.Valid, "GC Allocated In Frame recorder");
            var mainValues = new long[frames];
            var scriptValues = new long[frames];
            var gcValues = new long[frames];
            for (int run = 0; run < 3; run++)
            {
                for (int i = 0; i < 30; i++) yield return null;
                for (int i = 0; i < frames; i++)
                {
                    yield return null;
                    mainValues[i] = main.LastValue;
                    scriptValues[i] = scripts.LastValue;
                    gcValues[i] = gc.LastValue;
                }
                Debug.Log($"[PhoneSaberIdlePerf] run={run} frames={frames} " +
                    Stats("main-ms", mainValues, 1e-6) + " " +
                    Stats("scripts-ms", scriptValues, 1e-6) + " " +
                    Stats("gc-bytes", gcValues, 1.0) + $" scripts-recorder={scripts.Valid}");
            }

            // delegate は一度だけ作る。reflection 呼出しや計測自体の配列確保を測定に含めない。
            Action updateInput = BindUpdate(input);
            Action updateSaber = BindUpdate(saber);
            Action updateSaber2 = BindUpdate(saber2);
            Action updateOverlay = BindUpdate(overlay);
            Action updateProbe = BindUpdate(probe);
            for (int i = 0; i < 1000; i++)
            {
                updateInput(); updateSaber(); updateSaber2(); updateOverlay(); updateProbe();
            }
            for (int run = 0; run < 5; run++)
            {
                const int iterations = 100000;
                long beforeGc = GC.GetAllocatedBytesForCurrentThread();
                long before = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++)
                {
                    updateInput(); updateSaber(); updateSaber2(); updateOverlay(); updateProbe();
                }
                long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - before;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeGc;
                Debug.Log($"[PhoneSaberIdleUpdates] run={run} iterations={iterations} " +
                    $"us-per-frame={elapsed * 1e6 / System.Diagnostics.Stopwatch.Frequency / iterations:F4} " +
                    $"allocated-bytes={allocated}");
            }
        }
        finally
        {
            Object.Destroy(inputObject);
            Object.Destroy(saberObject);
            Object.Destroy(saberObject2);
            Application.targetFrameRate = oldRate;
            QualitySettings.vSyncCount = oldVsync;
        }
        yield return null;
    }

    static Action BindUpdate(MonoBehaviour target) => (Action)Delegate.CreateDelegate(typeof(Action), target,
        target.GetType().GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance));

    static string Stats(string name, long[] values, double scale)
    {
        long sum = 0;
        foreach (long value in values) sum += value;
        Array.Sort(values);
        return FormattableString.Invariant($"{name}-mean={sum * scale / values.Length:F6} {name}-p50={values[values.Length / 2] * scale:F6} {name}-p95={values[(values.Length - 1) * 95 / 100] * scale:F6}");
    }

    static int FreePort()
    {
        using var socket = new UdpClient(0);
        return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
    }
}
