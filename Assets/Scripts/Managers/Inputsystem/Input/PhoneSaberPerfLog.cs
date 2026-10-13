using System.Globalization;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// 10 秒ごとに、描画のコマ数・コマ落ち・座標の届き方を events.log に1行残す(event=perf)。
// 2026-10-13: ビルド済みアプリが「もっさり」と報告されたが、Player の実際の fps を残す記録が無かった。
// 測るのは PC 側だけ(スマホの撮影→送信、表示機器の遅れは含まない)。毎フレームは数値の加算だけで、割り当てない。
public sealed class PhoneSaberPerfLog : MonoBehaviour
{
    public const double WindowSeconds = 10.0;
    // 起動確認(-phonesaberSmoke)では scene ごとに1行以上残るよう短くする。
    static double windowSeconds = WindowSeconds;
    PhoneSaberPerfWindow window;
    int redAtStart, blueAtStart;
    long predictionsAtStart, suppressedAtStart;
    PhoneSaberPerfContext context;
    static string lowPowerAtLaunch;
    readonly FrameTiming[] frameTiming = new FrameTiming[1];
    double gpuMs;
    double gpuSum;
    int gpuCount;
    static PhoneSaberLatencyEstimate redEstimate, blueEstimate;
    double nextLatencyLog;
    public static string LatencyLine(bool blue, double now)
    {
        var estimate = blue ? blueEstimate : redEstimate;
        var input = InputPoint.Instance;
        string sender = input == null ? null : blue ? input.BlueCoordinateSender : input.RedCoordinateSender;
        if (sender == "127.0.0.1" || sender == "::1")
            return "遅延の目安 " + (blue ? "青" : "赤") + ": スマホからの計測が届いていません（旧アプリ / P2P 経由）";
        if (input != null && estimate != null &&
            (estimate.Sender != (blue ? input.BlueCoordinateSender : input.RedCoordinateSender) ||
             now - (blue ? input.LastReceivedMonotonicTime2 : input.LastReceivedMonotonicTime) > 1))
            return "遅延の目安 " + (blue ? "青" : "赤") + ": 計測不足（座標の未受信 / 経路切替）";
        return estimate?.Line(blue ? "青" : "赤", now)
            ?? "遅延の目安 " + (blue ? "青" : "赤") + ": スマホからの計測が届いていません（旧アプリ / P2P 経由）";
    }
    public static string Verdict { get; private set; } = "PC描画: 計測中（10秒）";

    void OnEnable()
    {
        redEstimate = blueEstimate = null;
        nextLatencyLog = Time.realtimeSinceStartupAsDouble + WindowSeconds;
        lowPowerAtLaunch = Environment.GetEnvironmentVariable("PHONESABER_LOW_POWER_MODE") ?? "unknown";
        Application.onBeforeRender += BeforeRender;
    }

    void OnDisable() => Application.onBeforeRender -= BeforeRender;

    // 座標の取り込みや消費者を動かさず、公開済み / 読み出し済みの値だけ測る。
    [BeforeRenderOrder(10000)]
    void BeforeRender()
    {
        var input = InputPoint.Instance;
        if (input == null) return;
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        input.RedCoordinateTiming.BeforeRender(now, Time.frameCount);
        input.BlueCoordinateTiming.BeforeRender(now, Time.frameCount);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        if (PhoneSaberPlayerSmoke.ResolveSeconds(System.Environment.GetCommandLineArgs()) > 0f) windowSeconds = 4.0;
        var go = new GameObject("PhoneSaber Perf Log");
        go.AddComponent<PhoneSaberPerfLog>();
        DontDestroyOnLoad(go);
    }

