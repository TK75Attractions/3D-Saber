using System;
using System.Collections.Generic;

// chart.json のスキーマ。
// 例: { "bpm": 188.0, "notes": [ { "beat":5.0, "time":1595.74, "x":-0.66, "y":-1.76, "type":"tap", "color":"default", "direction":"none" } ] }
[Serializable]
public class ChartData
{
    public float bpm;
    public List<NoteData> notes = new List<NoteData>();
}

[Serializable]
public class NoteData
{
    public float beat;
    // chart.json の time はミリ秒。ゲーム中は秒で扱いたいので TimeSeconds で取得する。
    public float time;
    public float x;
    public float y;
    public string type = "tap";
    public string color = "default";
    public string direction = "none";

    public double TimeSeconds => time / 1000.0;
}
