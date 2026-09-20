using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class BleBridgeAutoStartPolicy
{
    public const string GameScenePath = "Assets/Scenes/Game.unity";

    public static bool CanStart(bool requested, bool isPlaying, string activeScenePath)
    {
        return requested &&
               isPlaying &&
               string.Equals(
                   (activeScenePath ?? string.Empty).Replace('\\', '/'),
                   GameScenePath,
                   StringComparison.Ordinal);
    }
}

public class UdpImuBridge : MonoBehaviour
{
    public static UdpImuBridge Instance { get; private set; }

    [Header("UDP")]
    [SerializeField] private string host = "127.0.0.1";
    [SerializeField] private int commandPort = 9001;
    [SerializeField] private int dataPort = 9002;

    [Header("BLE bridge auto start")]
    // Camera-only / Virtual IMUを既定経路とする。BLE子プロセス起動は、
    // 実機接続を検証するときだけInspectorから明示的に有効化する。
    [SerializeField] private bool autoStartBleBridge = false;
    [SerializeField] private string pythonExecutable = "python3";
    [SerializeField, Min(0.2f)] private float bridgeProbeSeconds = 0.75f;
    [SerializeField, Min(1f)] private float bridgeRetrySeconds = 5f;

    [Header("Swing event")]
    [SerializeField, Min(0f)] private float staleEventSeconds = 0.15f;
    [SerializeField] private bool logAcceptedEvents = true;
    [SerializeField] private bool logRejectedEvents = true;

    [Header("Debug (read only)")]
    [SerializeField] private bool bridgeConnected;
    [SerializeField] private string lastSwing = "none";
    [SerializeField] private double lastLocalReceiveTime;
    [SerializeField] private double lastUdpLatencyMs;
    [SerializeField] private double lastMainThreadHandoffMs;
    [SerializeField] private float lastCallbackFrameMs;
    [SerializeField] private int staleEvents;
    [SerializeField] private int invalidPackets;
    [SerializeField] private bool bridgeProcessReady;
    [SerializeField] private BleBridgeConnectionState bleState;
    [SerializeField] private string bleDevice = "XIAO-LSM6DSV16X";
    [SerializeField] private BleBridgeConnectionState leftBleState;
    [SerializeField] private BleBridgeConnectionState rightBleState;
    [SerializeField] private string leftBleDevice = "";
    [SerializeField] private string rightBleDevice = "";

    // raw IMU互換は任意の位置fallbackのみに残す。Swing判定はこれを読まない。
    private readonly object rawImuLock = new object();
    private Vector3 latestAcceleration;
    private Vector3 latestGyro;
    private bool hasImuData;

    private readonly SwingSequenceTracker sequenceTracker = new SwingSequenceTracker();
    private readonly SwingSequenceTracker leftSequenceTracker = new SwingSequenceTracker();
    private readonly SwingSequenceTracker rightSequenceTracker = new SwingSequenceTracker();
    private UdpClient sender;
    private UdpClient receiver;
    private IPEndPoint sendEndpoint;
    private SynchronizationContext mainThreadContext;
    private int closing;
    private ImuBleBridgeLauncher bridgeLauncher;
    private long lastBridgeReadyTimestampTicks;
    private string lastLauncherError;
    private bool bridgeStartAnnounced;

    public event Action<SwingEvent> OnSwingReceived;
    public event Action<SwingEvent> OnLeftSwingReceived;
    public event Action<SwingEvent> OnRightSwingReceived;
    // main threadで通知。再接続前や無効化中に受信した方向ヒントを持ち越さない。
    public event Action<long> OnSwingSessionReset;

    public bool IsBridgeConnected => bridgeConnected;
    public int ReceivedEvents => sequenceTracker.ReceivedEvents;
    public int AcceptedEvents => sequenceTracker.AcceptedEvents;
    public int DuplicateEvents => sequenceTracker.DuplicateEvents;
    public int OutOfOrderEvents => sequenceTracker.OutOfOrderEvents;
    public int MissingEvents => sequenceTracker.MissingEvents;
    public int StaleEvents => Volatile.Read(ref staleEvents);
    public int InvalidPackets => Volatile.Read(ref invalidPackets);
    public string LastSwingDebug => lastSwing;
    public bool IsBridgeProcessReady => bridgeProcessReady;
    public BleBridgeConnectionState BleState => bleState;
    public string BleDevice => bleDevice;
    public BleBridgeConnectionState LeftBleState => leftBleState;
    public BleBridgeConnectionState RightBleState => rightBleState;
    public string LeftBleDevice => leftBleDevice;
    public string RightBleDevice => rightBleDevice;
    public bool OwnsBleBridgeProcess => bridgeLauncher != null && bridgeLauncher.OwnsRunningProcess;

