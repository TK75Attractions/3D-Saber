using System;
using System.Diagnostics;

// PhoneSaberSender が Unity の Mac を見つけるためのみに Bonjour を公開する。
// 座標は従来通り InputPoint が UDP 5005 / 5006 で直接受信する。
public sealed class PhoneSaberBonjourPublisher : IDisposable
{
    public const string ServiceName = "Phone Saber Unity";
    public const string ServiceType = "_phonesaber._udp";
    public const string Domain = "local.";

    static readonly object Gate = new object();
    static Process process;
    static PhoneSaberBonjourPublisher owner;

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
        get
        {
            lock (Gate) return ProcessIsRunning(process);
        }
    }

    public bool Start(int port)
    {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        lock (Gate)
        {
            if (disposed) return false;
            if (ProcessIsRunning(process)) return true;

            StopProcessLocked();
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/dns-sd",
                    Arguments = $"-R \"{ServiceName}\" {ServiceType} {Domain} {port}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                process = new Process { StartInfo = startInfo };
                process.OutputDataReceived += IgnoreProcessOutput;
                process.ErrorDataReceived += IgnoreProcessOutput;
                if (!process.Start())
                {
                    throw new InvalidOperationException("dns-sd process did not start");
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                owner = this;
                UnityEngine.Debug.Log(
                    $"[PhoneSaber] Bonjour published: {ServiceName} ({ServiceType}) port {port}");
                return true;
            }
            catch (Exception exception)
            {
                StopProcessLocked();
                UnityEngine.Debug.LogError($"[PhoneSaber] Bonjour publish failed: {exception.Message}");
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

    static bool ProcessIsRunning(Process candidate)
    {
        if (candidate == null) return false;
        try { return !candidate.HasExited; }
        catch { return false; }
    }

    static void IgnoreProcessOutput(object sender, DataReceivedEventArgs args) { }

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
