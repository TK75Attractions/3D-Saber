using System.Collections.Generic;
using UnityEngine;

// 判定の遅れ補正を、決め打ちの 120 ms から「その場で測った遅れ」に合わせる（2026-10-17 ユーザー承認）。
// 実測（10-17 プロジェクター、iPhone 30 fps）: 推定の合計 約85 ms に対し、当たりの中央が 120 ms 補正で −31 ms（早め側）。
// ゲームは GamePlayManager.cameraJudgeLatencySeconds を毎フレーム判定の時計へ渡すので、その公開値だけを書き換える。
// ゲーム本体のファイル・判定幅・ノーツの見た目と音は変えない。F8 で「ゲームの設定（120 ms）」へ戻せる。
public static class PhoneSaberJudgeLatencySettings
{
    public const int GameDefault = 0;
    public const int Automatic = 1;
    public const int MinimumMs = 30, MaximumMs = 250;
    public const int MinimumOffsetMs = -100, MaximumOffsetMs = 150;

    public static string Key(string station) => "PhoneSaber.JudgeLatency." + PhoneSaberStation.Normalize(station) + ".v1";
    public static string OffsetKey(string station) => Key(station) + ".Offset";

    public static int Clamp(int mode) => mode == GameDefault ? GameDefault : Automatic;
    // 未設定の台は自動（測定値）。明示的に「ゲームの設定」を選んだ台はそのまま。
    public static int Load(string station) => Clamp(PlayerPrefs.GetInt(Key(station), Automatic));
    public static int LoadOffset(string station) => ClampOffset(PlayerPrefs.GetInt(OffsetKey(station), 0));
    public static int ClampOffset(int ms) => Mathf.Clamp(ms, MinimumOffsetMs, MaximumOffsetMs);

    public static void Save(string station, int mode, int offsetMs)
    {
        PlayerPrefs.SetInt(Key(station), Clamp(mode));
        PlayerPrefs.SetInt(OffsetKey(station), ClampOffset(offsetMs));
        PlayerPrefs.Save();
    }

    // 当たりのずれの中央（＋は遅め）だけ補正を増やせば中央に来る。10 ms 単位に丸める。
    public const int MinimumHitsToCentre = 15;
    public static int CentredOffset(int currentOffsetMs, double hitMedianMs) =>
        ClampOffset(currentOffsetMs + (int)System.Math.Round(hitMedianMs / 10.0) * 10);

    // 赤・青の推定の平均（片方だけならその値）。どちらも無ければ null。
    public static double? Combine(double? red, double? blue)
    {
        bool r = Usable(red), b = Usable(blue);
        if (r && b) return (red.Value + blue.Value) / 2;
        if (r) return red;
        if (b) return blue;
        return null;
    }

    static bool Usable(double? ms) => ms.HasValue && !double.IsNaN(ms.Value) && !double.IsInfinity(ms.Value) && ms.Value > 0;

    // 10 秒ごとの推定の揺れで判定が跳ねないよう、ゆっくり追う（新しい値の 30%）。
    public static double Smooth(double? previous, double sample) =>
        previous.HasValue ? previous.Value + 0.3 * (sample - previous.Value) : sample;

    // 書き込む秒数。自動で測定値が無いときはゲームの値のまま（推測で動かさない）。
    // F8 の予測が ON だと、判定（SaberCutJudge）は先読みした刃の位置で切る。先読み分は補正から差し引く。
    public static double SecondsToApply(int mode, double? automaticMs, int offsetMs, double gameSeconds, double predictionLeadMs = 0)
    {
        if (Clamp(mode) == GameDefault || !automaticMs.HasValue) return gameSeconds;
        double lead = double.IsNaN(predictionLeadMs) || predictionLeadMs < 0 ? 0 : predictionLeadMs;
        double ms = automaticMs.Value + ClampOffset(offsetMs) - lead;
        return Mathf.Clamp((float)ms, MinimumMs, MaximumMs) / 1000.0;
    }
}

public sealed class PhoneSaberJudgeLatency : MonoBehaviour
{
    public static int Mode { get; private set; } = PhoneSaberJudgeLatencySettings.Automatic;
    public static int OffsetMs { get; private set; }
    public static double? AutomaticMs { get; private set; }
    public static double? GameMs { get; private set; }
    public static double? AppliedMs { get; private set; }
    public static double PredictionLeadMs { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        string station = PhoneSaberStation.Read();
        Mode = PhoneSaberJudgeLatencySettings.Load(station);
        OffsetMs = PhoneSaberJudgeLatencySettings.LoadOffset(station);
        var go = new GameObject("PhoneSaber Judge Latency");
        go.AddComponent<PhoneSaberJudgeLatency>();
        DontDestroyOnLoad(go);
    }

