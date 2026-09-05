using UnityEngine;

// 名前/APIは互換性のため維持するが、拍同期は行わない。
// Perfect確定イベントで細線だけを光らせ、GamePlayManagerが減衰を駆動する。
public class GateBeatPulse : MonoBehaviour
{
    public SongPlayer songPlayer;
    public float bpm = 120f;
    // 譜面のトータルオフセット(chart.offsetMs + extra + プレイヤー調整)。ノーツ到達と拍を一致させる。
    public double offsetSeconds;
    // Perfectの瞬間から発光が減衰しきるまでの秒数
    public float decaySeconds = 0.16f;
    public float pulseGain = 1.45f;

    private Material barMat;
    private Color gateColor;
    private float baseEmission;
    private float lastIntensity = -1f;
    private ScoreManager score;
    private bool subscribed;
    private double lastPerfect = double.NegativeInfinity;
    public float CurrentIntensity { get; private set; }

    // JudgeGate の枠を見つけてパルスを付ける(冪等)。ゲートが無ければ null。
    public static GateBeatPulse Ensure(float bpm, double offsetSeconds, SongPlayer songPlayer, ScoreManager score = null)
    {
        var existing = Object.FindFirstObjectByType<GateBeatPulse>();
        if (existing != null)
        {
            existing.bpm = bpm;
            existing.offsetSeconds = offsetSeconds;
            existing.songPlayer = songPlayer;
            existing.Bind(score != null ? score : Object.FindFirstObjectByType<ScoreManager>());
            return existing;
        }

        var gate = GameObject.Find("JudgeGate");
        if (gate == null) return null;
        var top = gate.transform.Find("GateTop");
        if (top == null) return null;
        var mr = top.GetComponent<MeshRenderer>();
        if (mr == null || mr.sharedMaterial == null) return null;

        var pulse = gate.AddComponent<GateBeatPulse>();
        pulse.bpm = bpm;
        pulse.offsetSeconds = offsetSeconds;
        pulse.songPlayer = songPlayer;
        // 枠 4 本は同一マテリアルを共有しているので、1 つ操作すれば全部光る
        pulse.barMat = mr.sharedMaterial;
        pulse.gateColor = GameStageSkin.GateColor;
        pulse.baseEmission = GameStageSkin.GateEmission;
        pulse.Bind(score != null ? score : Object.FindFirstObjectByType<ScoreManager>());
        pulse.Tick(Time.unscaledTimeAsDouble);
        return pulse;
    }

    public void Bind(ScoreManager value)
    {
        if (score == value && subscribed) return;
        Unsubscribe();
        score = value;
        Subscribe();
    }

    private void Subscribe()
    {
        if (subscribed || score == null || !isActiveAndEnabled) return;
        score.OnJudgment += OnJudgment;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (subscribed && score != null) score.OnJudgment -= OnJudgment;
        subscribed = false;
    }

    private void OnEnable() { Subscribe(); }
    private void OnDisable()
    {
        Unsubscribe();
        lastPerfect = double.NegativeInfinity;
        ApplyIntensity(0f);
    }
    private void OnDestroy() { Unsubscribe(); }

    private void OnJudgment(JudgmentTier tier, int awarded)
    {
        if (!isActiveAndEnabled || tier != JudgmentTier.Perfect) return;
        // 同時斬りも加算せず1回分の上限で再点灯する。
        lastPerfect = Time.unscaledTimeAsDouble;
        ApplyIntensity(1f);
    }

    public static float PerfectIntensity01(double now, double perfectTime, float decay)
    {
        if (decay <= 0f || double.IsNaN(now) || double.IsNaN(perfectTime) ||
            double.IsInfinity(now) || double.IsInfinity(perfectTime) || now < perfectTime) return 0f;
        return Mathf.Clamp01(1f - (float)((now - perfectTime) / decay));
    }

    // 古い編集ツール/テスト用の拍計算API。プレイ中の枠からは呼ばない。
    public static float Intensity01(double songTime, float bpm, double offsetSeconds, float decaySeconds)
    {
        if (bpm <= 0f || decaySeconds <= 0f) return 0f;
        double beatInterval = 60.0 / bpm;
        double t = songTime - offsetSeconds;
        if (t < 0.0) return 0f;
        double sinceBeat = t % beatInterval;
        return Mathf.Clamp01(1f - (float)(sinceBeat / decaySeconds));
    }

    public void Tick(double now)
    {
        ApplyIntensity(PerfectIntensity01(now, lastPerfect, decaySeconds));
    }

    private void ApplyIntensity(float intensity)
    {
        CurrentIntensity = intensity;
        // 変化が無ければ SetColor を呼ばない(毎フレームのマテリアル更新を抑制)
        if (Mathf.Approximately(intensity, lastIntensity)) return;
        lastIntensity = intensity;
        if (barMat == null || !barMat.HasProperty("_EmissionColor")) return;
        float e = baseEmission * (1f + pulseGain * intensity);
        Color c = gateColor; c.a = 1f;
        barMat.SetColor("_EmissionColor", c * e);
    }
}
