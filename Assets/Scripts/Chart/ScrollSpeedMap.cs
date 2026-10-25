using System;
using System.Collections.Generic;
using System.Linq;

// 緩急の裏譜面(logic!!! の「S」、2026-10-26)の流れる速さの表。
// 速さ v(t) を積み上げた「見かけの位置」X(t) = ∫v dt を持ち、ノーツの見かけの残り時間を X(届く時刻) − X(今) で出す。
// v は 0 以上なので X は減らず、ノーツどうしの追い越しは起きない。v が 0 の間は空中のノーツと小節線が全部止まり、
// v が 2 なら倍の速さで迫る。届く時刻(判定)は変わらない。
// 時刻は譜面の時刻(秒。offsetMs などのずらしは呼び出し側で引く)。最初の変わり目より前は 1 倍。
public sealed class ScrollSpeedMap
{
    // 読めなくなるほど速くしない(読み込みでもこの範囲に収める)。
    public const double MaxSpeed = 4;
    // カウントインの一言(W と同じ形)。着地の枠は実際の時刻で縮むので、止まって見えても枠で切れる。
    public const string RuleCaption = "止まったり走ったりする。着地の枠で切ろう";
    // 区間ごとの始まりの時刻・見かけの位置・速さと、区間の中の速さの傾き(1秒あたり)。最後の区間は傾き 0。
    readonly double[] start, position, speed, slope;

    ScrollSpeedMap(List<double> start, List<double> speed, List<double> slope)
    {
        this.start = start.ToArray();
        this.speed = speed.ToArray();
        this.slope = slope.ToArray();
        position = new double[this.start.Length];
        for (int i = 1; i < position.Length; i++)
        {
            double d = this.start[i] - this.start[i - 1];
            position[i] = position[i - 1] + this.speed[i - 1] * d + this.slope[i - 1] * d * d * .5;
        }
    }

    // 変わり目が1つも無ければ null(いつもの一定の速さ)。
    public static ScrollSpeedMap Build(IEnumerable<ScrollSpeedData> source)
    {
        var points = (source ?? Enumerable.Empty<ScrollSpeedData>()).Where(p => p != null && Finite(p.time)).OrderBy(p => p.time).ToList();
        if (points.Count == 0) return null;
        var start = new List<double>(); var speed = new List<double>(); var slope = new List<double>();
        double v = 1;
        for (int i = 0; i < points.Count; i++)
        {
            double t = points[i].time / 1000.0;
            double next = i + 1 < points.Count ? points[i + 1].time / 1000.0 : double.PositiveInfinity;
            // 同じ時刻の変わり目は、あとのものだけを使う。
            if (next <= t) continue;
            double target = Finite(points[i].speed) ? Math.Max(0, Math.Min(MaxSpeed, points[i].speed)) : 1;
            double ramp = Finite(points[i].ramp) ? Math.Max(0, points[i].ramp / 1000.0) : 0;
            if (ramp > 1e-6 && Math.Abs(target - v) > 1e-9)
            {
                double a = (target - v) / ramp, end = t + ramp;
                start.Add(t); speed.Add(v); slope.Add(a);
                // 次の変わり目が先に来たら、そこまで変えた速さから次を始める。
                if (end < next) { start.Add(end); speed.Add(target); slope.Add(0); v = target; }
                else v += a * (next - t);
            }
            else { start.Add(t); speed.Add(target); slope.Add(0); v = target; }
        }
        return start.Count > 0 ? new ScrollSpeedMap(start, speed, slope) : null;
    }

    public int SegmentCount => start.Length;

    // 時刻 t(秒)の見かけの位置(秒の単位。1 倍なら t と同じだけ進む)。
    public double PositionAt(double t)
    {
        if (!Finite(t)) return t;
        if (t < start[0]) return t - start[0];
        int i = SegmentAt(t);
        double d = t - start[i];
        return position[i] + speed[i] * d + slope[i] * d * d * .5;
    }

    // 時刻 t の速さ(倍)。
    public double SpeedAt(double t)
    {
        if (!Finite(t) || t < start[0]) return 1;
        int i = SegmentAt(t);
        return speed[i] + slope[i] * (t - start[i]);
    }

    // now(秒)から hit(秒)までの見かけの残り時間。
    public double Remaining(double now, double hit) => PositionAt(hit) - PositionAt(now);

    // 見かけの位置 x にはじめて着く時刻(止まっている間の位置なら、止まり始め)。最後が止まったままで着かないなら +∞。
    public double TimeAtPosition(double x)
    {
        if (!Finite(x)) return x;
        if (x < 0) return start[0] + x;
        // 終わりの位置が x 以上になる最初の区間を探す(最後の区間は止まっていなければ果てまで進む)。
        int lo = 0, hi = start.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (position[mid + 1] >= x) hi = mid; else lo = mid + 1;
        }
        double delta = Math.Max(0, x - position[lo]), v = speed[lo], a = slope[lo];
        if (delta <= 0) return start[lo];
        double root = Math.Sqrt(Math.Max(0, v * v + 2 * a * delta));
        if (v + root <= 1e-12) return double.PositiveInfinity;
        return start[lo] + 2 * delta / (v + root);
    }

    // start[i] <= t の最後の区間。
    int SegmentAt(double t)
    {
        int lo = 0, hi = start.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (start[mid] <= t) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
