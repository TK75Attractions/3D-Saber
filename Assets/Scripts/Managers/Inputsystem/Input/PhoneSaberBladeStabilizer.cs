using UnityEngine;

// 剣の揺れ止め（2026-10-18 ユーザー:「剣のブレで勝手にノーツが切られる」「選択が途中でキャンセルされる」）。
// 認識した両端は、コマごとに剣の向きに沿って伸び縮みしたり、誤認識で大きく跳んだりする。
// 判定は両端を結んだ線分で切るので、その揺れがノーツに触れて勝手に切れる。
// 遅れを足さないよう、中心と向きはそのまま使い、次の2つだけを抑える。
//  1. 長さの急な変化: 1 受信あたり ±MaximumLengthChange まで（実際の傾き＝見かけの長さの変化は数コマかけて起きる）。
//  2. ありえない跳び: 直前の速さから外挿した中心から、ふだんの刃の長さ × JumpBladeLengths（60 fps 換算、受信の間隔に比例）
//     以上離れた受信は、MaximumRejectedSamples 回だけ捨てて前の刃を保つ。続けば本当の動きとして受け入れる。
//     実際の振りは 3 m 先で 2 m/s でも 1 コマ（16 ms）で刃の長さの数 % しか動かないので、本物の振りは捨てない。
//     座標の単位（直結の world 写像か画面経由か）に左右されないよう、基準は刃の長さにする。
// 端の順序（丸い剣で A/B が入れ替わる）は前の刃に近い方へそろえる。F8 で台ごとに OFF にできる（既定 ON）。
public sealed class PhoneSaberBladeStabilizer
{
    public const float MaximumLengthChange = .15f;
    // 2026-10-18 午後: 基準が刃の長さ 1 本・最大 2 回だと、iPhone が 30 fps に落ちたとき本物の振りを
    // 13〜24 % 捨て、剣が頻繁に固まった。基準を 60 fps 換算で刃の長さ 2 本にし、受信の間隔に比例して広げ、捨てるのは 1 回まで。
    // 置きっぱなしの赤い物は F8 の「無視」（PhoneSaberIgnoreZones）で消す。ここは本当の一瞬の誤認識だけを拾う。
    public const float JumpBladeLengths = 2f;
    public const double ReferenceInterval = 1.0 / 60;
    public const int MaximumRejectedSamples = 1;
    public const double MaximumGapSeconds = .25;

    Vector2 lastA, lastB, lastMid, velocity;
    float lastLength, typicalLength;
    double lastTime = double.NaN, acceptedTime = double.NaN;
    bool hasSample;
    int rejected;

    public int RejectedJumps { get; private set; }
    public int LimitedLengths { get; private set; }
    public int Samples { get; private set; }

    public void Reset() { typicalLength = 0; hasSample = false; rejected = 0; lastTime = acceptedTime = double.NaN; velocity = Vector2.zero; }

    public void ResetCounters() { RejectedJumps = LimitedLengths = Samples = 0; }

    // 同じ受信時刻はもう一度処理せず、前の結果を返す。
    public void Apply(double receiveTime, Vector2 a, Vector2 b, out Vector2 resultA, out Vector2 resultB)
    {
        resultA = a; resultB = b;
        if (!Finite(a) || !Finite(b) || double.IsNaN(receiveTime) || double.IsInfinity(receiveTime)) { Reset(); return; }
        if (hasSample && receiveTime <= lastTime) { resultA = lastA; resultB = lastB; return; }
        double dt = hasSample ? receiveTime - lastTime : double.NaN;
        if (!hasSample || dt > MaximumGapSeconds)
        {
            Accept(receiveTime, a, b, (a + b) * .5f, Vector2.zero);
            return;
        }
        Samples++;
        // 端の順序を前の刃にそろえる（入れ替わりで刃が半回転したように見えるのを防ぐ）。
        if ((lastA - b).sqrMagnitude + (lastB - a).sqrMagnitude < (lastA - a).sqrMagnitude + (lastB - b).sqrMagnitude)
            (a, b) = (b, a);
        Vector2 mid = (a + b) * .5f;
        double sinceAccepted = receiveTime - acceptedTime;
        Vector2 expected = lastMid + velocity * (float)sinceAccepted;
        float jump = Mathf.Max(typicalLength, lastLength) * JumpBladeLengths
            * (float)System.Math.Max(1.0, sinceAccepted / ReferenceInterval);
        if (jump > 1e-5f && (mid - expected).sqrMagnitude > jump * jump && rejected < MaximumRejectedSamples)
        {
            rejected++;
            RejectedJumps++;
            lastTime = receiveTime;
            resultA = lastA; resultB = lastB;
            return;
        }
        Vector2 axis = b - a;
        float length = axis.magnitude;
        if (length > 1e-5f && lastLength > 1e-5f)
        {
            float limited = Mathf.Clamp(length, lastLength * (1 - MaximumLengthChange), lastLength * (1 + MaximumLengthChange));
            if (limited != length)
            {
                LimitedLengths++;
                Vector2 half = axis / length * (limited * .5f);
                a = mid - half; b = mid + half;
            }
        }
        // 捨てた受信の間は時間が空くので、速さは受け入れた2点の間で測る。
        Vector2 measured = (mid - lastMid) / (float)System.Math.Max(1e-3, sinceAccepted);
        Accept(receiveTime, a, b, mid, Vector2.Lerp(velocity, measured, .5f));
        resultA = a; resultB = b;
    }

    void Accept(double time, Vector2 a, Vector2 b, Vector2 mid, Vector2 newVelocity)
    {
        lastA = a; lastB = b; lastMid = mid; velocity = newVelocity;
        lastLength = (b - a).magnitude;
        typicalLength = typicalLength > 0 ? typicalLength + .05f * (lastLength - typicalLength) : lastLength;
        lastTime = acceptedTime = time; hasSample = true; rejected = 0;
    }

    static bool Finite(Vector2 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y);
}

public static class PhoneSaberBladeStabilizerSettings
{
    public static int Revision { get; private set; }
    public static string Key(string station) => "PhoneSaber.BladeStabilizer." + PhoneSaberStation.Normalize(station) + ".v1";
    public static bool Load(string station) => PlayerPrefs.GetInt(Key(station), 1) != 0;

    public static void Save(string station, bool enabled)
    {
        PlayerPrefs.SetInt(Key(station), enabled ? 1 : 0);
        PlayerPrefs.Save();
        Revision++;
        PhoneSaberEventLog.Record(station, "SYSTEM", "blade-stabilizer-setting", "enabled=" + (enabled ? 1 : 0));
    }
}
