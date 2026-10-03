using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

// P2P bridge の自動起動で、起動 script の場所・引数・無効化の判定が正しいことを確認する。
public class PhoneSaberP2PBridgeProcessTests
{
    static System.Func<string, string> Env(Dictionary<string, string> values) =>
        key => values.TryGetValue(key, out string value) ? value : null;

    [Test]
    public void LauncherIsFoundInTheSiblingSchoolFestivalRepository()
    {
        string path = PhoneSaberP2PBridgeProcess.ResolveLauncherPath(
            "/Users/someone/ws/3D-Saber/Assets", Env(new Dictionary<string, string>()));
        Assert.AreEqual(Path.Combine("/Users/someone/ws", "school-festival",
                                     PhoneSaberP2PBridgeProcess.LauncherRelativePath), path);
    }

    [Test]
    public void EnvironmentVariableOverridesTheLauncherPath()
    {
        string path = PhoneSaberP2PBridgeProcess.ResolveLauncherPath(
            "/x/3D-Saber/Assets",
            Env(new Dictionary<string, string> { { PhoneSaberP2PBridgeProcess.ScriptEnvironmentVariable, " /opt/bridge.py " } }));
        Assert.AreEqual("/opt/bridge.py", path);
    }

    [Test]
    public void ArgumentsForwardToInputPointPortsAndExitWithUnity()
    {
        string arguments = PhoneSaberP2PBridgeProcess.BuildArguments("/a b/phone_saber_p2p_bridge.py", 5005, 5006, 4242);
        StringAssert.StartsWith("\"/a b/phone_saber_p2p_bridge.py\" -- ", arguments);
        StringAssert.Contains("--red-port 5005 --blue-port 5006", arguments);
        StringAssert.Contains("--exit-with-parent 4242", arguments);
        StringAssert.Contains($"--name \"{PhoneSaberP2PBridgeProcess.ServiceName}\"", arguments);
    }

    [Test]
    public void AutoStartCanBeTurnedOff()
    {
        string key = PhoneSaberP2PBridgeProcess.DisableEnvironmentVariable;
        Assert.IsFalse(PhoneSaberP2PBridgeProcess.IsDisabled(Env(new Dictionary<string, string>())));
        Assert.IsTrue(PhoneSaberP2PBridgeProcess.IsDisabled(Env(new Dictionary<string, string> { { key, "0" } })));
        Assert.IsTrue(PhoneSaberP2PBridgeProcess.IsDisabled(Env(new Dictionary<string, string> { { key, "OFF" } })));
        Assert.IsFalse(PhoneSaberP2PBridgeProcess.IsDisabled(Env(new Dictionary<string, string> { { key, "1" } })));
    }

    [Test]
    public void MissingLauncherDoesNotStartAndDoesNotThrow()
    {
        var bridge = new PhoneSaberP2PBridgeProcess();
        string missingAssets = Path.Combine(Path.GetTempPath(), "no-such-workspace", "3D-Saber", "Assets");
        UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
        try
        {
            Assert.IsFalse(bridge.Start(5005, 5006, missingAssets));
        }
        finally
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            bridge.Dispose();
        }
    }
}
