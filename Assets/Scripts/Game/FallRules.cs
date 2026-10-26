using System;
using UnityEngine;

// 花吹雪(F)。実際の残り時間だけで等速落下させる。判定の時計・座標は変更しない。
public static class FallRules
{
    public const float DefaultHeight = 3.2f;
    public const double PetalHideSeconds = .2;
    // gen_fall.py のワールド座標・横間隔とそろえる。判定位置は譜面側で固定する。
    public const float LineY = -.2f;
    public const float LineHalfWidth = 3.15f;
    public const float LineThickness = .024f;
    public const float MinimumNoteGap = 1.3f;

    public static bool Enabled(FallChartData data) => data != null && data.enabled;

    public static float Height(float value) =>
        float.IsNaN(value) || float.IsInfinity(value) || value <= 0f
            ? DefaultHeight : Mathf.Min(value, 6f);

    public static FallChartData Normalize(FallChartData data)
    {
        data ??= new FallChartData();
        data.height = Height(data.height);
        return data;
    }

    // 出現時は高さそのもの、到着時から先は0。接近時間を変えても出現位置は変えない。
    public static float Offset(float height, double remaining, double approach)
    {
        if (!Finite(remaining) || !Finite(approach) || approach <= .0001) return 0f;
        return Height(height) * Mathf.Clamp01((float)(remaining / approach));
    }

    public static Vector3 VisualPosition(Vector2 landing, float judgeZ, float height,
        double remaining, double approach)
    {
        return new Vector3(landing.x, landing.y + Offset(height, remaining, approach), judgeZ);
    }

    // 花びらだけを薄く出し入れする。ノーツ本体の透明度には触れない。
    public static float PetalAlpha(double remaining, double approach)
    {
        if (!Finite(remaining) || !Finite(approach)
            || remaining <= PetalHideSeconds || remaining > approach)
            return 0f;
        double enter = Math.Max(0, Math.Min(1, (approach - remaining) / .08));
        double leave = Math.Max(0, Math.Min(1, (remaining - PetalHideSeconds) / .12));
        return (float)(.45 * enter * leave);
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
