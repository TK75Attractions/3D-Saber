using System;
using System.Collections.Generic;
using System.Globalization;

// 選曲時に譜面の負荷を伝える。集計のために元ノーツの順番や時刻を書き換えない。
public sealed class SongChartInsights
{
    public static readonly SongChartInsights Empty = new SongChartInsights();
    public int Notes { get; private set; }
    public int Longs { get; private set; }
    public int Flicks { get; private set; }
    public int SimultaneousGroups { get; private set; }
    public double EndSeconds { get; private set; }
    public float Bpm { get; private set; }

    public static SongChartInsights From(ChartData chart)
    {
        var result = new SongChartInsights();
        if (chart?.notes == null) return result;
        result.Bpm = chart.bpm;
        var times = new List<double>();
        foreach (var note in chart.notes)
        {
            if (note == null) continue;
            double time = SongPreviewWindow.NoteTime(chart, note);
            if (double.IsNaN(time) || double.IsInfinity(time)) continue;
            result.Notes++;
            if (note.IsLong) result.Longs++;
            if (note.IsDirection) result.Flicks++;
            result.EndSeconds = Math.Max(result.EndSeconds, time + SongPreviewWindow.Linger(note));
            times.Add(time);
        }
        times.Sort();
        for (int start = 0; start < times.Count;)
        {
            int end = start + 1;
            while (end < times.Count && times[end] - times[start] <= NoteSpawner.SimultaneousEpsilonSeconds) end++;
            if (end - start > 1) result.SimultaneousGroups++;
            start = end;
        }
        return result;
    }

    public string Summary => Notes == 0 ? "遊べる譜面がありません"
        : "BPM " + (float.IsNaN(Bpm) || float.IsInfinity(Bpm) || Bpm <= 0 ? "--" : Bpm.ToString("0.#", CultureInfo.InvariantCulture))
          + "  /  " + Notes.ToString("N0") + " NOTES  /  譜面 " + Duration(EndSeconds);
    public string Techniques => Notes == 0 ? "別の難易度を選んでください"
        : "ロング " + Longs + "  /  フリック " + Flicks + "  /  同時 " + SimultaneousGroups + " 組";
    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return "0:00";
        long value = (long)Math.Min(359999, Math.Ceiling(seconds));
        return (value / 60).ToString(CultureInfo.InvariantCulture) + ":" + (value % 60).ToString("00", CultureInfo.InvariantCulture);
    }
}
