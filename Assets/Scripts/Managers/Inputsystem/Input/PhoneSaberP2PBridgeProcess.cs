using System;
using System.Diagnostics;
using System.IO;

// PhoneSaberSender の P2P(peer-to-peer Wi-Fi)用 bridge を、InputPoint の受信開始に合わせて自動起動する。
// bridge は iPhone からの座標を P2P で受け取り、本文を変えずに 127.0.0.1:5005 / 5006 へ転送する。
// InputPoint の UDP 受信はそのままで、bridge は追加の経路にすぎない(起動できなくても従来の LAN 受信は動く)。
// bridge 本体と build 手順は school-festival repo の ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py にある。
public sealed class PhoneSaberP2PBridgeProcess : IDisposable
{
    // 既定では Unity project(3D-Saber)と並んでいる school-festival repo の launcher を使う。
    public const string LauncherRelativePath = "ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py";
    // 起動 script の場所を変えるときの環境変数。"0" / "off" を指定すると自動起動しない。
    public const string ScriptEnvironmentVariable = "PHONESABER_P2P_BRIDGE_SCRIPT";
    public const string DisableEnvironmentVariable = "PHONESABER_P2P_BRIDGE";
    public const string ServiceName = "Phone Saber Unity P2P";
    const string PythonExecutable = "/usr/bin/python3";

    static readonly object Gate = new object();
    static Process process;
    static PhoneSaberP2PBridgeProcess owner;
    static string lastWarning;

    bool disposed;

    public static bool IsSupported
    {
        get
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            return true;
#else
            return false;
#endif
        }
    }

    public static bool IsRunning
    {
        get { lock (Gate) return ProcessIsRunning(process); }
    }

    // Unity project の Assets から、隣の school-festival repo の launcher を探す。
    public static string ResolveLauncherPath(string dataPath, Func<string, string> environment)
    {
        string configured = environment(ScriptEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        string projectRoot = Path.GetFullPath(Path.Combine(dataPath, ".."));
        string workspace = Path.GetDirectoryName(projectRoot);
        return workspace == null ? null : Path.Combine(workspace, "school-festival", LauncherRelativePath);
    }

    public static bool IsDisabled(Func<string, string> environment)
    {
        string value = environment(DisableEnvironmentVariable);
        return value != null && (value.Trim() == "0" || value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    // launcher が必要なら build してから bridge を exec する。転送先は InputPoint の port。
    // --exit-with-parent により、Unity が異常終了しても bridge が残らない。
    public static string BuildArguments(string launcherPath, int redPort, int bluePort, int parentPid)
    {
        return $"\"{launcherPath}\" -- --name \"{ServiceName}\" --red-port {redPort} --blue-port {bluePort} " +
               $"--exit-with-parent {parentPid}";
    }

    public bool Start(int redPort, int bluePort, string dataPath)
    {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        lock (Gate)
        {
            if (disposed) return false;
            if (ProcessIsRunning(process)) return ReferenceEquals(owner, this);
            StopProcessLocked();

            Func<string, string> environment = Environment.GetEnvironmentVariable;
            if (IsDisabled(environment)) return false;
            string launcher = ResolveLauncherPath(dataPath, environment);
            if (launcher == null || !File.Exists(launcher))
            {
                WarnOnce($"[PhoneSaber][P2P] bridge launcher not found: {launcher}. " +
                         $"P2P は使わず従来の LAN 受信だけで動きます({ScriptEnvironmentVariable} で場所を指定できます)");
                return false;
            }
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = PythonExecutable,
                    Arguments = BuildArguments(launcher, redPort, bluePort,
                                               Process.GetCurrentProcess().Id),
                    WorkingDirectory = Path.GetDirectoryName(launcher),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                process = new Process { StartInfo = startInfo };
                process.OutputDataReceived += ForwardOutput;
                process.ErrorDataReceived += ForwardOutput;
                if (!process.Start()) throw new InvalidOperationException("bridge process did not start");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                owner = this;
                lastWarning = null;
                UnityEngine.Debug.Log($"[PhoneSaber][P2P] bridge started (RED {redPort} / BLUE {bluePort})");
                return true;
            }
            catch (Exception exception)
            {
                StopProcessLocked();
                WarnOnce($"[PhoneSaber][P2P] bridge start failed: {exception.Message}. 従来の LAN 受信だけで動きます");
                return false;
            }
        }
#else
        return false;
#endif
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (disposed) return;
            disposed = true;
            if (ReferenceEquals(owner, this)) StopProcessLocked();
        }
    }

    static void WarnOnce(string message)
    {
        if (message == lastWarning) return;
        lastWarning = message;
        UnityEngine.Debug.LogWarning(message);
    }

    // bridge は状態変化と 10 秒ごとの集計だけを出すので、そのまま Console へ流す。
    static void ForwardOutput(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data)) return;
        if (args.Data.Contains("failed") || args.Data.Contains("cannot") || args.Data.Contains("error"))
            UnityEngine.Debug.LogWarning("[PhoneSaber]" + args.Data);
        else
            UnityEngine.Debug.Log("[PhoneSaber]" + args.Data);
    }

    static bool ProcessIsRunning(Process candidate)
    {
        if (candidate == null) return false;
        try { return !candidate.HasExited; }
        catch { return false; }
    }

    static void StopProcessLocked()
    {
        var active = process;
        process = null;
        owner = null;
        if (active == null) return;
        try
        {
            if (!active.HasExited)
            {
                active.Kill();
                active.WaitForExit(1000);
            }
        }
        catch { }
        finally
        {
            active.Dispose();
        }
    }

#if UNITY_EDITOR_OSX
    [UnityEditor.InitializeOnLoadMethod]
    static void InstallEditorCleanup()
    {
        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= StopForEditor;
        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += StopForEditor;
        UnityEditor.EditorApplication.quitting -= StopForEditor;
        UnityEditor.EditorApplication.quitting += StopForEditor;
        UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange state)
    {
        if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) StopForEditor();
    }

    static void StopForEditor()
    {
        lock (Gate) StopProcessLocked();
    }
#endif
}
