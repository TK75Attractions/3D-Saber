using System.Collections.Generic;
using System.Threading;

// ノーツごとの向き違い通知。抑制した振りも記録し、同じ振りを時間差で通知し直させない。
public sealed class WrongDirectionCueGate
{
    public const double CooldownSeconds = .25;
    static long sequence;
    public static ulong NewSwing() => (ulong)Interlocked.Increment(ref sequence);

    readonly HashSet<ulong> seen = new HashSet<ulong>(16);
    double lastSignal = double.NegativeInfinity;

    public bool TrySignal(ulong swingId, double now)
    {
        if (swingId == 0 || double.IsNaN(now) || double.IsInfinity(now)) return false;
        if (!seen.Add(swingId) || now - lastSignal < CooldownSeconds) return false;
        lastSignal = now;
        return true;
    }

    public void Reset()
    {
        seen.Clear();
        lastSignal = double.NegativeInfinity;
    }
}
