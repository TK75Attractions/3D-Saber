using System;
using System.Collections.Generic;
using System.Linq;

// カット1回の予定。ロングは各カットを別の予定にする。
public readonly struct HeatCut
{
    public readonly double Time;
    public readonly HeatHand RequiredHand;
    public readonly bool HeatNote;
    public readonly int SourceIndex;
    public readonly int CutIndex;

    public HeatCut(double time, HeatHand requiredHand, bool heatNote,
        int sourceIndex = -1, int cutIndex = 0)
    {
        Time = time;
        RequiredHand = requiredHand;
        HeatNote = heatNote;
        SourceIndex = sourceIndex;
        CutIndex = cutIndex;
    }
}

public sealed class HeatTrace
{
    public HeatCut Cut;
    public HeatHand Hand;
    public int RightBefore, LeftBefore, RightAfter, LeftAfter;
    public bool Accepted;
    public bool FixedHandBelowMax;
}

public sealed class HeatAudit
{
    // 未完了なら三つの結論は null。探索上限を安全判定と取り違えない。
    public bool Complete;
    public bool? HasPlayablePath;
    public bool? AllChoicesPlayable;
    public bool? FixedHandsSafe;
    public int FirstProblemSource = -1;
    public int ExaminedChoices;
}

public static class HeatForecast
{
    public static HeatSession CreateSession(ChartData chart, double extraOffsetSeconds = 0)
    {
        if (chart == null || chart.heat == null)
            throw new ArgumentException("熱交換の設定が必要です。", nameof(chart));
        return new HeatSession(chart.heat,
            new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap),
            chart.offsetMs / 1000.0 + extraOffsetSeconds);
    }

    // longCutTime は (ノーツ番号, カット番号) → オフセットを足す前の譜面秒。
    // ロングの切り方は lengthMs だけでは決まらないため、時刻の指定を必須にする。
    public static List<HeatCut> FromChart(ChartData chart, double extraOffsetSeconds = 0,
        Func<int, int, double> longCutTime = null)
    {
        if (chart == null) throw new ArgumentNullException(nameof(chart));
        double offset = chart.offsetMs / 1000.0 + extraOffsetSeconds;
        if (!HeatRules.Finite(offset)) throw new ArgumentOutOfRangeException(nameof(extraOffsetSeconds));
        var result = new List<HeatCut>();
        if (chart.notes == null) return result;
        for (int i = 0; i < chart.notes.Count; i++)
        {
            var note = chart.notes[i];
            if (note == null) continue;
            int count = Math.Max(1, note.count);
            if (count > 1 && longCutTime == null)
                throw new ArgumentException("ロングには各カットの予定時刻が必要です。", nameof(longCutTime));
            double previous = double.NegativeInfinity;
            for (int cut = 0; cut < count; cut++)
            {
                double time = count > 1 ? longCutTime(i, cut) : note.TimeSeconds;
                if (!HeatRules.Finite(time) || time < previous)
                    throw new ArgumentException("カット時刻は有限で、ノーツ内では時刻順にしてください。");
                previous = time;
                result.Add(new HeatCut(time + offset, HeatRules.FromColor(note.color),
                    note.heat, i, cut));
            }
        }
        return Ordered(result);
    }

    static List<HeatCut> Ordered(IEnumerable<HeatCut> source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        var result = source.OrderBy(c => c.Time).ToList();
        foreach (var cut in result)
            if (!HeatRules.Finite(cut.Time)
                || (cut.RequiredHand != HeatHand.Any
                    && cut.RequiredHand != HeatHand.Right && cut.RequiredHand != HeatHand.Left))
                throw new ArgumentException("カット時刻または手の指定が不正です。", nameof(source));
        return result;
    }

    // 熱の低い手を選ぶ試走。失敗後も続けて、どこで余裕がなくなるかを返す。
    // 同時刻は入力順。全選択・同時刻の全順序の検査は Audit を使う。
    public static List<HeatTrace> Simulate(HeatSession initial, IEnumerable<HeatCut> source)
    {
        if (initial == null) throw new ArgumentNullException(nameof(initial));
        var session = initial.Clone();
        var result = new List<HeatTrace>();
        foreach (var cut in Ordered(source))
        {
            if (!session.Advance(cut.Time))
                throw new ArgumentException("初期状態より前のカットは試走できません。", nameof(source));
            var row = new HeatTrace
            {
                Cut = cut,
                Hand = session.ChooseHand(cut.RequiredHand),
                RightBefore = session.HeatOf(HeatHand.Right),
                LeftBefore = session.HeatOf(HeatHand.Left),
                FixedHandBelowMax = cut.RequiredHand == HeatHand.Any
                    || !session.IsOverheated(cut.RequiredHand)
            };
            row.Accepted = session.TryCut(row.Hand, cut.HeatNote, cut.Time);
            row.RightAfter = session.HeatOf(HeatHand.Right);
            row.LeftAfter = session.HeatOf(HeatHand.Left);
            result.Add(row);
        }
        return result;
    }

    public static bool FixedHandsBelowMax(IEnumerable<HeatTrace> trace)
    {
        if (trace == null) throw new ArgumentNullException(nameof(trace));
        return trace.All(row => row.FixedHandBelowMax);
    }

    static (ulong, int, int, double, double) Key(ulong mask, HeatSession session)
    {
        var s = session.StateKey;
        return (mask, s.Item1, s.Item2, s.Item3, s.Item4);
    }

    // その時点で切れる手をすべて分岐する。同時刻の処理順もすべて調べる。
    // AllChoicesPlayable は、合法な選択を続けたどの経路にも行き止まりがないこと。
    // FixedHandsSafe は、到達可能な全状態で色指定の手が上限未満であること。
    // 座標・振りの所要時間・判定窓内での時刻ずれまでは証明しない。
    public static HeatAudit Audit(HeatSession initial, IEnumerable<HeatCut> source,
        int choiceBudget = 100000)
    {
        if (initial == null) throw new ArgumentNullException(nameof(initial));
        if (choiceBudget < 1) throw new ArgumentOutOfRangeException(nameof(choiceBudget));
        var cuts = Ordered(source);
        var report = new HeatAudit();
        bool allPlayable = true, fixedSafe = true;
        var states = new List<HeatSession> { initial.Clone() };
        int start = 0;
        while (start < cuts.Count && states.Count > 0)
        {
            int end = start + 1;
            while (end < cuts.Count && cuts[end].Time == cuts[start].Time) end++;
            int count = end - start;
            if (count > 63) return report;

            var layer = new Dictionary<(ulong, int, int, double, double), HeatSession>();
            foreach (var state in states)
            {
                if (!state.Advance(cuts[start].Time))
                    throw new ArgumentException("初期状態より前のカットは検査できません。", nameof(source));
                layer[Key(0, state)] = state;
            }

            for (int done = 0; done < count && layer.Count > 0; done++)
            {
                var next = new Dictionary<(ulong, int, int, double, double), HeatSession>();
                foreach (var pair in layer)
                {
                    ulong mask = pair.Key.Item1;
                    var state = pair.Value;
                    for (int n = 0; n < count; n++)
                    {
                        ulong bit = 1UL << n;
                        if ((mask & bit) != 0) continue;
                        HeatCut cut = cuts[start + n];
                        if (cut.RequiredHand != HeatHand.Any && state.IsOverheated(cut.RequiredHand))
                        {
                            fixedSafe = false;
                            if (report.FirstProblemSource < 0) report.FirstProblemSource = cut.SourceIndex;
                        }

                        bool accepted = false;
                        for (int h = 0; h < 2; h++)
                        {
                            var hand = h == 0 ? HeatHand.Right : HeatHand.Left;
                            if (cut.RequiredHand != HeatHand.Any && cut.RequiredHand != hand) continue;
                            if (++report.ExaminedChoices > choiceBudget) return report;
                            if (!state.CanCut(hand, cut.HeatNote)) continue;
                            accepted = true;
                            var branch = state.Clone();
                            branch.TryCut(hand, cut.HeatNote, cut.Time);
                            next[Key(mask | bit, branch)] = branch;
                        }
                        if (!accepted)
                        {
                            allPlayable = false;
                            if (report.FirstProblemSource < 0) report.FirstProblemSource = cut.SourceIndex;
                        }
                    }
                }
                layer = next;
            }
            states = layer.Values.ToList();
            start = end;
        }

        report.Complete = true;
        report.HasPlayablePath = states.Count > 0;
        report.AllChoicesPlayable = allPlayable && states.Count > 0;
        report.FixedHandsSafe = fixedSafe;
        return report;
    }
}
