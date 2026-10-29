using System;
using System.Collections.Generic;

public sealed partial class PlayStats
{
    bool Batch5aResolved()
    {
        if (notes.Count == 0) return false;
        foreach (var n in notes) if (!n.Result.HasValue) return false;
        return true;
    }

    public bool Batch5aGoodStart()
    {
        int count = 0;
        foreach (var group in groups)
        {
            if (!AllClean(group)) return false;
            count += group.Count;
            // 10個目と同時に来たノーツも、組の途中で切らずに見る。
            if (count >= AchievementCatalog.GoodStartNotes) return true;
        }
        return false;
    }

    public bool Batch5aFirstArrow()
    {
        var arrows = FirstNotesWith(n => n.Arrow);
        if (arrows.Count == 0) return false;
        foreach (int k in arrows) if (!notes[k].Success || notes[k].WrongSwing) return false;
        return true;
    }

    public bool Batch5aGoodEnding()
    {
        if (!AllPerfect(finalGroup) || firstGroup.Count == 0) return false;
        bool failed = false;
        foreach (int k in firstGroup)
        {
            if (!notes[k].Result.HasValue) return false;
            failed |= notes[k].Result == JudgmentTier.Bad || notes[k].Result == JudgmentTier.Miss;
        }
        return failed;
    }

    public bool Batch5aBounceBack()
    {
        if (!Batch5aResolved()) return false;
        int failures = 0;
        // 最後の組には次のノーツが無いので、出どころの案どおり数えない。
        for (int i = 0; i + 1 < groups.Count; i++)
        {
            int here = 0;
            foreach (int k in groups[i])
                if (notes[k].Result == JudgmentTier.Bad || notes[k].Result == JudgmentTier.Miss) here++;
            if (here == 0) continue;
            if (!AllPerfect(groups[i + 1])) return false;
            failures += here;
        }
        return failures >= AchievementCatalog.BounceBackFailures;
    }

    public bool Batch5aZanshin()
    {
        var scenes = new HashSet<int>();
        for (int k = 0; k < notes.Count; k++)
        {
            var n = notes[k];
            if (!n.Long) continue;
            if (!Finite(n.EndTime) || n.EndTime < n.Time) return false;
            for (int i = 0; i < groups.Count; i++)
            {
                var next = groups[i];
                double gap = notes[next[0]].Time - n.EndTime;
                if (gap < -FinalNoteGroup.Tolerance - 1e-9) continue;
                if (next.Contains(k)) continue;
                if (gap > AchievementCatalog.ZanshinNextSeconds + 1e-9) break;
                if (!n.Perfect || n.CutsDone != n.Cuts || !AllPerfect(next)) return false;
                scenes.Add(i);
                break;
            }
        }
        return scenes.Count >= AchievementCatalog.ZanshinScenes;
    }

    // その譜面の着地点の最小・最大を四すみとする。幅や高さが潰れた譜面は使わない。
    public bool Batch5aFourCorners()
    {
        if (notes.Count == 0) return false;
        float left = float.PositiveInfinity, right = float.NegativeInfinity;
        float bottom = float.PositiveInfinity, top = float.NegativeInfinity;
        foreach (var n in notes)
        {
            if (!Finite(n.X) || !Finite(n.Y)) return false;
            left = Math.Min(left, n.X); right = Math.Max(right, n.X);
            bottom = Math.Min(bottom, n.Y); top = Math.Max(top, n.Y);
        }
        float tolerance = AchievementCatalog.CornerTolerance;
        if (right - left <= 2 * tolerance || top - bottom <= 2 * tolerance) return false;
        int mask = 0;
        foreach (var n in notes)
        {
            if (!n.Perfect) continue;
            if (Math.Abs(n.X - left) <= tolerance && Math.Abs(n.Y - bottom) <= tolerance) mask |= 1;
            if (Math.Abs(n.X - right) <= tolerance && Math.Abs(n.Y - bottom) <= tolerance) mask |= 2;
            if (Math.Abs(n.X - left) <= tolerance && Math.Abs(n.Y - top) <= tolerance) mask |= 4;
            if (Math.Abs(n.X - right) <= tolerance && Math.Abs(n.Y - top) <= tolerance) mask |= 8;
        }
        return mask == 15;
    }