    void Update()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        var input = InputPoint.Instance;
        var scene = SceneManager.GetActiveScene();
        // 切替前の低fpsや別のvsync条件を、新しいシーン名で報告しない。
        var currentContext = new PhoneSaberPerfContext(scene.handle, QualitySettings.vSyncCount,
            Application.targetFrameRate, Screen.currentResolution.refreshRateRatio.value, Screen.width, Screen.height,
            QualitySettings.GetQualityLevel(), (int)Screen.fullScreenMode);
        if (!window.Started || !currentContext.Equals(context))
        {
            context = currentContext;
            window.Start(now);
            input?.RedCoordinateTiming.ClearWindow();
            input?.BlueCoordinateTiming.ClearWindow();
            gpuMs = 0;
            gpuSum = 0; gpuCount = 0;
            Verdict = "PC描画: 計測中（シーン / 描画条件の切替）";
            redAtStart = input != null ? input.ReceivedPacketCount : 0;
            blueAtStart = input != null ? input.ReceivedPacketCount2 : 0;
            predictionsAtStart = input != null ? input.PredictionDraws : 0;
            suppressedAtStart = input != null ? input.PredictionSuppressedDraws : 0;
            UpdateLatency(input, now);
            return;
        }
        UpdateLatency(input, now);
        window.Frame(now);
        if (FrameTimingManager.IsFeatureEnabled()) FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.IsFeatureEnabled() && FrameTimingManager.GetLatestTimings(1, frameTiming) > 0 && frameTiming[0].gpuFrameTime > 0)
        {
            gpuMs = Math.Max(gpuMs, frameTiming[0].gpuFrameTime);
            gpuSum += frameTiming[0].gpuFrameTime; gpuCount++;
        }
        if (window.Elapsed(now) < windowSeconds) return;
        int red = input != null ? input.ReceivedPacketCount : 0, blue = input != null ? input.ReceivedPacketCount2 : 0;
        double seconds = window.Elapsed(now);
        double fps = window.Frames / seconds;
        string limiter = PhoneSaberPresentationDiagnosis.Limiter(fps, Screen.currentResolution.refreshRateRatio.value,
            QualitySettings.vSyncCount, Application.targetFrameRate, lowPowerAtLaunch, gpuCount > 0 ? gpuSum / gpuCount : 0);
        Verdict = string.Format(CultureInfo.InvariantCulture, "PC描画: {0:0} fps / {1}（表示実Hzは未測定）", fps,
            PhoneSaberPresentationDiagnosis.Label(limiter));
        string detail = string.Format(CultureInfo.InvariantCulture,
            "scene={0} fps={1:0.0} frame-ms-max={2:0.0} frames-over-25ms={3} frames-over-50ms={4} " +
            "red-pkt-per-s={5:0.0} blue-pkt-per-s={6:0.0} screen={7}x{8} refresh-hz={9:0} vsync={10} target-fps={11} quality={12} platform={13}",
            scene.name, fps, window.MaxFrameMs, window.Over25, window.Over50,
            (red - redAtStart) / seconds, (blue - blueAtStart) / seconds, Screen.width, Screen.height,
            Screen.currentResolution.refreshRateRatio.value, QualitySettings.vSyncCount, Application.targetFrameRate,
            QualitySettings.GetQualityLevel(), Application.platform);
        detail += string.Format(CultureInfo.InvariantCulture,
            " fullscreen={0} graphics-api={1} queued-frames={2} gpu-ms-max={3} limiter={4} low-power-at-launch={5} glass-hz=unknown gpu-samples={6} gpu-ms-mean={7}",
            Screen.fullScreenMode, SystemInfo.graphicsDeviceType, QualitySettings.maxQueuedFrames,
            gpuCount > 0 ? gpuMs.ToString("0.0", CultureInfo.InvariantCulture) : "unknown", limiter, lowPowerAtLaunch,
            gpuCount, gpuCount > 0 ? (gpuSum / gpuCount).ToString("0.0", CultureInfo.InvariantCulture) : "unknown");
        long predictions = input != null ? input.PredictionDraws : 0;
        long suppressed = input != null ? input.PredictionSuppressedDraws : 0;
        detail += string.Format(CultureInfo.InvariantCulture,
            " prediction-mode={0} prediction-lead-ms={1} prediction-source={2} prediction-draws={3} prediction-suppressed-draws={4}",
            input != null && input.PredictionAutomatic ? "auto" : input != null && input.PredictionHorizonMilliseconds > 0 ? "manual" : "off",
            input != null ? input.PredictionHorizonMilliseconds : 0, input != null ? input.PredictionLeadSource : "none",
            Math.Max(0, predictions - predictionsAtStart), Math.Max(0, suppressed - suppressedAtStart));
        if (input != null)
        {
            detail += input.RedCoordinateTiming.Format("red") + input.BlueCoordinateTiming.Format("blue");
            var redAge = input.RedCoordinateTiming.Shown;
            var blueAge = input.BlueCoordinateTiming.Shown;
            Verdict += " / 受信→描画直前 R " + AgeLabel(redAge) + " B " + AgeLabel(blueAge);
            input.RedCoordinateTiming.ClearWindow(); input.BlueCoordinateTiming.ClearWindow();
        }
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "SYSTEM", "perf", detail);
        window.Start(now);
        gpuMs = 0;
        gpuSum = 0; gpuCount = 0;
        redAtStart = red; blueAtStart = blue;
        predictionsAtStart = predictions; suppressedAtStart = suppressed;
    }

    // perf窓がシーン切替で再開しても、診断行は10秒ごとに残す。
    void UpdateLatency(InputPoint input, double now)
    {
        if (input == null || now < nextLatencyLog) return;
        nextLatencyLog = now + WindowSeconds;
        double clockNow = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        redEstimate = Estimate(input, false, clockNow, input.RedCoordinateTiming.Shown);
        blueEstimate = Estimate(input, true, clockNow, input.BlueCoordinateTiming.Shown);
        PhoneSaberEventLog.Record(input.StationLabel, "RED", "latency-estimate", redEstimate.Log());
        PhoneSaberEventLog.Record(input.StationLabel, "BLUE", "latency-estimate", blueEstimate.Log());
    }

    static PhoneSaberLatencyEstimate Estimate(InputPoint input, bool blue, double now, PhoneSaberTimingStats timing)
    {
        string sender = blue ? input.BlueCoordinateSender : input.RedCoordinateSender;
        return new PhoneSaberLatencyEstimate(input.ReadPhoneMetrics(sender, now), timing, sender,
            now - (blue ? input.LastReceivedMonotonicTime2 : input.LastReceivedMonotonicTime), now,
            Screen.currentResolution.refreshRateRatio.value, QualitySettings.vSyncCount);
    }

    static string AgeLabel(PhoneSaberTimingStats stats) => stats.Count == 0 ? "未測定" :
        string.Format(CultureInfo.InvariantCulture, "{0:0.0}/{1:0.0}ms(中央値/p95)", stats.MedianMs, stats.P95Ms);
}

