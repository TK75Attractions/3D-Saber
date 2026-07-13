using System.Collections.Generic;
using UnityEngine;

// 譜面の中身からプレイ難易度(1〜10)を推定する。曲選択画面の難易度数値表示用。
// 指標: 平均密度 / ピーク密度(2秒窓) / 方向・同時押し・ロングの割合。
// 重みは ElDorado の3譜面が体感(easy≈3 / hard≈8)に合うよう調整した経験則。
// 空譜面(未制作)は 0 を返し、UI側は数値を表示しない。
public static class ChartDifficultyRater
{
    // 同時押しとみなす時刻差(秒)。NoteSpawner.SimultaneousEpsilonSeconds と同義。
    private const double SimultaneousEpsilon = 0.01;

    public static int Rate(ChartData chart)
    {
        if (chart == null || chart.notes == null || chart.notes.Count == 0) return 0;

        var times = new List<double>(chart.notes.Count);
        int directions = 0;
        int longs = 0;
        foreach (NoteData n in chart.notes)
        {
            if (n == null) continue;
            times.Add(n.TimeSeconds);
            if (n.IsDirection) directions++;
            if (n.IsLong) longs++;
        }
        if (times.Count == 0) return 0;
        times.Sort();

        // 短すぎる譜面で平均密度が暴れないように最低30秒とみなす
        double duration = System.Math.Max(30.0, times[times.Count - 1] - times[0]);
        double avgNps = times.Count / duration;

        // ピーク密度: 2秒スライド窓の最大ノーツ数 ÷ 2秒
        double peakNps = 0.0;
        int lo = 0;
        for (int hi = 0; hi < times.Count; hi++)
        {
            while (times[hi] - times[lo] > 2.0) lo++;
            double nps = (hi - lo + 1) / 2.0;
            if (nps > peakNps) peakNps = nps;
        }

        int simultaneous = 0;
        for (int i = 1; i < times.Count; i++)
        {
            if (times[i] - times[i - 1] <= SimultaneousEpsilon) simultaneous++;
        }

        double count = times.Count;
        double score = 1.0
            + avgNps * 1.1
            + peakNps * 0.7
            + (directions / count) * 3.0
            + (simultaneous / count) * 4.0
            + (longs / count) * 1.5;

        return Mathf.Clamp(Mathf.RoundToInt((float)score), 1, 10);
    }
}