    // いちばん弱い区間を先に選ぶ。ノーツ不足だから別の区間へ替えることはしない。
    public bool Batch5aQuiet()
    {
        if (Stage?.sections == null || !Finite(PlayStartSeconds) || !Finite(PlayEndSeconds)
            || PlayEndSeconds <= PlayStartSeconds) return false;
        StagePerformanceTimeline.Section quiet = null;
        foreach (var s in Stage.sections)
        {
            if (s == null || !Finite(s.startSeconds) || !Finite(s.endSeconds) || !Finite(s.intensity)
                || s.startSeconds < 0 || s.endSeconds <= s.startSeconds || s.intensity < 0) return false;
            if (s.endSeconds <= PlayStartSeconds || s.startSeconds >= PlayEndSeconds) continue;
            if (quiet == null || s.intensity < quiet.intensity
                || (s.intensity == quiet.intensity && (s.startSeconds < quiet.startSeconds
                    || (s.startSeconds == quiet.startSeconds && s.endSeconds < quiet.endSeconds)))) quiet = s;
        }
        if (quiet == null || quiet.startSeconds < PlayStartSeconds || quiet.endSeconds > PlayEndSeconds) return false;
        return AllPerfect(Window(quiet.startSeconds, quiet.endSeconds), AchievementCatalog.QuietMinNotes);
    }

    public bool Batch5aKannon()
    {
        if (Chart?.notes == null) return false;
        foreach (var group in groups)
        {
            bool red = false, blue = false;
            foreach (int k in group)
            {
                var n = notes[k];
                if (!n.Arrow || !n.Perfect || n.Index < 0 || n.Index >= Chart.notes.Count) continue;
                var data = Chart.notes[n.Index];
                if (data == null) continue;
                float x = CutDirectionHelper.ToVector(CutDirectionHelper.Parse(data.direction)).x;
                red |= n.Red && !n.Gold && x > 0;
                blue |= n.Blue && !n.Gold && x < 0;
            }
            if (red && blue) return true;
        }
        return false;
    }

    // 同時の組は1回。矢印だけが続く一続きの場面を、途中で切らずに見る。
    public bool Batch5aArrowRun()
    {
        int count = 0;
        bool perfect = true;
        foreach (var group in groups)
        {
            bool arrows = true;
            foreach (int k in group) arrows &= notes[k].Arrow;
            if (arrows) { count++; perfect &= AllPerfect(group); }
            else
            {
                if (count >= AchievementCatalog.ArrowRunGroups && perfect) return true;
                count = 0; perfect = true;
            }
        }
        return count >= AchievementCatalog.ArrowRunGroups && perfect;
    }

    static bool Batch5aMeasured(Note n) =>
        !n.Long && n.Cut && n.ErrorMs.HasValue && Finite(n.ErrorMs.Value);

    public bool Batch5aRhythm()
    {
        if (!Batch5aResolved()) return false;
        int misses = 0, measured = 0, perfect = 0;
        foreach (var n in notes)
        {
            if (n.Result == JudgmentTier.Miss) misses++;
            if (!Batch5aMeasured(n)) continue;
            measured++;
            if (n.Perfect) perfect++;
        }
        return measured >= AchievementCatalog.RhythmMinMeasured
            && (long)misses * AchievementCatalog.RhythmMissDenominator >= (long)notes.Count * AchievementCatalog.RhythmMissNumerator
            && (long)perfect * AchievementCatalog.RhythmPerfectDenominator >= (long)measured * AchievementCatalog.RhythmPerfectNumerator;
    }

    public bool Batch5aAttendance() => AllCut(n => true, 1);

    public bool Batch5aAllChords()
    {
        int count = 0;
        foreach (var group in groups)
        {
            if (group.Count < 2) continue;
            if (!AllPerfect(group)) return false;
            count++;
        }
        return count >= AchievementCatalog.ChordMinGroups;
    }

    public bool Batch5aFinalBest()
    {
        int count = 0, bestIndex = -1;
        double best = double.PositiveInfinity;
        bool tied = false;
        for (int k = 0; k < notes.Count; k++)
        {
            var n = notes[k];
            if (!Batch5aMeasured(n)) continue;
            count++;
            double error = Math.Abs(n.ErrorMs.Value);
            if (error < best) { best = error; bestIndex = k; tied = false; }
            else if (error == best) tied = true;
        }
        return count >= AchievementCatalog.FinalBestMinMeasured && !tied && finalGroup.Contains(bestIndex);
    }

    public bool Batch5aSteady()
    {
        int count = 0;
        double mean = 0, sum = 0;
        // 符号つきのずれの母標準偏差。大きな値でも桁落ちしにくい形で足す。
        foreach (var n in notes)
        {
            if (!Batch5aMeasured(n)) continue;
            double delta = n.ErrorMs.Value - mean;
            mean += delta / ++count;
            sum += delta * (n.ErrorMs.Value - mean);
        }
        double limit = AchievementCatalog.SteadyMaxDeviationMs;
        return count >= AchievementCatalog.SteadyMinMeasured && Finite(sum) && sum >= 0
            && sum / count <= limit * limit + 1e-9;
    }

    public bool Batch5aAllGood()
    {
        if (notes.Count < AchievementCatalog.AllGoodMinNotes) return false;
        foreach (var n in notes) if (n.Result != JudgmentTier.Good) return false;
        return true;
    }
}
