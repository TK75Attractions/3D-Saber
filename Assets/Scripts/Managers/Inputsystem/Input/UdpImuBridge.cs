using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class UdpImuBridge : MonoBehaviour
{
    public static UdpImuBridge Instance { get; private set; }

    [Header("UDP")]
    [SerializeField] private string host = "127.0.0.1";
    [SerializeField] private int commandPort = 9001;
    [SerializeField] private int dataPort = 9002;

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

    // raw IMU互換は任意の位置fallbackのみに残す。Swing判定はこれを読まない。
    private readonly object rawImuLock = new object();
    private Vector3 latestAcceleration;
    private Vector3 latestGyro;
    private bool hasImuData;

    private readonly SwingSequenceTracker sequenceTracker = new SwingSequenceTracker();
    private UdpClient sender;
    private UdpClient receiver;
    private IPEndPoint sendEndpoint;
    private SynchronizationContext mainThreadContext;
    private int closing;

    public event Action<SwingEvent> OnSwingReceived;

    public bool IsBridgeConnected => bridgeConnected;
    public int ReceivedEvents => sequenceTracker.ReceivedEvents;
    public int AcceptedEvents => sequenceTracker.AcceptedEvents;
    public int DuplicateEvents => sequenceTracker.DuplicateEvents;
    public int OutOfOrderEvents => sequenceTracker.OutOfOrderEvents;
    public int MissingEvents => sequenceTracker.MissingEvents;
    public int StaleEvents => Volatile.Read(ref staleEvents);
    public int InvalidPackets => Volatile.Read(ref invalidPackets);
    public string LastSwingDebug => lastSwing;

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
        if (message.StartsWith("STATE:", StringComparison.Ordinal))
        {
            string state = message.Substring(6).Trim();
            bool connected = state.Equals("CONNECTED", StringComparison.Ordinal);
            if (connected)
            {
                sequenceTracker.ResetSession();
            }
            PostToMain(() => bridgeConnected = connected);
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

        SwingSequenceResult sequenceResult = sequenceTracker.Observe(swing.Sequence);
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

    private void DispatchSwingOnMainThread(SwingEvent receivedSwing)
    {
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
        lastSwing = $"seq={swing.Sequence} {swing.Direction} strength={swing.Strength:F2}";
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
