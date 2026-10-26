using System;
using System.Collections.Generic;

// 32A: 出現順(譜面の時刻順)で先頭を固定し、±1msを連鎖させて別の時刻までまとめない。
internal sealed class GoldChordGate
{
    public const double ChartTolerance = .001;
    public const double CutWindow = .060;
    const double Epsilon = 1e-9;
    struct Group
    {
        public double hitTime, firstCut;
        public int step;
        public bool started, bellPlayed;
    }
    internal struct Decision { public bool main, bell, coin, advance; public int step; }
    readonly List<Group> groups = new List<Group>();

    public void Reserve(int count)
    {
        if (groups.Capacity < count) groups.Capacity = count;
    }

    public int Register(double hitTime)
    {
        for (int i = 0; i < groups.Count; i++)
            if (Math.Abs(groups[i].hitTime - hitTime) <= ChartTolerance + Epsilon) return i;
        groups.Add(new Group { hitTime = hitTime });
        return groups.Count - 1;
    }

    public Decision Decide(int index, double cutTime, int step)
    {
        Group group = groups[index];
        step = Math.Max(0, Math.Min(GoldNoteSfx.CriticalSteps - 1, step));
        Decision result;
        if (!group.started)
        {
            group.started = true; group.firstCut = cutTime; group.step = step;
            result = new Decision { main = true, coin = true, advance = true, step = step };
        }
        else if (Math.Abs(cutTime - group.firstCut) <= CutWindow + Epsilon)
        {
            result = new Decision { bell = !group.bellPlayed, step = group.step };
            group.bellPlayed = true;
        }
        else result = new Decision { main = true, step = step };
        groups[index] = group;
        return result;
    }

    public void Reset() { groups.Clear(); }
}
