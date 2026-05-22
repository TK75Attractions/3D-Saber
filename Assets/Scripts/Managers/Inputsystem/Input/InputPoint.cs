using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Globalization;
using System;

public class InputPoint : MonoBehaviour
{
    public static InputPoint Instance { get; private set; }

    UdpClient udpClient1;
    UdpClient udpClient2;
    Thread receiveThread1;
    Thread receiveThread2;
    public int port = 5005;
    public int port2 = 5006;

    float rawX, rawY;
    float rawX1a, rawY1a, rawX1b, rawY1b;
    bool hasNewData = false;
    bool hasStickData = false;
    float rawX2, rawY2;
    float rawX2a, rawY2a, rawX2b, rawY2b;
    bool hasNewData2 = false;
    bool hasStickData2 = false;

    [Header("Diagnostics (read-only)")]
    [SerializeField] Vector2 latestRawMidpoint;
    [SerializeField] Vector2 latestRawA;
    [SerializeField] Vector2 latestRawB;
    [SerializeField] Vector2 latestLocalPosition;
    [SerializeField] Vector2 latestLocalStickA;
    [SerializeField] Vector2 latestLocalStickB;
    [SerializeField] int packetCount;
    bool loggedFirstPacket;

    public Vector2 NormalizedPosition { get; private set; }
    public Vector2 NormalizedPosition2 { get; private set; }
    public float LocalAngleDeg { get; private set; }
    public float LocalAngleDeg2 { get; private set; }
    public double LastReceivedTime { get; private set; } = -1000.0;
    public bool IsRecentlyActive(double thresholdSeconds = 1.0)
        => (Time.timeAsDouble - LastReceivedTime) < thresholdSeconds;

    public Vector2 LocalStickA { get; private set; }
    public Vector2 LocalStickB { get; private set; }
    public Vector2 LocalStickA2 { get; private set; }
    public Vector2 LocalStickB2 { get; private set; }
    public float LocalStickLength { get; private set; }
    public float LocalStickLength2 { get; private set; }

    // sub で追加された正規化長＆Raw座標
    public float LocalStickLengthNormalized { get; private set; }
    public float LocalStickLengthNormalized2 { get; private set; }
    public Vector2 LocalStickRawA { get; private set; }
    public Vector2 LocalStickRawB { get; private set; }
    public Vector2 LocalStickRawA2 { get; private set; }
    public Vector2 LocalStickRawB2 { get; private set; }

    object lockObj = new object();
    object lockObj2 = new object();

    public float camWidth = 1920f;
    public float camHeight = 1080f;
    public RectTransform boardRect;
    public Vector2 LocalPosition { get; private set; }
    public Vector2 LocalPosition2 { get; private set; }

    [Header("Debug")]
    public bool debugCoordinates = false;

    [Header("IMU Fallback")]
    public bool useImuFallback = false;
    public float imuPositionScale = 200f;

    [Header("Direct world mapping")]
    public bool useDirectWorldMapping = false;
    public Vector2 worldScale = new Vector2(5.5f, 3.0f);
    public Vector2 worldOffset = Vector2.zero;

    void Awake() { Instance = this; }

    float NormalizeAxis(float v, float span)
    {
        if (Mathf.Abs(v) <= 1.5f) return Mathf.Clamp(v, -1f, 1f);
        return Mathf.Clamp((v / span) * 2f - 1f, -1f, 1f);
    }

    void Start()
    {
        udpClient1 = new UdpClient(port);
        receiveThread1 = new Thread(() => ReceiveData(udpClient1, lockObj, false));
        receiveThread1.IsBackground = true;
        receiveThread1.Start();

        udpClient2 = new UdpClient(port2);
        receiveThread2 = new Thread(() => ReceiveData(udpClient2, lockObj2, true));
        receiveThread2.IsBackground = true;
        receiveThread2.Start();
    }

