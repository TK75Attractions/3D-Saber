using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

// PhoneSaberSender の P2P(peer-to-peer Wi-Fi)用 bridge を、InputPoint の受信 socket が揃っている間だけ動かす。
// bridge は iPhone からの座標を P2P で受け取り、本文を変えずに 127.0.0.1:5005 / 5006 へ転送する。
// InputPoint の UDP 受信はそのままで、bridge は追加の経路にすぎない(起動できなくても従来の LAN 受信は動く)。
// bridge 本体と build 手順は school-festival repo の ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py にある。
// 開始/停止は Bonjour publisher と同じく InputPoint.SetReceiverAlive から(受信 thread 上で)呼ばれる。
public sealed class PhoneSaberP2PBridgeProcess : IDisposable
{
    // 既定では Unity project(3D-Saber)と並んでいる school-festival repo の launcher を使う。
    public const string LauncherRelativePath = "ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py";
    // 起動 script の場所を変えるときの環境変数。PHONESABER_P2P_BRIDGE=0 / off で自動起動しない。
    public const string ScriptEnvironmentVariable = "PHONESABER_P2P_BRIDGE_SCRIPT";
    public const string DisableEnvironmentVariable = "PHONESABER_P2P_BRIDGE";
    // 近くに複数の Mac があっても iPhone 側で区別できるよう、実際の公開名には Mac の名前を付ける。
    public const string ServiceNameBase = "Phone Saber Unity P2P";
    // DNS-SD の instance 名は UTF-8 で 63 byte まで。
    public const int MaxServiceNameBytes = 63;
    const string PythonExecutable = "/usr/bin/python3";
    const string NiceExecutable = "/usr/bin/nice";
    const int LauncherExitWaitMilliseconds = 1000;

    // テストでは false にして本物の bridge を起動しない(Tests/Editor・Tests/PlayMode の SetUpFixture が切り替える)。
    public static bool AutoStartEnabled = true;

    static readonly object Gate = new object();
    static Process process;
    static LaunchState launch;
    static PhoneSaberP2PBridgeProcess owner;
    static string lastWarning;
    // 起動/build に失敗したら、次の script compile(Editor では SessionState で Play をまたいで保持)まで再試行しない。
    // (macOS 以外では読む箇所が無く CS0414 になるため抑止する)
#pragma warning disable 0414
    static volatile bool startFailed;
#pragma warning restore 0414

    bool disposed;

    // 起動 1 回分の状態。意図した停止かどうかを Exited / 出力 handler が lock なしで判定するために使う。
    sealed class LaunchState
    {
        public volatile bool Stopping;
    }

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

    // "Phone Saber Unity P2P (<Mac の名前>)"。制御文字・引用符を除き、全体を 63 UTF-8 byte 以内に収める。
    public static string BuildServiceName(string machineName)
    {
        string cleaned = SanitizeMachineName(machineName);
        string prefix = ServiceNameBase + " (";
        int budget = MaxServiceNameBytes - Encoding.UTF8.GetByteCount(prefix) - 1;
        cleaned = TruncateUtf8(cleaned, budget).TrimEnd();
        return cleaned.Length == 0 ? ServiceNameBase : prefix + cleaned + ")";
    }

