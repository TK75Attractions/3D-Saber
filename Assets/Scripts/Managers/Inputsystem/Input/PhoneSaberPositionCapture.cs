using System;
using System.Collections.Generic;
using UnityEngine;

// 受信した全パケットの中点を採取する。フレームごとの最新値の重複採取はしない。
public sealed class PhoneSaberPositionCapture
{
    public const double Duration = 1.0;
    readonly object sync = new object();
    readonly List<Vector2> samples = new List<Vector2>(128);
    bool collecting;
    bool blue;
    double started;
    double firstReceived;
    double lastReceived;

    public void Begin(bool secondStick, double now)
    {
        lock (sync)
        {
            samples.Clear(); blue = secondStick; started = now; collecting = true;
            firstReceived = double.PositiveInfinity; lastReceived = double.NegativeInfinity;
        }
    }

    public void Cancel() { lock (sync) { collecting = false; samples.Clear(); } }

    public void Add(bool secondStick, Vector2 a, Vector2 b, double received)
    {
        lock (sync)
        {
            if (!collecting || secondStick != blue || received < started || received > started + Duration ||
                samples.Count >= 512 || !PhoneSaberPositionCalibration.IsFinite(a.x) ||
                !PhoneSaberPositionCalibration.IsFinite(a.y) || !PhoneSaberPositionCalibration.IsFinite(b.x) ||
                !PhoneSaberPositionCalibration.IsFinite(b.y) || (a - b).sqrMagnitude < 1) return;
            samples.Add((a + b) * 0.5f);
            firstReceived = Math.Min(firstReceived, received);
            lastReceived = Math.Max(lastReceived, received);
        }
    }

    public bool Finish(out Vector2 median, out string error)
    {
        lock (sync)
        {
            collecting = false;
            median = Vector2.zero;
            error = "有効な剣の受信が不足しています。送信・色を確認して再採取してください。";
            if (samples.Count < 10 || lastReceived - firstReceived < 0.7 || lastReceived < started + 0.8) return false;
            var xs = new float[samples.Count]; var ys = new float[samples.Count];
            for (int i = 0; i < samples.Count; i++) { xs[i] = samples[i].x; ys[i] = samples[i].y; }
            Array.Sort(xs); Array.Sort(ys);
            median = new Vector2(Median(xs), Median(ys));
            // 多数の点が動いている場合は、角の採取をやり直してもらう。
            int stable = 0;
            foreach (Vector2 p in samples) if ((p - median).sqrMagnitude <= 40 * 40) stable++;
            if (stable < Math.Ceiling(samples.Count * 0.8))
            {
                error = "剣が動いています。中点を指定の隅に合わせ、静止して再採取してください。";
                return false;
            }
            error = "";
            return true;
        }
    }

    static float Median(float[] values)
    {
        int mid = values.Length / 2;
        return values.Length % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) * 0.5f;
    }
}
