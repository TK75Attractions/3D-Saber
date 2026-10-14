using System;
using UnityEngine;

// 時計・受信・Unity のライフサイクルに依存しない、端点だけの短時間予測。
// 座標の単位は呼び出し側と同じ。セーバー経路では適用前の world XY を使う。
public struct PhoneSaberEndpointPredictor
{
    public const double MaximumGapSeconds = 0.1;
    public const double DecayStartSeconds = 1.0 / 30.0;
    public const int MaximumHorizonMilliseconds = 60;
    public const float DefaultMaximumDisplacement = 0.35f;

    readonly float maximumDisplacement;
    readonly bool configuredDisplacement;
    Vector2 latestA, latestB, previousVelocityA, previousVelocityB, velocityA, velocityB;
    double latestTime;
    int sampleCount;
    Vector2 oldestMid, olderMid, previousMid;
    double oldestTime, olderTime, previousTime;
    public bool LastPredictionSuppressed { get; private set; }

    public PhoneSaberEndpointPredictor(float maximumDisplacement)
    {
        if (!Finite(maximumDisplacement) || maximumDisplacement < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDisplacement));
        this = default;
        this.maximumDisplacement = maximumDisplacement;
        configuredDisplacement = true;
    }

    public void Reset() { sampleCount = 0; }

    public void AddSample(double receiveTimeSeconds, Vector2 a, Vector2 b)
    {
        if (double.IsNaN(receiveTimeSeconds) || double.IsInfinity(receiveTimeSeconds) ||
            !Finite(a.x) || !Finite(a.y) || !Finite(b.x) || !Finite(b.y)) return;
        // 同じ受信を描画フレームごとに速度へ加えない。時刻逆行も無視する。
        if (sampleCount > 0 && receiveTimeSeconds <= latestTime) return;
        double gap = receiveTimeSeconds - latestTime;
        if (sampleCount == 0 || gap > MaximumGapSeconds)
        {
            sampleCount = 1;
            velocityA = velocityB = previousVelocityA = previousVelocityB = Vector2.zero;
        }
        else
        {
            Vector2 nextA = (a - latestA) / (float)gap;
            Vector2 nextB = (b - latestB) / (float)gap;
            // 起動・欠測復帰の1区間だけでは跳ねか定速かを区別できない。3点まで延長しない。
            velocityA = sampleCount == 1 ? Vector2.zero : ConservativeVelocity(previousVelocityA, nextA);
            velocityB = sampleCount == 1 ? Vector2.zero : ConservativeVelocity(previousVelocityB, nextB);
            previousVelocityA = nextA;
            previousVelocityB = nextB;
            sampleCount = Math.Min(4, sampleCount + 1);
        }
        oldestMid = olderMid; oldestTime = olderTime;
        olderMid = previousMid; olderTime = previousTime;
        previousMid = (latestA + latestB) * 0.5f; previousTime = latestTime;
        latestA = a; latestB = b; latestTime = receiveTimeSeconds;
    }

    public void Predict(double nowSeconds, int horizonMilliseconds, out Vector2 a, out Vector2 b)
    {
        a = latestA; b = latestB;
        LastPredictionSuppressed = true;
        // OFF は加算すらしない（符号付きゼロを含め、そのまま返す）。
        if (horizonMilliseconds <= 0 || sampleCount < 3 ||
            double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds)) return;
        double age = Math.Max(0, nowSeconds - latestTime);
        if (age >= MaximumGapSeconds) return;
        double decay = age <= DecayStartSeconds ? 1 :
            (MaximumGapSeconds - age) / (MaximumGapSeconds - DecayStartSeconds);
        float seconds = (float)(Math.Min(horizonMilliseconds, MaximumHorizonMilliseconds) * 0.001 * decay);
        // 受信の古さを H に足さない。無音中に走り続けることを防ぐ。
        Vector2 deltaA = Displacement(velocityA, seconds), deltaB = Displacement(velocityB, seconds);
        LastPredictionSuppressed = deltaA.x == 0 && deltaA.y == 0 && deltaB.x == 0 && deltaB.y == 0;
        a += deltaA;
        b += deltaB;
    }

    // F8「自動」専用。手動・OFF の既存ベクトルと受信時刻の観測値は変えない。
    public void PredictAdaptive(double nowSeconds, int horizonMilliseconds, out Vector2 a, out Vector2 b)
    {
        a = latestA; b = latestB;
        LastPredictionSuppressed = true;
        if (horizonMilliseconds <= 0 || sampleCount < 4 ||
            double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds)) return;
        double age = Math.Max(0, nowSeconds - latestTime);
        if (age >= MaximumGapSeconds) return;
        // 1サンプルの欠測は最大1/30秒だけ進める。以後は減衰し、100msで必ず停止。
        double seconds = Math.Min(horizonMilliseconds, MaximumHorizonMilliseconds) * .001 +
            Math.Min(age, DecayStartSeconds);
        double decay = age <= 2 * DecayStartSeconds ? 1 :
            (MaximumGapSeconds - age) / DecayStartSeconds;
        Vector2 mid = (latestA + latestB) * 0.5f;
        Vector2 velocity = new Vector2(
            AdaptiveAxis(oldestMid.x, olderMid.x, previousMid.x, mid.x, seconds),
            AdaptiveAxis(oldestMid.y, olderMid.y, previousMid.y, mid.y, seconds));
        // 共通の平行移動にし、端点雑音による伸縮・回転を先読みで増幅しない。
        Vector2 delta = Displacement(velocity, (float)seconds) * (float)decay;
        LastPredictionSuppressed = delta.x == 0 && delta.y == 0;
        a += delta; b += delta;
    }

    float AdaptiveAxis(float oldest, float older, float previous, float latest, double horizon)
    {
        double span = latestTime - oldestTime;
        if (span < .001 || span > .15) return 0;
        // 不等間隔の直近4点を二次の最小二乗。時刻を[-1,0]にして桁落ちを抑える。
        double t0 = -1, t1 = (olderTime - latestTime) / span, t2 = (previousTime - latestTime) / span;
        double y0 = oldest - latest, y1 = older - latest, y2 = previous - latest;
        double s1 = t0 + t1 + t2, s2 = t0*t0 + t1*t1 + t2*t2;
        double s3 = t0*t0*t0 + t1*t1*t1 + t2*t2*t2;
        double s4 = t0*t0*t0*t0 + t1*t1*t1*t1 + t2*t2*t2*t2;
        double r0 = y0 + y1 + y2, r1 = t0*y0 + t1*y1 + t2*y2;
        double r2 = t0*t0*y0 + t1*t1*y1 + t2*t2*y2;
        double aa = s2 - s1*s1/4, ab = s3 - s1*s2/4, bb = s4 - s2*s2/4;
        double av = r1 - s1*r0/4, bv = r2 - s2*r0/4;
        double determinant = aa*bb - ab*ab;
        if (determinant <= 1e-8) return 0;
        double velocity = (av*bb - bv*ab) / determinant / span;
        double acceleration = 2 * (bv*aa - av*ab) / determinant / (span*span);
        // 静止雑音約0.08 worldから速度の不確かさを見積もり、静止中の延長を抑える。
        double noiseSpeed = .08 / span;
        double confidence = Math.Max(0, Math.Min(1, (Math.Abs(velocity) - noiseSpeed) / (3 * noiseSpeed)));
        double delta = velocity*horizon + .5*acceleration*horizon*horizon;
        // まだ受信していない折り返しを作らない。外向き延長は既存の移動量上限で制限。
        if (delta * velocity <= 0 || double.IsNaN(delta) || double.IsInfinity(delta)) return 0;
        return (float)(delta / horizon * confidence);
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    static Vector2 ConservativeVelocity(Vector2 previous, Vector2 current)
    {
        // 直近3点の2区間で符号が一致する成分だけ、小さい速度を基に減速も抑制。
        // 1区間だけの跳ねや折り返しで、古い速度を延長しない。
        return new Vector2(ConservativeAxis(previous.x, current.x), ConservativeAxis(previous.y, current.y));
    }

    static float ConservativeAxis(float previous, float current)
    {
        if (!Finite(previous) || !Finite(current) || previous == 0 || current == 0 ||
            Math.Sign(previous) != Math.Sign(current)) return 0;
        if (Math.Abs(current) >= Math.Abs(previous)) return previous;
        // 減速中は速度比でも抑制する。符号反転が届く前の外向き予測を減らす。
        // 認識・端点・受信値は変更せず、任意の描画予測だけに適用。
        float ratio = Math.Abs(current / previous);
        return current * ratio;
    }

    Vector2 Displacement(Vector2 velocity, float seconds)
    {
        if (!Finite(velocity.x) || !Finite(velocity.y)) return Vector2.zero;
        Vector2 delta = velocity * seconds;
        double length = Math.Sqrt((double)delta.x * delta.x + (double)delta.y * delta.y);
        float limit = configuredDisplacement ? maximumDisplacement : DefaultMaximumDisplacement;
        if (length > limit) delta *= (float)(limit / length);
        return delta;
    }
}
