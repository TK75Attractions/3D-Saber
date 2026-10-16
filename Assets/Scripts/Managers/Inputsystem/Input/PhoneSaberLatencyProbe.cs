using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// 画面→カメラ→認識→UDP→受信 の実遅延を測る運営用テスト（F9 で開始、自動終了）。
// 画面に赤い棒を左右交互に出し、スマホに画面を撮らせる。棒を切り替えた Update から、
// 赤の受信位置が切り替わった UDP の受信時刻までを1サンプルとする。
// 画面の往路とスマホの復路を含むループであり、プレイの片道遅延とは異なる。
// iPhone/Android・Mac/Windows とも同じ方法で比較できる。シーンや prefab は変更しない。
[DefaultExecutionOrder(-1900)]
public sealed class PhoneSaberLatencyProbe : MonoBehaviour
{
    readonly PhoneSaberLatencyLoop loop = new PhoneSaberLatencyLoop();
    bool active;
    PhoneSaberGuiRenderer guiRenderer;
    int loggedSamples;
    GUIStyle textStyle;
    // 20回ごとにイベントログへ自動記録する（手入力なしで後から比較できるように）。
    const int LogEvery = 20;
    // ブロックごとに latency-block を記録し、PhoneSaber/tools/latency_report.py で集計する。
    const int BlockSize = 15;
    // 画面設定は変えず、その台の現在の条件を測る。
    // 複数ブロックで測定し、必要量に達したら自動終了する。
    public const int TargetBlocks = 8;
    string progressText = "";
    int progressBlocks = -1, progressSamples = -1;
    int blockIndex, blockStartPackets;
    double blockStartedAt, startedAt, resultUntil, switchAt, repaintMs;
    int drawnSide = -1;
    // blockConfiguration は StartBlock 前（Play 停止・破棄での終了処理）でも null にしない。
    string resultText = "", blockConfiguration = "", blockSender, blockPhoneIdentity;
    bool blockMismatch;
    readonly List<double> blockPresent = new List<double>();
    readonly List<double> blockC2s = new List<double>(), blockNetwork = new List<double>(), blockFps = new List<double>();
    PhoneSaberMetrics blockPhone;
    int blockPhoneSamples, blockRttSamples;
    double lastMetricsAt, nextConfigurationCheck;
    readonly List<double> blockSamples = new List<double>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        var probe = FindFirstObjectByType<PhoneSaberLatencyProbe>();
        if (probe == null)
            probe = new GameObject("PhoneSaber Latency Probe").AddComponent<PhoneSaberLatencyProbe>();
        DontDestroyOnLoad(probe.gameObject);
        if (probe.active) probe.Finish();
        probe.EnsureGuiRenderer();
        probe.guiRenderer.enabled = false;
        // 無人計測: -phonesaberLatencyProbe [ブロック数] で起動すると、キー操作なしで F9 と同じ計測を始め、
        // 終わったらアプリを終了する（スマホが画面を向いていなければ 40 秒であきらめる）。Editor では無効。
        if (!Application.isEditor)
        {
            int blocks = ResolveAutoBlocks(System.Environment.GetCommandLineArgs());
            if (blocks > 0)
            {
                probe.autoBlocks = blocks;
                probe.autoStartAt = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp) + AutoStartDelaySeconds;
            }
        }
    }

    // 測れなかったときの切り分け用: この測定で受信した赤の中点の範囲（[-1, 1]）。
    float seenMinX, seenMaxX, seenMinY, seenMaxY;
    public const string AutoArgument = "-phonesaberLatencyProbe";
    public const double AutoStartDelaySeconds = 8, AutoGiveUpSeconds = 40;
    int autoBlocks;
    bool autoRunning;
    double autoStartAt = double.PositiveInfinity, autoQuitAt = double.PositiveInfinity;

    // 引数が無ければ 0（無効）。ブロック数が無い・範囲外なら TargetBlocks。
    public static int ResolveAutoBlocks(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] != AutoArgument) continue;
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out int blocks) && blocks >= 1 && blocks <= TargetBlocks)
                return blocks;
            return TargetBlocks;
        }
        return 0;
    }

    void Awake() => EnsureGuiRenderer();

    void EnsureGuiRenderer()
    {
        if (guiRenderer != null) return;
        guiRenderer = gameObject.AddComponent<PhoneSaberLatencyGuiRenderer>();
        // この表示は固定 Rect のみで、GUILayout の準備も不要。
        guiRenderer.Initialize(DrawGUI, false);
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        bool toggle = keyboard != null && keyboard.f9Key.wasPressedThisFrame;
        if (autoBlocks > 0)
        {
            double autoNow = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
            if (!active && autoNow >= autoStartAt) { toggle = true; autoRunning = true; autoStartAt = double.PositiveInfinity; }
            if (autoNow >= autoQuitAt) { autoQuitAt = double.PositiveInfinity; Application.Quit(); }
        }
        // 非表示フレームでは Stopwatch の読み出し・秒への変換もしない。
        if (!active && !toggle)
        {
            if (resultUntil > 0 && SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp) >= resultUntil)
            { resultUntil = 0; if (guiRenderer != null) guiRenderer.enabled = false; }
            return;
        }
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        if (toggle)
        {
            if (active) Finish();
            else
            {
                active = true;
                PhoneSaberDisplaySync.Measuring = true;
                EnsureGuiRenderer();
                guiRenderer.enabled = true;
                resultText = ""; resultUntil = 0; startedAt = now;
                seenMinX = seenMinY = float.PositiveInfinity; seenMaxX = seenMaxY = float.NegativeInfinity;
                loggedSamples = 0; loop.Reset(now); blockIndex = 0; StartBlock(now);
            }
        }
        if (!active) return;
        var input = InputPoint.Instance;
        bool hasPacket = input != null && input.HasValidStickEndpoints;
        float x = hasPacket ? ReadNormalizedX(input) : 0f;
        float y = hasPacket ? (input.LocalStickA.y + input.LocalStickB.y) * 0.5f : 0f;
        double packetTime = input != null ? input.LastReceivedMonotonicTime : double.NegativeInfinity;
        CollectPhoneMetrics(input, now);
        int before = loop.TotalSamples, previousSide = loop.Side;
        loop.Tick(now, packetTime, x, y, hasPacket);
        if (hasPacket)
        {
            seenMinX = Mathf.Min(seenMinX, x); seenMaxX = Mathf.Max(seenMaxX, x);
            seenMinY = Mathf.Min(seenMinY, y); seenMaxY = Mathf.Max(seenMaxY, y);
        }
        int total = loop.TotalSamples;
        if (loop.Side != previousSide) { switchAt = now; drawnSide = -1; repaintMs = double.NaN; }
        if (total > before)
        {
            blockSamples.Add(loop.SamplesMs[loop.SamplesMs.Count - 1]);
            if (!double.IsNaN(repaintMs)) blockPresent.Add(repaintMs);
        }
        if (total >= loggedSamples + LogEvery) { loggedSamples = total; Record("latency-loop"); }
        if (blockSamples.Count >= BlockSize)
        {
            RecordBlock(now); blockIndex++;
            if (blockIndex >= (autoRunning ? autoBlocks : TargetBlocks)) Finish(); else StartBlock(now);
        }
        if (active && now - startedAt >= 180) Finish();
        if (active && autoRunning && loop.TotalSamples == 0 && now - startedAt >= AutoGiveUpSeconds) Finish();
    }

    // 比較に必要な記録量の目安を画面に出す（振る必要はなく、カメラを画面に向けたまま待つ）。
    public static string ProgressText(int recordedBlocks, int samplesInBlock)
    {
        if (recordedBlocks >= TargetBlocks) return "測定完了（自動終了）";
        return $"測定 {recordedBlocks}/{TargetBlocks} ブロック（今のブロック {samplesInBlock}/{BlockSize} 回）、約2分で自動終了";
    }

    void StartBlock(double now)
    {
        blockSamples.Clear(); blockPresent.Clear(); blockC2s.Clear(); blockNetwork.Clear(); blockFps.Clear();
        blockPhone = null; lastMetricsAt = double.NegativeInfinity; blockPhoneSamples = blockRttSamples = 0;
        blockPhoneIdentity = null; blockMismatch = false;
        blockConfiguration = PhoneSaberPerfLog.DisplayConfiguration(); nextConfigurationCheck = now;
        blockStartedAt = now;
        var input = InputPoint.Instance;
        blockSender = input?.RedCoordinateSender;
        blockStartPackets = input != null ? input.ReceivedPacketCount : 0;
    }

    void CollectPhoneMetrics(InputPoint input, double now)
    {
        if (input == null) return;
        string sender = input.RedCoordinateSender;
        if (blockSender == null) blockSender = sender;
        if (sender != blockSender) blockMismatch = true;
        if (now >= nextConfigurationCheck)
        {
            nextConfigurationCheck = now + 1;
            if (PhoneSaberPerfLog.DisplayConfiguration() != blockConfiguration) blockMismatch = true;
        }
        var phone = input.ReadPhoneMetrics(sender, now);
        if (phone == null || sender == "127.0.0.1" || sender == "::1" || phone.ReceivedAt == lastMetricsAt) return;
        string identity = phone.Platform + ":" + phone.App + ":" + phone.FpsMode;
        if (blockPhoneIdentity != null && identity != blockPhoneIdentity) blockMismatch = true;
        blockPhoneIdentity = identity; lastMetricsAt = phone.ReceivedAt;
        if (!phone.C2sMedian.HasValue || !phone.Rtt.HasValue || !phone.CaptureFps.HasValue ||
            now - phone.ReceivedAt + phone.RttAge > 10) return;
        blockPhone = phone;
        blockC2s.Add(phone.C2sMedian.Value); blockNetwork.Add(phone.Rtt.Value); blockFps.Add(phone.CaptureFps.Value);
        blockPhoneSamples += phone.C2sCount; blockRttSamples += phone.RttCount;
    }

    PhoneSaberDisplayMeasurement Decompose(double now)
    {
        PhoneSaberMetrics phone = null;
        if (!blockMismatch && blockPhone != null && now - blockPhone.ReceivedAt <= 15)
            phone = new PhoneSaberMetrics {
                C2sMedian = new PhoneSaberTimingStats(blockC2s.ToArray()).MedianMs, C2sCount = blockPhoneSamples,
                Rtt = new PhoneSaberTimingStats(blockNetwork.ToArray()).MedianMs, RttCount = blockRttSamples,
                RttAge = blockPhone.RttAge, ReceivedAt = blockPhone.ReceivedAt,
                CaptureFps = new PhoneSaberTimingStats(blockFps.ToArray()).MedianMs, Route = blockPhone.Route
            };
        return new PhoneSaberDisplayMeasurement(new PhoneSaberTimingStats(blockSamples.ToArray()), phone,
            new PhoneSaberTimingStats(blockPresent.ToArray()), now, blockMismatch);
    }

    void RecordBlock(double now)
    {
        var input = InputPoint.Instance;
        double seconds = Math.Max(1e-3, now - blockStartedAt);
        // 受信数はブロック全体の平均（赤）。スマホの fps（30/60）の判別に使う。
        double redRate = input != null ? (input.ReceivedPacketCount - blockStartPackets) / seconds : 0;
        string route = input != null
            ? PhoneSaberStatsDisplay.ClassifyRoute(input.ReadInputStats().SenderIP, PhoneSaberBonjourPublisher.IsSupported).Replace(' ', '-')
            : "none";
        var stats = new PhoneSaberTimingStats(blockSamples.ToArray());
        string detail = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "block={0} max-queued-frames={1} median-ms={2:0} p95-ms={3:0} n={4} red-pkt-per-s={5:0.0} route={6} " +
            "platform={7} editor={8} rejected={9} misses={10}",
            blockIndex, QualitySettings.maxQueuedFrames, stats.MedianMs, stats.P95Ms, stats.Count, redRate, route,
            Application.platform, Application.isEditor, loop.Rejected, loop.Misses);
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "RED", "latency-block", detail);
        if (PhoneSaberPerfLog.DisplayConfiguration() != blockConfiguration) blockMismatch = true;
        var measurement = Decompose(now);
        resultText = measurement.Result;
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "RED", "display-measured", measurement.Log() +
            " block=" + blockIndex + " configuration=" + blockConfiguration + " conditions-changed=" + (blockMismatch ? "1" : "0") +
            " phone-windows=" + blockC2s.Count + " rtt-count-source=sum-snapshot-counts capture-fps=" +
            (blockFps.Count > 0 ? new PhoneSaberTimingStats(blockFps.ToArray()).MedianMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "unknown") +
            " " + blockConfiguration.Replace(',', ' ') + " platform=" + Application.platform + " sender=" + (blockSender ?? "unknown"));
        if (measurement.Valid)
            PhoneSaberDisplayMeasurementSettings.Save(PhoneSaberStation.Read(), blockConfiguration,
                measurement.DisplayMs, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        PhoneSaberEventLog.Current?.Flush();
    }

    // 閾値は [-1, 1] の単位。ワールド座標を使うと台の scale・感度で判定が変わる。
    internal static float ReadNormalizedX(InputPoint input)
        => (input.LocalStickA.x + input.LocalStickB.x) * 0.5f;

    // Play 停止・シーン破棄でも途中結果を残す。
    void OnDisable()
    {
        if (active) Finish();
        resultUntil = 0;
        if (guiRenderer != null) guiRenderer.enabled = false;
    }

    void OnDestroy() { if (active) Finish(); }

    void Finish()
    {
        if (blockIndex == 0 || blockSamples.Count < BlockSize && blockSamples.Count > 0)
            RecordBlock(SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
        active = false;
        PhoneSaberDisplaySync.Measuring = false;
        resultUntil = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp) + 20;
        if (guiRenderer != null) guiRenderer.enabled = true;
        Record("latency-loop-end");
        if (autoRunning) { autoRunning = false; autoQuitAt = resultUntil - 17; } // 結果を3秒だけ見せて終了
    }

    void Record(string kind)
    {
        var input = InputPoint.Instance;
        string context = "";
        if (input != null)
        {
            var red = input.ReadInputStats();
            // 赤の pkt/s でスマホのカメラ fps（30/60）が分かる。経路は Mac の P2P bridge か LAN か。
            context = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                " red-pkt-per-s={0} route={1} sender={2}", red.PacketsPerSecond,
                PhoneSaberStatsDisplay.ClassifyRoute(red.SenderIP, PhoneSaberBonjourPublisher.IsSupported).Replace(' ', '-'),
                red.SenderIP);
        }
        string detail = loop.LogDetail() + context +
            $" platform={Application.platform} editor={Application.isEditor} " +
            $"render-fps={(1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f)):0} max-queued-frames={QualitySettings.maxQueuedFrames}";
        if (seenMaxX >= seenMinX)
            detail += string.Format(System.Globalization.CultureInfo.InvariantCulture,
                " seen-x={0:0.00}..{1:0.00} seen-y={2:0.00}..{3:0.00}", seenMinX, seenMaxX, seenMinY, seenMaxY);
        Debug.Log("[PhoneSaber] " + kind + " " + detail);
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "RED", kind, detail);
        PhoneSaberEventLog.Current?.Flush();
    }

    void DrawGUI()
    {
        if (!active && resultUntil <= 0) return;
        if (textStyle == null)
        {
            textStyle = new GUIStyle(GUI.skin.label)
            {
                font = Resources.Load<Font>("Fonts/NotoSansJP-Light"),
                fontSize = 22, wordWrap = true, alignment = TextAnchor.UpperCenter,
            };
            // 白文字は赤と誤認識されないが、念のため暗めの灰色にする。
            textStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
        }
        int previousDepth = GUI.depth;
        Color previousColour = GUI.color;
        GUI.depth = -20000;
        float w = Screen.width, h = Screen.height;
        if (!active)
        {
            GUI.Label(new Rect(20, 20, w - 40, 100), resultText, textStyle);
            GUI.depth = previousDepth; GUI.color = previousColour; return;
        }
        if (Event.current.type == EventType.Repaint && drawnSide != loop.Side && loop.Waiting)
        {
            repaintMs = (SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp) - switchAt) * 1000;
            drawnSide = loop.Side;
        }
        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
        GUI.color = Color.red;
        float barWidth = w * 0.08f;
        float centre = loop.Side == 0 ? w * 0.25f : w * 0.75f;
        GUI.DrawTexture(new Rect(centre - barWidth * 0.5f, h * 0.12f, barWidth, h * 0.76f), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(20, 10, w - 40, h * 0.1f),
            "スマホのカメラをこの画面に向け、赤い棒だけが映るようにして自動終了まで待ってください。", textStyle);
        GUI.Label(new Rect(20, h * 0.89f, w - 40, h * 0.11f), string.IsNullOrEmpty(resultText) ? loop.Summary() : resultText, textStyle);
        if (progressBlocks != blockIndex || progressSamples != blockSamples.Count)
        {
            progressBlocks = blockIndex; progressSamples = blockSamples.Count;
            progressText = ProgressText(blockIndex, blockSamples.Count);
        }
        GUI.Label(new Rect(20, h * 0.80f, w - 40, h * 0.09f), progressText, textStyle);
        GUI.depth = previousDepth;
        GUI.color = previousColour;
    }
}

