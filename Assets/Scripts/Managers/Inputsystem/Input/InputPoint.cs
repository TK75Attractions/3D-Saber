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
    // 最後にデータを受信した時刻（Time.timeAsDouble 基準）。棒1（port）/棒2（port2）で別管理。
    // SaberInputBridge が「UDP 無音 → マウスフォールバック/非表示」を判定するのに使う。
    public double LastReceivedTime { get; private set; } = -1000.0;
    public double LastReceivedTime2 { get; private set; } = -1000.0;
    // 「最近データが来ているか」を判定するヘルパー。既定 1 秒。
    public bool IsRecentlyActive(double thresholdSeconds = 1.0)
    {
        return (Time.timeAsDouble - LastReceivedTime) < thresholdSeconds;
    }
    public bool IsRecentlyActive2(double thresholdSeconds = 1.0)
    {
        return (Time.timeAsDouble - LastReceivedTime2) < thresholdSeconds;
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
    // 0..1 に正規化した棒の長さ（対角最大長 sqrt(8) で割る）
    public float LocalStickLengthNormalized { get; private set; }
    public float LocalStickLengthNormalized2 { get; private set; }
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
    [Header("Debug")]
    public bool debugCoordinates = false;
    public bool debugReceiveRate = false;

    int receivedCountPort1Window = 0;
    int receivedCountPort2Window = 0;
    float lastRateLogTime = 0f;

    [Header("IMU Fallback")]
    public bool useImuFallback = false;
    public float imuPositionScale = 200f;

    [Header("Direct world mapping (推奨：正規化入力 -1〜+1 をワールド座標へ直結)")]
    // true にすると、UDP で受け取った (x, y) を camWidth/camHeight や boardRect を経由せず、
    // worldScale を掛けるだけで LocalPosition / LocalStickA / LocalStickB を計算する。
    // 例：入力 (-1, -1)〜(+1, +1) かつ worldScale=(5.5, 3.0) → ワールド (-5.5, -3.0)〜(+5.5, +3.0)
    public bool useDirectWorldMapping = false;
    public Vector2 worldScale = new Vector2(5.5f, 3.0f);
    public Vector2 worldOffset = Vector2.zero;

    [Header("Sensitivity")]
    // 入力感度。中央を基準に手の移動量を増幅する(1=等倍、2=同じ動きで2倍動く)。
    // 画面端まで大きく動かないと届かない問題への対策。
    // 棒の2端点は「中点の移動」に合わせて平行移動するため、棒の長さ・角度は変わらない
    // (端点ごとにスケールすると棒が2倍に伸びてノーツ判定の難度が変わってしまう)。
    public float sensitivity = 2f;

    void Awake()
    {
        // 既にシーン跨ぎの受信機が稼働中(タイトル/曲選択で EnsureInstance 済み)の場合、
        // シーン直置きの自分が二重にポートを掴もうとすると SocketException (Address already in use)
        // になり、しかも Instance を上書きしてしまうと「死んだ受信機」が全入力の窓口になる。
        // → 先住インスタンスを優先し、後から来た自分はコンポーネントだけ退場する(GOと他コンポは残す)。
        if (Instance != null && Instance != this)
        {
            if (Application.isPlaying) Destroy(this);
            else DestroyImmediate(this);
            return;
        }
        Instance = this;
    }

    // どのシーンからでも呼べる生成ヘルパー(タイトル/曲選択でもセーバーを使えるようにする)。
    // 送信側仕様(中央原点 -1..+1 正規化 → world直結)で構成し、シーンを跨いで維持する
    // (UDPソケットをシーン遷移ごとに開き直さないため)。冪等。
    public static InputPoint EnsureInstance()
    {
        var ip = Instance != null ? Instance : FindFirstObjectByType<InputPoint>();
        if (ip == null)
        {
            var go = new GameObject("InputPoint");
            if (Application.isPlaying) DontDestroyOnLoad(go);
            ip = go.AddComponent<InputPoint>();
        }
        // EditMode では Awake が呼ばれないため、ここでも明示的に設定する(冪等)
        Instance = ip;
        ip.useDirectWorldMapping = true;
        ip.worldScale = new Vector2(5.5f, 3.0f);
        ip.worldOffset = Vector2.zero;
        return ip;
    }

    // 入力座標が既に -1..1 の範囲で来る場合と、ピクセル座標で来る場合の両対応を行う。
    // 小さな絶対値 (<=1.5) はそのまま正規化値とみなし、大きければピクセル幅で正規化する。
    float NormalizeAxis(float v, float span)
    {
        if (Mathf.Abs(v) <= 1.5f)
        {
            return Mathf.Clamp(v, -1f, 1f);
        }
        float n = (v / span) * 2f - 1f;
        return Mathf.Clamp(n, -1f, 1f);
    }

    // 1点(x,y)を ToLocalPosition が期待する単位へ揃える純関数。
    // 入力が正規化(-1..1、両軸とも |v|<=1.5)かピクセルかを点単位で自動判別する。
    //   direct モード: 正規化を期待 → ピクセルなら正規化へ変換
    //   legacy モード: カメラ座標(ピクセル)を期待 → 正規化ならピクセルへ変換
    // 棒1・棒2の両方がこの関数を通ることで変換の対称性を保証する
    // (棒2だけ direct+ピクセルで素通しになり画面隅に張り付くバグの再発防止)。
    public static Vector2 CanonicalizePoint(float x, float y, float width, float height, bool directMapping)
    {
        bool isNormalized = Mathf.Abs(x) <= 1.5f && Mathf.Abs(y) <= 1.5f;
        if (directMapping)
        {
            return isNormalized
                ? new Vector2(x, y)
                : new Vector2((x / width) * 2f - 1f, (y / height) * 2f - 1f);
        }
        return isNormalized
            ? new Vector2((x + 1f) * 0.5f * width, (y + 1f) * 0.5f * height)
            : new Vector2(x, y);
    }

    // NormalizedPosition(0..1)用の変換。こちらも棒1/棒2共通の純関数。
    public static Vector2 Normalized01(float x, float y, float width, float height)
    {
        bool isNormalized = Mathf.Abs(x) <= 1.5f && Mathf.Abs(y) <= 1.5f;
        return isNormalized
            ? new Vector2((x + 1f) * 0.5f, (y + 1f) * 0.5f)
            : new Vector2(x / width, y / height);
    }

    // 中点(CanonicalizePoint 通過後の座標)に感度を適用する純関数。
    // 生値に掛けると |v|<=1.5 の正規化/ピクセル自動判別が壊れるため、必ず canonical 化の後に呼ぶ。
    //   direct モード: 中心0の -1..1 → 単純スケール後 ±1 にクランプ
    //   legacy モード: 中心 (w/2, h/2) のピクセル → 中心基準スケール後 0..幅/高さ にクランプ
    public static Vector2 ApplySensitivity(Vector2 mid, float sensitivity, bool directMapping, float width, float height)
    {
        if (directMapping)
        {
            return new Vector2(
                Mathf.Clamp(mid.x * sensitivity, -1f, 1f),
                Mathf.Clamp(mid.y * sensitivity, -1f, 1f));
        }
        float cx = width * 0.5f;
        float cy = height * 0.5f;
        return new Vector2(
            Mathf.Clamp(cx + (mid.x - cx) * sensitivity, 0f, width),
            Mathf.Clamp(cy + (mid.y - cy) * sensitivity, 0f, height));
    }

    // NormalizedPosition(0..1)への感度適用。中心 0.5 基準でスケールし 0..1 にクランプ。
    public static Vector2 ApplySensitivity01(Vector2 normalized, float sensitivity)
    {
        return new Vector2(
            Mathf.Clamp01(0.5f + (normalized.x - 0.5f) * sensitivity),
            Mathf.Clamp01(0.5f + (normalized.y - 0.5f) * sensitivity));
    }

    void Start()
    {
        // Awake で重複退場した場合は受信を開始しない
        if (Instance != this) return;

        lastRateLogTime = Time.realtimeSinceStartup;

        try
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
        catch (SocketException e)
        {
            // 例外で Start が途切れて無言の入力死を起こさないよう、明示的な警告に変えて続行する
            Debug.LogWarning($"[InputPoint] UDP ポート {port}/{port2} を開けませんでした(既に使用中?): {e.Message}");
        }
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
                        Interlocked.Increment(ref receivedCountPort2Window);
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
                        Interlocked.Increment(ref receivedCountPort1Window);
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

        if (debugReceiveRate)
        {
            float now = Time.realtimeSinceStartup;
            float dt = now - lastRateLogTime;
            if (dt >= 1.0f)
            {
                int c1 = Interlocked.Exchange(ref receivedCountPort1Window, 0);
                int c2 = Interlocked.Exchange(ref receivedCountPort2Window, 0);
                float hz1 = c1 / dt;
                float hz2 = c2 / dt;
                Debug.Log($"[InputPoint] recv rate: port {port}={hz1:F1}Hz ({c1} pkt/{dt:F2}s), port {port2}={hz2:F1}Hz ({c2} pkt/{dt:F2}s)");
                lastRateLogTime = now;
            }
        }

        if (updated)
        {
            // 中点: 正規化/ピクセルの判別と単位揃えは棒1・棒2共通の純関数で行う
            NormalizedPosition = ApplySensitivity01(Normalized01(x, y, camWidth, camHeight), sensitivity);
            Vector2 mid1 = CanonicalizePoint(x, y, camWidth, camHeight, useDirectWorldMapping);
            // 感度は中点に掛け、端点は同じ量だけ平行移動する(棒の長さ・角度を保つ)
            Vector2 sensMid1 = ApplySensitivity(mid1, sensitivity, useDirectWorldMapping, camWidth, camHeight);
            Vector2 delta1 = sensMid1 - mid1;
            LocalPosition = ToLocalPosition(sensMid1.x, sensMid1.y);

            if (updatedStick)
            {
                Vector2 end1a = CanonicalizePoint(x1a, y1a, camWidth, camHeight, useDirectWorldMapping) + delta1;
                Vector2 end1b = CanonicalizePoint(x1b, y1b, camWidth, camHeight, useDirectWorldMapping) + delta1;
                LocalStickRawA = ToLocalPosition(end1a.x, end1a.y);
                LocalStickRawB = ToLocalPosition(end1b.x, end1b.y);

                // -1..1 正規化は既存の NormalizeAxis で扱う（混在対応）
                float nxA = NormalizeAxis(x1a, camWidth);
                float nyA = NormalizeAxis(y1a, camHeight);
                float nxB = NormalizeAxis(x1b, camWidth);
                float nyB = NormalizeAxis(y1b, camHeight);

                LocalStickA = new Vector2(nxA, nyA);
                LocalStickB = new Vector2(nxB, nyB);

                // 棒長と角度は正規化座標で計算
                LocalStickLength = Vector2.Distance(LocalStickA, LocalStickB);
                LocalStickLengthNormalized = LocalStickLength / Mathf.Sqrt(8f);
                LocalAngleDeg = Mathf.Atan2(nyB - nyA, nxB - nxA) * Mathf.Rad2Deg;
            }
            LastReceivedTime = Time.timeAsDouble;
            if (debugCoordinates)
            {
                bool isNorm = Mathf.Abs(x) <= 1.5f && Mathf.Abs(y) <= 1.5f;
                Debug.Log($"[InputPoint] raw=({x:F2},{y:F2}) norm=({NormalizedPosition.x:F3},{NormalizedPosition.y:F3}) local=({LocalPosition.x:F1},{LocalPosition.y:F1}) isNorm={isNorm}");
                if (updatedStick)
                {
                    Debug.Log($"[InputPoint] stickRawA=({LocalStickRawA.x:F1},{LocalStickRawA.y:F1}) stickRawB=({LocalStickRawB.x:F1},{LocalStickRawB.y:F1}) stickNormA=({LocalStickA.x:F3},{LocalStickA.y:F3}) stickNormB=({LocalStickB.x:F3},{LocalStickB.y:F3})");
                }
            }
        }

        if (updated2)
        {
            // 棒2も棒1と同一の純関数で変換する。
            // (旧実装は direct モードでピクセル→正規化の変換が抜けており、
            //  ピクセル送信のトラッカーだと棒2だけ画面隅に張り付くバグがあった)
            NormalizedPosition2 = ApplySensitivity01(Normalized01(x2, y2, camWidth, camHeight), sensitivity);
            Vector2 mid2 = CanonicalizePoint(x2, y2, camWidth, camHeight, useDirectWorldMapping);
            // 棒1と同じく: 感度は中点、端点は平行移動
            Vector2 sensMid2 = ApplySensitivity(mid2, sensitivity, useDirectWorldMapping, camWidth, camHeight);
            Vector2 delta2 = sensMid2 - mid2;
            LocalPosition2 = ToLocalPosition(sensMid2.x, sensMid2.y);

            if (updatedStick2)
            {
                Vector2 end2a = CanonicalizePoint(x2a, y2a, camWidth, camHeight, useDirectWorldMapping) + delta2;
                Vector2 end2b = CanonicalizePoint(x2b, y2b, camWidth, camHeight, useDirectWorldMapping) + delta2;
                LocalStickRawA2 = ToLocalPosition(end2a.x, end2a.y);
                LocalStickRawB2 = ToLocalPosition(end2b.x, end2b.y);

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
            LastReceivedTime2 = Time.timeAsDouble;
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
        //Debug.Log(LocalStickA + " " + LocalStickB);
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