using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class UdpImuBridge : MonoBehaviour
{
    public static UdpImuBridge Instance { get; private set; }

    [Header("UDP")]
    [SerializeField] private string host = "127.0.0.1";
    [SerializeField] private int commandPort = 9001;
    [SerializeField] private int dataPort = 9002;

    [Header("Latest Values")]
    [SerializeField] private Vector3 latestAcceleration;
    [SerializeField] private Vector3 latestGyro;
    [SerializeField] private bool bridgeConnected;
    [SerializeField] private bool hasImuData;

    private UdpClient sender;
    private UdpClient receiver;
    private IPEndPoint sendEndpoint;
    private readonly Queue<string> inbox = new Queue<string>();
    private readonly object inboxLock = new object();

    public Vector3 LatestAcceleration => latestAcceleration;
    public Vector3 LatestGyro => latestGyro;
    public bool IsBridgeConnected => bridgeConnected;
    public bool HasImuData => hasImuData;

    public static bool TryGetLatest(out Vector3 acceleration, out Vector3 gyro, out bool connected)
    {
        if (Instance == null)
        {
            acceleration = Vector3.zero;
            gyro = Vector3.zero;
            connected = false;
            return false;
        }

        acceleration = Instance.latestAcceleration;
        gyro = Instance.latestGyro;
        connected = Instance.bridgeConnected;
        return Instance.hasImuData;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        sendEndpoint = new IPEndPoint(IPAddress.Parse(host), commandPort);
        sender = new UdpClient();
        receiver = new UdpClient(dataPort);

        Haptic.SetTransport(OnHapticSend);
        BeginReceive();
        SendCommand("PING");

        Debug.Log($"UdpImuBridge started: cmd={commandPort}, data={dataPort}");
    }

    private void Update()
    {
        while (true)
        {
            string message;
            lock (inboxLock)
            {
                if (inbox.Count == 0)
                {
                    break;
                }

                message = inbox.Dequeue();
            }

            HandleMessage(message);
        }

        if (IsHapticTestKeyPressed())
        {
            Haptic.Vibrate(0.15f);
        }
    }

    private static bool IsHapticTestKeyPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.hKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.H);
#else
        return false;
#endif
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        Haptic.SetTransport(null);

        if (receiver != null)
        {
            receiver.Close();
            receiver = null;
        }

        if (sender != null)
        {
            sender.Close();
            sender = null;
        }
    }

    private void OnHapticSend(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        SendCommand("H:" + trimmed);
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

    private void BeginReceive()
    {
        if (receiver == null)
        {
            return;
        }

        receiver.BeginReceive(OnReceive, null);
    }

    private void OnReceive(IAsyncResult ar)
    {
        if (receiver == null)
        {
            return;
        }

        IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
        byte[] bytes;
        try
        {
            bytes = receiver.EndReceive(ar, ref any);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("UdpImuBridge receive error: " + ex.Message);
            BeginReceive();
            return;
        }

        string msg = Encoding.UTF8.GetString(bytes).Trim();
        lock (inboxLock)
        {
            inbox.Enqueue(msg);
        }

        BeginReceive();
    }

    private void HandleMessage(string message)
    {
        if (message.StartsWith("STATE:"))
        {
            bridgeConnected = message.EndsWith("CONNECTED", StringComparison.Ordinal);
            return;
        }

        if (!message.StartsWith("IMU:"))
        {
            return;
        }

        string payload = message.Substring(4);
        string[] values = payload.Split(',');
        if (values.Length < 6)
        {
            return;
        }

        if (!TryParse(values[0], out float ax) ||
            !TryParse(values[1], out float ay) ||
            !TryParse(values[2], out float az) ||
            !TryParse(values[3], out float gx) ||
            !TryParse(values[4], out float gy) ||
            !TryParse(values[5], out float gz))
        {
            return;
        }

        latestAcceleration = new Vector3(ax, ay, az);
        latestGyro = new Vector3(gx, gy, gz);
        hasImuData = true;
    }

    private static bool TryParse(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}
