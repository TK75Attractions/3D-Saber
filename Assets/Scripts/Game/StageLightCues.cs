using System;
using System.Collections.Generic;
using UnityEngine;

// 固定BPMや4拍子を仮定せず、譜面の実時刻から照明だけのキューを作る。
// 判定調整値を焼き込まず、元の譜面にも書き戻さない。
public sealed class StageLightCues
{
    readonly double[] times;
    public int Count => times.Length;

    public StageLightCues(ChartData chart)
    {
        var sorted = new List<double>();
        double offset = chart != null && Finite(chart.offsetMs) ? chart.offsetMs / 1000.0 : 0;
        if (chart?.notes != null)
            foreach (var note in chart.notes)
                if (note != null && Finite(note.time) && note.TimeSeconds + offset >= 0)
                    sorted.Add(note.TimeSeconds + offset);
        sorted.Sort();
        var selected = new List<double>();
        foreach (double time in sorted)
            // 同時押しを統合し、高密度区間でも照明の点滅を増やし過ぎない。
            if (selected.Count == 0 || time - selected[selected.Count - 1] >= .18) selected.Add(time);
        times = selected.ToArray();
    }

    public float Evaluate(double seconds)
    {
        if (!Finite(seconds) || seconds < 0 || times.Length == 0) return 0;
        int index = Array.BinarySearch(times, seconds);
        if (index < 0) index = ~index - 1;
        if (index < 0) return 0;
        double age = seconds - times[index];
        return age >= .5 ? 0 : Mathf.Exp((float)-age * 9f) * (1 - (float)age * 2);
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