    void ReceiveData(UdpClient client, object targetLock, bool secondStick)
    {
        while (client != null)
        {
            try
            {
                IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = client.Receive(ref endPoint);
                string message = Encoding.UTF8.GetString(data);
                string[] parts = message.Split(',');
                if (parts.Length != 2 && parts.Length != 4) continue;

                if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float a) ||
                    !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                    continue;

                bool isStick = parts.Length == 4;
                float c = 0f, d = 0f;
                if (isStick)
                {
                    if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c) ||
                        !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                        continue;
                }

                lock (targetLock)
                {
                    if (secondStick)
                    {
                        if (isStick)
                        {
                            rawX2a = a; rawY2a = b; rawX2b = c; rawY2b = d;
                            rawX2 = (a + c) * 0.5f; rawY2 = (b + d) * 0.5f;
                            hasStickData2 = true;
                        }
                        else { rawX2 = a; rawY2 = b; hasStickData2 = false; }
                        hasNewData2 = true;
                    }
                    else
                    {
                        if (isStick)
                        {
                            rawX1a = a; rawY1a = b; rawX1b = c; rawY1b = d;
                            rawX = (a + c) * 0.5f; rawY = (b + d) * 0.5f;
                            hasStickData = true;
                        }
                        else { rawX = a; rawY = b; hasStickData = false; }
                        hasNewData = true;
                    }
                }
            }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (ThreadInterruptedException) { break; }
        }
    }

    void Update()
    {
        float x = 0, y = 0;
        bool updated = false, updatedStick = false;
        float x1a = 0, y1a = 0, x1b = 0, y1b = 0;
        float x2 = 0, y2 = 0;
        bool updated2 = false, updatedStick2 = false;
        float x2a = 0, y2a = 0, x2b = 0, y2b = 0;

        lock (lockObj)
        {
            if (hasNewData)
            {
                x = rawX; y = rawY; hasNewData = false;
                updated = true; updatedStick = hasStickData;
                if (updatedStick) { x1a = rawX1a; y1a = rawY1a; x1b = rawX1b; y1b = rawY1b; }
            }
        }
        lock (lockObj2)
        {
            if (hasNewData2)
            {
                x2 = rawX2; y2 = rawY2; hasNewData2 = false;
                updated2 = true; updatedStick2 = hasStickData2;
                if (updatedStick2) { x2a = rawX2a; y2a = rawY2a; x2b = rawX2b; y2b = rawY2b; }
            }
        }

        if (updated)
        {
            bool inputIsNormalized = (Mathf.Abs(x) <= 1.5f && Mathf.Abs(y) <= 1.5f);
            if (inputIsNormalized)
            {
                float nx01 = (x + 1f) * 0.5f, ny01 = (y + 1f) * 0.5f;
                NormalizedPosition = new Vector2(nx01, ny01);
                LocalPosition = useDirectWorldMapping
                    ? ToLocalPosition(x, y)
                    : ToLocalPosition(nx01 * camWidth, ny01 * camHeight);
            }
            else
            {
                NormalizedPosition = new Vector2(x / camWidth, y / camHeight);
                if (useDirectWorldMapping)
                {
                    float normalizedX = (x / camWidth) * 2f - 1f;
                    float normalizedY = (y / camHeight) * 2f - 1f;
                    LocalPosition = ToLocalPosition(normalizedX, normalizedY);
                }
                else
                {
                    LocalPosition = ToLocalPosition(x, y);
                }
            }

            if (updatedStick)
            {
                bool stickAIsNorm = (Mathf.Abs(x1a) <= 1.5f && Mathf.Abs(y1a) <= 1.5f);
                bool stickBIsNorm = (Mathf.Abs(x1b) <= 1.5f && Mathf.Abs(y1b) <= 1.5f);

                float ax, ay, bx, by;
                if (useDirectWorldMapping)
                {
                    ax = stickAIsNorm ? x1a : ((x1a / camWidth) * 2f - 1f);
                    ay = stickAIsNorm ? y1a : ((y1a / camHeight) * 2f - 1f);
                    bx = stickBIsNorm ? x1b : ((x1b / camWidth) * 2f - 1f);
                    by = stickBIsNorm ? y1b : ((y1b / camHeight) * 2f - 1f);
                }
                else
                {
                    ax = stickAIsNorm ? ((x1a + 1f) * 0.5f * camWidth) : x1a;
                    ay = stickAIsNorm ? ((y1a + 1f) * 0.5f * camHeight) : y1a;
                    bx = stickBIsNorm ? ((x1b + 1f) * 0.5f * camWidth) : x1b;
                    by = stickBIsNorm ? ((y1b + 1f) * 0.5f * camHeight) : y1b;
                }

                LocalStickRawA = ToLocalPosition(ax, ay);
                LocalStickRawB = ToLocalPosition(bx, by);

                float nxA = NormalizeAxis(x1a, camWidth);
                float nyA = NormalizeAxis(y1a, camHeight);
                float nxB = NormalizeAxis(x1b, camWidth);
                float nyB = NormalizeAxis(y1b, camHeight);

                LocalStickA = new Vector2(nxA, nyA);
                LocalStickB = new Vector2(nxB, nyB);
                LocalStickLength = Vector2.Distance(LocalStickA, LocalStickB);
                LocalStickLengthNormalized = LocalStickLength / Mathf.Sqrt(8f);
                LocalAngleDeg = Mathf.Atan2(nyB - nyA, nxB - nxA) * Mathf.Rad2Deg;

                // mainの診断フィールド
                latestRawA = new Vector2(x1a, y1a);
                latestRawB = new Vector2(x1b, y1b);
                latestLocalStickA = LocalStickA;
                latestLocalStickB = LocalStickB;
            }

            LastReceivedTime = Time.timeAsDouble;
            latestRawMidpoint = new Vector2(x, y);
            latestLocalPosition = LocalPosition;
            packetCount++;

            if (!loggedFirstPacket)
            {
                loggedFirstPacket = true;
                Debug.Log($"InputPoint: 1st packet raw_mid=({x:F3},{y:F3}) rawA=({latestRawA.x:F3},{latestRawA.y:F3}) rawB=({latestRawB.x:F3},{latestRawB.y:F3}) → LocalPosition=({LocalPosition.x:F3},{LocalPosition.y:F3}) [useDirectWorldMapping={useDirectWorldMapping}, worldScale={worldScale}]");
            }
            if (debugCoordinates)
            {
                Debug.Log($"[InputPoint] raw=({x:F2},{y:F2}) norm=({NormalizedPosition.x:F3},{NormalizedPosition.y:F3}) local=({LocalPosition.x:F1},{LocalPosition.y:F1})");
                if (updatedStick)
                    Debug.Log($"[InputPoint] stickRawA=({LocalStickRawA.x:F1},{LocalStickRawA.y:F1}) stickRawB=({LocalStickRawB.x:F1},{LocalStickRawB.y:F1}) stickNormA=({LocalStickA.x:F3},{LocalStickA.y:F3}) stickNormB=({LocalStickB.x:F3},{LocalStickB.y:F3})");
            }
        }

        if (updated2)
        {
            bool input2IsNormalized = (Mathf.Abs(x2) <= 1.5f && Mathf.Abs(y2) <= 1.5f);
            if (input2IsNormalized)
            {
                float nx01 = (x2 + 1f) * 0.5f, ny01 = (y2 + 1f) * 0.5f;
                NormalizedPosition2 = new Vector2(nx01, ny01);
                LocalPosition2 = useDirectWorldMapping
                    ? ToLocalPosition(x2, y2)
                    : ToLocalPosition(nx01 * camWidth, ny01 * camHeight);
            }
            else
            {
                NormalizedPosition2 = new Vector2(x2 / camWidth, y2 / camHeight);
                LocalPosition2 = ToLocalPosition(x2, y2);
            }

            if (updatedStick2)
            {
                bool stickAIsNorm = (Mathf.Abs(x2a) <= 1.5f && Mathf.Abs(y2a) <= 1.5f);
                bool stickBIsNorm = (Mathf.Abs(x2b) <= 1.5f && Mathf.Abs(y2b) <= 1.5f);

                float ax, ay, bx, by;
                if (useDirectWorldMapping)
                {
                    ax = x2a; ay = y2a; bx = x2b; by = y2b;
                }
                else
                {
                    ax = stickAIsNorm ? ((x2a + 1f) * 0.5f * camWidth) : x2a;
                    ay = stickAIsNorm ? ((y2a + 1f) * 0.5f * camHeight) : y2a;
                    bx = stickBIsNorm ? ((x2b + 1f) * 0.5f * camWidth) : x2b;
                    by = stickBIsNorm ? ((y2b + 1f) * 0.5f * camHeight) : y2b;
                }

                LocalStickRawA2 = ToLocalPosition(ax, ay);
                LocalStickRawB2 = ToLocalPosition(bx, by);

                float nxA2 = NormalizeAxis(x2a, camWidth);
                float nyA2 = NormalizeAxis(y2a, camHeight);
                float nxB2 = NormalizeAxis(x2b, camWidth);
                float nyB2 = NormalizeAxis(y2b, camHeight);

                LocalStickA2 = new Vector2(nxA2, nyA2);
                LocalStickB2 = new Vector2(nxB2, nyB2);
                LocalStickLength2 = Vector2.Distance(LocalStickA2, LocalStickB2);
                LocalStickLengthNormalized2 = LocalStickLength2 / Mathf.Sqrt(8f);
                LocalAngleDeg2 = Mathf.Atan2(nyB2 - nyA2, nxB2 - nxA2) * Mathf.Rad2Deg;
            }
        }

        if (updated) return;
        if (!useImuFallback) return;
        if (!UdpImuBridge.TryGetLatest(out Vector3 accel, out Vector3 gyro, out bool connected) || !connected) return;

        Vector2 imuLocal = new Vector2(gyro.y, -gyro.x) * (imuPositionScale / 100f);
        LocalPosition = Vector2.Lerp(LocalPosition, imuLocal, 0.2f);
        float nx = Mathf.Clamp01((accel.x + 1f) * 0.5f);
        float ny = Mathf.Clamp01((accel.y + 1f) * 0.5f);
        NormalizedPosition = new Vector2(nx, ny);

        if (boardRect != null)
        {
            Vector2 half = boardRect.rect.size * 0.5f;
            LocalPosition = new Vector2(
                Mathf.Clamp(LocalPosition.x, -half.x, half.x),
                Mathf.Clamp(LocalPosition.y, -half.y, half.y)
            );
        }
    }

    Vector2 ToLocalPosition(float x, float y)
    {
        if (useDirectWorldMapping)
            return new Vector2(x * worldScale.x + worldOffset.x, y * worldScale.y + worldOffset.y);

        float screenX = (x / camWidth) * Screen.width;
        float screenY = (y / camHeight) * Screen.height;
        if (boardRect == null) return new Vector2(screenX, screenY);

        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            boardRect, new Vector2(screenX, screenY), null, out Vector2 localPos);
        return localPos;
    }

    void OnDestroy()
    {
        receiveThread1?.Interrupt();
        receiveThread2?.Interrupt();
        udpClient1?.Close();
        udpClient2?.Close();
    }
}