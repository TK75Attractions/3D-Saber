using System;

// 譜面エディタ(Note-Recorder)の内部数値を、本編が読む chart.json の数値へ変換する純ロジック。
// UnityEngine に依存しない（dotnet 単体でユニットテストできるようにするため意図的に分離している）。
public static class ChartConvertMath
{
    // startTick(整数Tick) を拍に直す。beat = startTick / resolution。
    public static float TickToBeat(int startTick, int resolution)
    {
        if (resolution <= 0) resolution = 1;
        return startTick / (float)resolution;
    }

    // startTick(整数Tick) → 本編の time(ミリ秒)。time = beat * 60000 / bpm。
    // 本編はこの time(ms) を実タイミングとして使う（beat は未使用の飾り）。
    public static float TickToTimeMs(int startTick, int resolution, float bpm)
    {
        if (bpm <= 0f) bpm = 120f;
        return TickToBeat(startTick, resolution) * 60000f / bpm;
    }

    // エディタの offset(秒) → 本編の offsetMs。意味（全ノーツを後ろにずらす量）も一致する。
    public static float OffsetSecondsToMs(float offsetSeconds) => offsetSeconds * 1000f;

    // gridSize×gridSize の整数グリッド座標 g(0..gridSize-1) を [min,max] のワールド座標へ線形写像。
    // g=0 → min, g=gridSize-1 → max。
    public static float GridToWorld(int g, int gridSize, float min, float max)
    {
        if (gridSize <= 1) return (min + max) * 0.5f;
        float t = g / (float)(gridSize - 1);
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
        return min + t * (max - min);
    }

    // flipY=true のとき、行0を上端(=max)へ向ける。GridLayoutGroup は通常 上→下 に並ぶため、
    // 行0が画面上端になるケースに対応する。実機で上下が反転していたらこのフラグで直す。
    public static int ApplyFlip(int g, int gridSize, bool flip)
    {
        if (!flip) return g;
        return (gridSize - 1) - g;
    }

    // エディタの type(0:Tap,1:Flick,2:Long) → 本編の type 文字列。
    // フリックは本編では「方向指定カット」= "direction"。
    public static string TypeToString(int type)
    {
        switch (type)
        {
            case 1: return "direction";
            case 2: return "long";
            default: return "tap";
        }
    }

    // エディタの direction(0..8) → 本編の direction 文字列。
    // 0:なし, 以降は反時計回り（右→右上→上→左上→左→左下→下→右下）。
    private static readonly string[] DirNames =
        { "none", "right", "upright", "up", "upleft", "left", "downleft", "down", "downright" };

    public static string DirectionToString(int dir)
    {
        if (dir < 0 || dir >= DirNames.Length) return "none";
        return DirNames[dir];
    }

    // ロングノーツの必要カット数。type!=2(ロング以外)は常に 1。
    // duration(Tick)を拍に直し cutsPerBeat を掛け、「開始カット + 各拍のカット」で +1、最低 2。
    public static int LongCount(int type, int durationTicks, int resolution, float cutsPerBeat)
    {
        if (type != 2) return 1;
        if (resolution <= 0) resolution = 1;
        float durBeats = durationTicks / (float)resolution;
        int n = (int)Math.Round(durBeats * cutsPerBeat, MidpointRounding.AwayFromZero) + 1;
        return n < 2 ? 2 : n;
    }
}
