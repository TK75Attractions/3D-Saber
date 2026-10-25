using System;
using UnityEngine;

// 風による見た目のずれ。譜面の位置・判定・表示設定には触れない。
public static class WindRules
{
    public const double CalmSeconds = .35;
    public const double SwayBeats = 2;
    public const string RuleCaption = "風に流されて来る。着地の枠で切ろう";

    public static Vector2 Offset(float windX, float windY, float windSpin, float windSway,
        double remainingSeconds, double approachSeconds, double beat)
    {
        if (!Finite(windX) || !Finite(windY) || !Finite(windSpin) || !Finite(windSway)
            || !Finite(remainingSeconds) || !Finite(approachSeconds) || !Finite(beat))
            return Vector2.zero;
        if (remainingSeconds <= CalmSeconds || approachSeconds <= CalmSeconds)
            return Vector2.zero;

        double p = Math.Max(0, Math.Min(1,
            (remainingSeconds - CalmSeconds) / (approachSeconds - CalmSeconds)));
        double e = p * p * (3 - 2 * p);
        double angle = windSpin * (1 - p);
        double c = Math.Cos(angle);
        double s = Math.Sin(angle);
        // 大きい拍でも位相の計算を有限に保つ。負の拍も同じ周期で揺れる。
        double phase = 2 * Math.PI * (beat % SwayBeats) / SwayBeats;
        float x = (float)((windX * c - windY * s) * e
            + windSway * e * Math.Sin(phase));
        float y = (float)((windX * s + windY * c) * e);
        return Finite(x) && Finite(y) ? new Vector2(x, y) : Vector2.zero;
    }

    // 出方(2026-10-26 ユーザー「W譜面のノーツの出現の仕方の改善の余地もある」)。
    // 出た瞬間は小さく、回り込みながら GrowSeconds で本来の大きさになる(osu! の Grow・Spin In に倣う。ずれた所にいきなり出ない)。
    // 流れる速さ(画面単位/秒)に合わせて葉のように傾く(Beat Saber の改造譜面の曲がった道に倣う)。静かな区間では元の姿勢。
    public const double GrowSeconds = .22;
    public const float GrowFrom = .25f;
    public const float SpinInDegrees = 90f;
    public const float BankDegreesPerSpeed = 7f;
    public const float MaxBankDegrees = 28f;

    // 出現からの秒 → 大きさの倍率(GrowFrom→1、なめらかに)。
    public static float GrowScale(double sinceSpawn)
    {
        float k = EaseOut(sinceSpawn / GrowSeconds);
        return Mathf.Lerp(GrowFrom, 1f, k);
    }

    // 出現からの秒 → 回り込みの残りの角度(SpinInDegrees→0)。
    public static float SpinIn(double sinceSpawn) => SpinInDegrees * (1f - EaseOut(sinceSpawn / GrowSeconds));

    // 横に流れる速さに合わせた傾き(右へ流れるときは右へ傾く=時計回り)。静かな区間・届いたあとは 0。
    public static float BankDegrees(float windX, float windY, float windSpin, float windSway,
        double remainingSeconds, double approachSeconds, double beat, double beatsPerSecond)
    {
        if (remainingSeconds <= CalmSeconds) return 0f;
        const double h = 1.0 / 60.0;
        float now = Offset(windX, windY, windSpin, windSway, remainingSeconds, approachSeconds, beat).x;
        float next = Offset(windX, windY, windSpin, windSway, remainingSeconds - h, approachSeconds,
            beat + h * (Finite(beatsPerSecond) ? beatsPerSecond : 0)).x;
        float speed = (float)((next - now) / h);
        return Mathf.Clamp(-speed * BankDegreesPerSpeed, -MaxBankDegrees, MaxBankDegrees);
    }

    static float EaseOut(double t)
    {
        if (!Finite(t)) return 1f;
        float k = Mathf.Clamp01((float)t);
        return 1f - (1f - k) * (1f - k);
    }

    // 譜面の風の値は「判定面で見たときの画面上のずれ」。奥のノーツは遠いぶん小さく見えるので、
    // カメラからの距離に比例して大きく動かし、見た目のずれを譜面の値にそろえる(本編のカメラは判定面の 7 手前)。
    public const float ViewDistance = 7f;

    public static float DepthScale(float noteZ)
        => float.IsNaN(noteZ) || float.IsInfinity(noteZ) ? 1f : Mathf.Max(1f, (noteZ + ViewDistance) / ViewDistance);

    public static Vector2 WorldOffset(float windX, float windY, float windSpin, float windSway,
        double remainingSeconds, double approachSeconds, double beat, float noteZ)
        => Offset(windX, windY, windSpin, windSway, remainingSeconds, approachSeconds, beat) * DepthScale(noteZ);

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
