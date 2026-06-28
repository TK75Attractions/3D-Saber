using System.Collections.Generic;
using UnityEngine;

// 本編(StreamingAssets/Songs/<id>/chart.json)が読む形式の DTO。
// JsonUtility はフィールド名でシリアライズするので、本編 NoteData/ChartData と同じキー名にすること。
// クラス名は本編側(SaberGame アセンブリ)の ChartData/NoteData と衝突しないよう Export* にしている。
[System.Serializable]
public class ExportNote
{
    public float beat;     // 本編では未使用（参考値）
    public float time;     // ミリ秒（本編が使う実タイミング）
    public float x;        // ワールド座標
    public float y;        // ワールド座標
    public string type = "tap";        // "tap" / "direction" / "long"
    public string color = "default";   // Note-Recorder に色概念がないため既定
    public string direction = "none";
    public int count = 1;              // ロングの必要カット数（tap/flick は 1）
}

[System.Serializable]
public class ExportChart
{
    public float bpm;
    public float coordScale = 1.0f;
    public float offsetMs = 0f;
    public List<ExportNote> notes = new List<ExportNote>();
}

// Note-Recorder の内部データ(ChartData/BeatNoteData) を本編形式へ変換する。
// 数値変換は ChartConvertMath（Unity非依存・テスト済み）に委譲し、ここは組み立てに徹する。
public static class ChartExporter
{
    public static ExportChart Build(
        ChartData source, float bpm, int resolution, float offsetSeconds,
        int gridSize, Vector2 xRange, Vector2 yRange, float longCutsPerBeat, bool flipY)
    {
        var outChart = new ExportChart
        {
            bpm = bpm,
            coordScale = 1.0f,
            offsetMs = ChartConvertMath.OffsetSecondsToMs(offsetSeconds),
        };

        if (source != null && source.notes != null)
        {
            foreach (var n in source.notes)
            {
                int gx = n.x;                                       // X は左右そのまま（左端=min）
                int gy = ChartConvertMath.ApplyFlip(n.y, gridSize, flipY);
                outChart.notes.Add(new ExportNote
                {
                    beat = ChartConvertMath.TickToBeat(n.startTick, resolution),
                    time = ChartConvertMath.TickToTimeMs(n.startTick, resolution, bpm),
                    x = ChartConvertMath.GridToWorld(gx, gridSize, xRange.x, xRange.y),
                    y = ChartConvertMath.GridToWorld(gy, gridSize, yRange.x, yRange.y),
                    type = ChartConvertMath.TypeToString(n.type),
                    color = "default",
                    direction = ChartConvertMath.DirectionToString(n.direction),
                    count = ChartConvertMath.LongCount(n.type, n.duration, resolution, longCutsPerBeat),
                });
            }
        }
        return outChart;
    }

    public static string ToJson(ExportChart chart) => JsonUtility.ToJson(chart, true);
}
