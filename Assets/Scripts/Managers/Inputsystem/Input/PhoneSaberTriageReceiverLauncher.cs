using System;
using System.Diagnostics;
using System.IO;

// Play 開始時に、iPhone の Debug Recording を受け取る診断の受信側
// (PhoneSaber の Start PhoneSaber と同じ launcher)を裏で起動する。
// すでに動いていれば launcher が重複を検知してすぐ終わり、古いコードのまま待機中の
// 受信側は launcher が入れ替える。Codex 解析を途中で止めないよう、Unity を閉じても受信側は残す。
// PHONESABER_TRIAGE_RECEIVER=0 で無効。テスト中は PhoneSaberP2PBridgeProcess.AutoStartEnabled(false)に従う。
public static class PhoneSaberTriageReceiverLauncher
{
    public const string LauncherRelativePath = "ios/PhoneSaberSender/Tools/phone_saber_receiver_launcher.py";
    public const string DisableEnvironmentVariable = "PHONESABER_TRIAGE_RECEIVER";
    // Finder から起動した Unity の PATH には Homebrew がない(codex は node 製で /opt/homebrew/bin にある)。
    const string ExtraPath = "/opt/homebrew/bin:/usr/local/bin";
    // 受信 thread の再起動などで続けて呼ばれても、launcher を何度も起動しない。
    const double MinimumIntervalSeconds = 30;
    static DateTime lastLaunchUtc = DateTime.MinValue;

    // Unity project の Assets から、プロジェクト内の PhoneSaber の launcher を探す。
    public static string ResolveLauncherPath(string dataPath)
    {
        if (string.IsNullOrEmpty(dataPath)) return null;
        string projectRoot = Path.GetFullPath(Path.Combine(dataPath, ".."));
        return Path.Combine(projectRoot, "PhoneSaber", LauncherRelativePath);
    }

    // sh で裏に回し、出力は捨てる(launcher 自身が ~/Library/Logs/PhoneSaber に記録する)。
    public static string BuildShellCommand(string launcherPath)
    {
        return "nohup /usr/bin/python3 " + ShellQuote(launcherPath) + " >/dev/null 2>&1 &";
    }

    public static string ShellQuote(string text)
    {
        return "'" + text.Replace("'", "'\\''") + "'";
    }

    public static bool IsDisabled(string value)
    {
        return value != null && (value.Trim() == "0" || value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    public static bool EnsureStarted(string dataPath)
    {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return false;
        if (IsDisabled(Environment.GetEnvironmentVariable(DisableEnvironmentVariable))) return false;
        if ((DateTime.UtcNow - lastLaunchUtc).TotalSeconds < MinimumIntervalSeconds) return false;
        string launcher = ResolveLauncherPath(dataPath);
        if (launcher == null || !File.Exists(launcher))
        {
            UnityEngine.Debug.LogWarning($"[PhoneSaber][DIAG] 受信側の launcher が見つかりません: {launcher}。" +
                                         "診断の自動受信は使わず、ゲームはそのまま動きます");
            return false;
        }
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = "-c " + ShellQuote(BuildShellCommand(launcher)),
                WorkingDirectory = Path.GetDirectoryName(launcher),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            string path = Environment.GetEnvironmentVariable("PATH");
            info.EnvironmentVariables["PATH"] = string.IsNullOrEmpty(path) ? ExtraPath : ExtraPath + ":" + path;
            using (var shell = Process.Start(info))
            {
                if (shell != null) shell.WaitForExit(2000);
            }
            lastLaunchUtc = DateTime.UtcNow;
            UnityEngine.Debug.Log("[PhoneSaber][DIAG] 診断の受信側を確認・起動しました(TCP 8765。動いていればそのまま使います)");
            return true;
        }
        catch (Exception exception)
        {
            UnityEngine.Debug.LogWarning($"[PhoneSaber][DIAG] 受信側を起動できませんでした: {exception.Message}");
            return false;
        }
#else
        return false;
#endif
    }
}