// 計測前の値を一度だけ保存・復元する。無効化と破棄が続いても二重に復元しない。
public sealed class PhoneSaberQueuedFramesRestore
{
    bool pending;
    int previous;

    public void Begin(int value)
    {
        if (pending) return;
        previous = value;
        pending = true;
    }

    public bool TryFinish(out int value)
    {
        value = previous;
        if (!pending) return false;
        pending = false;
        return true;
    }
}

// Unity の時計・描画を使わない判定部分（EditMode テスト対象）。
public sealed class PhoneSaberLatencyLoop
{
    // 受信 x は [-1, 1]。棒は画面の 25% と 75% なので、本来 1.0 前後動く。
    public const float MoveThreshold = 0.3f;
    // カメラ露光＋認識＋通信がこれより速いことはない。下回るのは揺れ・誤認識による偽の応答。
    public const double MinPlausibleMs = 25.0;
    public const double TimeoutSeconds = 1.5;
    public const int MaxSamples = 60;
    readonly System.Random random;
    readonly List<double> samples = new List<double>();
    double switchAt, nextSwitchAt, lastPacketTime;
    float? baseline;
    // 左右それぞれの棒の受信位置（落ち着いた値の移動平均）。両方わかったら、切替先の側に近い値だけを応答とする。
    readonly float?[] sideX = new float?[2];
    // スマホを横向きに置くと、画面の左右の動きはカメラの上下に出る。x と y の両方で距離を見る。
    readonly float[] sideY = new float[2];
    float baselineY;

