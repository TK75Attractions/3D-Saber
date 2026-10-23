using System;
using System.Collections.Generic;
using UnityEngine;

// 受信した全パケットの中点を採取する。フレームごとの最新値の重複採取はしない。
public sealed class PhoneSaberPositionCapture
{
    public const double Duration = 1.0;
    readonly object sync = new object();
    readonly List<Vector2> samples = new List<Vector2>(128);
    readonly List<Vector2> endpoints = new List<Vector2>(256);
    bool collecting;
    bool blue;
    double started;
    double firstReceived;
    double lastReceived;

    public void Begin(bool secondStick, double now)
    {
        lock (sync)
        {
            samples.Clear(); endpoints.Clear(); blue = secondStick; started = now; collecting = true;
            firstReceived = double.PositiveInfinity; lastReceived = double.NegativeInfinity;
        }
    }

    public void Cancel() { lock (sync) { collecting = false; samples.Clear(); endpoints.Clear(); } }

    public void Add(bool secondStick, Vector2 a, Vector2 b, double received, float width = 1920, float height = 1080)
    {
        lock (sync)
        {
            if (!collecting || secondStick != blue || received < started || received > started + Duration ||
                samples.Count >= 512 || !PhoneSaberPositionCalibration.IsFinite(a.x) ||
                !PhoneSaberPositionCalibration.IsFinite(a.y) || !PhoneSaberPositionCalibration.IsFinite(b.x) ||
                !PhoneSaberPositionCalibration.IsFinite(b.y) || (a - b).sqrMagnitude < 1) return;
            samples.Add((a + b) * 0.5f);
            // iPhone の送信ピクセル座標を、補正・感度を通さずカメラ 0..1 に戻す。
            endpoints.Add(new Vector2(a.x / (width - 1), a.y / (height - 1)));
            endpoints.Add(new Vector2(b.x / (width - 1), b.y / (height - 1)));
            firstReceived = Math.Min(firstReceived, received);
            lastReceived = Math.Max(lastReceived, received);
        }
    }

    public bool Finish(out Vector2 median, out string error) => Finish(out median, out _, out error);

    public bool Finish(out Vector2 median, out Rect cameraBounds, out string error)
    {
        lock (sync)
        {
            collecting = false;
            median = Vector2.zero;
            cameraBounds = default;
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
            // 中点の安定判定で採用した同じパケットの両端を囲む。孤立した誤検出は含めない。
            Vector2 low = Vector2.one * float.PositiveInfinity, high = Vector2.one * float.NegativeInfinity;
            for (int i = 0; i < samples.Count; i++)
            {
                if ((samples[i] - median).sqrMagnitude > 40 * 40) continue;
                for (int end = 0; end < 2; end++)
                {
                    low = Vector2.Min(low, endpoints[2 * i + end]);
                    high = Vector2.Max(high, endpoints[2 * i + end]);
                }
            }
            cameraBounds = Rect.MinMaxRect(low.x, low.y, high.x, high.y);
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
