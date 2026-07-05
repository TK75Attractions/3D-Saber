using UnityEngine;

// 判定ゲートの枠を曲の BPM に同期して拍ごとに明滅させる「視覚メトロノーム」。
// リング収束(連続変化)だけではタイミングの予測が難しいため、
// 画面側に「拍そのもの」の離散的なアンカーを与える。
// GameStageSkin が建てた JudgeGate の枠マテリアル(4 本共有)の emission を上下させるだけで、
// サイズや形は変えない(視覚ノイズを増やさないため)。
public class GateBeatPulse : MonoBehaviour
{
    public SongPlayer songPlayer;
    public float bpm = 120f;
    // 譜面のトータルオフセット(chart.offsetMs + extra + プレイヤー調整)。ノーツ到達と拍を一致させる。
    public double offsetSeconds;
    // 拍の瞬間から発光が減衰しきるまでの秒数
    public float decaySeconds = 0.12f;
    // 拍の瞬間に emission を何倍まで持ち上げるか(1 + gain 倍)
    public float pulseGain = 0.9f;

    private Material barMat;
    private Color gateColor;
    private float baseEmission;
    private float lastIntensity = -1f;

    // JudgeGate の枠を見つけてパルスを付ける(冪等)。ゲートが無ければ null。
    public static GateBeatPulse Ensure(float bpm, double offsetSeconds, SongPlayer songPlayer)
    {
        var existing = Object.FindFirstObjectByType<GateBeatPulse>();
        if (existing != null)
        {
            existing.bpm = bpm;
            existing.offsetSeconds = offsetSeconds;
            existing.songPlayer = songPlayer;
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
        return pulse;
    }

    // 拍の強度 0..1。拍の瞬間 1 → decaySeconds かけて 0 へ線形減衰。曲開始前(t<0)は 0。
    // テストから直接叩く純関数。
    public static float Intensity01(double songTime, float bpm, double offsetSeconds, float decaySeconds)
    {
        if (bpm <= 0f || decaySeconds <= 0f) return 0f;
        double beatInterval = 60.0 / bpm;
        double t = songTime - offsetSeconds;
        if (t < 0.0) return 0f;
        double sinceBeat = t % beatInterval;
        return Mathf.Clamp01(1f - (float)(sinceBeat / decaySeconds));
    }

    void Update()
    {
        if (barMat == null) return;
        double songTime = songPlayer != null ? songPlayer.SongTime : 0.0;
        float intensity = Intensity01(songTime, bpm, offsetSeconds, decaySeconds);
        // 変化が無ければ SetColor を呼ばない(毎フレームのマテリアル更新を抑制)
        if (Mathf.Approximately(intensity, lastIntensity)) return;
        lastIntensity = intensity;
        if (!barMat.HasProperty("_EmissionColor")) return;
        float e = baseEmission * (1f + pulseGain * intensity);
        Color c = gateColor; c.a = 1f;
        barMat.SetColor("_EmissionColor", c * e);
    }
}
