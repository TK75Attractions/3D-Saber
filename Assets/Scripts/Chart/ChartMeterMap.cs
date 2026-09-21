using System;
using System.Collections.Generic;

// beat は BPM と同じ四分音符単位。変更点自身を新しい小節の先頭にする。
[Serializable]
public sealed class ChartTimeSignature
{
    public float beat;
    public int numerator = 4;
    public int denominator = 4;

    public ChartTimeSignature Clone() => new ChartTimeSignature
        { beat = beat, numerator = numerator, denominator = denominator };
}

// 本編と譜面エディターで小節境界・小節番号を共有する。
public sealed class ChartMeterMap
{
    public const double Epsilon = 0.00001;
    public readonly struct Position
    {
        public readonly int Measure, Beat, Numerator, Denominator;
        public readonly double BarStart, BarEnd, Fraction;
        public Position(int measure, int beat, int numerator, int denominator,
            double start, double end, double fraction)
        {
            Measure = measure; Beat = beat; Numerator = numerator; Denominator = denominator;
            BarStart = start; BarEnd = end; Fraction = fraction;
        }
    }

    readonly List<ChartTimeSignature> signatures;
    readonly List<int> firstMeasures = new List<int>();

    public ChartMeterMap(IEnumerable<ChartTimeSignature> source, int defaultNumerator = 4)
    {
        signatures = Normalize(source);
        if (signatures.Count == 0 || signatures[0].beat > 0)
            signatures.Insert(0, new ChartTimeSignature { numerator = Math.Max(1, Math.Min(64, defaultNumerator)) });
        firstMeasures.Add(1);
        for (int i = 1; i < signatures.Count; i++)
        {
            double distance = signatures[i].beat - signatures[i - 1].beat;
            int bars = (int)Math.Ceiling(distance / Length(signatures[i - 1]) - Epsilon);
            firstMeasures.Add(firstMeasures[i - 1] + Math.Max(1, bars));
        }
    }

    // 不正な変更点は無視。同じ位置では入力順で最後の指定を採用し、元のリストは変更しない。
    public static List<ChartTimeSignature> Normalize(IEnumerable<ChartTimeSignature> source)
    {
        var sorted = new SortedDictionary<float, ChartTimeSignature>();
        if (source != null)
            foreach (var item in source)
                if (item != null && !float.IsNaN(item.beat) && !float.IsInfinity(item.beat) && item.beat >= 0 &&
                    item.numerator >= 1 && item.numerator <= 64 && ValidDenominator(item.denominator))
                    sorted[item.beat] = item.Clone();
        return new List<ChartTimeSignature>(sorted.Values);
    }

    public static bool ValidDenominator(int value) => value >= 1 && value <= 64 && (value & (value - 1)) == 0;
    static double Length(ChartTimeSignature item) => item.numerator * 4.0 / item.denominator;

    public Position At(double beat)
    {
        if (double.IsNaN(beat) || double.IsInfinity(beat)) beat = 0;
        beat = Math.Max(0, beat);
        int index = 0;
        while (index + 1 < signatures.Count && signatures[index + 1].beat <= beat + Epsilon) index++;
        var signature = signatures[index];
        double length = Length(signature);
        int bar = Math.Max(0, (int)Math.Floor((beat - signature.beat + Epsilon) / length));
        double start = signature.beat + bar * length;
        double end = start + length;
        if (index + 1 < signatures.Count) end = Math.Min(end, signatures[index + 1].beat);
        double inside = Math.Max(0, beat - start) * signature.denominator / 4.0;
        int whole = (int)Math.Floor(inside + Epsilon);
        return new Position(firstMeasures[index] + bar, whole + 1, signature.numerator,
            signature.denominator, start, end, Math.Max(0, inside - whole));
    }

    public double AdjacentBar(double beat, bool forward)
    {
        var position = At(beat);
        if (forward) return position.BarEnd;
        if (beat > position.BarStart + Epsilon) return position.BarStart;
        return position.BarStart <= 0 ? 0 : At(position.BarStart - Epsilon * 4).BarStart;
    }

    public IEnumerable<double> BarStarts(double from, double through)
    {
        if (double.IsNaN(through) || double.IsInfinity(through) || through < 0) yield break;
        double beat = At(Math.Max(0, from)).BarStart;
        if (beat < from - Epsilon) beat = At(beat).BarEnd;
        while (beat <= through + Epsilon)
        {
            yield return beat;
            double next = At(beat).BarEnd;
            if (next <= beat + Epsilon) yield break;
            beat = next;
        }
    }
}