    public static void Set(int mode, int offsetMs)
    {
        mode = PhoneSaberJudgeLatencySettings.Clamp(mode);
        offsetMs = PhoneSaberJudgeLatencySettings.ClampOffset(offsetMs);
        if (mode == Mode && offsetMs == OffsetMs) return;
        Mode = mode; OffsetMs = offsetMs;
        string station = PhoneSaberStation.Read();
        PhoneSaberJudgeLatencySettings.Save(station, mode, offsetMs);
        PhoneSaberEventLog.Record(station, "SYSTEM", "judge-latency-setting",
            "mode=" + (mode == PhoneSaberJudgeLatencySettings.GameDefault ? "game" : "auto") + " offset-ms=" + offsetMs);
    }

    GamePlayManager manager;
    double gameSeconds = double.NaN;
    double nextTick;
    double lastLoggedMs = double.NaN;
    int lastLoggedMode = -1;

    void Update()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (manager == null)
        {
            if (now < nextTick) return;
            nextTick = now + 2;
            Refresh(); // メニューでも注意と測定値を更新する
            manager = FindAnyObjectByType<GamePlayManager>();
            if (manager == null) return;
            // シーンごとに作り直される。最初に見た値がゲームの設定（Inspector／コード既定）。
            gameSeconds = manager.cameraJudgeLatencySeconds;
            GameMs = gameSeconds * 1000;
        }
        if (now < nextTick) return;
        nextTick = now + 2;
        Refresh();
        var input = InputPoint.Instance;
        PredictionLeadMs = input == null ? 0 : (input.PredictionLead(false) + input.PredictionLead(true)) / 2.0;
        double seconds = PhoneSaberJudgeLatencySettings.SecondsToApply(Mode, AutomaticMs, OffsetMs, gameSeconds, PredictionLeadMs);
        if (!(seconds >= 0)) return;
        manager.cameraJudgeLatencySeconds = (float)seconds;
        AppliedMs = seconds * 1000;
        if (Mode != lastLoggedMode || double.IsNaN(lastLoggedMs) || System.Math.Abs(AppliedMs.Value - lastLoggedMs) >= 5)
        {
            lastLoggedMode = Mode; lastLoggedMs = AppliedMs.Value;
            PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "SYSTEM", "judge-latency", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "mode={0} applied-ms={1:0} auto-ms={2} offset-ms={3} game-ms={4:0} prediction-lead-ms={5:0}",
                Mode == PhoneSaberJudgeLatencySettings.GameDefault ? "game" : "auto", AppliedMs.Value,
                AutomaticMs.HasValue ? AutomaticMs.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "unknown",
                OffsetMs, gameSeconds * 1000, PredictionLeadMs));
        }
    }

    // 座標の受信→描画の実測を含む推定（スマホ＋通信＋PC＋画面＋露光）。古い推定は使わない。
    void Refresh()
    {
        var input = InputPoint.Instance;
        if (input == null) return;
        double clock = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        double? red = Total(PhoneSaberPerfLog.Estimate(input, false, clock, input.RedCoordinateTiming.Shown));
        double? blue = Total(PhoneSaberPerfLog.Estimate(input, true, clock, input.BlueCoordinateTiming.Shown));
        string sender = input.RedCoordinateSender ?? input.BlueCoordinateSender;
        var phone = sender != null ? input.ReadPhoneMetrics(sender, clock) : null;
        PhoneSaberDisplayNotice.PhoneWarning = phone == null ? ""
            : PhoneSaberDisplayNotice.PhoneWarningFor(sender, phone.Thermal, phone.FpsMode);
        double? sample = PhoneSaberJudgeLatencySettings.Combine(red, blue);
        if (sample.HasValue) AutomaticMs = PhoneSaberJudgeLatencySettings.Smooth(AutomaticMs, sample.Value);
    }

    static double? Total(PhoneSaberLatencyEstimate estimate) =>
        estimate != null && estimate.Status == "estimate" ? estimate.TotalMs : null;
}

// 直近の当たり（Miss 以外）の判定ずれ。F8 で「補正をあと何 ms 動かせば中央か」を見るため。
public static class PhoneSaberJudgeTimingStats
{
    const int Capacity = 40;
    static readonly List<double> recent = new List<double>(Capacity);
    static int next;

    public static void AddHit(double judgeMs)
    {
        if (double.IsNaN(judgeMs) || double.IsInfinity(judgeMs)) return;
        if (recent.Count < Capacity) recent.Add(judgeMs);
        else { recent[next] = judgeMs; next = (next + 1) % Capacity; }
    }

    public static int Count => recent.Count;

    public static double? Median()
    {
        if (recent.Count == 0) return null;
        var sorted = new List<double>(recent);
        sorted.Sort();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public static void Clear() { recent.Clear(); next = 0; }
}