    public static bool TryGetLatest(out Vector3 acceleration, out Vector3 gyro, out bool connected)
    {
        UdpImuBridge instance = Instance;
        if (instance == null)
        {
            acceleration = Vector3.zero;
            gyro = Vector3.zero;
            connected = false;
            return false;
        }

        lock (instance.rawImuLock)
        {
            acceleration = instance.latestAcceleration;
            gyro = instance.latestGyro;
            connected = instance.bridgeConnected;
            return instance.hasImuData;
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        mainThreadContext = SynchronizationContext.Current;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        OnSwingSessionReset?.Invoke(SwingMonotonicClock.Timestamp);
    }

    private void OnDisable()
    {
        OnSwingSessionReset?.Invoke(SwingMonotonicClock.Timestamp);
    }

    private void Start()
    {
        try
        {
            sendEndpoint = new IPEndPoint(IPAddress.Parse(host), commandPort);
            sender = new UdpClient();
            receiver = new UdpClient(dataPort);
            Haptic.SetTransport(OnHapticSend);
            BeginReceive();
            SendCommand("PING");
            Debug.Log($"[Swing] UDP event receiver ready: 0.0.0.0:{dataPort}");
            string activeScenePath = SceneManager.GetActiveScene().path;
            if (BleBridgeAutoStartPolicy.CanStart(
                    autoStartBleBridge,
                    Application.isPlaying,
                    activeScenePath))
            {
                bridgeLauncher = new ImuBleBridgeLauncher();
                StartCoroutine(EnsureBleBridgeRunning());
            }
            else if (autoStartBleBridge)
            {
                Debug.LogWarning(
                    $"[IMU BLE] Auto start skipped outside Game.unity: " +
                    $"{(string.IsNullOrEmpty(activeScenePath) ? "<temporary scene>" : activeScenePath)}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Swing] UDP startup failed on port {dataPort}: {ex.Message}");
        }
    }

    // TestではAddComponent後、最初のframe/Start前に呼ぶ。
    public void ConfigureForTests(int receivePort, float staleSeconds = 0.15f)
    {
        dataPort = receivePort;
        staleEventSeconds = staleSeconds;
        logAcceptedEvents = false;
        logRejectedEvents = false;
        autoStartBleBridge = false;
    }

    // GamePlayManagerが実行時生成した直後、Startより前に呼ぶ。
    public void ConfigureBleBridgeAutoStart(bool enabled)
    {
        autoStartBleBridge = enabled;
    }

    private void OnDestroy()
    {
        Interlocked.Exchange(ref closing, 1);
        if (Instance == this)
        {
            Instance = null;
            Haptic.SetTransport(null);
        }
        receiver?.Close();
        receiver = null;
        sender?.Close();
        sender = null;
        bridgeLauncher?.Dispose();
        bridgeLauncher = null;
    }

    private void BeginReceive()
    {
        if (receiver == null || Volatile.Read(ref closing) != 0)
        {
            return;
        }
        try
        {
            receiver.BeginReceive(OnReceive, null);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnReceive(IAsyncResult asyncResult)
    {
        UdpClient activeReceiver = receiver;
        if (activeReceiver == null || Volatile.Read(ref closing) != 0)
        {
            return;
        }

        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        byte[] bytes;
        try
        {
            bytes = activeReceiver.EndReceive(asyncResult, ref remote);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (SocketException ex)
        {
            PostToMain(() => Debug.LogWarning("[Swing] UDP receive error: " + ex.Message));
            BeginReceive();
            return;
        }

        long receiveTimestamp = SwingMonotonicClock.Timestamp;
        string message = Encoding.UTF8.GetString(bytes).Trim();
        try
        {
            // sequence判定を到着順に保つため、次のreceive開始前に処理する。
            // ここでは軽量なparseとmain-threadへのPostだけを行う。
            ProcessReceivedMessage(message, receiveTimestamp);
        }
        finally
        {
            BeginReceive();
        }
    }

    private void ProcessReceivedMessage(string message, long receiveTimestamp)
    {
        if (BleBridgeStatusParser.TryParse(message, out BleBridgeStatusUpdate statusUpdate))
        {
            PostToMain(() => ApplyBridgeStatus(statusUpdate, receiveTimestamp));
            return;
        }

        // 旧bridge / Virtual IMUの全体STATEも継続受理する。
        if (message.StartsWith("STATE:", StringComparison.Ordinal))
        {
            string state = message.Substring(6).Trim();
            bool connected = state.Equals("CONNECTED", StringComparison.Ordinal);
            if (connected)
            {
                sequenceTracker.ResetSession();
                leftSequenceTracker.ResetSession();
                rightSequenceTracker.ResetSession();
            }
            PostToMain(() =>
            {
                bridgeConnected = connected;
                OnSwingSessionReset?.Invoke(receiveTimestamp);
            });
            return;
        }

        if (message.StartsWith("IMU:", StringComparison.Ordinal))
        {
            ParseLegacyRawImu(message.Substring(4));
            return;
        }

        if (!SwingPacketParser.TryParse(message, receiveTimestamp, out SwingEvent swing))
        {
            Interlocked.Increment(ref invalidPackets);
            if (logRejectedEvents)
            {
                PostToMain(() => Debug.LogWarning("[Swing] invalid packet: " + message));
            }
            return;
        }

        SwingSequenceTracker tracker = swing.Side == SaberSide.Left
            ? leftSequenceTracker
            : swing.Side == SaberSide.Right ? rightSequenceTracker : sequenceTracker;
        SwingSequenceResult sequenceResult = tracker.Observe(swing.Sequence);
        if (sequenceResult != SwingSequenceResult.Accepted)
        {
            if (logRejectedEvents)
            {
                PostToMain(() => Debug.LogWarning(
                    $"[Swing] {sequenceResult}: seq={swing.Sequence} " +
                    $"duplicate={DuplicateEvents} outOfOrder={OutOfOrderEvents}"));
            }
            return;
        }

        PostToMain(() => DispatchSwingOnMainThread(swing));
    }

    private IEnumerator EnsureBleBridgeRunning()
    {
        var probeWait = new WaitForSecondsRealtime(Mathf.Max(0.2f, bridgeProbeSeconds));
        var retryWait = new WaitForSecondsRealtime(Mathf.Max(1f, bridgeRetrySeconds));
        while (isActiveAndEnabled && Volatile.Read(ref closing) == 0)
        {
            SendCommand("PING");
            yield return probeWait;

            double readyAgeMs = lastBridgeReadyTimestampTicks == 0
                ? double.PositiveInfinity
                : SwingMonotonicClock.ElapsedMilliseconds(
                    lastBridgeReadyTimestampTicks,
                    SwingMonotonicClock.Timestamp);
            if (readyAgeMs <= Mathf.Max(1000f, bridgeRetrySeconds * 1000f))
            {
                yield return retryWait;
                continue;
            }

            bridgeProcessReady = false;
            if (bridgeLauncher != null && !bridgeLauncher.OwnsRunningProcess)
            {
                string previousError = bridgeLauncher.LastError;
                if (!string.IsNullOrEmpty(previousError) &&
                    !string.Equals(previousError, lastLauncherError, StringComparison.Ordinal))
                {
                    lastLauncherError = previousError;
                    Debug.LogWarning("[IMU BLE] Bridge stopped; Camera-only remains available: " + previousError);
                }
                if (!bridgeStartAnnounced)
                {
                    bridgeStartAnnounced = true;
                    Debug.Log("[IMU BLE] Starting bridge...");
                }
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                if (!bridgeLauncher.TryStart(
                        pythonExecutable,
                        projectRoot,
                        commandPort,
                        dataPort))
                {
                    string error = bridgeLauncher.LastError ?? "unknown error";
                    if (!string.Equals(error, lastLauncherError, StringComparison.Ordinal))
                    {
                        lastLauncherError = error;
                        Debug.LogWarning("[IMU BLE] Bridge start failed; Camera-only remains available: " + error);
                    }
                }
                else
                {
                    lastLauncherError = null;
                }
            }
            yield return retryWait;
        }
    }

    private void ApplyBridgeStatus(BleBridgeStatusUpdate update, long receiveTimestamp)
    {
        if (update.IsBridgeReady)
        {
            bool changed = !bridgeProcessReady;
            bridgeProcessReady = true;
            bridgeStartAnnounced = false;
            lastLauncherError = null;
            lastBridgeReadyTimestampTicks = receiveTimestamp;
            if (changed) Debug.Log("[IMU BLE] Bridge ready");
            return;
        }

        BleBridgeConnectionState previous = bleState;
        bleState = update.State;
        if (!string.IsNullOrEmpty(update.DeviceName)) bleDevice = update.DeviceName;
        if (update.Side == SaberSide.Left)
        {
            leftBleState = update.State;
            if (!string.IsNullOrEmpty(update.DeviceName)) leftBleDevice = update.DeviceName;
        }
        else if (update.Side == SaberSide.Right)
        {
            rightBleState = update.State;
            if (!string.IsNullOrEmpty(update.DeviceName)) rightBleDevice = update.DeviceName;
        }
        bridgeConnected = IsConnectedState(bleState);
        if (previous == update.State) return;

        string name = update.DeviceName;
        switch (update.State)
        {
            case BleBridgeConnectionState.Searching:
                Debug.Log($"[IMU BLE] Searching for {name}...");
                break;
            case BleBridgeConnectionState.NotFound:
                Debug.Log("[IMU BLE] Device not found; retrying...");
                break;
            case BleBridgeConnectionState.DeviceFound:
                Debug.Log($"[IMU BLE] Device found: {name}");
                break;
            case BleBridgeConnectionState.Connecting:
                Debug.Log("[IMU BLE] Connecting...");
                break;
            case BleBridgeConnectionState.Connected:
                Debug.Log($"[IMU BLE] Connected: {name}");
                sequenceTracker.ResetSession();
                if (update.Side == SaberSide.Left) leftSequenceTracker.ResetSession();
                if (update.Side == SaberSide.Right) rightSequenceTracker.ResetSession();
                OnSwingSessionReset?.Invoke(receiveTimestamp);
                break;
            case BleBridgeConnectionState.NotificationsActive:
                Debug.Log("[IMU BLE] Swing notifications active");
                break;
            case BleBridgeConnectionState.Disconnected:
                Debug.Log("[IMU BLE] Disconnected; reconnecting...");
                OnSwingSessionReset?.Invoke(receiveTimestamp);
                break;
        }
    }

    private static bool IsConnectedState(BleBridgeConnectionState state)
    {
        return state == BleBridgeConnectionState.Connected ||
               state == BleBridgeConnectionState.NotificationsActive;
    }

    private void DispatchSwingOnMainThread(SwingEvent receivedSwing)
    {
        if (!isActiveAndEnabled) return;
        long callbackTimestamp = SwingMonotonicClock.Timestamp;
        SwingEvent swing = receivedSwing.WithMainThreadHandoff(callbackTimestamp);
        if (SwingEventTiming.IsStale(swing, callbackTimestamp, staleEventSeconds))
        {
            Interlocked.Increment(ref staleEvents);
            if (logRejectedEvents)
            {
                Debug.LogWarning(
                    $"[Swing] stale seq={swing.Sequence} " +
                    $"handoff={swing.MainThreadHandoffLatencyMs:F2}ms limit={staleEventSeconds * 1000f:F0}ms");
            }
            return;
        }

        lastLocalReceiveTime = swing.LocalReceiveTimeSeconds;
        lastUdpLatencyMs = swing.LocalhostTransportLatencyMs;
        lastMainThreadHandoffMs = swing.MainThreadHandoffLatencyMs;
        lastCallbackFrameMs = Time.unscaledDeltaTime * 1000f;
        lastSwing = $"{swing.Side} seq={swing.Sequence} {swing.Direction} strength={swing.Strength:F2}";
        if (logAcceptedEvents)
        {
            string udp = double.IsNaN(swing.LocalhostTransportLatencyMs)
                ? "n/a (real bridge)"
                : $"{swing.LocalhostTransportLatencyMs:F3}ms";
            Debug.Log(
                $"[Swing] {lastSwing} receive={swing.LocalReceiveTimeSeconds:F6}s " +
                $"UDP={udp} main-handoff={swing.MainThreadHandoffLatencyMs:F3}ms " +
                $"frame={lastCallbackFrameMs:F2}ms " +
                $"counts recv={ReceivedEvents} dup={DuplicateEvents} " +
                $"outOfOrder={OutOfOrderEvents} stale={StaleEvents}");
        }
        OnSwingReceived?.Invoke(swing);
        if (swing.Side == SaberSide.Left) OnLeftSwingReceived?.Invoke(swing);
        if (swing.Side == SaberSide.Right) OnRightSwingReceived?.Invoke(swing);
    }

    private void ParseLegacyRawImu(string payload)
    {
        string[] values = payload.Split(',');
        if (values.Length < 6 ||
            !TryParseFloat(values[0], out float ax) ||
            !TryParseFloat(values[1], out float ay) ||
            !TryParseFloat(values[2], out float az) ||
            !TryParseFloat(values[3], out float gx) ||
            !TryParseFloat(values[4], out float gy) ||
            !TryParseFloat(values[5], out float gz))
        {
            Interlocked.Increment(ref invalidPackets);
            return;
        }

        lock (rawImuLock)
        {
            latestAcceleration = new Vector3(ax, ay, az);
            latestGyro = new Vector3(gx, gy, gz);
            hasImuData = true;
        }
    }

    private void PostToMain(Action action)
    {
        SynchronizationContext context = mainThreadContext;
        if (context == null)
        {
            return;
        }
        context.Post(_ =>
        {
            if (this != null && Volatile.Read(ref closing) == 0)
            {
                action();
            }
        }, null);
    }

    private void OnHapticSend(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length > 0)
        {
            SendCommand("H:" + trimmed);
        }
    }

    private void SendCommand(string command)
    {
        if (sender == null || sendEndpoint == null)
        {
            return;
        }
        byte[] payload = Encoding.UTF8.GetBytes(command);
        sender.Send(payload, payload.Length, sendEndpoint);
    }

    private static bool TryParseFloat(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}
