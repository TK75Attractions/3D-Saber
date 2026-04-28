using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Globalization;
using System;

public class InputPoint : MonoBehaviour
{
    // Singleton（どこからでもアクセスするため）
    public static InputPoint Instance { get; private set; }

    UdpClient udpClient1;
    UdpClient udpClient2;
    Thread receiveThread1;
    Thread receiveThread2;
    public int port = 5005;
    public int port2 = 5006;

    // 生データ（スレッドで更新される）
    float rawX, rawY;
    float rawX1a, rawY1a, rawX1b, rawY1b;
    bool hasNewData = false;
    bool hasStickData = false;
    float rawX2, rawY2;
    float rawX2a, rawY2a, rawX2b, rawY2b;
    bool hasNewData2 = false;
    bool hasStickData2 = false;

    // 他スクリプトが読む用（正規化済み）
    public Vector2 NormalizedPosition { get; private set; }
    public Vector2 NormalizedPosition2 { get; private set; }
    public float LocalAngleDeg { get; private set; }
    public float LocalAngleDeg2 { get; private set; }
    public Vector2 LocalStickA { get; private set; }
    public Vector2 LocalStickB { get; private set; }
    public Vector2 LocalStickA2 { get; private set; }
    public Vector2 LocalStickB2 { get; private set; }
    public float LocalStickLength { get; private set; }
    public float LocalStickLength2 { get; private set; }

    // スレッド同期用
    object lockObj = new object();
    object lockObj2 = new object();

    // カメラ解像度
    public float camWidth = 1920f;
    public float camHeight = 1080f;
    public RectTransform boardRect;
    public Vector2 LocalPosition { get; private set; }
    public Vector2 LocalPosition2 { get; private set; }

    [Header("IMU Fallback")]
    public bool useImuFallback = true;
    public float imuPositionScale = 200f;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        // UDP受信開始(棒1)
        udpClient1 = new UdpClient(port);
        receiveThread1 = new Thread(() => ReceiveData(udpClient1, lockObj, false));
        receiveThread1.IsBackground = true;
        receiveThread1.Start();

        // UDP受信開始(棒2)
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
                if (parts.Length != 2 && parts.Length != 4)
                {
                    continue;
                }

                if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float a) ||
                    !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                {
                    continue;
                }

                bool isStick = parts.Length == 4;
                float c = 0f;
                float d = 0f;
                if (isStick)
                {
                    if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c) ||
                        !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    {
                        continue;
                    }
                }

                // メインスレッドと衝突しないようロック
                lock (targetLock)
                {
                    if (secondStick)
                    {
                        if (isStick)
                        {
                            rawX2a = a;
                            rawY2a = b;
                            rawX2b = c;
                            rawY2b = d;
                            rawX2 = (a + c) * 0.5f;
                            rawY2 = (b + d) * 0.5f;
                            hasStickData2 = true;
                        }
                        else
                        {
                            rawX2 = a;
                            rawY2 = b;
                            hasStickData2 = false;
                        }
                        hasNewData2 = true;
                    }
                    else
                    {
                        if (isStick)
                        {
                            rawX1a = a;
                            rawY1a = b;
                            rawX1b = c;
                            rawY1b = d;
                            rawX = (a + c) * 0.5f;
                            rawY = (b + d) * 0.5f;
                            hasStickData = true;
                        }
                        else
                        {
                            rawX = a;
                            rawY = b;
                            hasStickData = false;
                        }
                        hasNewData = true;
                    }
                }
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
        }
    }

    void Update()
    {
        float x = 0, y = 0;
        bool updated = false;
        bool updatedStick = false;
        float x1a = 0, y1a = 0, x1b = 0, y1b = 0;
        float x2 = 0, y2 = 0;
        bool updated2 = false;
        bool updatedStick2 = false;
        float x2a = 0, y2a = 0, x2b = 0, y2b = 0;

        // スレッドから受け取った値をコピー
        lock (lockObj)
        {
            if (hasNewData)
            {
                x = rawX;
                y = rawY;
                hasNewData = false;
                updated = true;
                updatedStick = hasStickData;
                if (updatedStick)
                {
                    x1a = rawX1a;
                    y1a = rawY1a;
                    x1b = rawX1b;
                    y1b = rawY1b;
                }
            }
        }

        lock (lockObj2)
        {
            if (hasNewData2)
            {
                x2 = rawX2;
                y2 = rawY2;
                hasNewData2 = false;
                updated2 = true;
                updatedStick2 = hasStickData2;
                if (updatedStick2)
                {
                    x2a = rawX2a;
                    y2a = rawY2a;
                    x2b = rawX2b;
                    y2b = rawY2b;
                }
            }
        }

        if (updated)
        {
            LocalPosition = ToLocalPosition(x, y);
            NormalizedPosition = new Vector2(x / camWidth, y / camHeight);
            if (updatedStick)
            {
                LocalStickA = ToLocalPosition(x1a, y1a);
                LocalStickB = ToLocalPosition(x1b, y1b);
                LocalStickLength = Vector2.Distance(LocalStickA, LocalStickB);
                LocalAngleDeg = Mathf.Atan2(y1b - y1a, x1b - x1a) * Mathf.Rad2Deg;
            }
        }

        if (updated2)
        {
            LocalPosition2 = ToLocalPosition(x2, y2);
            NormalizedPosition2 = new Vector2(x2 / camWidth, y2 / camHeight);
            if (updatedStick2)
            {
                LocalStickA2 = ToLocalPosition(x2a, y2a);
                LocalStickB2 = ToLocalPosition(x2b, y2b);
                LocalStickLength2 = Vector2.Distance(LocalStickA2, LocalStickB2);
                LocalAngleDeg2 = Mathf.Atan2(y2b - y2a, x2b - x2a) * Mathf.Rad2Deg;
            }
        }

        if (updated)
        {
            return;
        }

        if (!useImuFallback)
        {
            return;
        }

        if (!UdpImuBridge.TryGetLatest(out Vector3 accel, out Vector3 gyro, out bool connected) || !connected)
        {
            return;
        }

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
        // カメラ座標 → 画面座標に変換
        float screenX = (x / camWidth) * Screen.width;
        float screenY = (y / camHeight) * Screen.height;
        Vector2 screenPos = new Vector2(screenX, screenY);

        if (boardRect == null)
        {
            return screenPos;
        }

        // 画面座標 → boardRectのローカル座標
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            boardRect,
            screenPos,
            null,
            out Vector2 localPos
        );
        return localPos;
    }

    void OnDestroy()
    {
        // スレッド終了
        receiveThread1?.Interrupt(); // Abortより安全
        receiveThread2?.Interrupt();
        udpClient1?.Close();
        udpClient2?.Close();
    }
}