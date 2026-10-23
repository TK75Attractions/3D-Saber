using System;
using UnityEngine;

// 小窓の座標と案内。端の判定も描画も同じ向きの座標を使う。
public static class PlayRecoveryRules
{
    public const int Left = 1, Right = 2, Bottom = 4, Top = 8;

    public static Vector2 Display01(Vector2 raw, bool flipX, bool flipY) =>
        new Vector2(Mathf.Clamp01(flipX ? 1 - raw.x : raw.x), Mathf.Clamp01(flipY ? 1 - raw.y : raw.y));

    public static float PointAlpha(double now, double receivedAt) =>
        receivedAt < 0 ? 0 : now - receivedAt > .25 ? .3f : 1f;

    public static int Edges(Vector2 p) =>
        (p.x <= .1f ? Left : 0) | (p.x >= .9f ? Right : 0) |
        (p.y <= .1f ? Bottom : 0) | (p.y >= .9f ? Top : 0);

    // 角は両辺に三角を出し、文は近い方の辺について1行だけ出す。
    public static string Hint(Vector2 p)
    {
        int edges = Edges(p);
        if (edges == 0) return "";
        bool horizontal = Mathf.Min(p.x, 1 - p.x) <= Mathf.Min(p.y, 1 - p.y);
        if (horizontal)
            return p.x < .5f ? "最後は左の端でした。少し右へ" : "最後は右の端でした。少し左へ";
        return p.y < .5f ? "最後は下の端でした。少し上へ" : "最後は上の端でした。少し下へ";
    }

    public static bool Inside(Vector2 midpoint, Vector2 center, float diameter) =>
        diameter > 0 && (midpoint - center).sqrMagnitude <= diameter * diameter * .25f;
}

// 実時間で構え直しを測る。bit 0=赤、bit 1=青。未受信の色は required に含めない。
public sealed class PlayResumePose
{
    public const double HoldSeconds = .3, FallbackSeconds = 5;
    double started = double.NaN, held = double.NaN, lastStep = double.NaN;
    int required;
    public bool Active => !double.IsNaN(started);
    public bool Held { get; private set; }
    public bool TimedOut { get; private set; }

    public void Reset()
    {
        started = held = lastStep = double.NaN;
        required = 0; Held = TimedOut = false;
    }

    public void Step(double now, bool waiting, int needed, int fresh, int inside)
    {
        if (!waiting || needed == 0 || fresh == 0) { Reset(); return; }
        if (!Active || needed != required) { Reset(); started = now; required = needed; }
        bool together = (fresh & needed) == needed && (inside & needed) == needed;
        if (!together || (!double.IsNaN(lastStep) && now - lastStep > PlayTrackingWatch.MaxGapSeconds))
            held = double.NaN;
        if (together && double.IsNaN(held)) held = now;
        Held = together && now - held >= HoldSeconds - 1e-9;
        TimedOut = now - started >= FallbackSeconds;
        lastStep = now;
    }
}

// 再開後だけ、音が鳴り始めるまでの残り時間を表示する。曲の途中から鳴らす場合も同じ。
public sealed class ResumedSongWait
{
    double duration;
    bool active;
    int shown = -1;
    public string Caption { get; private set; } = "";

    public void Begin(double songTime, double playStart)
    {
        duration = Math.Max(0, playStart - songTime);
        active = duration > 0; shown = -1;
    }

    public bool Read(double songTime, double playStart, bool paused, out int number, out float progress)
    {
        number = 0; progress = 0;
        double remaining = playStart - songTime;
        if (!active) return false;
        if (remaining <= 0) { active = false; return false; }
        if (paused) return false;
        number = Math.Max(1, (int)Math.Ceiling(remaining));
        progress = Mathf.Clamp01((float)(1 - remaining / duration));
        // 秒が変わったときだけ文字列を作る。
        if (shown != number) { shown = number; Caption = "曲が始まるまで " + number; }
        return true;
    }
}
