using System;
using System.Collections.Generic;

// Manic Sonic の裏譜面「R(反転)」(2026-10-28)。反転を曲の流れに合わせて重ねる。
//   ・鏡門(U105): サビで奥に門が立ち、門を通ったノーツが左右反対の側へ渡る(gates、ノーツの mirror)。
//   ・天地返し(U103): BPM60 の頭で画面の絵が 180° 回り、最後に一回転して戻る(turns・creaks)。
// 時刻はどれもミリ秒(ノーツの time と同じ時計。offsetMs を足して使う)。決まりと数値は ReverseRules。
// ふつうの譜面では enabled が false のまま(JsonUtility は無い項目を空の入れ物にするので、F と同じく enabled で有無を決める)。
[Serializable]
public class ReverseChartData
{
    public bool enabled;
    // 鏡門。time に門が奥で止まり、endTime に割れる。on の間だけ鏡(それ以外は素通し)。
    public List<ReverseGateData> gates = new List<ReverseGateData>();
    // 天地返し。time〜endTime に、前の角度から degrees(通算の角度)へなめらかに回す。
    public List<ReverseTurnData> turns = new List<ReverseTurnData>();
    // 回る前ぶりのきしみ(± degrees だけ傾いて 0.25 秒で戻る)。
    public List<ReverseCreakData> creaks = new List<ReverseCreakData>();
}

// 鏡門。time に止まり(その接近時間 − 0.5 秒前から奥に見える)、endTime に割れる。
[Serializable]
public class ReverseGateData
{
    public float time;
    public float endTime;
    public List<ReverseSpanData> on = new List<ReverseSpanData>();
}

[Serializable]
public class ReverseSpanData
{
    public float time;
    public float endTime;
}

[Serializable]
public class ReverseTurnData
{
    public float time;
    public float endTime;
    // 終わりの角度(通算。180 で逆さ、360 で一回転して元の向き)。
    public float degrees;
}

[Serializable]
public class ReverseCreakData
{
    public float time;
    public float degrees;
}