    static string SanitizeMachineName(string machineName)
    {
        if (string.IsNullOrWhiteSpace(machineName)) return "";
        string name = machineName.Trim();
        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 6);
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            // 引用符と backslash は起動引数の quote を壊すので除く。
            if (char.IsControl(c) || c == '"' || c == '\\') continue;
            builder.Append(c);
        }
        return builder.ToString().Trim();
    }

    // surrogate pair を分断せずに UTF-8 byte 数で切り詰める。
    static string TruncateUtf8(string text, int maxBytes)
    {
        int bytes = 0;
        int index = 0;
        while (index < text.Length)
        {
            int length = char.IsSurrogatePair(text, index) ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(text.Substring(index, length));
            if (bytes + size > maxBytes) break;
            bytes += size;
            index += length;
        }
        return text.Substring(0, index);
    }

    // launcher が必要なら build してから bridge を exec する。転送先は InputPoint の port。
    // --exit-with-parent により、Unity が異常終了しても bridge が残らない。
    public static string BuildArguments(string launcherPath, string serviceName, int redPort, int bluePort, int parentPid)
    {
        return $"\"{launcherPath}\" -- --name \"{serviceName}\" --red-port {redPort} --blue-port {bluePort} " +
               $"--exit-with-parent {parentPid}";
    }

    // Editor 起動時の事前 build(低優先度)。/usr/bin/nice に渡す引数。
    public static string BuildPrebuildArguments(string launcherPath)
    {
        return $"-n 10 {PythonExecutable} \"{launcherPath}\" --build-only";
    }

    // bridge の出力のうち警告として出す行。それ以外(状態変化・10 秒ごとの集計)は通常 log。
    public static bool IsBridgeWarning(string line)
    {
        return line != null &&
               (line.Contains("[P2P] listener failed") || line.Contains("[P2P] cannot") ||
                line.Contains("bridge build failed") || line.Contains("diag relay: cannot"));
    }

    public bool Start(int redPort, int bluePort, string dataPath)
    {
        return Start(redPort, bluePort, dataPath, Environment.GetEnvironmentVariable);
    }

    // テストは environment を差し替えて、利用者の環境変数に左右されないようにする
    // (SaberTests.Editor へは SaberGameAssemblyInfo.cs の InternalsVisibleTo で公開)。
    internal bool Start(int redPort, int bluePort, string dataPath, Func<string, string> environment)
    {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        if (!AutoStartEnabled || environment == null) return false;
        lock (Gate)
        {
            if (disposed) return false;
            if (ProcessIsRunning(process)) return ReferenceEquals(owner, this);
            StopProcessLocked();
            if (startFailed || IsDisabled(environment)) return false;

            string launcher = dataPath == null ? null : ResolveLauncherPath(dataPath, environment);
            if (launcher == null || !File.Exists(launcher))
            {
                WarnOnce($"[PhoneSaber][P2P] bridge launcher not found: {launcher}. " +
                         $"P2P は使わず従来の LAN 受信だけで動きます({ScriptEnvironmentVariable} で場所を指定できます)");
                return false;
            }
            try
            {
                int parentPid;
                using (var current = Process.GetCurrentProcess()) parentPid = current.Id;
                string serviceName = BuildServiceName(Environment.MachineName);
                var state = new LaunchState();
                var started = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = PythonExecutable,
                        Arguments = BuildArguments(launcher, serviceName, redPort, bluePort, parentPid),
                        WorkingDirectory = Path.GetDirectoryName(launcher),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    },
                    EnableRaisingEvents = true
                };
                started.OutputDataReceived += (sender, args) => ForwardOutput(state, args.Data);
                started.ErrorDataReceived += (sender, args) => ForwardOutput(state, args.Data);
                started.Exited += (sender, args) => OnBridgeExited(state, started);
                process = started;
                launch = state;
                if (!started.Start()) throw new InvalidOperationException("bridge process did not start");
                started.BeginOutputReadLine();
                started.BeginErrorReadLine();
                owner = this;
                lastWarning = null;
                // launcher は初回だけ swiftc で build してから exec するので、ここではまだ「起動中」。
                Log(LogType.Log, $"[PhoneSaber][P2P] bridge starting (first run may build): " +
                                 $"\"{serviceName}\" RED {redPort} / BLUE {bluePort}");
                return true;
            }
            catch (Exception exception)
            {
                StopProcessLocked();
                startFailed = true;
                WarnOnce($"[PhoneSaber][P2P] bridge start failed: {exception.Message}. 従来の LAN 受信だけで動きます");
                return false;
            }
        }
#else
        return false;
