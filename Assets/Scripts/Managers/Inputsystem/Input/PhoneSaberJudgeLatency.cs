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
    // 予測 ON で基本の先読みを引いた後の下限。画面の追加先読みは、この下限からも同量を引く。
    // 2026-10-18 実測: 予測 ON（先読み 61〜80 ms）で補正 0 ms のとき、当たりの中央が −51 ms（早め）だった。
    public const int MinimumAfterPredictionMs = -150;
    public const int MinimumOffsetMs = -250, MaximumOffsetMs = 150;

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

    // 書き込む秒数。自動で測定値が無いときはゲームの値を基準に、画面だけの追加分を相殺する。
    // F8 の予測が ON だと、判定（SaberCutJudge）は先読みした刃の位置で切る。先読み分は補正から差し引く。
    public static double SecondsToApply(int mode, double? automaticMs, int offsetMs, double gameSeconds,
        double predictionLeadMs = 0, double visualExtraMs = 0)
    {
        double lead = double.IsNaN(predictionLeadMs) || predictionLeadMs < 0 ? 0 : predictionLeadMs;
        double extra = double.IsNaN(visualExtraMs) ? 0 : System.Math.Min(lead,
            System.Math.Max(0, System.Math.Min(visualExtraMs, PhoneSaberVisualLeadSettings.MaximumExtraMilliseconds)));
        // ゲーム設定・未測定でも、画面だけの追加分は必ず相殺する。
        if (Clamp(mode) == GameDefault || !automaticMs.HasValue) return gameSeconds - extra / 1000.0;
        double baseLead = lead - extra;
        // 予測なし: 30〜250 ms。予測あり: 判定は先読みした刃で切るので先読み分を引き、−150 ms まで許す
        // （自動予測の先読みは同じ推定から作られるので、引いた後は 0 付近〜負になり得る）。
        double ms = automaticMs.Value + ClampOffset(offsetMs) - baseLead;
        // 追加 0 の補正を先に確定してから同量を引く。元の −150 ms 下限に達していても、
        // 追加分に応じて最大 −230 ms まで許す。上限側も同様に実効補正を維持し、既定 0 は従来どおり。
        return (Mathf.Clamp((float)ms, baseLead > 0 ? MinimumAfterPredictionMs : MinimumMs, MaximumMs) - extra) / 1000.0;
    }

    // 予測の先読み（赤・青）。0 の色（来ていない・予測なし）は平均に入れない。
    public static double PredictionLead(int red, int blue)
    {
        if (red > 0 && blue > 0) return (red + blue) / 2.0;
        return System.Math.Max(0, System.Math.Max(red, blue));
    }
}

// InputPoint（−2000）と剣（−1500）の後、SaberCutJudge（既定0）の前に同じ先読みを反映する。
[DefaultExecutionOrder(-1400)]
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
        PhoneSaberJudgeTimingStats.Clear(); // 補正を変えたら、それ以前の当たりのずれは使わない
        string station = PhoneSaberStation.Read();
        PhoneSaberJudgeLatencySettings.Save(station, mode, offsetMs);
        PhoneSaberEventLog.Record(station, "SYSTEM", "judge-latency-setting",
            "mode=" + (mode == PhoneSaberJudgeLatencySettings.GameDefault ? "game" : "auto") + " offset-ms=" + offsetMs);
    }

    GamePlayManager manager;
    int managerSearchScene = -1;
    double gameSeconds = double.NaN;
    double nextTick;
    double lastLoggedMs = double.NaN;
    double lastLoggedEffectiveMs = double.NaN;
    int lastLoggedMode = -1;

    void Update()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (manager == null)
        {
            // メニューから本編へ移った直後は待たない。新しい判定時計にも最初のコマから追加分を相殺する。
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (now < nextTick && managerSearchScene == scene) return;
            managerSearchScene = scene;
            nextTick = now + 2;
            Refresh(); // メニューでも注意と測定値を更新する
            manager = FindAnyObjectByType<GamePlayManager>();
            if (manager == null) return;
            // シーンごとに作り直される。最初に見た値がゲームの設定（Inspector／コード既定）。
            gameSeconds = manager.cameraJudgeLatencySeconds;
            GameMs = gameSeconds * 1000;
            nextTick = now; // 見つけたその場で適用する（曲の最初の数秒を 120 ms のままにしない）
        }
        if (now >= nextTick)
        {
            nextTick = now + 2;
            Refresh();
        }
        var input = InputPoint.Instance;
        PredictionLeadMs = input == null ? 0 : PhoneSaberJudgeLatencySettings.PredictionLead(input.PredictionLead(false), input.PredictionLead(true));
        double baseLead = input == null ? 0 : PhoneSaberJudgeLatencySettings.PredictionLead(input.BasePredictionLead(false), input.BasePredictionLead(true));
        double seconds = PhoneSaberJudgeLatencySettings.SecondsToApply(Mode, AutomaticMs, OffsetMs, gameSeconds,
            PredictionLeadMs, PredictionLeadMs - baseLead);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return;
        manager.cameraJudgeLatencySeconds = (float)seconds;
        AppliedMs = seconds * 1000;
        if (Mode != lastLoggedMode || double.IsNaN(lastLoggedMs) || System.Math.Abs(AppliedMs.Value - lastLoggedMs) >= 5)
        {
            // 実効補正が動いたときだけ履歴を捨てる。画面だけの先回り変更では当たりの統計を保つ。
            double effectiveMs = AppliedMs.Value + PredictionLeadMs;
            if (!double.IsNaN(lastLoggedEffectiveMs) && System.Math.Abs(effectiveMs - lastLoggedEffectiveMs) >= 5)
                PhoneSaberJudgeTimingStats.Clear();
            lastLoggedEffectiveMs = effectiveMs;
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
            : PhoneSaberDisplayNotice.PhoneWarningFor(sender, phone.Thermal, phone.FpsMode, PhoneSaberUsbTether.ViaUsb(sender, clock));
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
