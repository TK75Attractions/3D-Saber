using System;
using UnityEngine;

// U47 流星。到着から lengthMs の終わりまで、一定速度で直線をなぞる。
public static class LongSlideRules
{
    public const float MinX = -3f, MaxX = 3f, MinY = -.95f, MaxY = 1.35f;
    public const float MaxStepPerCut = .4f;
    public const string RuleCaption = "逃げるロングを追いかけて切ろう";

    public static bool Enabled(NoteData note) => note != null && note.slide
        && note.count > 1 && Finite(note.lengthMs) && note.lengthMs > 0
        && Finite(note.slideToX) && Finite(note.slideToY);

    // 終点も x/y と同じ譜面座標。ワールドの範囲に収めてから譜面座標へ戻す。
    public static void Normalize(NoteData note, float scale)
    {
        if (note == null) return;
        if (!Enabled(note))
        {
            note.slide = false;
            note.slideToX = note.slideToY = 0f;
            return;
        }
        if (!Finite(scale) || scale <= 0) scale = 1;
        note.slideToX = (float)(Math.Max(MinX, Math.Min(MaxX, (double)note.slideToX * scale)) / scale);
        note.slideToY = (float)(Math.Max(MinY, Math.Min(MaxY, (double)note.slideToY * scale)) / scale);
    }

    public static Vector2 Position(Vector2 start, Vector2 end, double elapsed, double duration)
    {
        if (!Finite(elapsed) || !Finite(duration) || duration <= 0 || elapsed <= 0)
            return start;
        if (elapsed >= duration) return end;
        return Vector2.LerpUnclamped(start, end, (float)(elapsed / duration));
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