public readonly struct PhoneSaberPerfContext
{
    readonly int scene, vsync, target, width, height, quality, fullscreen;
    readonly double hz;
    public PhoneSaberPerfContext(int scene, int vsync, int target, double hz, int width, int height, int quality, int fullscreen)
    {
        this.scene = scene; this.vsync = vsync; this.target = target; this.hz = hz;
        this.width = width; this.height = height; this.quality = quality; this.fullscreen = fullscreen;
    }
    public bool Equals(PhoneSaberPerfContext other) => scene == other.scene && vsync == other.vsync &&
        target == other.target && hz == other.hz && width == other.width && height == other.height &&
        quality == other.quality && fullscreen == other.fullscreen;
}

// Unity の描画fpsはglassの更新数ではない。設定と観測が一致する場合だけ制限を名付ける。
public static class PhoneSaberPresentationDiagnosis
{
    public static string Limiter(double fps, double hz, int vsync, int target, string lowPower, double gpuMs)
    {
        double cap = vsync > 0 && hz > 0 ? hz / vsync : vsync == 0 && target > 0 ? target : 0;
        if (cap > 0 && Math.Abs(fps - cap) <= cap * 0.1) return vsync > 0 ? "vsync" : "target";
        if (lowPower == "1" && cap > 0 && fps < cap * 0.9) return "os-low-power-suspected";
        if (fps > 0 && gpuMs >= 1000.0 / fps * 0.9) return "gpu-suspected";
        if (cap == 0) return "uncapped-or-cpu-os-unknown";
        return "cpu-or-os-unknown";
    }

    public static string Label(string limiter)
    {
        switch (limiter)
        {
            case "vsync": return "vsync上限";
            case "target": return "target fps上限";
            case "os-low-power-suspected": return "低電力による制限の疑い（起動時ON）";
            case "gpu-suspected": return "GPU負荷の疑い";
            default: return "CPU / OS制限は未確定";
        }
    }
}

// main thread 専用の純粋な集計。UDPの破棄された中間packetは「使った」と数えない。
// readは公開座標の初回読み出しであり、実際にglassへ出た証明ではない。
public sealed class PhoneSaberCoordinateTiming
{
    const int Capacity = 16384;
    readonly List<double> age = new List<double>(Capacity), firstRead = new List<double>(Capacity);
    readonly List<double> firstRender = new List<double>(Capacity), apply = new List<double>(Capacity);
    readonly List<double> shown = new List<double>(Capacity);
    double received = double.NegativeInfinity, readReceived = double.NegativeInfinity;
    double renderedReceived = double.NegativeInfinity, shownReceived = double.NegativeInfinity;
    int lastFrame = -1, lastShownFrame = -1, dropped;
    public bool NeedsFirstRead => received != readReceived && !double.IsNegativeInfinity(received);

