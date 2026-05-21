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
    // 最後にデータを受信した時刻（Time.timeAsDouble 基準）。
    // SaberInputBridge が「UDP 無音 → マウスフォールバック」を判定するのに使う。
    public double LastReceivedTime { get; private set; } = -1000.0;
    // 「最近データが来ているか」を判定するヘルパー。既定 1 秒。
    public bool IsRecentlyActive(double thresholdSeconds = 1.0)
    {
        return (Time.timeAsDouble - LastReceivedTime) < thresholdSeconds;
    }
    public Vector2 LocalStickA { get; private set; }
    public Vector2 LocalStickB { get; private set; }
    // 元の board/local 座標（ピクセルや boardRect ローカル）を保持
    public Vector2 LocalStickRawA { get; private set; }
    public Vector2 LocalStickRawB { get; private set; }
    public Vector2 LocalStickA2 { get; private set; }
    public Vector2 LocalStickB2 { get; private set; }
    public float LocalStickLength { get; private set; }
    public float LocalStickLength2 { get; private set; }
    public Vector2 LocalStickRawA2 { get; private set; }
    public Vector2 LocalStickRawB2 { get; private set; }

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

    [Header("Direct world mapping (推奨：正規化入力 -1〜+1 をワールド座標へ直結)")]
    // true にすると、UDP で受け取った (x, y) を camWidth/camHeight や boardRect を経由せず、
    // worldScale を掛けるだけで LocalPosition / LocalStickA / LocalStickB を計算する。
    // 例：入力 (-1, -1)〜(+1, +1) かつ worldScale=(5.5, 3.0) → ワールド (-5.5, -3.0)〜(+5.5, +3.0)
    public bool useDirectWorldMapping = false;
    public Vector2 worldScale = new Vector2(5.5f, 3.0f);
    public Vector2 worldOffset = Vector2.zero;

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
                // 元のローカル座標を保持
                LocalStickRawA = ToLocalPosition(x1a, y1a);
                LocalStickRawB = ToLocalPosition(x1b, y1b);

                // -1..1 正規化（カメラ座標基準）
                float nxA = Mathf.Clamp((x1a / camWidth) * 2f - 1f, -1f, 1f);
                float nyA = Mathf.Clamp((y1a / camHeight) * 2f - 1f, -1f, 1f);
                float nxB = Mathf.Clamp((x1b / camWidth) * 2f - 1f, -1f, 1f);
                float nyB = Mathf.Clamp((y1b / camHeight) * 2f - 1f, -1f, 1f);

                LocalStickA = new Vector2(nxA, nyA);
                LocalStickB = new Vector2(nxB, nyB);

                // 棒長と角度は正規化座標で計算
                LocalStickLength = Vector2.Distance(LocalStickA, LocalStickB);
                LocalAngleDeg = Mathf.Atan2(nyB - nyA, nxB - nxA) * Mathf.Rad2Deg;
            }
            LastReceivedTime = Time.timeAsDouble;
        }

        if (updated2)
        {
            LocalPosition2 = ToLocalPosition(x2, y2);
            NormalizedPosition2 = new Vector2(x2 / camWidth, y2 / camHeight);
            if (updatedStick2)
            {
                LocalStickRawA2 = ToLocalPosition(x2a, y2a);
                LocalStickRawB2 = ToLocalPosition(x2b, y2b);

                float nxA2 = Mathf.Clamp((x2a / camWidth) * 2f - 1f, -1f, 1f);
                float nyA2 = Mathf.Clamp((y2a / camHeight) * 2f - 1f, -1f, 1f);
                float nxB2 = Mathf.Clamp((x2b / camWidth) * 2f - 1f, -1f, 1f);
                float nyB2 = Mathf.Clamp((y2b / camHeight) * 2f - 1f, -1f, 1f);

                LocalStickA2 = new Vector2(nxA2, nyA2);
                LocalStickB2 = new Vector2(nxB2, nyB2);

                LocalStickLength2 = Vector2.Distance(LocalStickA2, LocalStickB2);
                LocalAngleDeg2 = Mathf.Atan2(nyB2 - nyA2, nxB2 - nxA2) * Mathf.Rad2Deg;
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
        // 正規化入力モード：camWidth/camHeight や boardRect を一切経由せず直結
        if (useDirectWorldMapping)
        {
            return new Vector2(x * worldScale.x + worldOffset.x,
                               y * worldScale.y + worldOffset.y);
        }

        // 旧来のチェーン：カメラ座標 → 画面座標 → boardRect ローカル
        float screenX = (x / camWidth) * Screen.width;
        float screenY = (y / camHeight) * Screen.height;
        Vector2 screenPos = new Vector2(screenX, screenY);

        if (boardRect == null)
        {
            return screenPos;
        }

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