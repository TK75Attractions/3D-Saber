using System;
using System.Collections.Generic;

public enum LargeFlashKind { Full, Edge }
public enum LargeFlashTint { White, Gold, Cyan }

// 全画面と縁の光が共有する唯一の受付。時刻は呼び出し側から渡す。
// 両端を含む1秒でも3回以下にし、巻き戻った時計では受付を増やさない。
public sealed class LargeFlashBudget
{
    readonly Queue<double> accepted = new Queue<double>();
    double lastTime = double.NegativeInfinity;
    public int AcceptedCount { get; private set; }

    public bool TryTake(double time)
    {
        if (double.IsNaN(time) || double.IsInfinity(time) || time < lastTime) return false;
        lastTime = time;
        while (accepted.Count > 0 && time - accepted.Peek() > 1.0) accepted.Dequeue();
        if (accepted.Count >= 3) return false;
        accepted.Enqueue(time);
        AcceptedCount++;
        return true;
    }
}

public readonly struct FlashRect
{
    public readonly double Left, Bottom, Right, Top;
    public double Area => Math.Max(0, Right - Left) * Math.Max(0, Top - Bottom);

    public FlashRect(double left, double bottom, double right, double top)
    {
        Left = left; Bottom = bottom; Right = right; Top = top;
    }

    public FlashRect Inflate(double x, double y) => new FlashRect(Left - x, Bottom - y, Right + x, Top + y);

    public static FlashRect Intersection(FlashRect a, FlashRect b)
        => new FlashRect(Math.Max(a.Left, b.Left), Math.Max(a.Bottom, b.Bottom),
            Math.Min(a.Right, b.Right), Math.Min(a.Top, b.Top));
}

// カメラの表示領域を0..1として計算する。返す矩形同士は重ならない。
public static class LargeFlashLayout
{
    public const double EdgeFraction = .08;
    public static FlashRect ProtectedGate(FlashRect gate) => gate.Inflate(24.0 / 1920, 24.0 / 1080);

    public static void Build(LargeFlashKind kind, FlashRect protectedGate, List<FlashRect> result)
    {
        result.Clear();
        if (kind == LargeFlashKind.Full)
        {
            Subtract(new FlashRect(0, 0, 1, 1), protectedGate, result);
            return;
        }
        double e = EdgeFraction;
        Subtract(new FlashRect(0, 0, 1, e), protectedGate, result);
        Subtract(new FlashRect(0, 1 - e, 1, 1), protectedGate, result);
        Subtract(new FlashRect(0, e, e, 1 - e), protectedGate, result);
        Subtract(new FlashRect(1 - e, e, 1, 1 - e), protectedGate, result);
    }

    static void Subtract(FlashRect source, FlashRect hole, List<FlashRect> result)
    {
        var cut = FlashRect.Intersection(source, hole);
        if (cut.Area <= 0) { Add(source, result); return; }
        Add(new FlashRect(source.Left, source.Bottom, source.Right, cut.Bottom), result);
        Add(new FlashRect(source.Left, cut.Top, source.Right, source.Top), result);
        Add(new FlashRect(source.Left, cut.Bottom, cut.Left, cut.Top), result);
        Add(new FlashRect(cut.Right, cut.Bottom, source.Right, cut.Top), result);
    }

    static void Add(FlashRect rect, List<FlashRect> result)
    {
        if (rect.Area > 0) result.Add(rect);
    }
}
