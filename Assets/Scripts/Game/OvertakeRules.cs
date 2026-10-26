using System;
using UnityEngine;

// P/U72。奥だけ個別の速さにし、到着0.73秒前から共通の等速経路へ戻す。
// 速度・横の経路は生成時に確定し、飛行中に区間表を読み直さない。
public static class OvertakeRules
{
    public const double CommonSeconds = .73;
    public const double MinimumApproach = 1.5;
    public const double MaximumApproach = 4;
    public const double MaximumFarSeconds = 4;
    public const float MinimumScale = .5f;
    public const float MaximumScale = 4.5f;
    public const float MaximumLaneX = 8f;

    static bool Finite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    // 0・省略・壊れた値は従来の飛び方。正の値だけがPの指定。
    public static float NormalizeScale(float value) =>
        !Finite(value) || value <= 0f ? 0f
            : Mathf.Clamp(value, MinimumScale, MaximumScale);

    public static float NormalizeLane(float value) =>
        Finite(value) ? Mathf.Clamp(value, -MaximumLaneX, MaximumLaneX) : 0f;

    // プレイヤーの保存値は変更しない。Pの飛来計算だけに使う。
    public static double BaseApproach(double requested) =>
        Finite(requested)
            ? Math.Max(MinimumApproach, Math.Min(MaximumApproach, requested))
            : MinimumApproach;

    public static float FlightScale(float authoredScale, double approach)
    {
        double scale = NormalizeScale(authoredScale);
        if (scale <= 0) scale = 1;
        return (float)Math.Min(scale,
            MaximumFarSeconds / (BaseApproach(approach) - CommonSeconds));
    }

    public static double Duration(double approach, float flightScale) =>
        CommonSeconds + (BaseApproach(approach) - CommonSeconds) * flightScale;

    // 手前では実時間そのもの。奥では距離を個別の倍率で割る。
    public static double Remaining(double remaining, float flightScale)
    {
        if (remaining <= CommonSeconds || !Finite(flightScale) || flightScale <= 0f)
            return remaining;
        return CommonSeconds + (remaining - CommonSeconds) / flightScale;
    }

    public static Vector3 Position(Vector2 landing, float laneX, double remaining,
        double approach, float flightScale, float spawnZ, float judgeZ)
    {
        double basis = BaseApproach(approach);
        double duration = Duration(basis, flightScale);
        double u = remaining <= CommonSeconds ? 0
            : Math.Max(0, Math.Min(1,
                (remaining - CommonSeconds) / (duration - CommonSeconds)));
        float blend = (float)(u * u * (3 - 2 * u));
        float speed = (spawnZ - judgeZ) / (float)basis;
        return new Vector3(
            Mathf.LerpUnclamped(landing.x, laneX, blend),
            landing.y,
            judgeZ + speed * (float)Remaining(remaining, flightScale));
    }
}
