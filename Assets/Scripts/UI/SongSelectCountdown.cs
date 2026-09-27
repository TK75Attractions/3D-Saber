using System;

// 時間切れは一度だけ通知。画面遷移中は経過時間に含めない。
public sealed class SongSelectCountdown
{
    public double Remaining { get; private set; } = 100;
    bool expired;
    public void Reset() { Remaining = 100; expired = false; }
    public bool Tick(double deltaSeconds, bool active)
    {
        if (!active || expired || double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds < 0) return false;
        Remaining = Math.Max(0, Remaining - deltaSeconds);
        if (Remaining > .000001) return false;
        Remaining = 0; expired = true; return true;
    }
}
