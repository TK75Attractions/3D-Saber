using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// U47 流星。経路があれば折れ線、空なら従来の終点へ等速で滑る。
public static class LongSlideRules
{
    public const float MinX = -3f, MaxX = 3f, MinY = -.95f, MaxY = 1.35f;
    public const float MaxStepPerCut = .4f;
    public const int MaxPathPoints = 512;
    public const int MaxPathCracks = 16;

    public static bool Enabled(NoteData note) => note != null && note.slide
        && note.count > 1 && Finite(note.lengthMs) && note.lengthMs > 0
        && ((note.slidePath != null && note.slidePath.Count > 0)
            || (Finite(note.slideToX) && Finite(note.slideToY)));

    // 経路の時刻は到着からのミリ秒。0はノーツ自身の着地点とし、瞬間移動を作らない。
    // 同時刻は後に書いた点を採用する。多すぎる場合も最後の点を残す。
    public static List<SlidePathPoint> NormalizePath(
        IEnumerable<SlidePathPoint> path, float lengthMs, float scale)
    {
        var sorted = new SortedDictionary<float, SlidePathPoint>();
        if (!Finite(lengthMs) || lengthMs <= 0) return new List<SlidePathPoint>();
        if (!Finite(scale) || scale <= 0) scale = 1;
        if (path != null)
            foreach (var p in path)
            {
                if (p == null || !Finite(p.ms) || !Finite(p.x) || !Finite(p.y)) continue;
                float ms = Math.Max(0, Math.Min(lengthMs, p.ms));
                if (ms <= 0) continue;
                sorted[ms] = new SlidePathPoint
                {
                    ms = ms,
                    x = ClampCoordinate(p.x, scale, MinX, MaxX),
                    y = ClampCoordinate(p.y, scale, MinY, MaxY)
                };
            }
        var points = sorted.Values.ToList();
        if (points.Count <= MaxPathPoints) return points;
        var limited = new List<SlidePathPoint>(MaxPathPoints);
        for (int i = 0; i < MaxPathPoints; i++)
            limited.Add(points[(int)((long)i * (points.Count - 1) / (MaxPathPoints - 1))]);
        return limited;
    }

    public static void Normalize(NoteData note, float scale)
    {
        if (note == null) return;
        if (!Finite(scale) || scale <= 0) scale = 1;
        note.slidePath = NormalizePath(note.slidePath, note.lengthMs, scale);
        if (!Enabled(note) || !Finite(note.x) || !Finite(note.y))
        {
            note.slide = false;
            note.slideToX = note.slideToY = 0f;
            note.slidePath.Clear();
            return;
        }
        note.x = ClampCoordinate(note.x, scale, MinX, MaxX);
        note.y = ClampCoordinate(note.y, scale, MinY, MaxY);
        if (note.slidePath.Count > 0)
        {
            var last = note.slidePath[note.slidePath.Count - 1];
            note.slideToX = last.x;
            note.slideToY = last.y;
        }
        else
        {
            note.slideToX = ClampCoordinate(note.slideToX, scale, MinX, MaxX);
            note.slideToY = ClampCoordinate(note.slideToY, scale, MinY, MaxY);
        }
    }

    public static Vector2 Position(Vector2 start, Vector2 end, double elapsed, double duration)
    {
        if (!Finite(elapsed) || !Finite(duration) || duration <= 0 || elapsed <= 0)
            return start;
        if (elapsed >= duration) return end;
        return Vector2.LerpUnclamped(start, end, (float)(elapsed / duration));
    }

    // Normalize済みの経路を曲時計で引く。毎コマの加算や残り回数には依存しない。
    public static Vector2 Position(Vector2 start, Vector2 end,
        IReadOnlyList<SlidePathPoint> path, float scale, double elapsed, double duration)
    {
        if (path == null || path.Count == 0) return Position(start, end, elapsed, duration);
        if (!Finite(elapsed) || !Finite(duration) || duration <= 0 || elapsed <= 0) return start;
        double ms = Math.Min(elapsed, duration) * 1000.0;
        int lo = 0, hi = path.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (path[mid].ms < ms) lo = mid + 1;
            else hi = mid;
        }
        if (lo == path.Count)
        {
            var last = path[path.Count - 1];
            return new Vector2(last.x, last.y) * scale;
        }
        var right = path[lo];
        var left = lo == 0 ? null : path[lo - 1];
        double fromMs = left == null ? 0 : left.ms;
        Vector2 from = left == null ? start : new Vector2(left.x, left.y) * scale;
        Vector2 to = new Vector2(right.x, right.y) * scale;
        return Vector2.LerpUnclamped(from, to, (float)((ms - fromMs) / (right.ms - fromMs)));
    }

    static float ClampCoordinate(float value, float scale, float min, float max) =>
        (float)(Math.Max(min, Math.Min(max, (double)value * scale)) / scale);

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
