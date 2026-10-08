using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// 画面→カメラ→認識→UDP→受信 の実遅延を測る運営用テスト（F9 で開始/終了）。
// 画面に赤い棒を左右交互に出し、スマホに画面を撮らせる。棒を切り替えた Update から、
// 赤の受信位置が切り替わった UDP の受信時刻までを1サンプルとする。
// 表示遅延（描画・モニタ）も含むので、プレイヤーが感じる遅延にほぼ等しい。
// iPhone/Android・Mac/Windows とも同じ方法で比較できる。シーンや prefab は変更しない。
[DefaultExecutionOrder(-1900)]
public sealed class PhoneSaberLatencyProbe : MonoBehaviour
{
    readonly PhoneSaberLatencyLoop loop = new PhoneSaberLatencyLoop();
    bool active;
    int loggedSamples;
    GUIStyle textStyle;
    // 20回ごとにイベントログへ自動記録する（手入力なしで後から比較できるように）。
    const int LogEvery = 20;
    // PC 側の設定は同じ条件のまま交互に切り替えて比べる（時間・照明・置き方の差を打ち消す）。
    // ブロックごとに latency-block を記録し、PhoneSaber/tools/latency_report.py で集計する。
    const int BlockSize = 15;
    static readonly int[] QueuedFrameVariants = { 1, 2 };
    int blockIndex, blockStartTotal, blockStartPackets;
    double blockStartedAt;
    readonly List<double> blockSamples = new List<double>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        var probe = FindFirstObjectByType<PhoneSaberLatencyProbe>();
        if (probe == null)
            probe = new GameObject("PhoneSaber Latency Probe").AddComponent<PhoneSaberLatencyProbe>();
        DontDestroyOnLoad(probe.gameObject);
        probe.active = false;
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        if (keyboard != null && keyboard.f9Key.wasPressedThisFrame)
        {
            if (active) Finish();
            else { active = true; loggedSamples = 0; loop.Reset(now); blockIndex = 0; StartBlock(now); }
        }
        if (!active) return;
        var input = InputPoint.Instance;
        bool hasPacket = input != null && input.HasValidStickEndpoints;
        float x = hasPacket ? ReadNormalizedX(input) : 0f;
        double packetTime = input != null ? input.LastReceivedMonotonicTime : double.NegativeInfinity;
        int before = loop.TotalSamples;
        loop.Tick(now, packetTime, x, hasPacket);
        int total = loop.TotalSamples;
        if (total > before) blockSamples.Add(loop.SamplesMs[loop.SamplesMs.Count - 1]);
        if (total >= loggedSamples + LogEvery) { loggedSamples = total; Record("latency-loop"); }
        if (blockSamples.Count >= BlockSize) { RecordBlock(now); blockIndex++; StartBlock(now); }
    }

    void StartBlock(double now)
    {
        QualitySettings.maxQueuedFrames = QueuedFrameVariants[blockIndex % QueuedFrameVariants.Length];
        blockSamples.Clear();
        blockStartTotal = loop.TotalSamples;
        blockStartedAt = now;
        blockStartPackets = InputPoint.Instance != null ? InputPoint.Instance.ReceivedPacketCount : 0;
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
        PhoneSaberEventLog.Current?.Flush();
    }

    // 閾値は [-1, 1] の単位。ワールド座標を使うと台の scale・感度で判定が変わる。
    internal static float ReadNormalizedX(InputPoint input)
        => (input.LocalStickA.x + input.LocalStickB.x) * 0.5f;

    // Play 停止・シーン破棄でも途中結果を残す。
    void OnDisable() { if (active) Finish(); }

    void Finish()
    {
        active = false;
        Record("latency-loop-end");
        QualitySettings.maxQueuedFrames = 1; // 通常運転の値へ戻す（PhoneSaberOperatorOverlay と同じ）。
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
        Debug.Log("[PhoneSaber] " + kind + " " + detail);
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "RED", kind, detail);
        PhoneSaberEventLog.Current?.Flush();
    }

    void OnGUI()
    {
        if (!active) return;
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
        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
        GUI.color = Color.red;
        float barWidth = w * 0.08f;
        float centre = loop.Side == 0 ? w * 0.25f : w * 0.75f;
        GUI.DrawTexture(new Rect(centre - barWidth * 0.5f, h * 0.12f, barWidth, h * 0.76f), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(20, 10, w - 40, h * 0.1f),
            "遅延テスト [F9: 終了]  スマホのカメラをこの画面に向け、赤い棒だけが映るようにする", textStyle);
        GUI.Label(new Rect(20, h * 0.89f, w - 40, h * 0.11f), loop.Summary(), textStyle);
        GUI.depth = previousDepth;
        GUI.color = previousColour;
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
    public void Tick(double now, double packetTime, float x, bool hasPacket)
    {
        bool fresh = hasPacket && packetTime > lastPacketTime;
        if (fresh) lastPacketTime = packetTime;
        if (Waiting)
        {
            if (fresh && packetTime > switchAt && baseline.HasValue && Math.Abs(x - baseline.Value) > MoveThreshold)
            {
                double latencyMs = (packetTime - switchAt) * 1000.0;
                bool towardNewSide = !sideX[0].HasValue || !sideX[1].HasValue ||
                    Math.Abs(x - sideX[Side].Value) < Math.Abs(x - sideX[1 - Side].Value);
                if (latencyMs < MinPlausibleMs || !towardNewSide) Rejected++;
                else
                {
                    samples.Add(latencyMs);
                    TotalSamples++;
                    if (samples.Count > MaxSamples) samples.RemoveAt(0);
                    Waiting = false;
                    baseline = x;
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
            baseline = x;
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