#endif
    }

    // どちらかの receiver が失われた時に止める。後で両方 bind できたら Start で再開する。
    public void Stop()
    {
        lock (Gate)
        {
            if (ReferenceEquals(owner, this)) StopProcessLocked();
        }
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

    // テスト用: 一度だけの警告と失敗フラグを初期状態に戻す。
    internal static void ResetStateForTests()
    {
        lock (Gate)
        {
            lastWarning = null;
            startFailed = false;
        }
    }

    static void Log(LogType type, string message)
    {
        // Console を短く保つため stack trace は付けない。
        UnityEngine.Debug.LogFormat(type, LogOption.NoStacktrace, null, "{0}", message);
    }

    static void WarnOnce(string message)
    {
        if (message == lastWarning) return;
        lastWarning = message;
        Log(LogType.Warning, message);
    }

    // bridge は状態変化と 10 秒ごとの集計だけを出すので、そのまま Console へ流す。停止処理中の出力は捨てる。
    static void ForwardOutput(LaunchState state, string line)
    {
        if (state.Stopping || string.IsNullOrWhiteSpace(line)) return;
        Log(IsBridgeWarning(line) ? LogType.Warning : LogType.Log, "[PhoneSaber]" + line);
    }

    // 意図しない終了を 1 回だけ記録する。lock は取らない(WaitForExit 中の Exited 呼び出しと deadlock しないため)。
    static void OnBridgeExited(LaunchState state, Process exited)
    {
        if (state.Stopping) return;
        int code;
        try { code = exited.ExitCode; }
        catch { code = -1; }
        if (code == 0)
        {
            Log(LogType.Log, "[PhoneSaber][P2P] bridge exited (code 0)");
            return;
        }
        startFailed = true;
        Log(LogType.Warning, $"[PhoneSaber][P2P] bridge exited (code {code}). " +
                             "次の script compile / Editor 再起動まで自動起動しません。LAN 受信はそのまま動きます");
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
        var state = launch;
        process = null;
        launch = null;
        owner = null;
        if (active == null) return;
        if (state != null) state.Stopping = true;
        try
        {
            if (!active.HasExited)
            {
                // 初回 build 中は launcher(python)の子に swiftc がいる。Kill(SIGKILL)だけだと swiftc が孤児になるので、
                // 先に直下の子へ SIGTERM を送り、launcher が build 失敗として自分で終わるのを少し待つ。
                // (launcher 自身も SIGTERM を受けたら compiler を止める。exec 後の bridge には子がいない)
                if (TerminateChildren(active.Id)) active.WaitForExit(LauncherExitWaitMilliseconds);
                if (!active.HasExited)
                {
                    active.Kill();
                    active.WaitForExit(LauncherExitWaitMilliseconds);
                }
                Log(LogType.Log, "[PhoneSaber][P2P] bridge stopped");
            }
        }
        catch { }
        finally
        {
            active.Dispose();
        }
    }

    // /usr/bin/pkill -TERM -P <pid>: 直下の子に SIGTERM を送る。1 つでも送れたら true。
    static bool TerminateChildren(int parentPid)
    {
        try
        {
            using (var tool = new Process())
            {
                tool.StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/pkill",
                    Arguments = "-TERM -P " + parentPid,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (!tool.Start()) return false;
                if (!tool.WaitForExit(LauncherExitWaitMilliseconds)) return false;
                // pkill の exit code: 0 = 送信した / 1 = 該当なし。
                return tool.ExitCode == 0;
            }
        }
        catch
        {
            return false;
        }
    }

#if UNITY_EDITOR_OSX
    const string FailedSessionKey = "PhoneSaber.P2PBridge.StartFailed";
    const string PrebuiltSessionKey = "PhoneSaber.P2PBridge.PrebuildStarted";

    [UnityEditor.InitializeOnLoadMethod]
    static void InstallEditorHooks()
    {
        // Play 開始時の domain reload をまたいで失敗を覚えておき、毎回 build を試し直さない。
        if (UnityEditor.SessionState.GetBool(FailedSessionKey, false)) startFailed = true;
        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        UnityEditor.Compilation.CompilationPipeline.compilationFinished -= OnCompilationFinished;
        UnityEditor.Compilation.CompilationPipeline.compilationFinished += OnCompilationFinished;
        UnityEditor.EditorApplication.quitting -= StopForEditor;
        UnityEditor.EditorApplication.quitting += StopForEditor;
        UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        UnityEditor.EditorApplication.delayCall -= PrebuildOnce;
        UnityEditor.EditorApplication.delayCall += PrebuildOnce;
    }

    static void OnBeforeAssemblyReload()
    {
        StopForEditor();
        if (startFailed) UnityEditor.SessionState.SetBool(FailedSessionKey, true);
    }

    // script を直したら(bridge 側の設定を変えた可能性もあるので)もう一度試せるようにする。
    static void OnCompilationFinished(object context)
    {
        startFailed = false;
        UnityEditor.SessionState.EraseBool(FailedSessionKey);
    }

    static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange state)
    {
        if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) StopForEditor();
    }

    static void StopForEditor()
    {
        lock (Gate) StopProcessLocked();
    }

    // Editor session ごとに 1 回、Play 外で launcher --build-only を低優先度で走らせ、最初の Play をすぐ始められるようにする。
    // build 済みなら launcher は hash を確認して即終了する。Editor は待たない。
    static void PrebuildOnce()
    {
        if (!AutoStartEnabled || startFailed || Application.isBatchMode ||
            UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (UnityEditor.SessionState.GetBool(PrebuiltSessionKey, false)) return;
        Func<string, string> environment = Environment.GetEnvironmentVariable;
        if (IsDisabled(environment)) return;
        string launcher = ResolveLauncherPath(Application.dataPath, environment);
        if (launcher == null || !File.Exists(launcher)) return;
        UnityEditor.SessionState.SetBool(PrebuiltSessionKey, true);
        try
        {
            var prebuild = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = NiceExecutable,
                    Arguments = BuildPrebuildArguments(launcher),
                    WorkingDirectory = Path.GetDirectoryName(launcher),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            string failure = null;
            prebuild.OutputDataReceived += (sender, args) =>
            {
                if (args.Data != null && args.Data.StartsWith("[P2P] building bridge", StringComparison.Ordinal))
                    Log(LogType.Log, "[PhoneSaber][P2P] pre-building bridge in background (first time only)");
            };
            prebuild.ErrorDataReceived += (sender, args) =>
            {
                if (IsBridgeWarning(args.Data)) failure = args.Data;
            };
            prebuild.Exited += (sender, args) =>
            {
                int code;
                try { code = prebuild.ExitCode; }
                catch { code = -1; }
                if (code != 0)
                    Log(LogType.Log, $"[PhoneSaber][P2P] bridge pre-build failed (exit {code}) {failure}; Play 時にもう一度試します");
                prebuild.Dispose();
            };
            if (!prebuild.Start()) return;
            prebuild.BeginOutputReadLine();
            prebuild.BeginErrorReadLine();
        }
        catch (Exception exception)
        {
            Log(LogType.Log, $"[PhoneSaber][P2P] bridge pre-build could not start: {exception.Message}");
        }
    }
#endif
}