    public PhoneSaberLatencyLoop(int seed = 7) { random = new System.Random(seed); Reset(0); }

    public int Side { get; private set; }
    public bool Waiting { get; private set; }
    public int Misses { get; private set; }
    public IReadOnlyList<double> SamplesMs => samples;
    // 保持数の上限（MaxSamples）に関係なく、この測定で得た回数。
    public int TotalSamples { get; private set; }
    // 早すぎる・逆方向の動きとして捨てた回数。
    public int Rejected { get; private set; }

    public void Reset(double now)
    {
        samples.Clear();
        TotalSamples = 0;
        Rejected = 0;
        sideX[0] = sideX[1] = null;
        Misses = 0;
        Waiting = false;
        baseline = null;
        lastPacketTime = double.NegativeInfinity;
        nextSwitchAt = now + 1.0;
    }

    // packetTime は最新の赤の受信時刻（受信 thread の単調時計）、x はその中点。
    public void Tick(double now, double packetTime, float x, bool hasPacket) => Tick(now, packetTime, x, 0f, hasPacket);

    static double Distance(float x, float y, float x0, float y0)
        => Math.Sqrt((double)(x - x0) * (x - x0) + (double)(y - y0) * (y - y0));

    // x, y は赤の棒の中点（[-1, 1]）。向きに依らず、棒の位置が動いたことを 2 次元の距離で判定する。
    public void Tick(double now, double packetTime, float x, float y, bool hasPacket)
    {
        bool fresh = hasPacket && packetTime > lastPacketTime;
        if (fresh) lastPacketTime = packetTime;
        if (Waiting)
        {
            // 処理が遅れた場合も受信時刻で期限を判定し、期限後の応答を測定値にしない。
            if (fresh && packetTime > switchAt && packetTime - switchAt <= TimeoutSeconds && baseline.HasValue &&
                Distance(x, y, baseline.Value, baselineY) > MoveThreshold)
            {
                double latencyMs = (packetTime - switchAt) * 1000.0;
                bool towardNewSide = !sideX[0].HasValue || !sideX[1].HasValue ||
                    Distance(x, y, sideX[Side].Value, sideY[Side]) < Distance(x, y, sideX[1 - Side].Value, sideY[1 - Side]);
                if (latencyMs < MinPlausibleMs || !towardNewSide) Rejected++;
                else
                {
                    samples.Add(latencyMs);
                    TotalSamples++;
                    if (samples.Count > MaxSamples) samples.RemoveAt(0);
                    Waiting = false;
                    baseline = x; baselineY = y;
                    ScheduleNext(now);
                }
            }
            // 逆方向の受信が続く場合も、採用できないまま期限を過ぎたら失敗として進める。
            if (Waiting && now - switchAt > TimeoutSeconds)
            {
                Misses++;
                Waiting = false;
                baseline = null;
                ScheduleNext(now);
            }
            return;
        }
        if (fresh)
        {
            baseline = x; baselineY = y;
            sideY[Side] = sideX[Side].HasValue ? sideY[Side] * 0.8f + y * 0.2f : y;
            sideX[Side] = sideX[Side].HasValue ? sideX[Side].Value * 0.8f + x * 0.2f : x;
        }
        // 直前まで受信が続いている（棒を認識している）ときだけ切り替える。
        if (now >= nextSwitchAt && baseline.HasValue && now - lastPacketTime < 0.2)
        {
            Side = 1 - Side;
            switchAt = now;
            Waiting = true;
        }
    }

    void ScheduleNext(double now) => nextSwitchAt = now + 0.6 + random.NextDouble() * 0.4;

    // イベントログ用。ASCII の key=value で、直近 MaxSamples 回の統計。
    public string LogDetail()
    {
        if (samples.Count == 0) return $"n=0 misses={Misses}";
        var stats = new PhoneSaberTimingStats(samples.ToArray());
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "median-ms={0:0} p95-ms={1:0} min-ms={2:0} max-ms={3:0} n={4} total={5} misses={6} rejected={7}",
            stats.MedianMs, stats.P95Ms, stats.MinMs, stats.MaxMs, stats.Count, TotalSamples, Misses, Rejected);
    }

    public string Summary()
    {
        if (samples.Count == 0)
            return $"測定 0 回 / 失敗 {Misses} 回（赤の受信がない、または棒の移動を認識できない）";
        var stats = new PhoneSaberTimingStats(samples.ToArray());
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "画面→受信 中央値 {0:0} ms / p95 {1:0} ms / 最小 {2:0} / 最大 {3:0} ms（{4} 回, 失敗 {5} 回）",
            stats.MedianMs, stats.P95Ms, stats.MinMs, stats.MaxMs, stats.Count, Misses);
    }
}
