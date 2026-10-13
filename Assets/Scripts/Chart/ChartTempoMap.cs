using System;
using System.Collections.Generic;
using System.Linq;

[Serializable]
public sealed class ChartTempoPoint
{
    public float beat;
    public float timeMs;
}

// editorTempoMap は拍と譜面時刻の対応。OFFSET は呼び出し側で一度だけ加える。
// 既存の一定テンポ譜面は BPM と beatZeroMs の時計をそのまま使う。
public sealed class ChartTempoMap
{
    readonly List<ChartTempoPoint> points = new List<ChartTempoPoint>();
    readonly double bpm, origin;
    public IReadOnlyList<ChartTempoPoint> Points => points;

    public ChartTempoMap(double bpm, double originMs, IEnumerable<ChartTempoPoint> source)
    {
        this.bpm = Finite(bpm) && bpm > 0 ? bpm : 120;
        origin = Finite(originMs) ? originMs : 0;
        if (source == null) return;
        foreach (var point in source.Where(p => p != null && Finite(p.beat) && Finite(p.timeMs)).OrderBy(p => p.beat))
        {
            if (points.Count > 0)
            {
                var last = points[points.Count - 1];
                if (point.beat <= last.beat + .0001 || point.timeMs <= last.timeMs + .01) continue;
            }
            points.Add(new ChartTempoPoint { beat = point.beat, timeMs = point.timeMs });
        }
    }

    public double TimeAt(double beat)
    {
        if (points.Count == 0) return origin + beat * 60000 / bpm;
        if (points.Count == 1) return points[0].timeMs + (beat - points[0].beat) * 60000 / bpm;
        int i = 0;
        while (i < points.Count - 2 && points[i + 1].beat <= beat) i++;
        var a = points[i]; var b = points[i + 1];
        return a.timeMs + (beat - a.beat) * (b.timeMs - a.timeMs) / (b.beat - a.beat);
    }

    public double BeatAt(double timeMs)
    {
        if (points.Count == 0) return (timeMs - origin) * bpm / 60000;
        if (points.Count == 1) return points[0].beat + (timeMs - points[0].timeMs) * bpm / 60000;
        int i = 0;
        while (i < points.Count - 2 && points[i + 1].timeMs <= timeMs) i++;
        var a = points[i]; var b = points[i + 1];
        return a.beat + (timeMs - a.timeMs) * (b.beat - a.beat) / (b.timeMs - a.timeMs);
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
