using System;

// 時間切れは一度だけ通知。画面遷移中は経過時間に含めない。
public sealed class SongSelectCountdown
{
    // 曲選びの制限。10-30 本番の97回は中央値約20秒・9割が50秒以内・最長80秒だったので、100秒から60秒に詰めた(2026-10-30 ユーザー判断)。
    public const double LimitSeconds = 60;
    public double Remaining { get; private set; } = LimitSeconds;
    bool expired;
    public void Reset(double remaining = LimitSeconds)
    {
        Remaining = double.IsNaN(remaining) || double.IsInfinity(remaining) ? LimitSeconds : Math.Clamp(remaining, 0, LimitSeconds);
        expired = false;
    }
    public bool Tick(double deltaSeconds, bool active)
    {
        if (!active || expired || double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds < 0) return false;
        Remaining = Math.Max(0, Remaining - deltaSeconds);
        if (Remaining > .000001) return false;
        Remaining = 0; expired = true; return true;
    }
}
