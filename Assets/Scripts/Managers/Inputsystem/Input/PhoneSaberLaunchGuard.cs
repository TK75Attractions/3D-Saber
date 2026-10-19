using System;
using System.IO;
using UnityEngine;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

// macOS のウインドウ復元などによる直接起動を、監視付きランチャーへ戻す。
public static class PhoneSaberLaunchGuard
{
    const string LauncherPath = "/Applications/Beat Trace Slash.app";

    public static bool ShouldRelaunch(string[] args, string envValue, bool isMacPlayer, bool launcherExists)
    {
        if (!isMacPlayer || envValue == "0" || !launcherExists) return false;
        if (args != null)
            foreach (string arg in args)
                if (arg != null && arg.StartsWith("-phonesaber", StringComparison.Ordinal)) return false;
        return true;
    }

    // 最大 20 秒、この PID の終了を待ってからランチャーを開く。
    public static string RelaunchScript(int pid) =>
        "-c \"(i=0; while /bin/kill -0 " + pid + " 2>/dev/null && [ $i -lt 100 ]; do sleep 0.2; i=$((i+1)); done; " +
        "/usr/bin/open -a '" + LauncherPath + "') >/dev/null 2>&1 &\"";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void RelaunchAtStartup()
    {
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
        if (Application.isBatchMode) return;
        string[] args = Environment.GetCommandLineArgs();
        string envValue = Environment.GetEnvironmentVariable("PHONESABER_LAUNCH_GUARD");
        bool launcherExists = Directory.Exists(LauncherPath);
        if (!ShouldRelaunch(args, envValue, true, launcherExists))
        {
            if (!launcherExists && ShouldRelaunch(args, envValue, true, true))
                Debug.LogWarning("[PhoneSaberLaunchGuard] Launcher missing: " + LauncherPath);
            return;
        }

        try
        {
            // ランチャーは「ゲームが起動中」なら起動を断る。この process が終わってから開く（背景の sh は終了後も残る）。
            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = RelaunchScript(System.Diagnostics.Process.GetCurrentProcess().Id),
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                if (process == null) throw new InvalidOperationException("open did not start");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PhoneSaberLaunchGuard] Launcher failed: " + ex.Message);
            return;
        }

        // 終了完了までのポート確保は抑止せず、BeforeSceneLoad で速やかに終了を要求する。
        Debug.Log("[PhoneSaberLaunchGuard] Opening launcher and quitting direct player: " + LauncherPath);
        Application.Quit();
#endif
    }
}
