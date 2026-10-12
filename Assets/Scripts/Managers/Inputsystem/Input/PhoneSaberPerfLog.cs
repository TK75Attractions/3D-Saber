using System.Globalization;
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
        if (!window.Started)
        {
            window.Start(now);
            redAtStart = input != null ? input.ReceivedPacketCount : 0;
            blueAtStart = input != null ? input.ReceivedPacketCount2 : 0;
            return;
        }
        window.Frame(now);
        if (window.Elapsed(now) < windowSeconds) return;
        int red = input != null ? input.ReceivedPacketCount : 0, blue = input != null ? input.ReceivedPacketCount2 : 0;
        double seconds = window.Elapsed(now);
        string detail = string.Format(CultureInfo.InvariantCulture,
            "scene={0} fps={1:0.0} frame-ms-max={2:0.0} frames-over-25ms={3} frames-over-50ms={4} " +
            "red-pkt-per-s={5:0.0} blue-pkt-per-s={6:0.0} screen={7}x{8} refresh-hz={9:0} vsync={10} target-fps={11} quality={12} platform={13}",
            SceneManager.GetActiveScene().name, window.Frames / seconds, window.MaxFrameMs, window.Over25, window.Over50,
            (red - redAtStart) / seconds, (blue - blueAtStart) / seconds, Screen.width, Screen.height,
            Screen.currentResolution.refreshRateRatio.value, QualitySettings.vSyncCount, Application.targetFrameRate,
            QualitySettings.GetQualityLevel(), Application.platform);
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "SYSTEM", "perf", detail);
        window.Start(now);
        redAtStart = red; blueAtStart = blue;
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
