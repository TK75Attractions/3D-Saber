using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// 87A: 音源基準の区間を実際の再生範囲へ切り詰め、山場だけを細い帯で示す。
public sealed class SongProgressSections : MaskableGraphic
{
    StagePerformanceTimeline timeline;
    double rangeStart = double.NaN, rangeEnd = double.NaN;
    Vector2[] ranges = Array.Empty<Vector2>();

    internal void SetTimeline(StagePerformanceTimeline value, double start, double end)
    {
        timeline = value;
        rangeStart = rangeEnd = double.NaN;
        SetRange(start, end);
    }

    internal void SetRange(double start, double end)
    {
        if (rangeStart.Equals(start) && rangeEnd.Equals(end)) return;
        rangeStart = start; rangeEnd = end;
        ranges = RangesFor(timeline, start, end);
        SetVerticesDirty();
    }

    internal static Color MarkerColor(bool projector)
        => new Color(216f / 255f, 168f / 255f, 78f / 255f, projector ? 1f : .45f);

    internal void RefreshStyle() { color = MarkerColor(DisplaySettings.ProjectorMode); }

    // 重なりと接する区間を先に結合する。二重描画で色が濃くならない。
    internal static Vector2[] RangesFor(StagePerformanceTimeline value, double start, double end)
    {
        if (!Finite(start) || !Finite(end) || start < 0 || end <= start || value?.sections == null)
            return Array.Empty<Vector2>();
        var spans = new List<(double start, double end)>();
        foreach (var s in value.sections)
        {
            if (s == null || !Finite(s.startSeconds) || !Finite(s.endSeconds) || !Finite(s.intensity)
                || s.startSeconds < 0 || s.endSeconds <= s.startSeconds
                || s.intensity < StagePerformanceTimeline.StrongIntensity) continue;
            double a = Math.Max(start, s.startSeconds), b = Math.Min(end, s.endSeconds);
            if (b > a) spans.Add((a, b));
        }
        spans.Sort((a, b) => a.start.CompareTo(b.start));
        var result = new List<Vector2>();
        for (int i = 0; i < spans.Count; i++)
        {
            double a = spans[i].start, b = spans[i].end;
            while (i + 1 < spans.Count && spans[i + 1].start <= b)
                b = Math.Max(b, spans[++i].end);
            result.Add(new Vector2((float)((a - start) / (end - start)), (float)((b - start) / (end - start))));
        }
        return result.ToArray();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = rectTransform.rect;
        foreach (var span in ranges)
        {
            float left = Mathf.Lerp(r.xMin, r.xMax, span.x), right = Mathf.Lerp(r.xMin, r.xMax, span.y);
            int i = vh.currentVertCount;
            vh.AddVert(new Vector3(left, r.yMin, 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(left, r.yMax, 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(right, r.yMax, 0f), color, Vector2.zero);
            vh.AddVert(new Vector3(right, r.yMin, 0f), color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2); vh.AddTriangle(i, i + 2, i + 3);
        }
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
