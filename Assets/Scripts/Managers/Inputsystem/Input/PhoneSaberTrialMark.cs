using UnityEngine;
using UnityEngine.InputSystem;

// 実験で条件を変えるたびに F10 を押すと、events.log に「ここから試行 N」の印（event=trial-mark）を残す
// （2026-10-18 の実機比較用）。play_report.py --by-trial が印ごとに分けて集計する。
// 押した時点の主な設定も一緒に残す。判定・表示・入力は変えない。
public sealed class PhoneSaberTrialMark : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        var go = new GameObject("PhoneSaber Trial Mark");
        go.AddComponent<PhoneSaberTrialMark>().toast = go.AddComponent<PhoneSaberTrialToast>();
        DontDestroyOnLoad(go);
    }

    public static int Trial { get; private set; }
    PhoneSaberTrialToast toast;

    public static string Detail(int trial, int judgeMode, int offsetMs, double? appliedMs, int predictionLeadRed,
        int predictionLeadBlue, int frameCap) =>
        "n=" + trial + " judge=" + (judgeMode == PhoneSaberJudgeLatencySettings.GameDefault ? "game" : "auto") +
        " offset-ms=" + offsetMs +
        " applied-ms=" + (appliedMs.HasValue ? System.Math.Round(appliedMs.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown") +
        " prediction-red-ms=" + predictionLeadRed + " prediction-blue-ms=" + predictionLeadBlue + " frame-cap=" + frameCap;

    void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || !keyboard.f10Key.wasPressedThisFrame) return;
        Trial++;
        var input = InputPoint.Instance;
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "SYSTEM", "trial-mark", Detail(Trial,
            PhoneSaberJudgeLatency.Mode, PhoneSaberJudgeLatency.OffsetMs, PhoneSaberJudgeLatency.AppliedMs,
            input != null ? input.PredictionLead(false) : 0, input != null ? input.PredictionLead(true) : 0,
            PhoneSaberDisplaySync.FrameCapMode));
        PhoneSaberJudgeTimingStats.Clear(); // F8 の「当たりのずれ」をこの試行だけにする
        if (toast != null) toast.Show(Trial);
    }
}

// 印を付けたことを3秒だけ画面に出す。普段は無効（OnGUI の毎フレームの負荷を出さない）。
public sealed class PhoneSaberTrialToast : MonoBehaviour
{
    double until;
    int trial;
    GUIStyle style;

    void Awake() => enabled = false;

    public void Show(int value) { trial = value; until = Time.realtimeSinceStartupAsDouble + 3; enabled = true; }

    void Update() { if (Time.realtimeSinceStartupAsDouble > until) enabled = false; }

    void OnGUI()
    {
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter };
            style.normal.textColor = Color.cyan;
        }
        style.fontSize = Mathf.Max(16, Screen.height / 30);
        GUI.Box(new Rect(Screen.width * 0.3f, Screen.height * 0.08f, Screen.width * 0.4f, style.fontSize * 2.2f),
            $"試行 {trial} 開始（記録に印を付けました）", style);
    }
}
