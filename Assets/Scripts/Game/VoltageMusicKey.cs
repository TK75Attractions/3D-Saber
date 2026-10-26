using System;
using System.Collections.Generic;

public readonly struct VoltageMusicKey
{
    public readonly int Tonic;
    public readonly bool Minor;
    static readonly int[] majorSteps = { 0, 2, 4, 7, 9, 12, 14, 16 };
    static readonly int[] minorSteps = { 0, 3, 5, 7, 10, 12, 15, 17 };

    public VoltageMusicKey(int tonic, bool minor)
    {
        Tonic = ((tonic % 12) + 12) % 12;
        Minor = minor;
    }

    public static VoltageMusicKey Parse(string text)
    {
        var fallback = new VoltageMusicKey(7, false);
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        string[] parts = text.Trim().Replace('♯', '#').Replace('♭', 'b')
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return fallback;
        bool minor = string.Equals(parts[1], "minor", StringComparison.OrdinalIgnoreCase);
        if (!minor && !string.Equals(parts[1], "major", StringComparison.OrdinalIgnoreCase)) return fallback;

        string note = parts[0];
        int tonic;
        switch (char.ToUpperInvariant(note[0]))
        {
            case 'C': tonic = 0; break;
            case 'D': tonic = 2; break;
            case 'E': tonic = 4; break;
            case 'F': tonic = 5; break;
            case 'G': tonic = 7; break;
            case 'A': tonic = 9; break;
            case 'B': tonic = 11; break;
            default: return fallback;
        }
        if (note.Length == 2)
        {
            if (note[1] == '#') tonic++;
            else if (note[1] == 'b' || note[1] == 'B') tonic--;
            else return fallback;
        }
        else if (note.Length != 1) return fallback;
        return new VoltageMusicKey(tonic, minor);
    }

    public int StepMidi(int step, int level)
    {
        if (step < 1 || step > 8) throw new ArgumentOutOfRangeException(nameof(step));
        return 60 + Tonic + (Minor ? minorSteps : majorSteps)[step - 1] + (level >= 2 ? 12 : 0);
    }

    public int[] ChordMidi() => new[] { 60 + Tonic, 60 + Tonic + (Minor ? 3 : 4), 67 + Tonic, 72 + Tonic };
}

// 密な入力では交互に落とす。さらに移動する1秒窓で8音を上限にする。
// 和音は交互の間引きより優先するが、8音の上限は越えない。落とした音は予約しない。
public sealed class VoltageSoundLimiter
{
    readonly Queue<double> played = new Queue<double>();
    double lastInput = double.NegativeInfinity;
    bool skip;

    public bool TryPlay(double time, bool chord = false)
    {
        if (double.IsNaN(time) || double.IsInfinity(time) || time < lastInput) return false;
        bool dense = time - lastInput < .125;
        lastInput = time;
        skip = dense && !skip;
        while (played.Count > 0 && time - played.Peek() > 1.0) played.Dequeue();
        if ((skip && !chord) || played.Count >= 8) return false;
        played.Enqueue(time);
        return true;
    }
}
