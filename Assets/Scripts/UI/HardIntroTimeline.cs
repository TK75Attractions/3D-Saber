using System;
using UnityEngine;

// 承認済みWeb試作と同じ24秒。時計・写真・文字の順番を描画から分離する。
public static class HardIntroTimeline
{
    static readonly float[] LineDelays = { .02f, .12f, 0, .075f, .15f, .035f, .11f, .185f };
    public const float Duration = 24f, PhotoStart = 4.4f, TitleStart = 17.8f;
    public const float ClearEnd = 19.5f, LineStart = 19.65f, LineEnd = 22f, PlayStart = 22.8f;
    public const float NameStart = 18.25f, NameEnd = 19.15f, LevelStart = 19.2f, LevelEnd = 20.1f;
    public const float MorseStart = .55f, MorseEnd = 17.1f;
    public static bool EnabledFor(string difficulty) => string.Equals(difficulty, "hard", StringComparison.OrdinalIgnoreCase);
    public static float Smooth(float a, float b, float time) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, time));
    public static int PhotoAt(float time) => time < PhotoStart || time >= TitleStart ? -1 : time < 8.8f ? 0 : time < 13.2f ? 1 : 2;
    public static float PhotoBegin(int index) => index == 0 ? PhotoStart : index == 1 ? 8.8f : 13.2f;
    public static float PhotoEnd(int index) => index == 0 ? 8.8f : index == 1 ? 13.2f : TitleStart;
    public static string PhotoResource(int index) => "UI/HardIntro/" + (index == 0 ? "campus-courtyard" : index == 1 ? "campus-field" : "campus-gate");
    public static float NoiseAt(float time)
    {
        if (time < PhotoStart) return .1f + .9f * Smooth(.4f, 4.15f, time);
        if (time >= TitleStart) return .34f * (1f - Smooth(TitleStart, ClearEnd, time));
        float grain = Mathf.Lerp(.30f, .20f, Mathf.InverseLerp(PhotoStart, TitleStart, time));
        float peak = .96f * (1f - Smooth(PhotoStart, PhotoStart + .95f, time));
        int index = PhotoAt(time);
        float cut = index > 0 ? .16f * (1f - Smooth(PhotoBegin(index), PhotoBegin(index) + .48f, time)) : 0;
        return Mathf.Max(grain, peak) + cut;
    }
    public static float LineProgress(float time, int index)
    {
        return Mathf.Clamp01((time - LineStart - LineDelays[index]) / (LineEnd - LineStart));
    }
    public static Vector2 LineOffset(float time, int index, bool reduced)
    {
        if (reduced) return Vector2.zero;
        float remaining = Mathf.Pow(1f - LineProgress(time, index), 4f);
        float side = index == 0 || index >= 5 ? 1f : -1f;
        return new Vector2(side * 1420f, -side * 28f) * remaining;
    }
}
