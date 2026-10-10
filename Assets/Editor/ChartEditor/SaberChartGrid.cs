using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>テンポ地図の点。拍と、その拍の譜面時刻(ms)の対応。</summary>
    [Serializable]
    public sealed class SaberChartTempoPoint
    {
        public float beat;
        public float timeMs;
    }

    /// <summary>
    /// 編集用の格子(拍と譜面時刻の対応)。テンポ地図が無ければ BPM と原点の一定テンポ、
    /// 点が1つなら BPM のまま点の位置へ合わせ、2つ以上なら点の間を直線で結ぶ(osu! の赤線・.chart のアンカーと同じ考え方)。
    /// 両端の外側は、いちばん近い区間のテンポを延ばす。本編の小節線・カウントインは使わない(エディターの格子だけ)。
    /// </summary>
    public sealed class SaberChartGrid
    {
        private readonly float bpm;
        private readonly float originMs;
        private readonly List<SaberChartTempoPoint> points;

        public SaberChartGrid(float bpm, float originMs, IEnumerable<SaberChartTempoPoint> tempoMap)
        {
            this.bpm = float.IsNaN(bpm) || float.IsInfinity(bpm) || bpm <= 0f ? 120f : bpm;
            this.originMs = float.IsNaN(originMs) || float.IsInfinity(originMs) ? 0f : originMs;
            points = Clean(tempoMap);
        }

        public bool UsesTempoMap => points.Count > 0;
        public IReadOnlyList<SaberChartTempoPoint> Points => points;

        /// <summary>拍の位置の譜面時刻(ms)。負の拍も扱う(相対移動の計算用)。</summary>
        public float TimeAt(float beat)
        {
            if (points.Count == 0) return originMs + beat * 60000f / bpm;
            if (points.Count == 1) return points[0].timeMs + (beat - points[0].beat) * 60000f / bpm;
            int index = SegmentForBeat(beat);
            SaberChartTempoPoint a = points[index], b = points[index + 1];
            return a.timeMs + (beat - a.beat) * (b.timeMs - a.timeMs) / (b.beat - a.beat);
        }

        /// <summary>譜面時刻(ms)の拍。曲頭より前は負になる。</summary>
        public float BeatAt(float timeMs)
        {
            if (points.Count == 0) return (timeMs - originMs) * bpm / 60000f;
            if (points.Count == 1) return points[0].beat + (timeMs - points[0].timeMs) * bpm / 60000f;
            int index = SegmentForTime(timeMs);
            SaberChartTempoPoint a = points[index], b = points[index + 1];
            return a.beat + (timeMs - a.timeMs) * (b.beat - a.beat) / (b.timeMs - a.timeMs);
        }

        /// <summary>その拍の区間のテンポ(BPM)。</summary>
        public float TempoAt(float beat)
        {
            if (points.Count < 2) return bpm;
            int index = SegmentForBeat(beat);
            SaberChartTempoPoint a = points[index], b = points[index + 1];
            return (b.beat - a.beat) * 60000f / (b.timeMs - a.timeMs);
        }

        private int SegmentForBeat(float beat)
        {
            int index = 0;
            while (index < points.Count - 2 && points[index + 1].beat <= beat) index++;
            return index;
        }

        private int SegmentForTime(float timeMs)
        {
            int index = 0;
            while (index < points.Count - 2 && points[index + 1].timeMs <= timeMs) index++;
            return index;
        }

        /// <summary>不正な点を除き、拍の順に並べる。拍も時刻も前の点より進んでいる点だけを残す。</summary>
        public static List<SaberChartTempoPoint> Clean(IEnumerable<SaberChartTempoPoint> source)
        {
            var result = new List<SaberChartTempoPoint>();
            if (source == null) return result;
            foreach (SaberChartTempoPoint point in source
                         .Where(p => p != null && Finite(p.beat) && Finite(p.timeMs))
                         .OrderBy(p => p.beat))
            {
                if (result.Count > 0)
                {
                    SaberChartTempoPoint last = result[result.Count - 1];
                    if (point.beat <= last.beat + .0001f || point.timeMs <= last.timeMs + .01f) continue;
                }
                result.Add(new SaberChartTempoPoint { beat = point.beat, timeMs = point.timeMs });
            }
            return result;
        }

        /// <summary>
        /// ノーツの拍の値と時刻からテンポ地図を作る。同じ拍は時刻を平均し、直線に乗る点はまとめる
        /// (許容 toleranceMs)。テンポの変わる曲の拍の値(校歌など)から、格子を起こすのに使う。
        /// </summary>
        public static List<SaberChartTempoPoint> FromNotes(IEnumerable<SaberChartNote> notes, float toleranceMs = 3f)
        {
            var knots = new SortedDictionary<double, List<float>>();
            if (notes != null)
                foreach (SaberChartNote note in notes)
                {
                    if (note == null || !Finite(note.beat) || !Finite(note.time)) continue;
                    double beat = Math.Round(note.beat, 3);
                    if (!knots.TryGetValue(beat, out var times)) knots[beat] = times = new List<float>();
                    times.Add(note.time);
                }
            var raw = Clean(knots.Select(knot => new SaberChartTempoPoint { beat = (float)knot.Key, timeMs = knot.Value.Average() }));
            if (raw.Count <= 2) return raw;
            var keep = new bool[raw.Count];
            keep[0] = keep[raw.Count - 1] = true;
            Simplify(raw, 0, raw.Count - 1, Mathf.Max(.1f, toleranceMs), keep);
            return raw.Where((point, index) => keep[index]).ToList();
        }

        // Ramer–Douglas–Peucker: 端点を結ぶ直線から最も離れた点が許容を超えれば残して分割する。
        private static void Simplify(List<SaberChartTempoPoint> points, int first, int last, float tolerance, bool[] keep)
        {
            if (last <= first + 1) return;
            SaberChartTempoPoint a = points[first], b = points[last];
            float slope = (b.timeMs - a.timeMs) / (b.beat - a.beat);
            int farthest = -1;
            float distance = tolerance;
            for (int i = first + 1; i < last; i++)
            {
                float expected = a.timeMs + (points[i].beat - a.beat) * slope;
                float error = Mathf.Abs(points[i].timeMs - expected);
                if (error <= distance) continue;
                distance = error;
                farthest = i;
            }
            if (farthest < 0) return;
            keep[farthest] = true;
            Simplify(points, first, farthest, tolerance, keep);
            Simplify(points, farthest, last, tolerance, keep);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
