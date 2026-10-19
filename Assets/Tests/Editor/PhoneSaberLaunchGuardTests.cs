using NUnit.Framework;

public class PhoneSaberLaunchGuardTests
{
    [TestCase(new string[] { }, null, true, true, true, TestName = "NoArgsRelaunches")]
    [TestCase(new[] { "3D-Saber" }, null, true, true, true, TestName = "ExecutableOnlyRelaunches")]
    [TestCase(new[] { "-phonesaberStation", "A" }, null, true, true, false, TestName = "WatchdogIsUntouched")]
    [TestCase(new[] { "-phonesaberSmoke", "5" }, null, true, true, false, TestName = "SmokeIsUntouched")]
    [TestCase(new[] { "-phonesaberTool=value" }, null, true, true, false, TestName = "AnyPhoneSaberPrefixIsUntouched")]
    [TestCase(new[] { "-phonesaber" }, null, true, true, false, TestName = "BarePrefixIsUntouched")]
    [TestCase(new string[] { }, "0", true, true, false, TestName = "EnvironmentZeroDisablesGuard")]
    [TestCase(new string[] { }, "", true, true, true, TestName = "EmptyEnvironmentEnablesGuard")]
    [TestCase(new string[] { }, "1", true, true, true, TestName = "EnvironmentOneEnablesGuard")]
    [TestCase(new string[] { }, null, true, false, false, TestName = "MissingLauncherDoesNothing")]
    [TestCase(new string[] { }, null, false, true, false, TestName = "EditorDoesNothing")]
    [TestCase(new string[] { }, null, false, true, false, TestName = "WindowsPlayerDoesNothing")]
    [TestCase(new[] { "-screen-fullscreen", "0", "-logFile", "x" }, null, true, true, true,
        TestName = "UnityArgumentsRelaunch")]
    public void DecisionMatchesLaunchContext(string[] args, string envValue, bool isMacPlayer,
        bool launcherExists, bool expected)
    {
        Assert.AreEqual(expected, PhoneSaberLaunchGuard.ShouldRelaunch(args, envValue, isMacPlayer, launcherExists));
    }
}
