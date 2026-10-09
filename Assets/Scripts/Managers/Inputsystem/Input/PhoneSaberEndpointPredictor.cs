using System;
using UnityEngine;

// 時計・受信・Unity のライフサイクルに依存しない、端点だけの短時間予測。
// 座標の単位は呼び出し側と同じ。セーバー経路では適用前の world XY を使う。
public sealed class PhoneSaberEndpointPredictor
{
    public const double MaximumGapSeconds = 0.1;
    public const double DecayStartSeconds = 1.0 / 30.0;
    public const int MaximumHorizonMilliseconds = 60;
    public const float DefaultMaximumDisplacement = 0.35f;

    readonly float maximumDisplacement;
    Vector2 latestA, latestB, previousVelocityA, previousVelocityB, velocityA, velocityB;
    double latestTime;
    int sampleCount;

    public PhoneSaberEndpointPredictor(float maximumDisplacement = DefaultMaximumDisplacement)
    {
        if (!Finite(maximumDisplacement) || maximumDisplacement < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDisplacement));
        this.maximumDisplacement = maximumDisplacement;
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
            velocityA = sampleCount == 1 ? nextA : ConservativeVelocity(previousVelocityA, nextA);
            velocityB = sampleCount == 1 ? nextB : ConservativeVelocity(previousVelocityB, nextB);
            previousVelocityA = nextA;
            previousVelocityB = nextB;
            sampleCount = 2;
        }
        latestA = a; latestB = b; latestTime = receiveTimeSeconds;
    }

    public void Predict(double nowSeconds, int horizonMilliseconds, out Vector2 a, out Vector2 b)
    {
        a = latestA; b = latestB;
        // OFF は加算すらしない（符号付きゼロを含め、そのまま返す）。
        if (horizonMilliseconds <= 0 || sampleCount < 2 ||
            double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds)) return;
        double age = Math.Max(0, nowSeconds - latestTime);
        if (age >= MaximumGapSeconds) return;
        double decay = age <= DecayStartSeconds ? 1 :
            (MaximumGapSeconds - age) / (MaximumGapSeconds - DecayStartSeconds);
        float seconds = (float)(Math.Min(horizonMilliseconds, MaximumHorizonMilliseconds) * 0.001 * decay);
        // 受信の古さを H に足さない。無音中に走り続けることを防ぐ。
        a += Displacement(velocityA, seconds);
        b += Displacement(velocityB, seconds);
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
        if (length > maximumDisplacement) delta *= (float)(maximumDisplacement / length);
        return delta;
    }
}
