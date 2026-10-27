using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Globalization;
using System;

// 受信値の取り込みを、読む側（SaberInputBridge・遅延テスト等）より先に行い、1フレーム遅れを防ぐ。
[DefaultExecutionOrder(-2000)]
public class InputPoint : MonoBehaviour
{
    // Singleton（どこからでもアクセスするため）
    public static InputPoint Instance { get; private set; }

    UdpClient udpClient1;
    UdpClient udpClient2;
    Thread receiveThread1;
    Thread receiveThread2;
    readonly object networkLifecycleLock = new object();
    ManualResetEvent receiverStopSignal1;
    ManualResetEvent receiverStopSignal2;
    volatile bool networkShutdown = true;
    volatile bool receiverAlive1;
    volatile bool receiverAlive2;
    int receiverRestartCount1;
    int receiverRestartCount2;
    string lastReceiverExitReason1 = "Not started";
    string lastReceiverExitReason2 = "Not started";
    string pendingReceiverDiagnostic1;
    string pendingReceiverDiagnostic2;
    const int ReceiverInitialBackoffMilliseconds = 200;
    const int ReceiverMaximumBackoffMilliseconds = 2000;
    const int ReceiverJoinTimeoutMilliseconds = 2000;
    PhoneSaberBonjourPublisher bonjourPublisher;
    // Android 版が同じ Wi-Fi 上の PC(Windows / Mac)を見つけるための UDP 5007 応答。
    PhoneSaberDiscoveryResponder discoveryResponder;
    // iPhone からの P2P 経路用 bridge(座標を 127.0.0.1:5005/5006 へ転送するだけ)。受信処理は変えない。
    // Bonjour と同じく、5005/5006 の両方を bind できている間だけ動かす(iPhone が LAN へ戻れるように)。
    PhoneSaberP2PBridgeProcess p2pBridge;
    // SetReceiverAlive は受信 thread で動くため、Application.dataPath は main thread で控えておく。
    string p2pDataPath;
    // main thread の StartNetworkServices だけが書く。読む側は lock を取らない
    // (受信 thread が networkLifecycleLock を持ったまま外部 process を起動・停止しても、毎フレームの読み出しを止めない)。
    volatile string phoneSaberStation;
    // 到着統計と送信元ゲートを同じ lock で同期。座標・最終有効入力時刻は受理時だけ更新する。
    readonly PhoneSaberPacketStatistics redInputStats = new PhoneSaberPacketStatistics();
    readonly PhoneSaberPacketStatistics blueInputStats = new PhoneSaberPacketStatistics();
    PhoneSaberConnectionEvents redConnectionEvents;
    PhoneSaberConnectionEvents blueConnectionEvents;
    public int port = 5005;
    public int port2 = 5006;

    // 生データ（スレッドで更新される）
    float rawX, rawY;
    float rawX1a, rawY1a, rawX1b, rawY1b;
    volatile bool hasNewData = false;
    bool hasStickData = false;
    long rawReceiveTimestampTicks;
    string rawSender, rawSender2;
    public string RedCoordinateSender { get; private set; }
    public string BlueCoordinateSender { get; private set; }
    public PhoneSaberMetrics ReadPhoneMetrics(string sender, double now)
        => Volatile.Read(ref discoveryResponder)?.Metrics.Read(sender, now);
    public System.Collections.Generic.Dictionary<string, PhoneSaberMetrics> ReadFreshPhoneMetrics(double now)
        => Volatile.Read(ref discoveryResponder)?.Metrics.ReadFresh(now)
            ?? new System.Collections.Generic.Dictionary<string, PhoneSaberMetrics>();
    float rawX2, rawY2;
    float rawX2a, rawY2a, rawX2b, rawY2b;
    volatile bool hasNewData2 = false;
    bool hasStickData2 = false;
    long rawReceiveTimestampTicks2;