    public void Applied(double receiveAt, double appliedAt)
    {
        if (received == receiveAt) return;
        received = receiveAt;
        Add(apply, (appliedAt - receiveAt) * 1000.0);
    }

    public void Read(double now)
    {
        if (!NeedsFirstRead) return;
        readReceived = received;
        Add(firstRead, (now - received) * 1000.0);
    }

    public void BeforeRender(double now, int frame)
    {
        // 新しい座標が公開(Applied)されてから、初めて描画直前まで来るまで。「遅延の目安」の PC の項に使う。
        // 読み出し側の実装に依らず、座標が来ていない間は標本を増やさない（2026-10-13 の実機:
        // 曲選択では age が「最後に読まれた座標」からの経過になり、数秒〜数十秒と出ていた）。
        if (frame != lastShownFrame && received != shownReceived && !double.IsNegativeInfinity(received))
        {
            lastShownFrame = frame;
            shownReceived = received;
            Add(shown, (now - received) * 1000.0);
        }
        if (frame == lastFrame || double.IsNegativeInfinity(readReceived)) return;
        lastFrame = frame;
        double ms = (now - readReceived) * 1000.0;
        Add(age, ms);
        if (renderedReceived == readReceived) return;
        renderedReceived = readReceived;
        Add(firstRender, ms);
    }

    void Add(List<double> values, double ms)
    {
        if (values.Count < Capacity) values.Add(ms);
        else dropped++;
    }

    public PhoneSaberTimingStats Age => new PhoneSaberTimingStats(age);
    public PhoneSaberTimingStats FirstRead => new PhoneSaberTimingStats(firstRead);
    public PhoneSaberTimingStats FirstRender => new PhoneSaberTimingStats(firstRender);
    public PhoneSaberTimingStats Apply => new PhoneSaberTimingStats(apply);
    public PhoneSaberTimingStats Shown => new PhoneSaberTimingStats(shown);

    public string Format(string colour) => Metric(colour + "-coord-age-render", Age) +
        Metric(colour + "-receive-first-use", FirstRead) + Metric(colour + "-receive-first-render", FirstRender) +
        Metric(colour + "-receive-apply", Apply) + Metric(colour + "-receive-shown", Shown) +
        " " + colour + "-timing-dropped=" + dropped;

    static string Metric(string name, PhoneSaberTimingStats stats) => string.Format(CultureInfo.InvariantCulture,
        " {0}-n={1} {0}-median-ms={2} {0}-p95-ms={3}", name, stats.Count,
        stats.Count == 0 ? "unknown" : stats.MedianMs.ToString("0.0", CultureInfo.InvariantCulture),
        stats.Count == 0 ? "unknown" : stats.P95Ms.ToString("0.0", CultureInfo.InvariantCulture));

    public void ClearWindow()
    {
        age.Clear(); firstRead.Clear(); firstRender.Clear(); apply.Clear(); shown.Clear(); dropped = 0;
    }

    // 別の送信元へ切り替わったら、前の端末の「読み出し済み座標」を診断に混ぜない。
    public void ResetSource()
    {
        ClearWindow();
        received = readReceived = renderedReceived = shownReceived = double.NegativeInfinity;
        lastFrame = lastShownFrame = -1;
    }
}

// フレーム間隔の集計(純粋な計算。テストできる)。
public struct PhoneSaberPerfWindow
{
    double startedAt, lastFrameAt;
    public bool Started { get; private set; }
    public int Frames { get; private set; }
    public int Over25 { get; private set; }
    public int Over50 { get; private set; }
    public double MaxFrameMs { get; private set; }

    public void Start(double now)
    {
        Started = true; startedAt = now; lastFrameAt = now;
        Frames = 0; Over25 = 0; Over50 = 0; MaxFrameMs = 0;
    }

    public void Frame(double now)
    {
        double ms = (now - lastFrameAt) * 1000.0;
        lastFrameAt = now;
        Frames++;
        if (ms > MaxFrameMs) MaxFrameMs = ms;
        if (ms > 25.0) Over25++;
        if (ms > 50.0) Over50++;
    }

    public double Elapsed(double now) => now - startedAt;
}
