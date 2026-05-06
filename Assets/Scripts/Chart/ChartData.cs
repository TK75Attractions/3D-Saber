using System;
using System.Collections.Generic;

// chart.json のスキーマ。
// 例: { "bpm": 188.0, "notes": [ { "beat":5.0, "time":1595.74, "x":-0.66, "y":-1.76, "type":"tap", "color":"default", "direction":"none" } ] }
[Serializable]
public class ChartData
{
    public float bpm;
    // 譜面ソフトの座標 → ワールド座標へのスケール（既定 1.0：既に world 単位）。
    // 譜面ソフトがピクセルで吐く場合は 1920 板なら 11/1920≒0.00573 など。
    public float coordScale = 1.0f;
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
    public string type = "tap";   // "tap" / "direction" / "long"
    public string color = "default";
    public string direction = "none";
    public int count = 1;          // long 用の必要切断回数（tap は 1）

    public double TimeSeconds => time / 1000.0;

    public bool IsLong => count > 1 || (type != null && type.ToLowerInvariant() == "long");
    public bool IsDirection => !string.IsNullOrEmpty(direction) && direction.ToLowerInvariant() != "none";
}