    // 他スクリプトが読む用（正規化済み）
    public Vector2 NormalizedPosition { get; private set; }
    // 「この赤を無視する」用: 最後に届いた生の両端（カメラ画像の 0..1、位置補正前）と、無視した受信の数。
    public Vector2 LastRawEndsA { get; private set; }
    public Vector2 LastRawEndsB { get; private set; }
    public Vector2 LastRawEndsA2 { get; private set; }
    public Vector2 LastRawEndsB2 { get; private set; }
    public int IgnoredSamples { get; private set; }
    public int IgnoredSamples2 { get; private set; }
    System.Collections.Generic.List<Rect> ignoreZones;
    int ignoreZonesRevision = -1;
    string ignoreZonesStation;
    System.Collections.Generic.List<Rect> IgnoreZones()
    {
        string station = StationLabel;
        if (ignoreZones == null || ignoreZonesRevision != PhoneSaberIgnoreZones.Revision || ignoreZonesStation != station)
        {
            ignoreZones = PhoneSaberIgnoreZones.Load(station);
            ignoreZonesRevision = PhoneSaberIgnoreZones.Revision;
            ignoreZonesStation = station;
        }
        return ignoreZones;
    }
    public Vector2 NormalizedPosition2 { get; private set; }
    public float LocalAngleDeg { get; private set; }
    public float LocalAngleDeg2 { get; private set; }
    // 最後にデータを受信した時刻（unscaled monotonic real time 基準）。棒1/2で別管理。
    // SaberInputBridge が「UDP 無音 → マウスフォールバック/非表示」を判定するのに使う。
    public double LastReceivedTime { get; private set; } = -1000.0;
    public double LastReceivedTime2 { get; private set; } = -1000.0;
    // 最後に採用したカメラの中点。位置補正・感度補正の前、画像内の 0..1。
    // 小窓用の時刻は受信機の再起動でも残す。古い点として表示し、現在位置とは扱わない。
    public Vector2 LastCameraPosition01 { get; private set; }
    public Vector2 LastCameraPosition012 { get; private set; }
    public double LastCameraReceivedTime { get; private set; } = -1000;
    public double LastCameraReceivedTime2 { get; private set; } = -1000;
    // UDP receive() 完了時点のOS monotonic clock。Task 1のCamera/IMU照合は
    // UnityのUpdate時刻ではなく、この受信時刻をSwingEventと同じ時計で使う。
    public double LastReceivedMonotonicTime { get; private set; } = double.NegativeInfinity;
    public double LastReceivedMonotonicTime2 { get; private set; } = double.NegativeInfinity;
    // 診断用。受信・公開・消費者の読み出しを同じ monotonic 時計で区別する。
    public PhoneSaberCoordinateTiming RedCoordinateTiming { get; } = new PhoneSaberCoordinateTiming();
    public PhoneSaberCoordinateTiming BlueCoordinateTiming { get; } = new PhoneSaberCoordinateTiming();
    static void RecordCoordinateRead(PhoneSaberCoordinateTiming timing)
    {
        // 同じサンプルの端点を繰り返し読んでも時計取得・記録は初回だけ。
        if (timing.NeedsFirstRead)
            timing.Read(SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
    }

    // 「最近データが来ているか」を判定するヘルパー。既定 1 秒。
    public bool IsRecentlyActive(double thresholdSeconds = 1.0)
    {
        return (Time.realtimeSinceStartupAsDouble - LastReceivedTime) < thresholdSeconds;
    }
    public bool IsRecentlyActive2(double thresholdSeconds = 1.0)
    {
        return (Time.realtimeSinceStartupAsDouble - LastReceivedTime2) < thresholdSeconds;
    }
    public Vector2 LocalStickA { get; private set; }
    public Vector2 LocalStickB { get; private set; }
    // 元の board/local 座標（ピクセルや boardRect ローカル）を保持
    Vector2 localStickRawA;
    public Vector2 LocalStickRawA
    {
        get { RecordCoordinateRead(RedCoordinateTiming); return localStickRawA; }
        private set { localStickRawA = value; }
    }
    Vector2 localStickRawB;
    public Vector2 LocalStickRawB
    {
        get { RecordCoordinateRead(RedCoordinateTiming); return localStickRawB; }
        private set { localStickRawB = value; }
    }
    public Vector2 LocalStickA2 { get; private set; }
    public Vector2 LocalStickB2 { get; private set; }
    public float LocalStickLength { get; private set; }
    public float LocalStickLength2 { get; private set; }
    // 0..1 に正規化した棒の長さ（対角最大長 sqrt(8) で割る）
    public float LocalStickLengthNormalized { get; private set; }
    public float LocalStickLengthNormalized2 { get; private set; }
    Vector2 localStickRawA2;
    public Vector2 LocalStickRawA2
    {
        get { RecordCoordinateRead(BlueCoordinateTiming); return localStickRawA2; }
        private set { localStickRawA2 = value; }
    }
    Vector2 localStickRawB2;
    public Vector2 LocalStickRawB2
    {
        get { RecordCoordinateRead(BlueCoordinateTiming); return localStickRawB2; }
        private set { localStickRawB2 = value; }
    }
    // 直近packetが4要素の棒端点を含んでいたか。2要素packetで必ずfalseに戻す。
    public bool HasValidStickEndpoints { get; private set; }
    public bool HasValidStickEndpoints2 { get; private set; }

    // 予測設定と履歴の世代は main thread 専用。受信・認識結果には触れない。
    public int PredictionHorizonMilliseconds { get; private set; }
    public int PredictionRevision { get; private set; }
    public bool PredictionAutomatic { get; private set; }
    public bool PredictionClocksSynchronized { get; set; }
    public string PredictionLeadSource { get; private set; } = "manual";
    public long PredictionDraws { get; private set; }
    public long PredictionSuppressedDraws { get; private set; }
    double nextPredictionRefresh;
    PhoneSaberAutomaticLeadPolicy redPrediction = new PhoneSaberAutomaticLeadPolicy(), bluePrediction = new PhoneSaberAutomaticLeadPolicy();
    public int PredictionLead(bool blue) => PredictionAutomatic ? (blue ? bluePrediction : redPrediction).Lead : PredictionHorizonMilliseconds;
    public string PredictionSource(bool blue) => PredictionAutomatic ? (blue ? bluePrediction : redPrediction).Source : "manual";

    public void RecordPrediction(bool suppressed)
    {
        PredictionDraws++;
        if (suppressed) PredictionSuppressedDraws++;
    }

    public void SetPredictionAutomatic()
    {
        redPrediction = new PhoneSaberAutomaticLeadPolicy(); bluePrediction = new PhoneSaberAutomaticLeadPolicy();
        PredictionAutomatic = true;
        PhoneSaberPredictionSettings.SaveAutomatic(StationLabel, true);
        PredictionRevision++;
        RefreshPredictionLead(true);
    }

    void RefreshPredictionLead(bool forceLog = false)
    {
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        RefreshPredictionColour(false, now, redPrediction, forceLog);
        RefreshPredictionColour(true, now, bluePrediction, forceLog);
        PredictionHorizonMilliseconds = redPrediction.Lead;
        PredictionLeadSource = redPrediction.Source;
    }

    void RefreshPredictionColour(bool blue, double now, PhoneSaberAutomaticLeadPolicy policy, bool forceLog)
    {
        var stats = ReadInputStats(blue);
        PhoneSaberPredictionSettings.AutomaticLead(stats, PredictionClocksSynchronized, out bool measured);
        string sender = blue ? BlueCoordinateSender : RedCoordinateSender;
        var estimate = PhoneSaberPerfLog.Estimate(this, blue, now, (blue ? BlueCoordinateTiming : RedCoordinateTiming).Shown);
        double? total = PhoneSaberPredictionSettings.AutomaticEstimate(estimate);
        double expiresAt = (blue ? LastReceivedMonotonicTime2 : LastReceivedMonotonicTime) + 15;
        if (total.HasValue)
            expiresAt = System.Math.Min(expiresAt, System.Math.Min(estimate.Phone.ReceivedAt + 15,
                estimate.Phone.ReceivedAt + 10 - estimate.Phone.RttAge.Value));
        if (!total.HasValue && !PredictionClocksSynchronized && policy.Source == "one-way-ts")
            expiresAt = now;
        string device = estimate.Phone == null ? null : estimate.Phone.Platform + ":" + estimate.Phone.App;
        if (policy.Update(now, sender, total, measured ? (double?)stats.OneWayDelay.MedianMs : null, expiresAt, device) || forceLog)
            PhoneSaberEventLog.Record(StationLabel, blue ? "BLUE" : "RED", "prediction-lead",
                "lead-ms=" + policy.Lead + " source=" + policy.Source + " median-ms=" +
                policy.Median.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                " clamped=" + (policy.Clamped ? "1" : "0") + " maximum-ms=" + PhoneSaberEndpointPredictor.MaximumHorizonMilliseconds +
                " sender=" + (sender ?? "unknown"));
    }

    public void SetPredictionHorizonMilliseconds(int milliseconds)
    {
        PredictionAutomatic = false;
        PredictionLeadSource = "manual";
        PhoneSaberPredictionSettings.SaveAutomatic(StationLabel, false);
        PredictionHorizonMilliseconds = PhoneSaberPredictionSettings.Clamp(milliseconds);
        PredictionRevision++;
        PhoneSaberPredictionSettings.Save(StationLabel, PredictionHorizonMilliseconds);
        PhoneSaberEventLog.Record(StationLabel, "SYSTEM", "prediction-lead",
            "lead-ms=" + PredictionHorizonMilliseconds + " source=manual clamped=0");
    }

    // 色ごとのreceiver診断。一方の異常は反対色の状態に影響させない。
    public bool ReceiverAlive => receiverAlive1;
    public bool ReceiverAlive2 => receiverAlive2;
    public bool BonjourPublicationEligible => !networkShutdown && receiverAlive1 && receiverAlive2;
    // 表示・毎フレーム用の読み出しは networkLifecycleLock を取らない。参照の読み出しは atomic で、
    // 停止直後の古い参照を読んでも、その publisher/responder 自身が停止済みとして false を返す。
    public bool BonjourPublisherRunning
    {
        get
        {
            var publisher = Volatile.Read(ref bonjourPublisher);
            return publisher != null && publisher.IsPublishing;
        }
    }
    public string StationLabel => phoneSaberStation ?? "";
    public bool DiscoveryResponderRunning
    {
        get
        {
            var responder = Volatile.Read(ref discoveryResponder);
            return responder != null && responder.IsRunning;
        }
    }

    // receive thread が書いた情報を一括コピーする。呼び出し側は統計を変更できない。
    public PhoneSaberInputStats ReadInputStats(bool secondStick = false)
    {
        return (secondStick ? blueInputStats : redInputStats).Read(SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
    }
    public int ReceiverRestartCount => Volatile.Read(ref receiverRestartCount1);
    public int ReceiverRestartCount2 => Volatile.Read(ref receiverRestartCount2);
    public string LastReceiverExitReason => lastReceiverExitReason1;
    public string LastReceiverExitReason2 => lastReceiverExitReason2;

    // 保存と座標補正は main thread、採取だけは receive thread から行う。
    PhoneSaberPositionCalibration positionCalibration;
    PhoneSaberAcknowledgementStore learnedAreaStore;
    int learnedAreaRevision = -1;
    bool positionCalibrationEnabled;
    string failedLearnedPositionCalibration;
    public string LearnedPositionCalibrationResult => failedLearnedPositionCalibration ?? PhoneSaberPositionCalibrationStore.LearnedResult(StationLabel);
    public PhoneSaberPositionCapture PositionCapture { get; } = new PhoneSaberPositionCapture();
    public bool HasPositionCalibration => positionCalibration != null;
    public bool PositionCalibrationEnabled => positionCalibrationEnabled && positionCalibration != null;

    public bool SavePositionCalibration(Vector2[] corners, out string error)
    {
        if (!PhoneSaberPositionCalibration.TryCreate(corners, out var calibration, out error)) return false;
        DiscardLearnedPositionCalibrations();
        PhoneSaberPositionCalibrationStore.Save(StationLabel, calibration);
        failedLearnedPositionCalibration = null;
        positionCalibration = calibration;
        positionCalibrationEnabled = true;
        PredictionRevision++;
        return true;
    }

    public void SetPositionCalibrationEnabled(bool enabled)
    {
        positionCalibrationEnabled = enabled && HasPositionCalibration;
        PredictionRevision++;
        PhoneSaberPositionCalibrationStore.SetEnabled(StationLabel, positionCalibrationEnabled);
    }

    public void ResetPositionCalibration()
    {
        PositionCapture.Cancel();
        DiscardLearnedPositionCalibrations();
        PhoneSaberPositionCalibrationStore.Reset(StationLabel);
        failedLearnedPositionCalibration = null;
        positionCalibration = null;
        positionCalibrationEnabled = false;
        PredictionRevision++;
    }

    // スレッド同期用
    object lockObj = new object();
    object lockObj2 = new object();

    // カメラ解像度
    public float camWidth = 1920f;
    public float camHeight = 1080f;
    public RectTransform boardRect;
    Vector2 localPosition;
    public Vector2 LocalPosition
    {
        get { RecordCoordinateRead(RedCoordinateTiming); return localPosition; }
        private set { localPosition = value; }
    }
    Vector2 localPosition2;
    public Vector2 LocalPosition2
    {
        get { RecordCoordinateRead(BlueCoordinateTiming); return localPosition2; }
        private set { localPosition2 = value; }
    }
    // 感度・写像を掛ける前の「送信側そのままの中点」。InputDebugOverlay(F3)が
    // 「ポインタが端まで届かないのは送信側の可動域か受信側の変換か」を切り分けるために表示する。
    public Vector2 LastRaw { get; private set; }
    public Vector2 LastRaw2 { get; private set; }
    [Header("Debug")]
    public bool debugCoordinates = false;
    public bool debugReceiveRate = false;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [Tooltip("100ms級のUDP受信・座標反映停止だけをConsoleに出す診断フラグ。ゲーム入力には影響しない。")]
    public bool freezeDiagnostics = false;
    public bool FreezeDiagnosticsEnabled => freezeDiagnostics;
    long lastFreezeReceiveTimestampTicks1;
    long lastFreezeReceiveTimestampTicks2;
    string pendingFreezeReceiveLog1;
    string pendingFreezeReceiveLog2;
#else
    public bool FreezeDiagnosticsEnabled => false;
#endif

    int receivedCountPort1Window = 0;
    int receivedCountPort2Window = 0;
    int receivedPacketCountPort1 = 0;
    int receivedPacketCountPort2 = 0;
    float lastRateLogTime = 0f;

    // Unity が受理した累計パケット数。デバッグ表示用で、ゲーム動作には使わない。
    public int ReceivedPacketCount => Volatile.Read(ref receivedPacketCountPort1);
    public int ReceivedPacketCount2 => Volatile.Read(ref receivedPacketCountPort2);

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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (freezeDiagnostics) Instance.freezeDiagnostics = true;
#endif
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
    float NormalizeAxis(float v, float span, bool forcePixels = false)
    {
        if (!forcePixels && Mathf.Abs(v) <= 1.5f)
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
    // 位置補正済みの点は必ずピクセルなので、forcePixels で (0,0) の誤判別も避ける。
    // 棒1・棒2の両方がこの関数を通ることで変換の対称性を保証する
    // (棒2だけ direct+ピクセルで素通しになり画面隅に張り付くバグの再発防止)。
    public static Vector2 CanonicalizePoint(float x, float y, float width, float height, bool directMapping, bool forcePixels = false)
    {
        bool isNormalized = !forcePixels && Mathf.Abs(x) <= 1.5f && Mathf.Abs(y) <= 1.5f;
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
    public static Vector2 Normalized01(float x, float y, float width, float height, bool forcePixels = false)
    {
        bool isNormalized = !forcePixels && Mathf.Abs(x) <= 1.5f && Mathf.Abs(y) <= 1.5f;
        return isNormalized
            ? new Vector2((x + 1f) * 0.5f, (y + 1f) * 0.5f)
            : new Vector2(x / width, y / height);
    }

    // 4値packet(棒の両端)の単位は packet ごとに1回だけ決める。4値すべて |v|<=1.5 なら従来どおり正規化、
    // 1つでも超えれば4値ともピクセル(ピクセル送信で画像の隅 (0,0) に来た端点を、正規化の画面中央と取り違えない)。
    // 4値とも 1.5 以下の極小ピクセルは従来どおり正規化扱い。2値packetには使わない(点単位の判別のまま)。
    static bool IsPixelStickPacket(float ax, float ay, float bx, float by)
    {
        return !(Mathf.Abs(ax) <= 1.5f && Mathf.Abs(ay) <= 1.5f && Mathf.Abs(bx) <= 1.5f && Mathf.Abs(by) <= 1.5f);
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

    void OnEnable()
    {
        // Awake で重複退場した場合は受信を開始しない
        if (!Application.isPlaying || Instance != this) return;

        lastRateLogTime = Time.realtimeSinceStartup;
        StartNetworkServices();
    }

    void StartNetworkServices()
    {
        lock (networkLifecycleLock)
        {
            // Stop/Join未完了の旧threadがいる間は重複起動しない。
            if ((receiveThread1 != null && receiveThread1.IsAlive) ||
                (receiveThread2 != null && receiveThread2.IsAlive)) return;
            receiveThread1 = null;
            receiveThread2 = null;
            // 旧thread終了後、新しい受信を始める前に前回の入力を無効化する。
            // 未公開packetを再開後に取り込むと、古い座標の鮮度だけが新着へ戻ってしまう。
            lock (lockObj) { hasNewData = hasStickData = false; rawSender = null; }
            lock (lockObj2) { hasNewData2 = hasStickData2 = false; rawSender2 = null; }
            HasValidStickEndpoints = HasValidStickEndpoints2 = false;
            LastReceivedTime = LastReceivedTime2 = -1000.0;
            LastReceivedMonotonicTime = LastReceivedMonotonicTime2 = double.NegativeInfinity;
            RedCoordinateSender = BlueCoordinateSender = null;
            RedCoordinateTiming.ResetSource();
            BlueCoordinateTiming.ResetSource();
            receiverStopSignal1?.Dispose();
            receiverStopSignal2?.Dispose();
            bonjourPublisher?.Dispose();
            bonjourPublisher = new PhoneSaberBonjourPublisher();
            discoveryResponder?.Dispose();
            discoveryResponder = new PhoneSaberDiscoveryResponder();
            p2pBridge?.Dispose();
            p2pBridge = new PhoneSaberP2PBridgeProcess();
            p2pDataPath = Application.dataPath;
            phoneSaberStation = PhoneSaberStation.Read();
            positionCalibration = PhoneSaberPositionCalibrationStore.Load(phoneSaberStation, out positionCalibrationEnabled);
            PredictionHorizonMilliseconds = PhoneSaberPredictionSettings.Load(phoneSaberStation);
            PredictionAutomatic = PhoneSaberPredictionSettings.LoadAutomatic(phoneSaberStation);
            if (PredictionAutomatic)
            {
                redPrediction = new PhoneSaberAutomaticLeadPolicy(); bluePrediction = new PhoneSaberAutomaticLeadPolicy();
                PredictionHorizonMilliseconds = PhoneSaberPredictionSettings.AutomaticFallbackMilliseconds;
                PredictionLeadSource = "fallback";
            }
            PredictionRevision++;
            PositionCapture.Cancel();
            double eventStart = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
            string eventStation = phoneSaberStation;
            redConnectionEvents = new PhoneSaberConnectionEvents(eventStart, PhoneSaberBonjourPublisher.IsSupported,
                (kind, detail) => PhoneSaberEventLog.Record(eventStation, "RED", kind, detail));
            blueConnectionEvents = new PhoneSaberConnectionEvents(eventStart, PhoneSaberBonjourPublisher.IsSupported,
                (kind, detail) => PhoneSaberEventLog.Record(eventStation, "BLUE", kind, detail));
            PhoneSaberEventLog.Record(eventStation, "SYSTEM", "receivers-starting", "ports=" + port + "," + port2);
            redInputStats.Reset(SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
            blueInputStats.Reset(SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
            // iPhone の Debug Recording を受け取る診断の受信側も、ターミナルを開かずに使えるようにする。
            PhoneSaberTriageReceiverLauncher.EnsureStarted(p2pDataPath);

            networkShutdown = false;
            receiverAlive1 = false;
            receiverAlive2 = false;
            receiverStopSignal1 = new ManualResetEvent(false);
            receiverStopSignal2 = new ManualResetEvent(false);
            receiveThread1 = new Thread(() => ReceiverSupervisor(false));
            receiveThread2 = new Thread(() => ReceiverSupervisor(true));
            receiveThread1.Name = "PhoneSaber RED UDP receiver";
            receiveThread2.Name = "PhoneSaber BLUE UDP receiver";
            receiveThread1.IsBackground = true;
            receiveThread2.IsBackground = true;
            receiveThread1.Start();
            receiveThread2.Start();
        }
    }

    void ReceiverSupervisor(bool secondStick)
    {
        int backoffMilliseconds = ReceiverInitialBackoffMilliseconds;
        ManualResetEvent stopSignal = secondStick ? receiverStopSignal2 : receiverStopSignal1;
        while (!networkShutdown)
        {
            UdpClient client = null;
            string exitReason = null;
            try
            {
                client = new UdpClient(secondStick ? port2 : port);
                SetReceiverClient(secondStick, client);
                SetReceiverAlive(secondStick, true);
                PhoneSaberEventLog.Record(phoneSaberStation, secondStick ? "BLUE" : "RED", "receiver-start",
                    "port=" + (secondStick ? port2 : port));
                backoffMilliseconds = ReceiverInitialBackoffMilliseconds;
                ReceiveData(client, secondStick ? lockObj2 : lockObj, secondStick);
                if (!networkShutdown) exitReason = "receive loop returned unexpectedly";
            }
            catch (Exception e) when (e is SocketException || e is ObjectDisposedException ||
                                      e is ThreadInterruptedException)
            {
                if (!networkShutdown) exitReason = $"{e.GetType().Name}: {e.Message}";
            }
            catch (Exception e)
            {
                if (!networkShutdown) exitReason = $"unexpected {e.GetType().Name}: {e.Message}";
            }
            finally
            {
                (secondStick ? blueInputStats : redInputStats).ResetSenderGate();
                SetReceiverAlive(secondStick, false);
                ClearReceiverClient(secondStick, client);
                client?.Close();
            }

            if (networkShutdown) break;

            if (secondStick) Interlocked.Increment(ref receiverRestartCount2);
            else Interlocked.Increment(ref receiverRestartCount1);
            RecordReceiverExit(secondStick, exitReason ?? "unknown receiver failure", false);
            PhoneSaberEventLog.Record(phoneSaberStation, secondStick ? "BLUE" : "RED", "receiver-restart",
                "count=" + (secondStick ? ReceiverRestartCount2 : ReceiverRestartCount) + " delay-ms=" + backoffMilliseconds);
            if (stopSignal == null || stopSignal.WaitOne(backoffMilliseconds)) break;
            backoffMilliseconds = Math.Min(backoffMilliseconds * 2, ReceiverMaximumBackoffMilliseconds);
        }
    }

    void ReceiveData(UdpClient client, object targetLock, bool secondStick)
    {
        var statistics = secondStick ? blueInputStats : redInputStats;
        string colour = secondStick ? "BLUE" : "RED";
        statistics.ResetSenderGate((kind, detail) => PhoneSaberEventLog.Record(phoneSaberStation, colour, kind, detail));
        // 送信元は標準の IPEndPoint で受ける（独自 EndPoint は OS・runtime 依存の挙動を避けるため使わない）。
        // 受信 buffer と送信元 IP 文字列だけを再利用し、packet ごとの byte[] と文字列を作らない。
        EndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);
        IPAddress lastSenderAddress = null;
        string senderIP = "";
        var data = new byte[PhoneSaberPacketParser.MaximumDatagramBytes];
        var parser = new PhoneSaberPacketParser();
        Socket socket = client.Client;
        while (!networkShutdown)
        {
            int length = socket.ReceiveFrom(data, 0, data.Length, SocketFlags.None, ref endPoint);
            long receiveTimestampTicks = SwingMonotonicClock.Timestamp;
            IPAddress senderAddress = ((IPEndPoint)endPoint).Address;
            if (!senderAddress.Equals(lastSenderAddress))
            {
                lastSenderAddress = senderAddress;
                senderIP = senderAddress.ToString();
            }
            // ソケット受信直後の壁時計。解析・main thread 待ちを遅延に含めない。
            double receiveEpoch = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            bool parsed = parser.TryParse(data.AsSpan(0, length), out bool isStick,
                out float a, out float b, out float c, out float d, out double? sentEpoch);
            // 数値として読めても NaN・無限大・桁外れは座標ではない（中点が Infinity になり剣が消える）。
            // 不正な payload と同じく「到着したが無効」として数える。
            if (parsed && !PhoneSaberPacketParser.IsUsableCoordinates(isStick, a, b, c, d)) parsed = false;
            bool accepted = RecordInputStats(secondStick, receiveTimestampTicks, senderIP, parsed, receiveEpoch, sentEpoch);
            if (!parsed || !accepted) continue;

            // 補正前のカメラ座標を、採取中の指定色だけ記録する。
            if (isStick) PositionCapture.Add(secondStick, new Vector2(a, b), new Vector2(c, d),
                SwingMonotonicClock.ToSeconds(receiveTimestampTicks), camWidth, camHeight);

            // メインスレッドと衝突しないようロック
            lock (targetLock)
            {
                // 停止と受信が重なっても、破棄済みの入力を再び新着にしない。
                if (networkShutdown) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                RecordFreezeReceiveGap(secondStick, receiveTimestampTicks);
#endif
                if (secondStick)
                {
                    Interlocked.Increment(ref receivedCountPort2Window);
                    Interlocked.Increment(ref receivedPacketCountPort2);
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
                    rawReceiveTimestampTicks2 = receiveTimestampTicks;
                    rawSender2 = senderIP;
                    hasNewData2 = true;
                }
                else
                {
                    Interlocked.Increment(ref receivedCountPort1Window);
                    Interlocked.Increment(ref receivedPacketCountPort1);
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
                    rawReceiveTimestampTicks = receiveTimestampTicks;
                    rawSender = senderIP;
                    hasNewData = true;
                }
            }
        }
    }

    bool RecordInputStats(bool secondStick, long timestampTicks, string senderIP, bool parsed,
        double receiveEpoch, double? sentEpoch)
    {
        (secondStick ? blueConnectionEvents : redConnectionEvents)?.Packet(
            SwingMonotonicClock.ToSeconds(timestampTicks), senderIP);
        return (secondStick ? blueInputStats : redInputStats).Record(
            SwingMonotonicClock.ToSeconds(timestampTicks), senderIP, parsed, receiveEpoch, sentEpoch);
    }

    // ACK の適用も保存も main thread。再送ACKでリセット済みの補正を復活させない。
    void DiscardLearnedPositionCalibrations()
    {
        PhoneSaberDiscoveryResponder.Acknowledgements.DiscardLearnedAreas(StationLabel);
        if (!ReferenceEquals(learnedAreaStore, PhoneSaberDiscoveryResponder.Acknowledgements))
            learnedAreaStore?.DiscardLearnedAreas(StationLabel);
        learnedAreaRevision = -1;
    }

    public bool ApplyLearnedPositionCalibration(PhoneSaberAcknowledgementStore store, string learnedAt)
    {
        int revision = store.Revision;
        if (ReferenceEquals(learnedAreaStore, store) && learnedAreaRevision == revision) return false;
        learnedAreaStore = store;
        learnedAreaRevision = revision;
        if (!store.TryLearnedAreaApplication(StationLabel, false, out var application) || application.Attempted) return false;
        application.Ack.TryLearnedBounds(out var bounds);
        return ApplyLearnedPositionCalibration(application,
            Rect.MinMaxRect(bounds.xMin, bounds.yMin, bounds.xMax, bounds.yMax), learnedAt, out _);
    }

    // F8 の完了ACKとチュートリアルの両方が、同じ変換・保存・スマホ範囲更新・診断を使う。
    public bool ApplyLearnedPositionCalibration(Rect bounds, string learnedAt, out string error)
    {
        // 既存のRect契約にはIDが無い。最新指示の一意な端末のACKと一致するときだけ接続する。
        // 矩形一致は指示を探すキーにはせず、古い指示へ遡らない。
        var store = learnedAreaStore ?? PhoneSaberDiscoveryResponder.Acknowledgements;
        error = "最新の学習指示と送信元を一意に確認できません。";
        if (!store.TryLearnedAreaApplication(StationLabel, true, out var application) ||
            !application.Ack.TryLearnedBounds(out var learned) ||
            bounds != Rect.MinMaxRect(learned.xMin, learned.yMin, learned.xMax, learned.yMax)) return false;
        return ApplyLearnedPositionCalibration(application, bounds, learnedAt, out error);
    }

    bool ApplyLearnedPositionCalibration(PhoneSaberAcknowledgementStore.LearnedAreaApplication application,
        Rect bounds, string learnedAt, out string error)
    {
        if (application.Attempted) { error = application.Error; return application.Success; }
        application.Attempted = true;
        application.Error = "学習範囲の適用を完了できませんでした。";
        bool applied = PhoneSaberPositionCalibration.TryFromLearnedArea(bounds, camWidth, camHeight, out var calibration, out error);
        string result = PhoneSaberPositionCalibration.LearnedAreaResult(bounds, applied, error);
        if (applied)
        {
            PositionCapture.Cancel();
            PhoneSaberPositionCalibrationStore.Save(StationLabel, calibration,
                learnedAt ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), result);
            failedLearnedPositionCalibration = null;
            positionCalibration = calibration;
            positionCalibrationEnabled = true;
            PredictionRevision++;
            // 広げた写像の画面端までスマホも探す。タイトル復帰時と同じ余白・0..1 制限を使う。
            if (StationLabel == "A" || StationLabel == "B")
                TutorialRangeBaseline.SendPhoneArea(TutorialRangeBaseline.PhoneAreaFor(calibration.CopyCorners(), camWidth, camHeight));
        }
        else failedLearnedPositionCalibration = result;
        PhoneSaberEventLog.Record(StationLabel, "SYSTEM", "learned-area-apply",
            $"id={application.Ack.Id} from={application.Ack.Sender} " + result);
        application.Success = applied;
        application.Error = error;
        return applied;
    }

    void Update()
    {
        ApplyLearnedPositionCalibration(PhoneSaberDiscoveryResponder.Acknowledgements, null);
        // 自動の統計読出しは秒1回だけ。描画ごとに時刻配列や文字列を作らない。
        // 予測の有効期限は受信と同じ Stopwatch 基準。Unity 起動からの経過秒とは比較しない。
        double predictionNow = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        if (PredictionAutomatic && (predictionNow >= nextPredictionRefresh ||
            redPrediction.NeedsExpiry(predictionNow) || bluePrediction.NeedsExpiry(predictionNow)))
        {
            nextPredictionRefresh = predictionNow + 1;
            RefreshPredictionLead();
        }
        // Join timeout後も旧threadが完全終了するまでは重複起動せず、終了確認後にだけ再開する。
        if (networkShutdown && isActiveAndEnabled && Instance == this) StartNetworkServices();
        FlushReceiverDiagnostics();
        if (!networkShutdown)
        {
            double eventNow = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
            redConnectionEvents?.Poll(eventNow);
            blueConnectionEvents?.Poll(eventNow);
            redInputStats.PollSenderGate(eventNow);
            blueInputStats.PollSenderGate(eventNow);
        }
        ApplyLatestInput(true);
    }

    // main thread 専用。描画用の消費者が LateUpdate / onBeforeRender で読む直前に呼ぶ。
    // 自動では呼ばない（ゲームの判定・フィルタ・予測の更新順は変更しない）。新着なしでは何もしない。
    public void ResampleLatestInput()
    {
        if (isActiveAndEnabled && Instance == this) ApplyLatestInput(false);
    }

    void ApplyLatestInput(bool allowImuFallback)
    {
        float x = 0, y = 0;
        bool updated = false;
        bool updatedStick = false;
        float x1a = 0, y1a = 0, x1b = 0, y1b = 0;
        long receiveTimestampTicks = 0;
        string sender = null;
        float x2 = 0, y2 = 0;
        bool updated2 = false;
        bool updatedStick2 = false;
        float x2a = 0, y2a = 0, x2b = 0, y2b = 0;
        long receiveTimestampTicks2 = 0;
        string sender2 = null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        string freezeReceiveLog1 = null;
        string freezeReceiveLog2 = null;
#endif

        // 新着なしでは受信 thread の lock を待たない。volatile は通知だけに使い、
        // 座標・フラグのコピーと通知のクリアは従来どおり同じ lock 内で行う。
        bool readRed = hasNewData;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        readRed |= Volatile.Read(ref pendingFreezeReceiveLog1) != null;
#endif
        // スレッドから受け取った値をコピー
        // 有効フラグもこの座標スナップショットと一緒に main thread で公開する。
        // receive thread が次の packet のフラグだけを先に公開してはいけない。
        if (readRed) lock (lockObj)
        {
            if (hasNewData)
            {
                x = rawX;
                y = rawY;
                hasNewData = false;
                receiveTimestampTicks = rawReceiveTimestampTicks;
                sender = rawSender;
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            freezeReceiveLog1 = pendingFreezeReceiveLog1;
            pendingFreezeReceiveLog1 = null;
#endif
        }

        bool readBlue = hasNewData2;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        readBlue |= Volatile.Read(ref pendingFreezeReceiveLog2) != null;
#endif
        if (readBlue) lock (lockObj2)
        {
            if (hasNewData2)
            {
                x2 = rawX2;
                y2 = rawY2;
                hasNewData2 = false;
                receiveTimestampTicks2 = rawReceiveTimestampTicks2;
                sender2 = rawSender2;
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            freezeReceiveLog2 = pendingFreezeReceiveLog2;
            pendingFreezeReceiveLog2 = null;
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (freezeDiagnostics)
        {
            if (freezeReceiveLog1 != null) Debug.Log(freezeReceiveLog1);
            if (freezeReceiveLog2 != null) Debug.Log(freezeReceiveLog2);
        }
#endif

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

        // 4値packetの単位は補正前の生の4値で1回だけ決め、生の両端(無視範囲用)・中点・刃の両端・LocalStick で共有する。
        bool pixelStick1 = updatedStick && IsPixelStickPacket(x1a, y1a, x1b, y1b);
        bool pixelStick2 = updatedStick2 && IsPixelStickPacket(x2a, y2a, x2b, y2b);

        // 「この赤を無視する」（PhoneSaberIgnoreZones）。両端とも同じ範囲に入る受信は使わない（前の位置を保つ）。
        // 範囲を覚えるために、捨てる前の生の両端（カメラ画像の 0..1）を残す。
        if (updated && updatedStick)
        {
            LastRawEndsA = Normalized01(x1a, y1a, camWidth, camHeight, pixelStick1);
            LastRawEndsB = Normalized01(x1b, y1b, camWidth, camHeight, pixelStick1);
            if (PhoneSaberIgnoreZones.Enabled && PhoneSaberIgnoreZones.Covers(IgnoreZones(), LastRawEndsA, LastRawEndsB)) { updated = false; IgnoredSamples++; }
        }
        if (updated2 && updatedStick2)
        {
            LastRawEndsA2 = Normalized01(x2a, y2a, camWidth, camHeight, pixelStick2);
            LastRawEndsB2 = Normalized01(x2b, y2b, camWidth, camHeight, pixelStick2);
            if (PhoneSaberIgnoreZones.Enabled && PhoneSaberIgnoreZones.Covers(IgnoreZones(), LastRawEndsA2, LastRawEndsB2)) { updated2 = false; IgnoredSamples2++; }
        }

        // 両色とも更新がなく IMU も使わない場合、座標処理へ進む必要がない。
        // 受信機監視・接続イベント・診断ログ・レート表示は上で従来どおり行う。
        if (!updated && !updated2 && (!allowImuFallback || !useImuFallback)) return;
        bool calibrated = PositionCalibrationEnabled;
        if (updated)
        {
            // 無視した受信の送信元を、保持中の座標や遅延統計へ結び付けない。
            if (RedCoordinateSender != sender) RedCoordinateTiming.ResetSource();
            RedCoordinateSender = sender;
            LastRaw = new Vector2(x, y);
            LastCameraPosition01 = Normalized01(x, y, camWidth, camHeight, pixelStick1);
            if (calibrated) CalibrateCoordinates(ref x, ref y, ref x1a, ref y1a, ref x1b, ref y1b, updatedStick);
            // 位置補正済みなら従来どおりピクセル。補正なしでも、ピクセルの4値packetは中点・両端・LocalStick をそろえてピクセルにする。
            bool forcePixels1 = calibrated || pixelStick1;
            // 中点: 正規化/ピクセルの判別と単位揃えは棒1・棒2共通の純関数で行う
            NormalizedPosition = ApplySensitivity01(Normalized01(x, y, camWidth, camHeight, forcePixels1), sensitivity);
            Vector2 mid1 = CanonicalizePoint(x, y, camWidth, camHeight, useDirectWorldMapping, forcePixels1);
            // 感度は中点に掛け、端点は同じ量だけ平行移動する(棒の長さ・角度を保つ)
            Vector2 sensMid1 = ApplySensitivity(mid1, sensitivity, useDirectWorldMapping, camWidth, camHeight);
            Vector2 delta1 = sensMid1 - mid1;
            LocalPosition = ToLocalPosition(sensMid1.x, sensMid1.y);

            HasValidStickEndpoints = updatedStick;
            if (updatedStick)
            {
                Vector2 end1a = CanonicalizePoint(x1a, y1a, camWidth, camHeight, useDirectWorldMapping, forcePixels1) + delta1;
                Vector2 end1b = CanonicalizePoint(x1b, y1b, camWidth, camHeight, useDirectWorldMapping, forcePixels1) + delta1;
                LocalStickRawA = ToLocalPosition(end1a.x, end1a.y);
                LocalStickRawB = ToLocalPosition(end1b.x, end1b.y);

                // -1..1 正規化は既存の NormalizeAxis で扱う（混在対応）
                float nxA = NormalizeAxis(x1a, camWidth, forcePixels1);
                float nyA = NormalizeAxis(y1a, camHeight, forcePixels1);
                float nxB = NormalizeAxis(x1b, camWidth, forcePixels1);
                float nyB = NormalizeAxis(y1b, camHeight, forcePixels1);

                LocalStickA = new Vector2(nxA, nyA);
                LocalStickB = new Vector2(nxB, nyB);

                // 棒長と角度は正規化座標で計算
                LocalStickLength = Vector2.Distance(LocalStickA, LocalStickB);
                LocalStickLengthNormalized = LocalStickLength / Mathf.Sqrt(8f);
                LocalAngleDeg = Mathf.Atan2(nyB - nyA, nxB - nxA) * Mathf.Rad2Deg;
            }
            LastReceivedMonotonicTime = SwingMonotonicClock.ToSeconds(receiveTimestampTicks);
            double appliedAt = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
            // 取込み待ちの時間を鮮度へ戻し、古い受信を新着扱いしない。
            LastReceivedTime = Time.realtimeSinceStartupAsDouble - (appliedAt - LastReceivedMonotonicTime);
            LastCameraReceivedTime = LastReceivedTime;
            RedCoordinateTiming.Applied(LastReceivedMonotonicTime, appliedAt);
            if (debugCoordinates)
            {
                bool isNorm = !forcePixels1 && Mathf.Abs(x) <= 1.5f && Mathf.Abs(y) <= 1.5f;
                Debug.Log($"[InputPoint] raw=({x:F2},{y:F2}) norm=({NormalizedPosition.x:F3},{NormalizedPosition.y:F3}) local=({localPosition.x:F1},{localPosition.y:F1}) isNorm={isNorm}");
                if (updatedStick)
                {
                    Debug.Log($"[InputPoint] stickRawA=({localStickRawA.x:F1},{localStickRawA.y:F1}) stickRawB=({localStickRawB.x:F1},{localStickRawB.y:F1}) stickNormA=({LocalStickA.x:F3},{LocalStickA.y:F3}) stickNormB=({LocalStickB.x:F3},{LocalStickB.y:F3})");
                }
            }
        }

        if (updated2)
        {
            if (BlueCoordinateSender != sender2) BlueCoordinateTiming.ResetSource();
            BlueCoordinateSender = sender2;
            LastRaw2 = new Vector2(x2, y2);
            LastCameraPosition012 = Normalized01(x2, y2, camWidth, camHeight, pixelStick2);
            if (calibrated) CalibrateCoordinates(ref x2, ref y2, ref x2a, ref y2a, ref x2b, ref y2b, updatedStick2);
            // 棒1と同じく、位置補正済みか、ピクセルの4値packetならすべてピクセル。
            bool forcePixels2 = calibrated || pixelStick2;
            // 棒2も棒1と同一の純関数で変換する。
            // (旧実装は direct モードでピクセル→正規化の変換が抜けており、
            //  ピクセル送信のトラッカーだと棒2だけ画面隅に張り付くバグがあった)
            NormalizedPosition2 = ApplySensitivity01(Normalized01(x2, y2, camWidth, camHeight, forcePixels2), sensitivity);
            Vector2 mid2 = CanonicalizePoint(x2, y2, camWidth, camHeight, useDirectWorldMapping, forcePixels2);
            // 棒1と同じく: 感度は中点、端点は平行移動
            Vector2 sensMid2 = ApplySensitivity(mid2, sensitivity, useDirectWorldMapping, camWidth, camHeight);
            Vector2 delta2 = sensMid2 - mid2;
            LocalPosition2 = ToLocalPosition(sensMid2.x, sensMid2.y);

            HasValidStickEndpoints2 = updatedStick2;
            if (updatedStick2)
            {
                Vector2 end2a = CanonicalizePoint(x2a, y2a, camWidth, camHeight, useDirectWorldMapping, forcePixels2) + delta2;
                Vector2 end2b = CanonicalizePoint(x2b, y2b, camWidth, camHeight, useDirectWorldMapping, forcePixels2) + delta2;
                LocalStickRawA2 = ToLocalPosition(end2a.x, end2a.y);
                LocalStickRawB2 = ToLocalPosition(end2b.x, end2b.y);

                float nxA2 = NormalizeAxis(x2a, camWidth, forcePixels2);
                float nyA2 = NormalizeAxis(y2a, camHeight, forcePixels2);
                float nxB2 = NormalizeAxis(x2b, camWidth, forcePixels2);
                float nyB2 = NormalizeAxis(y2b, camHeight, forcePixels2);

                LocalStickA2 = new Vector2(nxA2, nyA2);
                LocalStickB2 = new Vector2(nxB2, nyB2);

                LocalStickLength2 = Vector2.Distance(LocalStickA2, LocalStickB2);
                LocalStickLengthNormalized2 = LocalStickLength2 / Mathf.Sqrt(8f);
                LocalAngleDeg2 = Mathf.Atan2(nyB2 - nyA2, nxB2 - nxA2) * Mathf.Rad2Deg;
            }
            LastReceivedMonotonicTime2 = SwingMonotonicClock.ToSeconds(receiveTimestampTicks2);
            double appliedAt = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
            // 棒2も受信から取込みまでの待ち時間を鮮度へ含める。
            LastReceivedTime2 = Time.realtimeSinceStartupAsDouble - (appliedAt - LastReceivedMonotonicTime2);
            LastCameraReceivedTime2 = LastReceivedTime2;
            BlueCoordinateTiming.Applied(LastReceivedMonotonicTime2, appliedAt);
        }

        if (updated)
        {
            return;
        }

        if (!allowImuFallback || !useImuFallback)
        {
            return;
        }

        if (!UdpImuBridge.TryGetLatest(out Vector3 accel, out Vector3 gyro, out bool connected) || !connected)
        {
            return;
        }

        Vector2 imuLocal = new Vector2(gyro.y, -gyro.x) * (imuPositionScale / 100f);
        LocalPosition = Vector2.Lerp(localPosition, imuLocal, 0.2f);

        float nx = Mathf.Clamp01((accel.x + 1f) * 0.5f);
        float ny = Mathf.Clamp01((accel.y + 1f) * 0.5f);
        NormalizedPosition = new Vector2(nx, ny);

        if (boardRect != null)
        {
            Vector2 half = boardRect.rect.size * 0.5f;
            LocalPosition = new Vector2(
                Mathf.Clamp(localPosition.x, -half.x, half.x),
                Mathf.Clamp(localPosition.y, -half.y, half.y)
            );
        }
        //Debug.Log(LocalStickA + " " + LocalStickB);
    }

    // 両色とも同じ main thread の補正を通し、端点補正後に中点を計算する。
    void CalibrateCoordinates(ref float x, ref float y, ref float ax, ref float ay, ref float bx, ref float by, bool stick)
    {
        Vector2 mid;
        if (stick)
        {
            Vector2 a = positionCalibration.Map(new Vector2(ax, ay));
            Vector2 b = positionCalibration.Map(new Vector2(bx, by));
            ax = a.x; ay = a.y; bx = b.x; by = b.y;
            mid = (a + b) * 0.5f;
        }
        else mid = positionCalibration.Map(new Vector2(x, y));
        x = mid.x; y = mid.y;
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    void RecordFreezeReceiveGap(bool secondStick, long receiveTimestampTicks)
    {
        if (!freezeDiagnostics) return;
        long previous = secondStick
            ? lastFreezeReceiveTimestampTicks2
            : lastFreezeReceiveTimestampTicks1;
        if (secondStick) lastFreezeReceiveTimestampTicks2 = receiveTimestampTicks;
        else lastFreezeReceiveTimestampTicks1 = receiveTimestampTicks;
        if (previous == 0) return;
        double receiveTime = SwingMonotonicClock.ToSeconds(receiveTimestampTicks);
        double gapMs = (receiveTimestampTicks - previous) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (gapMs <= 100.0) return;
        string color = secondStick ? "BLUE" : "RED";
        string message = string.Format(CultureInfo.InvariantCulture,
            "[FREEZE][Unity RX][{0}] gap={1:F1}ms receive={2:F6} sinceLastReceive={1:F1}ms",
            color, gapMs, receiveTime);
        if (secondStick) pendingFreezeReceiveLog2 = message;
        else pendingFreezeReceiveLog1 = message;
    }
#endif

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

    void StopNetworkServices()
    {
        Thread thread1;
        Thread thread2;
        lock (networkLifecycleLock)
        {
            if (networkShutdown && receiveThread1 == null && receiveThread2 == null)
            {
                bonjourPublisher?.Dispose();
                bonjourPublisher = null;
                discoveryResponder?.Dispose();
                discoveryResponder = null;
                p2pBridge?.Dispose();
                p2pBridge = null;
                receiverAlive1 = false;
                receiverAlive2 = false;
                return;
            }
            networkShutdown = true;
            receiverAlive1 = false;
            receiverAlive2 = false;
            bonjourPublisher?.Dispose();
            bonjourPublisher = null;
            discoveryResponder?.Dispose();
            discoveryResponder = null;
            p2pBridge?.Dispose();
            p2pBridge = null;
            RecordReceiverExit(false, "intentional shutdown", true);
            RecordReceiverExit(true, "intentional shutdown", true);
            receiverStopSignal1?.Set();
            receiverStopSignal2?.Set();
            udpClient1?.Close();
            udpClient2?.Close();
            thread1 = receiveThread1;
            thread2 = receiveThread2;
        }

        bool stopped1 = JoinReceiver(thread1, "RED");
        bool stopped2 = JoinReceiver(thread2, "BLUE");

        lock (networkLifecycleLock)
        {
            if (stopped1 && stopped2)
            {
                receiveThread1 = null;
                receiveThread2 = null;
                udpClient1 = null;
                udpClient2 = null;
                receiverStopSignal1?.Dispose();
                receiverStopSignal2?.Dispose();
                receiverStopSignal1 = null;
                receiverStopSignal2 = null;
            }
            receiverAlive1 = false;
            receiverAlive2 = false;
        }
    }

    void SetReceiverClient(bool secondStick, UdpClient client)
    {
        lock (networkLifecycleLock)
        {
            if (secondStick) udpClient2 = client;
            else udpClient1 = client;
        }
    }

    void ClearReceiverClient(bool secondStick, UdpClient client)
    {
        lock (networkLifecycleLock)
        {
            if (secondStick)
            {
                if (ReferenceEquals(udpClient2, client)) udpClient2 = null;
            }
            else if (ReferenceEquals(udpClient1, client)) udpClient1 = null;
        }
    }

    void SetReceiverAlive(bool secondStick, bool alive)
    {
        lock (networkLifecycleLock)
        {
            if (secondStick) receiverAlive2 = alive;
            else receiverAlive1 = alive;

            if (networkShutdown || !receiverAlive1 || !receiverAlive2)
            {
                bonjourPublisher?.Stop();
                discoveryResponder?.Stop();
                p2pBridge?.Stop();
            }
            else
            {
                bonjourPublisher?.Start(port, phoneSaberStation);
                discoveryResponder?.Start(port, port2, phoneSaberStation);
                p2pBridge?.Start(port, port2, p2pDataPath, phoneSaberStation);
            }
        }
    }

    void RecordReceiverExit(bool secondStick, string reason, bool intentional)
    {
        string color = secondStick ? "BLUE" : "RED";
        PhoneSaberEventLog.Record(phoneSaberStation, color, intentional ? "receiver-stop" : "receiver-failed", reason);
        string diagnostic = $"[PhoneSaber][{color}] receiver {(intentional ? "stopped" : "failed")}: {reason}";
        if (secondStick)
        {
            lastReceiverExitReason2 = reason;
            if (!intentional) Interlocked.Exchange(ref pendingReceiverDiagnostic2, diagnostic);
        }
        else
        {
            lastReceiverExitReason1 = reason;
            if (!intentional) Interlocked.Exchange(ref pendingReceiverDiagnostic1, diagnostic);
        }
        if (intentional) Debug.Log(diagnostic);
    }

    void FlushReceiverDiagnostics()
    {
        string red = Interlocked.Exchange(ref pendingReceiverDiagnostic1, null);
        string blue = Interlocked.Exchange(ref pendingReceiverDiagnostic2, null);
        if (red != null) Debug.LogWarning(red);
        if (blue != null) Debug.LogWarning(blue);
    }

    static bool JoinReceiver(Thread thread, string color)
    {
        if (thread == null || thread == Thread.CurrentThread) return true;
        if (thread.Join(ReceiverJoinTimeoutMilliseconds)) return true;
        Debug.LogError($"[PhoneSaber][{color}] receiver did not stop within {ReceiverJoinTimeoutMilliseconds}ms");
        return false;
    }

    internal void StopAndFlushEvents()
    {
        StopNetworkServices();
        // 未反映の旧パケットと公開済みの鮮度を破棄し、再開後は新しい受信を待つ。
        // Join timeout 後も ReceiveData の停止確認と同じ lock で遅い書き込みを防ぐ。
        lock (lockObj)
        {
            hasNewData = false;
            hasStickData = false;
            rawSender = null;
        }
        lock (lockObj2)
        {
            hasNewData2 = false;
            hasStickData2 = false;
            rawSender2 = null;
        }
        HasValidStickEndpoints = HasValidStickEndpoints2 = false;
        LastReceivedTime = LastReceivedTime2 = -1000.0;
        LastReceivedMonotonicTime = LastReceivedMonotonicTime2 = double.NegativeInfinity;
        RedCoordinateSender = BlueCoordinateSender = null;
        RedCoordinateTiming.ResetSource();
        BlueCoordinateTiming.ResetSource();
        // Join 後に保存を通知する。disable では共有 writer を停止しない。
        PhoneSaberEventLog.Current?.Flush();
    }

    void OnDisable() => StopAndFlushEvents();
    void OnApplicationQuit() => StopAndFlushEvents();
    void OnDestroy() => StopAndFlushEvents();
}
