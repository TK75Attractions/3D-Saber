using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class BeatNoteData
{
    public int x;
    public int y;
    public int startTick;
    public int type; // 0: Tap, 1: Flick, 2: Long
    public int direction; // 0:なし, 1:右...反時計回り
    public int duration; // Longの長さ(Tick)

    public int endTick => startTick + duration;
}

// 譜面データそのもの（難易度ごとのファイル用）
[System.Serializable]
public class ChartData
{
    public List<BeatNoteData> notes = new List<BeatNoteData>();
}

// 楽曲全体のメタ情報（info.json用）
[System.Serializable]
public class SongInfo
{
    public string songId = "New_Song";
    public string songTitle = "Untitled Song";
    public float bpm = 120f;
    public float offset = 0f;
    public int resolution = 4;
    public int beatsPerMeasure = 4;
}