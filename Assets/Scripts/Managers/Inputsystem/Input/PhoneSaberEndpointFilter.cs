using System;
using UnityEngine;

// 受信端点だけを扱う One Euro フィルタ。時計・ソケット・MonoBehaviour に依存しない。
// 単位は予測前の world XY。台の写像を変えた場合は効果も変わる。
public sealed class PhoneSaberEndpointFilter
{
    public const double MaximumGapSeconds = 0.1;
    public const int Off = 0;
    public const int Weak = 1;
    public const int Medium = 2;
    // 中点は「中」のまま、半軸だけを平滑化。回転・伸縮の遅れが増えるため明示選択専用。
    public const int Steady = 3;

    struct Axis
    {
        double raw, filtered, derivative;

        public void Start(float value) { raw = filtered = value; derivative = 0; }

        public float Apply(float value, double dt, double minimum, double beta, double derivativeAlpha)
        {
            derivative += derivativeAlpha * (((double)value - raw) / dt - derivative);
            double alpha = Alpha(minimum + beta * Math.Abs(derivative), dt);
            filtered += alpha * ((double)value - filtered);
            raw = value;
            return (float)filtered;
        }
    }

    Axis ax, ay, bx, by, halfX, halfY;
    Vector2 previousRawA, previousRawB, filteredA, filteredB;
    double latestTime;
    bool hasSample;
    int lastMode;

    public void Reset() { hasSample = false; }

    public void Apply(double receiveTimeSeconds, Vector2 a, Vector2 b, int mode,
        out Vector2 resultA, out Vector2 resultB)
    {
        resultA = a; resultB = b;
        // OFF は符号付きゼロ・NaN・順序も含めてそのまま。並べ替えも算術も行わない。
        if (mode != Weak && mode != Medium && mode != Steady) { Reset(); lastMode = Off; return; }
        if (mode != lastMode) { Reset(); lastMode = mode; }
        if (double.IsNaN(receiveTimeSeconds) || double.IsInfinity(receiveTimeSeconds) ||
            !Finite(a.x) || !Finite(a.y) || !Finite(b.x) || !Finite(b.y)) { Reset(); return; }
        // 描画が60Hzでも同じ受信サンプルを二度フィルタへ入れない。
        if (hasSample && receiveTimeSeconds <= latestTime)
        {
            resultA = filteredA; resultB = filteredB;
            return;
        }
        double dt = receiveTimeSeconds - latestTime;
        if (!hasSample || dt > MaximumGapSeconds)
        {
            ax.Start(a.x); ay.Start(a.y); bx.Start(b.x); by.Start(b.y);
            if (mode == Steady)
            {
                Vector2 half = (b - a) * .5f;
                halfX.Start(half.x); halfY.Start(half.y);
            }
            hasSample = true;
        }
        else
        {
            // 前回の生端点との距離の和を最小化。同点は受信順を保つ。
            if (Distance(previousRawA, b) + Distance(previousRawB, a) <
                Distance(previousRawA, a) + Distance(previousRawB, b))
                (a, b) = (b, a);
            double minimum = mode == Weak ? 1.0 : 0.25;
            double beta = mode == Weak ? 10.0 : 7.0;
            double derivativeAlpha = Alpha(mode == Weak ? 10.0 : 60.0, dt);
            resultA = new Vector2(ax.Apply(a.x, dt, minimum, beta, derivativeAlpha),
                ay.Apply(a.y, dt, minimum, beta, derivativeAlpha));
            resultB = new Vector2(bx.Apply(b.x, dt, minimum, beta, derivativeAlpha),
                by.Apply(b.y, dt, minimum, beta, derivativeAlpha));
            if (mode == Steady)
            {
                // 予測器へ渡す中点の応答は変えず、伸縮・向きの高周波成分だけを抑える。
                Vector2 mid = (resultA + resultB) * .5f;
                Vector2 half = (b - a) * .5f;
                double shapeAlpha = Alpha(2.0, dt);
                half = new Vector2(halfX.Apply(half.x, dt, minimum, beta, shapeAlpha),
                    halfY.Apply(half.y, dt, minimum, beta, shapeAlpha));
                resultA = mid - half; resultB = mid + half;
            }
        }
        previousRawA = a; previousRawB = b;
        filteredA = resultA; filteredB = resultB;
        latestTime = receiveTimeSeconds;
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    static double Alpha(double cutoff, double dt) =>
        1.0 / (1.0 + 1.0 / (2.0 * Math.PI * cutoff * dt));

    static double Distance(Vector2 a, Vector2 b)
    {
        double dx = (double)a.x - b.x, dy = (double)a.y - b.y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
