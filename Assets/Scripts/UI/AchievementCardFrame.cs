using System;
using UnityEngine;
using UnityEngine.UI;

// 実績カードの面取りに沿うふち。外へ薄くなる光・輪郭・走る筋を、画像を使わずに描く。
// 動きはカードの年齢だけから決める。演出を下げる設定では、虹も含めて静かなふちを残す。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class AchievementCardFrame : MaskableGraphic
{
    public const float CornerCut = 12f, StrokeWidth = 2f, MaxGlowWidth = 24f;
    public const float PulseSeconds = .6f;
    const float MeshStep = 12f, RainbowSeconds = 4f;

    AchievementTier tier;
    double age;
    bool reducedEffects;

    public void SetAppearance(AchievementTier value, double seconds, bool reduced)
    {
        tier = value;
        age = reduced ? 0 : Math.Max(0, seconds);
        reducedEffects = reduced;
        raycastTarget = false;
        SetVerticesDirty();
    }

    public static Color TierColor(AchievementTier value)
    {
        switch (value)
        {
            case AchievementTier.Bronze: return new Color32(0xD0, 0x8A, 0x4E, 255);
            case AchievementTier.Silver: return new Color32(0xDC, 0xE4, 0xF0, 255);
            case AchievementTier.Gold: return new Color32(0xFF, 0xD8, 0x4D, 255);
            case AchievementTier.Rainbow: return Color.HSVToRGB(0f, .88f, 1f);
            default: return new Color32(0x70, 0x83, 0x9B, 255);
        }
    }

    public static float GlowWidth(AchievementTier value)
    {
        switch (value)
        {
            case AchievementTier.Bronze: return 10f;
            case AchievementTier.Silver: return 14f;
            case AchievementTier.Gold: return 18f;
            case AchievementTier.Rainbow: return MaxGlowWidth;
            default: return 0f;
        }
    }

    public static float CycleSeconds(AchievementTier value)
    {
        switch (value)
        {
            case AchievementTier.Bronze: return 2.4f;
            case AchievementTier.Silver: return 1.8f;
            case AchievementTier.Gold: return 1.3f;
            case AchievementTier.Rainbow: return 1.2f;
            default: return float.PositiveInfinity;
        }
    }

    // along はふち一周を 0〜1 とした距離。虹は位置によって色が変わり、4秒で色が一周する。
    public static Color EdgeColor(AchievementTier value, float along, double seconds, bool reduced)
    {
        if (value != AchievementTier.Rainbow) return TierColor(value);
        float turn = reduced ? 0f : (float)(Math.Max(0, seconds) / RainbowSeconds);
        return Color.HSVToRGB(Mathf.Repeat(along - turn, 1f), .88f, 1f);
    }

    // 入りきるまで滑らかに強まり、その後 0.6 秒で落ち着く。繰り返す点滅は入れない。
    public static float EntryPulse(double seconds, bool reduced)
    {
        if (reduced || seconds < 0) return 0f;
        if (seconds <= AchievementToast.EnterSeconds)
            return Mathf.SmoothStep(0f, 1f, (float)(seconds / AchievementToast.EnterSeconds));
        return 1f - Mathf.SmoothStep(0f, 1f,
            (float)((seconds - AchievementToast.EnterSeconds) / PulseSeconds));
    }

    public static Color GlowColor(AchievementTier value, float along, double seconds, bool reduced)
    {
        if (GlowWidth(value) <= 0f) return Color.clear;
        Color result = value == AchievementTier.Silver
            ? new Color(.38f, .65f, 1f)
            : EdgeColor(value, along, seconds, reduced);
        result.a = (.16f + .025f * (int)value) * (1f + 1.1f * EntryPulse(seconds, reduced));
        return result;
    }

    public static Color StreakColor(AchievementTier value, float along, double seconds, bool reduced)
    {
        if (reduced || value == AchievementTier.None) return Color.clear;
        float head = Mathf.Repeat((float)(Math.Max(0, seconds) / CycleSeconds(value)), 1f);
        float strength = StreakStrength(along, head);
        if (value == AchievementTier.Rainbow)
            strength = Mathf.Max(strength, StreakStrength(along, Mathf.Repeat(head + .5f, 1f)));
        Color result = value == AchievementTier.Rainbow
            ? Color.white : Color.Lerp(TierColor(value), Color.white, .55f);
        result.a = strength * (.2f + .18f * (int)value);
        return result;
    }

    static float StreakStrength(float along, float head)
    {
        float distance = Mathf.Abs(Mathf.Repeat(along - head + .5f, 1f) - .5f);
        return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f - distance / .065f));
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect rect = rectTransform.rect;
        if (rect.width <= 0f || rect.height <= 0f) return;
        float glowWidth = GlowWidth(tier);
        var edge = Points(rect, 0f);
        var inner = Points(rect, -StrokeWidth);
        var outer = Points(rect, glowWidth);
        float perimeter = 0f;
        for (int i = 0; i < edge.Length; i++)
            perimeter += Vector2.Distance(edge[i], edge[(i + 1) % edge.Length]);
        float travelled = 0f;
        for (int i = 0; i < edge.Length; i++)
        {
            float length = Vector2.Distance(edge[i], edge[(i + 1) % edge.Length]);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / MeshStep));
            for (int j = 0; j < steps; j++)
            {
                float from = j / (float)steps, to = (j + 1) / (float)steps;
                float s0 = (travelled + length * from) / perimeter;
                float s1 = (travelled + length * to) / perimeter;
                if (glowWidth > 0f)
                    Band(vh, edge, outer, i, from, to,
                        GlowColor(tier, s0, age, reducedEffects), GlowColor(tier, s1, age, reducedEffects), true);
                Color c0 = EdgeColor(tier, s0, age, reducedEffects);
                Color c1 = EdgeColor(tier, s1, age, reducedEffects);
                c0.a = c1.a = .9f;
                Band(vh, edge, inner, i, from, to, c0, c1, false);
                if (!reducedEffects && tier != AchievementTier.None)
                    Band(vh, edge, inner, i, from, to,
                        StreakColor(tier, s0, age, false), StreakColor(tier, s1, age, false), false);
            }
            travelled += length;
        }
    }

    // 斜めの辺も平行に広げる。塗りの角とふちの角を同じ形にそろえる。
    static Vector2[] Points(Rect rect, float outset)
    {
        float cut = Mathf.Clamp(CornerCut, 0f, Mathf.Min(rect.width, rect.height) * .3f);
        rect = new Rect(rect.xMin - outset, rect.yMin - outset,
            rect.width + outset * 2f, rect.height + outset * 2f);
        cut = Mathf.Max(0f, cut + outset * (2f - Mathf.Sqrt(2f)));
        return new[]
        {
            new Vector2(rect.xMin + cut, rect.yMax), new Vector2(rect.xMax - cut, rect.yMax),
            new Vector2(rect.xMax, rect.yMax - cut), new Vector2(rect.xMax, rect.yMin + cut),
            new Vector2(rect.xMax - cut, rect.yMin), new Vector2(rect.xMin + cut, rect.yMin),
            new Vector2(rect.xMin, rect.yMin + cut), new Vector2(rect.xMin, rect.yMax - cut),
        };
    }

    void Band(VertexHelper vh, Vector2[] from, Vector2[] to, int edge,
        float start, float end, Color startColor, Color endColor, bool fade)
    {
        int next = (edge + 1) % from.Length;
        Color farStart = startColor, farEnd = endColor;
        if (fade) farStart.a = farEnd.a = 0f;
        int first = vh.currentVertCount;
        vh.AddVert(Vector2.Lerp(from[edge], from[next], start), Tint(startColor), Vector2.zero);
        vh.AddVert(Vector2.Lerp(from[edge], from[next], end), Tint(endColor), Vector2.zero);
        vh.AddVert(Vector2.Lerp(to[edge], to[next], end), Tint(farEnd), Vector2.zero);
        vh.AddVert(Vector2.Lerp(to[edge], to[next], start), Tint(farStart), Vector2.zero);
        vh.AddTriangle(first, first + 1, first + 2);
        vh.AddTriangle(first, first + 2, first + 3);
    }

    Color Tint(Color part) => new Color(part.r * color.r, part.g * color.g, part.b * color.b, part.a * color.a);
}
