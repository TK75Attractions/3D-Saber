using System;

// 熱交換の設定。heat の項目が無い譜面では使わない。
[Serializable]
public sealed class HeatChartData
{
    public int max = 3;
    public float coolBeats = 2f;
    public int perCut = 1;
    public bool blockAll;
}

// Unity の手・描画・時計から独立した識別子。
public enum HeatHand { Any, Right, Left }

public static class HeatRules
{
    public const int DefaultMax = 3;
    public const int MaxSupported = 12;
    public const float DefaultCoolBeats = 2f;

    public static bool Finite(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);

    // 呼び出し元の設定を書き換えず、実行用の写しを作る。
    public static HeatChartData Normalize(HeatChartData data)
    {
        if (data == null) return null;
        int max = data.max > 0 ? Math.Min(MaxSupported, data.max) : DefaultMax;
        return new HeatChartData
        {
            max = max,
            coolBeats = Finite(data.coolBeats) && data.coolBeats > 0
                ? data.coolBeats : DefaultCoolBeats,
            perCut = data.perCut > 0 ? Math.Min(max, data.perCut) : 1,
            blockAll = data.blockAll
        };
    }

    public static HeatHand FromColor(string color)
    {
        switch ((color ?? "").Trim().ToLowerInvariant())
        {
            case "red": return HeatHand.Right;
            case "blue": return HeatHand.Left;
            default: return HeatHand.Any;
        }
    }
}

// 時刻はノーツと同じ秒。offsetSeconds は譜面と追加オフセットの合計。
// 最後に切った拍から coolBeats 拍休むと1段冷える。以後も同じ拍数ごと。
// ロングも受理されたカットごとに加熱し、休み時間を数え直す。
public sealed class HeatSession
{
    public int Max { get; }
    public int PerCut { get; }
    public double CoolBeats { get; }
    public bool BlockAll { get; }
    public double Time { get; private set; } = double.NegativeInfinity;

    readonly ChartTempoMap tempo;
    readonly double offsetSeconds;
    readonly int[] heat = new int[2];
    readonly double[] nextCoolBeat =
        { double.PositiveInfinity, double.PositiveInfinity };

    public HeatSession(HeatChartData settings, ChartTempoMap tempo, double offsetSeconds = 0)
    {
        var normalized = HeatRules.Normalize(settings ?? new HeatChartData());
        Max = normalized.max;
        PerCut = normalized.perCut;
        CoolBeats = normalized.coolBeats;
        BlockAll = normalized.blockAll;
        this.tempo = tempo ?? throw new ArgumentNullException(nameof(tempo));
        if (!HeatRules.Finite(offsetSeconds))
            throw new ArgumentOutOfRangeException(nameof(offsetSeconds));
        this.offsetSeconds = offsetSeconds;
    }

    HeatSession(HeatSession source)
    {
        Max = source.Max;
        PerCut = source.PerCut;
        CoolBeats = source.CoolBeats;
        BlockAll = source.BlockAll;
        tempo = source.tempo;
        offsetSeconds = source.offsetSeconds;
        Time = source.Time;
        Array.Copy(source.heat, heat, 2);
        Array.Copy(source.nextCoolBeat, nextCoolBeat, 2);
    }

    public HeatSession Clone() => new HeatSession(this);

    static int Index(HeatHand hand)
    {
        if (hand == HeatHand.Right) return 0;
        if (hand == HeatHand.Left) return 1;
        throw new ArgumentOutOfRangeException(nameof(hand));
    }

    public int HeatOf(HeatHand hand) => heat[Index(hand)];
    public bool IsOverheated(HeatHand hand) => HeatOf(hand) >= Max;

    public HeatHand ChooseHand(HeatHand required)
    {
        if (required != HeatHand.Any)
        {
            Index(required);
            return required;
        }
        // 同じ熱なら右。道具の結果が毎回同じになるよう固定する。
        return heat[0] <= heat[1] ? HeatHand.Right : HeatHand.Left;
    }

    public bool CanCut(HeatHand hand, bool heatNote)
    {
        if (hand != HeatHand.Right && hand != HeatHand.Left) return false;
        return !(heatNote || BlockAll) || !IsOverheated(hand);
    }

    public double NextCoolTime(HeatHand hand)
    {
        double beat = nextCoolBeat[Index(hand)];
        return double.IsPositiveInfinity(beat)
            ? double.PositiveInfinity
            : tempo.TimeAt(beat) / 1000.0 + offsetSeconds;
    }

    // 巻き戻った時計や壊れた時計で、冷却や加熱をやり直さない。
    // 同じ時刻で何度呼んでも、冷却は一度だけ。
    public bool Advance(double time)
    {
        if (!HeatRules.Finite(time) || time < Time) return false;
        double beat = tempo.BeatAt((time - offsetSeconds) * 1000.0);
        if (!HeatRules.Finite(beat)) return false;
        Time = time;
        for (int i = 0; i < 2; i++)
        {
            if (heat[i] == 0 || beat + 1e-9 < nextCoolBeat[i]) continue;
            double elapsed = Math.Floor((beat - nextCoolBeat[i] + 1e-9) / CoolBeats) + 1;
            int steps = (int)Math.Min(heat[i], Math.Max(0, elapsed));
            heat[i] -= steps;
            nextCoolBeat[i] = heat[i] == 0
                ? double.PositiveInfinity : nextCoolBeat[i] + steps * CoolBeats;
        }
        return true;
    }

    // 判定部品が受理したカットの通知。強さ・速さ・点数は受け取らない。
    // 通常ノーツを上限で切った場合も、休み時間をここから数え直す。
    public bool RecordCut(HeatHand hand, double time)
    {
        if (hand != HeatHand.Right && hand != HeatHand.Left) return false;
        if (!Advance(time)) return false;
        int i = Index(hand);
        heat[i] = Math.Min(Max, heat[i] + PerCut);
        nextCoolBeat[i] = tempo.BeatAt((time - offsetSeconds) * 1000.0) + CoolBeats;
        return true;
    }

    // 道具・テスト用。拒否された振りでは休み時間を数え直さない。
    public bool TryCut(HeatHand hand, bool heatNote, double time)
    {
        if (!Advance(time) || !CanCut(hand, heatNote)) return false;
        return RecordCut(hand, time);
    }

    internal (int, int, double, double) StateKey
        => (heat[0], heat[1], nextCoolBeat[0], nextCoolBeat[1]);
}
