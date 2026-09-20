using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

// Unityが起動したbridge子プロセスだけを所有する。
// 手動起動bridgeはUDP PINGで検出し、このclassからは停止しない。
public sealed class ImuBleBridgeLauncher : IDisposable
{
    private Process ownedProcess;
    private volatile string lastError;

    public bool OwnsRunningProcess => ownedProcess != null && !ownedProcess.HasExited;
    public string LastError => lastError;

    public bool TryStart(string pythonExecutable, string projectRoot, int commandPort, int dataPort)
    {
        if (OwnsRunningProcess) return true;
        if (ownedProcess != null)
        {
            ownedProcess.Dispose();
            ownedProcess = null;
        }

        string scriptPath = Path.Combine(projectRoot, "Tools", "mac_ble_udp_bridge.py");
        if (!File.Exists(scriptPath))
        {
            return Fail("Bridge script not found: " + scriptPath);
        }

        try
        {
            string selectedPython = string.IsNullOrWhiteSpace(pythonExecutable)
                ? "python3"
                : pythonExecutable;
            string localPython = Path.Combine(projectRoot, "Tools", ".venv", "bin", "python3");
            if (selectedPython == "python3" && File.Exists(localPython))
            {
                selectedPython = localPython;
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = selectedPython,
                Arguments = $"\"{scriptPath}\" --command-port {commandPort} --data-port {dataPort}",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnError;
            if (!process.Start())
            {
                process.Dispose();
                return Fail("Python process did not start");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            ownedProcess = process;
            lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public void StopOwnedProcess()
    {
        Process process = ownedProcess;
        ownedProcess = null;
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(1000);
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning("[IMU BLE] Could not stop owned bridge: " + ex.Message);
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        StopOwnedProcess();
    }

    private bool Fail(string message)
    {
        lastError = message;
        return false;
    }

    private void OnOutput(object sender, DataReceivedEventArgs args)
    {
        // 状態はUDPでUnityへ通知される。起動失敗だけを保持し、
        // Unity main threadのmonitorからConsoleに一度だけ出す。
        if (!string.IsNullOrWhiteSpace(args.Data) &&
            (args.Data.Contains("not installed") || args.Data.Contains("error")))
        {
            lastError = args.Data;
        }
    }

    private void OnError(object sender, DataReceivedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
        {
            lastError = args.Data;
        }
    }
}
