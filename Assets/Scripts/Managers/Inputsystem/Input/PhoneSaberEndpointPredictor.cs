using System;
using UnityEngine;

// 時計・受信・Unity のライフサイクルに依存しない、端点だけの短時間予測。
// 座標の単位は呼び出し側と同じ。セーバー経路では適用前の world XY を使う。
public struct PhoneSaberEndpointPredictor
{
    public const double MaximumGapSeconds = 0.1;
    public const double DecayStartSeconds = 1.0 / 30.0;
    public const int MaximumHorizonMilliseconds = 120;
    public const int MaximumManualHorizonMilliseconds = 60;
    public const float AutomaticMaximumDisplacement = 2f;
    public const float DefaultMaximumDisplacement = 0.35f;

    readonly float maximumDisplacement;
    readonly bool configuredDisplacement;
    Vector2 latestA, latestB, previousVelocityA, previousVelocityB, velocityA, velocityB;
    double latestTime;
    int sampleCount;
    // 固定長の履歴。配列を持たず、structのコピーも履歴を共有しない。
    struct Sample { public double time; public Vector2 mid; }
    Sample h0, h1, h2, h3, h4, h5, h6, h7;
    int historyCount;
    double packetInterval;
    Vector2 automaticVelocity, automaticAcceleration;
    Vector2 automaticRange;
    double automaticSpan;

    public bool LastPredictionSuppressed { get; private set; }

