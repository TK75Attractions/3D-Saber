using System;
using System.Collections.Generic;
using UnityEngine;

// time から level へ ramp ミリ秒かけて移る。時刻の原点・offsetMs はノーツと共通。
// 短すぎる ramp も本体では最低1秒にして、全面が急に明滅する指定を防ぐ。
[Serializable]
public sealed class DarkPointData
{
    public float time;
    public float level;
    public float ramp = 1000f;
}

public static class DarkRules
{
    // 投影する台に合わせる調整箇所。透明度ではなく、元の色に掛ける明るさ。
    public const float NoteMinimum = .15f;
    public const float StageMinimum = .14f;
    public const float HudMinimum = .18f;
    public const float FullRadius = 1.8f;
    public const float FadeRadius = 2.65f;
    public const float MinimumRampSeconds = 1f;
    public const double FreshSeconds = .15;

    public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    public static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
    public static bool Available(BladeSample blade) => blade.Valid && Finite(blade.A) && Finite(blade.B);
    public static float Level(float value) => Finite(value) ? Mathf.Clamp01(value) : 0f;
    public static float ClampBrightness(float value) => Finite(value) ? Mathf.Clamp(value, NoteMinimum, 1f) : 1f;
    public static float StageBrightness(float level) => Mathf.Lerp(1f, StageMinimum, Level(level));
    public static float HudBrightness(float level) => Mathf.Lerp(1f, HudMinimum, Level(level));
    public static float Smooth(float value)
    {
        float u = Mathf.Clamp01(value);
        return u * u * (3f - 2f * u);
    }

    public static Color Rgb(Color color, float brightness)
        => new Color(color.r * brightness, color.g * brightness, color.b * brightness, color.a);

    // Z方向の接近は照明距離に含めない。判定面のXYへ写し、奥のノーツも剣で先読みできる。
    // 中点ではなく線分全体までの距離。長い刃の先端でも同じ範囲を照らす。
    public static float Distance(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 delta = b - a;
        float along = delta.sqrMagnitude > .000001f
            ? Mathf.Clamp01(Vector2.Dot(point - a, delta) / delta.sqrMagnitude) : 0f;
        return Vector2.Distance(point, a + delta * along);
    }

    public static float Brightness(float level, SaberHand hand, Vector2 point, BladeSample red, BladeSample blue)
    {
        level = Level(level);
        if (level <= 0f || !Finite(point)) return 1f;
        bool r = Available(red), b = Available(blue);
        // 追跡できない担当色は即座に救済する。金・無色は使える側の光で照らせる。
        if ((!r && hand == SaberHand.Right) || (!b && hand == SaberHand.Left) || (!r && !b)) return 1f;
        float distance = Mathf.Min(
            r ? Distance(point, red.A, red.B) : float.PositiveInfinity,
            b ? Distance(point, blue.A, blue.B) : float.PositiveInfinity);
        // どちらの光でも色を問わず照らせる。境界は値も傾きも連続。
        float away = Smooth((distance - FullRadius) / (FadeRadius - FullRadius));
        return Mathf.Lerp(1f, NoteMinimum, level * away);
    }

    // 着地枠が既に持つ色と発光を同じ比率で変更する。フェード用のalphaは保つ。
    internal static void MultiplyMaterialRgb(Material material, float multiplier)
    {
        if (material == null) return;
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", Rgb(material.GetColor("_BaseColor"), multiplier));
        else if (material.HasProperty("_Color"))
            material.SetColor("_Color", Rgb(material.GetColor("_Color"), multiplier));
        if (material.HasProperty("_EmissionColor"))
            material.SetColor("_EmissionColor", Rgb(material.GetColor("_EmissionColor"), multiplier));
    }
}

// 状態を積算せず絶対時刻から求める。停止・途中開始・時刻の巻き戻しでも同じ答えになる。
public sealed class DarkTimeline
{
    struct Segment
    {
        public double start, duration;
        public float from, to;
    }

    readonly List<Segment> segments = new List<Segment>();
    public int Count => segments.Count;

    public static DarkTimeline Build(IList<DarkPointData> source)
    {
        if (source == null || source.Count == 0) return null;
        var ordered = new List<(DarkPointData point, int order)>();
        for (int i = 0; i < source.Count; i++)
        {
            var point = source[i];
            if (point == null || !DarkRules.Finite(point.time) || !DarkRules.Finite(point.level)) continue;
            ordered.Add((point, i));
        }
        ordered.Sort((a, b) => {
            int comparison = a.point.time.CompareTo(b.point.time);
            return comparison != 0 ? comparison : a.order.CompareTo(b.order);
        });
        var result = new DarkTimeline();
        for (int i = 0; i < ordered.Count; i++)
        {
            var point = ordered[i].point;
            // 同時刻の指定は最後の行を採用する。
            if (i + 1 < ordered.Count && ordered[i + 1].point.time == point.time) continue;
            double start = point.time / 1000.0;
            result.segments.Add(new Segment {
                start = start,
                from = result.Evaluate(start),
                to = DarkRules.Level(point.level),
                duration = Math.Max(DarkRules.MinimumRampSeconds,
                    DarkRules.Finite(point.ramp) ? point.ramp / 1000.0 : DarkRules.MinimumRampSeconds)
            });
        }
        return result.Count > 0 ? result : null;
    }

    public float Evaluate(double seconds)
    {
        if (!DarkRules.Finite(seconds)) return 0f;
        int lo = 0, hi = segments.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (segments[mid].start <= seconds) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return 0f;
        var segment = segments[found];
        return Mathf.Lerp(segment.from, segment.to,
            DarkRules.Smooth((float)((seconds - segment.start) / segment.duration)));
    }
}
