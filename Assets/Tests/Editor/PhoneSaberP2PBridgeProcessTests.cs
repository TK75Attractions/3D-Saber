using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// P2P bridge の自動起動で、起動 script の場所・引数・公開名・無効化・所有者判定が正しいことを確認する。
// 本物の bridge は起動しない(AutoStartEnabled はアセンブリ全体で false。所有者テストは sleep するだけの偽 launcher)。
public class PhoneSaberP2PBridgeProcessTests
{
    static Func<string, string> Env(Dictionary<string, string> values) =>
        key => values.TryGetValue(key, out string value) ? value : null;

    static readonly Func<string, string> EmptyEnvironment = Env(new Dictionary<string, string>());

    bool savedAutoStart;

    [SetUp]
    public void SaveAutoStart()
    {
        savedAutoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        PhoneSaberP2PBridgeProcess.ResetStateForTests();
    }

    [TearDown]
    public void RestoreAutoStart()
    {
        PhoneSaberP2PBridgeProcess.AutoStartEnabled = savedAutoStart;
        PhoneSaberP2PBridgeProcess.ResetStateForTests();
    }

    [Test]
    public void LauncherIsFoundInTheSiblingSchoolFestivalRepository()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "someone", "ws");
        string dataPath = Path.Combine(workspace, "3D-Saber", "Assets");
        string path = PhoneSaberP2PBridgeProcess.ResolveLauncherPath(dataPath, EmptyEnvironment);
        string expected = Path.Combine(Path.GetFullPath(workspace), "school-festival",
                                       PhoneSaberP2PBridgeProcess.LauncherRelativePath);
        Assert.AreEqual(Path.GetFullPath(expected), Path.GetFullPath(path));
    }

    [Test]
    public void EnvironmentVariableOverridesTheLauncherPath()
    {
        string path = PhoneSaberP2PBridgeProcess.ResolveLauncherPath(
            Path.Combine(Path.GetTempPath(), "x", "3D-Saber", "Assets"),
            Env(new Dictionary<string, string> { { PhoneSaberP2PBridgeProcess.ScriptEnvironmentVariable, " /opt/bridge.py " } }));
        Assert.AreEqual("/opt/bridge.py", path);
    }

    [Test]
    public void ArgumentsForwardToInputPointPortsAndExitWithUnity()
    {
        string arguments = PhoneSaberP2PBridgeProcess.BuildArguments(
            "/a b/phone_saber_p2p_bridge.py", "Phone Saber Unity P2P (Mac)", 5005, 5006, 4242);
        StringAssert.StartsWith("\"/a b/phone_saber_p2p_bridge.py\" -- ", arguments);
        StringAssert.Contains("--red-port 5005 --blue-port 5006", arguments);
        StringAssert.Contains("--exit-with-parent 4242", arguments);
        StringAssert.Contains("--name \"Phone Saber Unity P2P (Mac)\"", arguments);
    }

    [Test]
    public void PrebuildRunsTheLauncherInBuildOnlyMode()
    {
        string arguments = PhoneSaberP2PBridgeProcess.BuildPrebuildArguments("/a b/launcher.py");
        StringAssert.StartsWith("-n 10 /usr/bin/python3 \"/a b/launcher.py\"", arguments);
        StringAssert.EndsWith("--build-only", arguments);
    }

    [Test]
    public void ServiceNameIncludesTheMacName()
    {
        Assert.AreEqual("Phone Saber Unity P2P (Studio Mac)", PhoneSaberP2PBridgeProcess.BuildServiceName("Studio Mac"));
        Assert.AreEqual("Phone Saber Unity P2P (Satoshis-MacBook)",
                        PhoneSaberP2PBridgeProcess.BuildServiceName("Satoshis-MacBook.local"));
        Assert.AreEqual(PhoneSaberP2PBridgeProcess.ServiceNameBase, PhoneSaberP2PBridgeProcess.BuildServiceName(null));
        Assert.AreEqual(PhoneSaberP2PBridgeProcess.ServiceNameBase, PhoneSaberP2PBridgeProcess.BuildServiceName(" \t\n"));
    }

    [Test]
    public void ServiceNameDropsControlCharactersAndQuotes()
    {
        string name = PhoneSaberP2PBridgeProcess.BuildServiceName("A\u0001\"B\\C\u007F");
        Assert.AreEqual("Phone Saber Unity P2P (ABC)", name);
    }

    [Test]
    public void ServiceNameFitsDnsSdInstanceLimit()
    {
        string ascii = PhoneSaberP2PBridgeProcess.BuildServiceName(new string('m', 200));
        Assert.LessOrEqual(Encoding.UTF8.GetByteCount(ascii), PhoneSaberP2PBridgeProcess.MaxServiceNameBytes);
        StringAssert.EndsWith(")", ascii);

        // 多 byte 文字・surrogate pair を途中で切らない。
        string japanese = PhoneSaberP2PBridgeProcess.BuildServiceName("瀬高の\U0001F600MacBook Pro 文化祭用の展示機です");
        Assert.LessOrEqual(Encoding.UTF8.GetByteCount(japanese), PhoneSaberP2PBridgeProcess.MaxServiceNameBytes);
        StringAssert.StartsWith("Phone Saber Unity P2P (瀬高の", japanese);
        StringAssert.EndsWith(")", japanese);
        StringAssert.Contains("\U0001F600", japanese);
        for (int i = 0; i < japanese.Length; i++)
        {
            if (char.IsHighSurrogate(japanese[i])) Assert.IsTrue(char.IsSurrogatePair(japanese, i++));
            else Assert.IsFalse(char.IsLowSurrogate(japanese[i]));
        }
    }

    [Test]
    public void BridgeWarningsUseExplicitPrefixes()
    {
        Assert.IsTrue(PhoneSaberP2PBridgeProcess.IsBridgeWarning("[P2P] listener failed: POSIX 48; exiting"));
        Assert.IsTrue(PhoneSaberP2PBridgeProcess.IsBridgeWarning("[P2P] cannot open the local forwarding socket"));
        Assert.IsTrue(PhoneSaberP2PBridgeProcess.IsBridgeWarning("[P2P] bridge build failed:"));
        Assert.IsTrue(PhoneSaberP2PBridgeProcess.IsBridgeWarning("[P2P] diag relay: cannot start: POSIX 48"));
        Assert.IsFalse(PhoneSaberP2PBridgeProcess.IsBridgeWarning("[P2P] diag relay: forwarding to 127.0.0.1:5007"));
        Assert.IsFalse(PhoneSaberP2PBridgeProcess.IsBridgeWarning("[P2P] peer phone failed: cancelled"));
        Assert.IsFalse(PhoneSaberP2PBridgeProcess.IsBridgeWarning("[P2P] last 10s: RED=0 failed=0 peers=0"));
        Assert.IsFalse(PhoneSaberP2PBridgeProcess.IsBridgeWarning(null));
    }

    [Test]
    public void AutoStartCanBeTurnedOff()
    {
        string key = PhoneSaberP2PBridgeProcess.DisableEnvironmentVariable;
        Assert.IsFalse(PhoneSaberP2PBridgeProcess.IsDisabled(EmptyEnvironment));
        Assert.IsTrue(PhoneSaberP2PBridgeProcess.IsDisabled(Env(new Dictionary<string, string> { { key, "0" } })));
        Assert.IsTrue(PhoneSaberP2PBridgeProcess.IsDisabled(Env(new Dictionary<string, string> { { key, "OFF" } })));
        Assert.IsFalse(PhoneSaberP2PBridgeProcess.IsDisabled(Env(new Dictionary<string, string> { { key, "1" } })));
    }

    [Test]
    public void TestsRunWithAutoStartDisabled()
    {
        // SetUpFixture(PhoneSaberP2PBridgeEditorTestDefaults)が効いていること。
        Assert.IsFalse(savedAutoStart);
    }

    [Test]
    public void DisabledAutoStartNeverLaunches()
    {
        string launcher = WriteFakeLauncher();
        var bridge = new PhoneSaberP2PBridgeProcess();
        try
        {
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = false;
            Assert.IsFalse(bridge.Start(5005, 5006, Application.dataPath, LauncherEnvironment(launcher)));
            Assert.IsFalse(PhoneSaberP2PBridgeProcess.IsRunning);
            LogAssert.NoUnexpectedReceived();
        }
        finally
        {
            bridge.Dispose();
            File.Delete(launcher);
        }
    }

    [Test]
    public void MissingLauncherDoesNotStartAndDoesNotThrow()
    {
        var bridge = new PhoneSaberP2PBridgeProcess();
        string missingAssets = Path.Combine(Path.GetTempPath(), "no-such-workspace", "3D-Saber", "Assets");
        try
        {
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
            LogAssert.Expect(LogType.Warning, new Regex("launcher not found"));
            Assert.IsFalse(bridge.Start(5005, 5006, missingAssets, EmptyEnvironment));
            // 2 回目は同じ警告を繰り返さない。
            Assert.IsFalse(bridge.Start(5005, 5006, missingAssets, EmptyEnvironment));
            LogAssert.NoUnexpectedReceived();
        }
        finally
        {
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = false;
            bridge.Dispose();
        }
    }

    [Test]
    public void StopOnlyStopsTheOwnersProcess()
    {
        Assume.That(PhoneSaberP2PBridgeProcess.IsSupported, "macOS only");
        Assume.That(File.Exists("/usr/bin/python3") &&
                    (Directory.Exists("/Library/Developer/CommandLineTools") || Directory.Exists("/Applications/Xcode.app")),
                    "needs a working /usr/bin/python3");
        Assume.That(!PhoneSaberP2PBridgeProcess.IsRunning, "another bridge is running");

        string launcher = WriteFakeLauncher();
        var first = new PhoneSaberP2PBridgeProcess();
        var second = new PhoneSaberP2PBridgeProcess();
        try
        {
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
            Assert.IsTrue(first.Start(5005, 5006, Application.dataPath, LauncherEnvironment(launcher)));
            Assert.IsTrue(PhoneSaberP2PBridgeProcess.IsRunning);

            second.Stop();
            Assert.IsTrue(PhoneSaberP2PBridgeProcess.IsRunning, "non-owner Stop() must not stop the bridge");
            Assert.IsFalse(second.Start(5005, 5006, Application.dataPath, LauncherEnvironment(launcher)));
            second.Dispose();
            Assert.IsTrue(PhoneSaberP2PBridgeProcess.IsRunning, "non-owner Dispose() must not stop the bridge");

            first.Stop();
            Assert.IsFalse(PhoneSaberP2PBridgeProcess.IsRunning);
        }
        finally
        {
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = false;
            first.Dispose();
            second.Dispose();
            File.Delete(launcher);
        }
    }

    // 本物の bridge の代わりに、引数を無視して眠るだけの launcher。
    static string WriteFakeLauncher()
    {
        string path = Path.Combine(Path.GetTempPath(), $"phonesaber-fake-launcher-{Guid.NewGuid():N}.py");
        File.WriteAllText(path, "import time\ntime.sleep(30)\n");
        return path;
    }

    static Func<string, string> LauncherEnvironment(string launcher) =>
        Env(new Dictionary<string, string> { { PhoneSaberP2PBridgeProcess.ScriptEnvironmentVariable, launcher } });
}
