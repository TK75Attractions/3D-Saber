using System;
using System.Collections.Generic;

// 曲時計から得た拍だけで進める。普通の成功・停止した時計では段を失わない。
public sealed class GoldStaircase
{
    public int Count { get; private set; }
    public int NextStep => Math.Min(2, Count);
    double lastGold = double.NaN;

    public void Reset() { Count = 0; lastGold = double.NaN; }

    public void Judge(JudgmentTier tier)
    {
        if (tier == JudgmentTier.Bad || tier == JudgmentTier.Miss) Reset();
    }

    public void Tick(double beat)
    {
        if (!double.IsNaN(lastGold) && (beat < lastGold || beat - lastGold >= 4 - 1e-9)) Reset();
    }

    public void Gold(double beat, bool advance)
    {
        Tick(beat);
        lastGold = beat;
        if (advance) Count = Math.Min(3, Count + 1);
    }
}

// 袋の境目でも同じ音を続けない。Unity の乱数状態は使わない。
public sealed class PerfectCutBag
{
    readonly VoltageRandom random;
    readonly int[] bag = { 0, 1, 2, 3 };
    int next = 4, previous = -1;

    public PerfectCutBag(string song, string run)
    {
        random = new VoltageRandom(song, run + "/perfect");
    }

    public int Next()
    {
        if (next == 4)
        {
            for (int i = 3; i > 0; i--)
            {
                int j = (int)(random.Next() * (i + 1));
                int swap = bag[i]; bag[i] = bag[j]; bag[j] = swap;
            }
            if (bag[0] == previous)
            {
                int swap = bag[0]; bag[0] = bag[1]; bag[1] = swap;
            }
            next = 0;
        }
        return previous = bag[next++];
    }
}

public readonly struct MilestoneChoice
{
    public readonly bool Enabled, Jackpot;
    public readonly int Shape, Color, Inversion;
    public int Combination => (Shape * 3 + Color) * 3 + Inversion;

    public MilestoneChoice(int combination, bool jackpot)
    {
        Enabled = true; Jackpot = jackpot;
        Shape = combination / 9;
        Color = combination / 3 % 3;
        Inversion = combination % 3;
    }
}

public sealed class MilestoneLottery
{
    readonly VoltageRandom random;
    int previous = -1;
    public int JackpotCount { get; private set; }

    public MilestoneLottery(string song, string run)
    {
        random = new VoltageRandom(song, run + "/milestone");
    }

    public MilestoneChoice Next()
    {
        int choice = (int)(random.Next() * (previous < 0 ? 27 : 26));
        if (previous >= 0 && choice >= previous) choice++;
        previous = choice;
        bool jackpot = random.Next() < .1f && JackpotCount < 2;
        if (jackpot) JackpotCount++;
        return new MilestoneChoice(choice, jackpot);
    }
}

// GamePlayManager と同じく、オフセットとロングの実長を含む最大時刻で組を選ぶ。
public sealed class FinalNoteGroup
{
    public const double Tolerance = .001;
    public double LastTime { get; }
    public IReadOnlyList<int> Indices => indices;
    readonly List<int> indices = new List<int>();

    public FinalNoteGroup(ChartData chart, double offset, float secondsPerCut)
    {
        double last = 0;
        if (chart?.notes != null)
            foreach (var note in chart.notes)
                if (note != null)
                    last = Math.Max(last, note.TimeSeconds + offset + note.LingerSeconds(secondsPerCut));
        LastTime = last;
        if (chart?.notes != null)
            for (int i = 0; i < chart.notes.Count; i++)
            {
                var note = chart.notes[i];
                if (note != null && Contains(note.TimeSeconds + offset + note.LingerSeconds(secondsPerCut)))
                    indices.Add(i);
            }
    }

    public bool Contains(double endTime) => Math.Abs(endTime - LastTime) <= Tolerance + 1e-9;
}
