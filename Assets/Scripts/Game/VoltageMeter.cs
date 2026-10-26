using System;

// 採点を変更せず、通知されたコンボの増分だけを数える。
public sealed class VoltageMeter
{
    public const int MaximumLevel = 4;
    public const int StepsPerLevel = 8;

    public int Level { get; private set; }
    public int Steps { get; private set; }
    public int MaxLevelReached { get; private set; }

    public readonly struct Change
    {
        public readonly int PreviousLevel, PreviousSteps, Level, Steps, StepNumber;
        public bool Raised => Level > PreviousLevel;
        public bool Lowered => Level < PreviousLevel;

        public Change(int previousLevel, int previousSteps, int level, int steps, int stepNumber)
        {
            PreviousLevel = previousLevel; PreviousSteps = previousSteps;
            Level = level; Steps = steps; StepNumber = stepNumber;
        }
    }

    public event Action<Change> Changed;
    int previousCombo;

    public VoltageMeter(int initialLevel = 0)
    {
        Level = Math.Max(0, Math.Min(MaximumLevel, initialLevel));
        MaxLevelReached = Level;
    }

    public void SynchronizeCombo(int combo) => previousCombo = Math.Max(0, combo);

    public void ObserveCombo(int combo, bool broken)
    {
        combo = Math.Max(0, combo);
        int increase = Math.Max(0, combo - previousCombo);
        previousCombo = combo;
        if (broken)
        {
            int oldLevel = Level, oldSteps = Steps;
            Level = Math.Max(0, Level - 1);
            Steps = 0;
            if (oldLevel != Level || oldSteps != Steps)
                Changed?.Invoke(new Change(oldLevel, oldSteps, Level, Steps, 0));
            return;
        }

        for (int i = 0; i < increase; i++)
        {
            int oldLevel = Level, oldSteps = Steps;
            int step = Steps + 1;
            Steps = step;
            if (Steps == StepsPerLevel)
            {
                Steps = 0;
                Level = Math.Min(MaximumLevel, Level + 1);
            }
            MaxLevelReached = Math.Max(MaxLevelReached, Level);
            Changed?.Invoke(new Change(oldLevel, oldSteps, Level, Steps, step));
        }
    }
}

// 実行環境の文字列ハッシュや Unity の乱数状態に依存しない。
public sealed class VoltageRandom
{
    uint state;

    public VoltageRandom(string songId, string runId)
    {
        state = 2166136261;
        foreach (char c in (songId ?? "") + "\0" + (runId ?? ""))
            state = unchecked((state ^ c) * 16777619);
    }

    public float Next()
    {
        state = unchecked(state * 1664525 + 1013904223);
        return (state >> 8) / 16777216f;
    }
}
