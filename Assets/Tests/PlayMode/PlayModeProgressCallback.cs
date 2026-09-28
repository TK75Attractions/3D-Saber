using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework.Interfaces;
using UnityEngine;
using UnityEngine.TestRunner;
using Stopwatch = System.Diagnostics.Stopwatch;

[assembly: TestRunCallback(typeof(PlayModeProgressCallback))]

// Unity Test Runnerの無言時間をなくし、長い実時間テストの進捗をログへ残す。
public sealed class PlayModeProgressCallback : ITestRunCallback
{
    readonly Dictionary<string, Stopwatch> activeTests = new Dictionary<string, Stopwatch>();
    Stopwatch runClock;
    string runMode = "UNKNOWN";

    public void RunStarted(ITest testsToRun)
    {
        runClock = Stopwatch.StartNew();
        runMode = testsToRun.FullName.IndexOf("PlayMode", StringComparison.OrdinalIgnoreCase) >= 0 || Application.isPlaying
            ? "PLAYMODE"
            : "EDITMODE";
        Debug.Log($"[UNITY_TEST][RUN] mode={runMode} " +
                  $"discovered={testsToRun.TestCaseCount} state=start");
    }

    public void TestStarted(ITest test)
    {
        var timer = Stopwatch.StartNew();
        activeTests[test.Id] = timer;
        if (test.IsSuite)
        {
            if (IsTestClass(test.Name))
                Debug.Log($"[UNITY_TEST][{runMode}] class={test.Name} elapsed=0.0s state=start");
            return;
        }
        Debug.Log($"[UNITY_TEST][{runMode}] class={ClassName(test.FullName)} " +
                  $"test={test.Name} elapsed=0.0s state=start");
    }

    public void TestFinished(ITestResult result)
    {
        Stopwatch timer;
        if (!activeTests.TryGetValue(result.Test.Id, out timer)) timer = Stopwatch.StartNew();
        timer.Stop();
        activeTests.Remove(result.Test.Id);
        if (result.Test.IsSuite)
        {
            if (IsTestClass(result.Test.Name))
                Debug.Log($"[UNITY_TEST][{runMode}] class={result.Test.Name} " +
                          $"elapsed={timer.Elapsed.TotalSeconds:F1}s state={result.ResultState}");
            return;
        }
        Debug.Log($"[UNITY_TEST][{runMode}] class={ClassName(result.Test.FullName)} " +
                  $"test={result.Test.Name} elapsed={timer.Elapsed.TotalSeconds:F1}s state={result.ResultState}");
    }

    public void RunFinished(ITestResult testResults)
    {
        if (runClock == null) return;
        runClock.Stop();
        int completed = testResults.PassCount + testResults.FailCount +
                        testResults.SkipCount + testResults.InconclusiveCount;
        Debug.Log($"[UNITY_TEST][RUN] mode={runMode} completed={completed} " +
                  $"elapsed={runClock.Elapsed.TotalSeconds:F1}s state={testResults.ResultState}");
#if UNITY_EDITOR
        if (runMode == "PLAYMODE") SavePlayModeResult(testResults, completed);
#endif
    }

#if UNITY_EDITOR
    static void SavePlayModeResult(ITestResult testResults, int completed)
    {
        var xml = testResults.ToXml(true).OuterXml;
        string label;
        if (completed >= 170) label = "playmode-all";
        else if (xml.IndexOf("PhoneSaberReliabilityPlayTests.", StringComparison.Ordinal) >= 0)
            label = "phonesaber-playmode";
        else if (xml.IndexOf("FavoriteEffectsPlayTests.", StringComparison.Ordinal) >= 0)
            label = "playmode-favorite-effects";
        else label = "playmode-other";

        var path = Path.Combine(Path.GetTempPath(), $"3d-saber-{label}.xml");
        try
        {
            File.WriteAllText(path, xml);
            Debug.Log($"[UNITY_TEST][RESULT] group={label} completed={completed} xml={path}");
        }
        catch (Exception error)
        {
            Debug.LogError($"[UNITY_TEST][RESULT] group={label} state=XML_WRITE_FAILED " +
                           $"xml={path} error={error.GetType().Name}: {error.Message}");
        }
    }
#endif

    static bool IsTestClass(string name)
    {
        return !string.IsNullOrEmpty(name) &&
               (name.EndsWith("Tests", StringComparison.Ordinal) || name.EndsWith("Test", StringComparison.Ordinal));
    }

    static string ClassName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return "unknown";
        var separator = fullName.LastIndexOf('.');
        var name = separator >= 0 ? fullName.Substring(0, separator) : fullName;
        var nested = name.LastIndexOf('.');
        return nested >= 0 ? name.Substring(nested + 1) : name;
    }
}