    public PhoneSaberEndpointPredictor(float maximumDisplacement)
    {
        if (!Finite(maximumDisplacement) || maximumDisplacement < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDisplacement));
        this = default;
        this.maximumDisplacement = maximumDisplacement;
        configuredDisplacement = true;
    }

    public void Reset() { sampleCount = historyCount = 0; packetInterval = 0; }

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
        if (historyCount == 0 || gap > MaximumGapSeconds)
        {
            historyCount = 0; packetInterval = 0;
        }
        else if (gap >= .001)
            packetInterval = packetInterval == 0 ? gap : packetInterval + .2 * (Math.Min(gap, 2 * packetInterval) - packetInterval);
        h7 = h6; h6 = h5; h5 = h4; h4 = h3; h3 = h2; h2 = h1; h1 = h0;
        h0 = new Sample { time = receiveTimeSeconds, mid = (a + b) * .5f };
        historyCount = Math.Min(8, historyCount + 1);
        latestA = a; latestB = b; latestTime = receiveTimeSeconds;
        FitAutomatic();
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
        float seconds = (float)(Math.Min(horizonMilliseconds, MaximumManualHorizonMilliseconds) * 0.001 * decay);
        // 受信の古さを H に足さない。無音中に走り続けることを防ぐ。
        Vector2 deltaA = Displacement(velocityA, seconds), deltaB = Displacement(velocityB, seconds);
        LastPredictionSuppressed = deltaA.x == 0 && deltaA.y == 0 && deltaB.x == 0 && deltaB.y == 0;
        a += deltaA;
        b += deltaB;
    }

    // F8「自動」専用。手動・OFF の演算と受信した端点は変更しない。
    public void PredictAdaptive(double nowSeconds, int horizonMilliseconds, out Vector2 a, out Vector2 b)
    {
        a = latestA; b = latestB;
        LastPredictionSuppressed = true;
        if (horizonMilliseconds <= 0 || historyCount < 4 ||
            double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds)) return;
        double age = Math.Max(0, nowSeconds - latestTime);
        if (age >= MaximumGapSeconds || packetInterval < .001) return;
        // 普段のpacket間隔で期限を決める。1欠測から滑らかに縮め、100msで停止。
        double decayStart = Math.Min(MaximumGapSeconds * .8, 1.5 * packetInterval);
        double fraction = Math.Max(0, (age - decayStart) / (MaximumGapSeconds - decayStart));
        double decay = 1 - fraction * fraction * (3 - 2 * fraction);
        double seconds = (Math.Min(horizonMilliseconds, MaximumHorizonMilliseconds) * .001 +
            Math.Min(age, packetInterval)) * decay;
        Vector2 delta = new Vector2(AutomaticAxis(automaticVelocity.x, automaticAcceleration.x, seconds),
            AutomaticAxis(automaticVelocity.y, automaticAcceleration.y, seconds));
        // 観測窓の移動幅を超えて突然加速しない。world感度や棒の移動幅にも追従する上限。
        // 定加速度で静止から動いた場合も、末端速度は窓の平均速度の2倍まで。
        float rangeScale = (float)Math.Max(1, 2 * seconds / automaticSpan);
        delta.x = (float)Math.Max(-automaticRange.x * rangeScale, Math.Min(automaticRange.x * rangeScale, delta.x));
        delta.y = (float)Math.Max(-automaticRange.y * rangeScale, Math.Min(automaticRange.y * rangeScale, delta.y));
        double length = Math.Sqrt((double)delta.x * delta.x + (double)delta.y * delta.y);
        float limit = configuredDisplacement ? maximumDisplacement : AutomaticMaximumDisplacement;
        if (length > limit) delta *= (float)(limit / length);
        LastPredictionSuppressed = delta.x == 0 && delta.y == 0;
        a += delta; b += delta;
    }

    static float AutomaticAxis(float velocity, float acceleration, double horizon)
    {
        // 二次fitが減速を支持しても、未受信の反転へは進めない。停止位置までの積分だけ。
        if (velocity * acceleration < 0) horizon = Math.Min(horizon, -velocity / acceleration);
        double delta = velocity * horizon + .5 * acceleration * horizon * horizon;
        return double.IsNaN(delta) || double.IsInfinity(delta) ? 0 : (float)delta;
    }

    Sample History(int i)
    {
        switch (i)
        {
            case 0: return h0; case 1: return h1; case 2: return h2; case 3: return h3;
            case 4: return h4; case 5: return h5; case 6: return h6; default: return h7;
        }
    }

    void FitAutomatic()
    {
        automaticVelocity = automaticAcceleration = automaticRange = Vector2.zero;
        automaticSpan = 1;
        if (historyCount < 4 || packetInterval < .001) return;
        // 60fpsは約7点/110ms、30fpsは約5点/133ms。不等間隔を実時刻でfitする。
        double window = Math.Max(.11, 4 * packetInterval);
        int n = 1;
        while (n < historyCount && latestTime - History(n).time <= window + .000001) n++;
        if (n < 4) return;
        double span = latestTime - History(n - 1).time;
        if (span < .01 || span > .2) return;
        automaticSpan = span;
        Vector2 low = h0.mid, high = h0.mid;
        for (int i = 1; i < n; i++)
        {
            Vector2 mid = History(i).mid;
            low.x = Math.Min(low.x, mid.x); low.y = Math.Min(low.y, mid.y);
            high.x = Math.Max(high.x, mid.x); high.y = Math.Max(high.y, mid.y);
        }
        automaticRange = high - low;
        automaticVelocity.x = FitAxis(n, span, true, out float ax);
        automaticVelocity.y = FitAxis(n, span, false, out float ay);
        automaticAcceleration = new Vector2(ax, ay);
    }

    float FitAxis(int n, double span, bool x, out float acceleration)
    {
        acceleration = 0;
        double s1 = 0, s2 = 0, s3 = 0, s4 = 0, r0 = 0, r1 = 0, r2 = 0;
        double latest = x ? h0.mid.x : h0.mid.y;
        for (int i = 0; i < n; i++)
        {
            Sample sample = History(i);
            double t = (sample.time - latestTime) / span;
            double y = (x ? sample.mid.x : sample.mid.y) - latest;
            s1 += t; s2 += t*t; s3 += t*t*t; s4 += t*t*t*t;
            r0 += y; r1 += t*y; r2 += t*t*y;
        }
        double aa = s2 - s1*s1/n, ab = s3 - s1*s2/n, bb = s4 - s2*s2/n;
        double av = r1 - s1*r0/n, bv = r2 - s2*r0/n;
        double determinant = aa*bb - ab*ab;
        if (determinant <= 1e-8) return 0;
        double v = (av*bb - bv*ab) / determinant;
        double q = (bv*aa - av*ab) / determinant;
        double intercept = (r0 - v*s1 - q*s2) / n;
        double residual = 0;
        for (int i = 0; i < n; i++)
        {
            Sample sample = History(i);
            double t = (sample.time - latestTime) / span;
            double error = (x ? sample.mid.x : sample.mid.y) - latest - intercept - v*t - q*t*t;
            residual += error * error;
        }
        // 残差が小さくても端点の量子化・相関雑音を無視しない。
        double noise = Math.Max(.08, Math.Sqrt(residual / (n - 3)));
        double linear = av / aa / span;
        double linearError = noise / Math.Sqrt(aa) / span;
        double velocity = v / span;
        double velocityError = noise * Math.Sqrt(bb / determinant) / span;
        double confidence = Math.Min(Unit((Math.Abs(linear) / linearError - 3) / 2),
            Unit((Math.Abs(velocity) / velocityError - .5)));
        double acc = 2 * q / (span * span);
        double accError = 2 * noise * Math.Sqrt(aa / determinant) / (span * span);
        double supported = Unit((Math.Abs(acc) / accError - 2) / 2);
        // 最新の区間と逆方向のfitは、観測済みの反転や単発の跳ねとして抑制。
        double fresh = latest - (x ? h1.mid.x : h1.mid.y);
        if (fresh * velocity <= 0) return 0;
        acceleration = (float)(acc * supported * confidence);
        return (float)(velocity * confidence);
    }

    static double Unit(double value) => Math.Max(0, Math.Min(1, value));

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
