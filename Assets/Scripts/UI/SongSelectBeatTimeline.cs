using System;
using System.Collections.Generic;
using System.Linq;

// 音源時刻に対応する拍。ノーツの折れ線を使い、テンポ変化を固定BPMへ丸めない。
public sealed class SongSelectBeatTimeline
{
    public readonly struct BeatEvent
    {
        public readonly double Time;
        public readonly int Beat;
        public readonly bool Downbeat;
        public BeatEvent(double time, int beat, bool downbeat) { Time = time; Beat = beat; Downbeat = downbeat; }
    }
    readonly List<KeyValuePair<double, double>> points;
    readonly ChartMeterMap meter;
    public bool UsesNoteTiming { get; }

    public SongSelectBeatTimeline(IEnumerable<ChartData> source)
    {
        var charts = (source ?? Enumerable.Empty<ChartData>()).Where(c => c != null).ToList();
        var knots = new SortedDictionary<double, List<double>>();
        foreach (var chart in charts)
            foreach (var note in chart.notes ?? new List<NoteData>())
            {
                if (note == null || !Finite(note.beat) || !Finite(note.time) || !Finite(chart.offsetMs)) continue;
                double beat = Math.Round(note.beat, 3), time = SongPreviewWindow.NoteTime(chart, note);
                if (!knots.TryGetValue(beat, out var values)) knots[beat] = values = new List<double>();
                values.Add(time);
            }
        points = new List<KeyValuePair<double, double>>();
        foreach (var knot in knots)
        {
            double time = knot.Value.Average();
            // 不正な折り返しを捨て、時計が単調に進む組だけを残す。
            if (points.Count == 0 || time > points[points.Count - 1].Value + .000001)
                points.Add(new KeyValuePair<double, double>(knot.Key, time));
        }
        UsesNoteTiming = points.Count >= 2;
        if (!UsesNoteTiming)
        {
            var chart = charts.FirstOrDefault();
            double bpm = chart != null && Finite(chart.bpm) && chart.bpm > 0 ? chart.bpm : 120;
            double zero = chart != null && Finite(chart.beatZeroMs) && Finite(chart.offsetMs) ? (chart.beatZeroMs + chart.offsetMs) / 1000.0 : 0;
            points.Clear(); points.Add(new KeyValuePair<double, double>(0, zero));
            points.Add(new KeyValuePair<double, double>(1, zero + 60 / bpm));
        }
        var signatures = charts.FirstOrDefault(c => c.timeSignatures != null && c.timeSignatures.Count > 0)?.timeSignatures;
        meter = new ChartMeterMap(signatures);
    }
    public double TimeAt(double beat)
    {
        int i = 0;
        while (i < points.Count - 2 && points[i + 1].Key < beat) i++;
        var a = points[i]; var b = points[i + 1];
        return a.Value + (b.Value - a.Value) * (beat - a.Key) / (b.Key - a.Key);
    }
    double BeatAt(double time)
    {
        int i = 0;
        while (i < points.Count - 2 && points[i + 1].Value < time) i++;
        var a = points[i]; var b = points[i + 1];
        return a.Key + (b.Key - a.Key) * (time - a.Value) / (b.Value - a.Value);
    }
    public List<BeatEvent> Events(double from, double through)
    {
        var result = new List<BeatEvent>();
        if (!Finite(from) || !Finite(through) || through < from) return result;
        double first = Math.Ceiling(BeatAt(from) - .000001), last = Math.Floor(BeatAt(through) + .000001);
        if (first < int.MinValue || last > int.MaxValue || last - first > 100000) return result;
        for (int beat = (int)first; beat <= last; beat++)
        {
            double time = TimeAt(beat);
            var position = meter.At(beat);
            result.Add(new BeatEvent(time, beat, Math.Abs(position.BarStart - beat) < .001));
            if (beat == int.MaxValue) break;
        }
        return result;
    }
    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
