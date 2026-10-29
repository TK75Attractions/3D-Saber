using System;
using System.Collections.Generic;
using UnityEngine;

// 受理切断は OnPartialCut だけから記録し、最終結果とは分けて持つ。
public sealed partial class PlayStats
{
    public sealed class AcceptedCut
    {
        public int NoteIndex, CutIndex, Frame, Sample;
        public uint Generation;
        public SaberHand Hand;
        public double Time, ObservedAt;
        public ulong StrokeId;
        public bool Sweep, DirectionValid;
        public Vector2 Direction;
    }

    public sealed partial class Note
    {
        public readonly List<AcceptedCut> Accepted = new List<AcceptedCut>();
        public uint Generation;
        public bool HasGeneration;
        public int HandMask;
        public double? FirstCutTime, LastCutTime, ResolvedTime;
    }

    public sealed class CutCandidate
    {
        public int NoteIndex, Frame;
        public uint Generation;
        public SaberHand Hand;
        public double Time;
    }

    readonly List<AcceptedCut> accepted = new List<AcceptedCut>();
    readonly List<CutCandidate> candidates = new List<CutCandidate>();
    public IReadOnlyList<AcceptedCut> AcceptedCuts => accepted;
    public bool ActionsComplete { get; set; }
    public double CandidatesObservedUntil { get; set; } = double.NegativeInfinity;
    public double PlayStartSeconds { get; set; } = double.NaN;
    public double PlayEndSeconds { get; set; } = double.NaN;
    public AimObservation AimObserved { get; set; }

    public void BindGeneration(int k, uint generation)
    {
        if (k < 0 || k >= notes.Count) { ActionsComplete = false; return; }
        var n = notes[k];
        if (n.HasGeneration && n.Generation != generation) { ActionsComplete = false; return; }
        n.Generation = generation;
        n.HasGeneration = true;
    }

    public void AcceptCut(int k, int index, uint generation, SaberHand hand, double time, SaberCutRecord record)
    {
        if (k < 0 || k >= notes.Count) { ActionsComplete = false; return; }
        var n = notes[k];
        if (!n.HasGeneration) BindGeneration(k, generation);
        if (n.Generation != generation || index < 0 || index >= n.Cuts) { ActionsComplete = false; return; }
        if (index < n.Accepted.Count) return;
        if (index != n.Accepted.Count || !Finite(time)) { ActionsComplete = false; return; }
        var cut = new AcceptedCut
        {
            NoteIndex = k, CutIndex = index, Generation = generation, Hand = hand, Time = time,
            Frame = record.Frame, Sample = record.Sample, ObservedAt = record.ObservedAt,
            StrokeId = record.StrokeId, Sweep = record.Sweep,
            Direction = record.Direction, DirectionValid = record.DirectionValid
        };
        n.Accepted.Add(cut);
        accepted.Add(cut);
        n.CutsDone = n.Accepted.Count;
        n.HandMask |= HandBit(hand);
        if (!n.FirstCutTime.HasValue) n.FirstCutTime = time;
        n.LastCutTime = time;
        Candidate(k, generation, hand, time, record.Frame);
    }

    public void Candidate(int k, uint generation, SaberHand hand, double time, int frame)
    {
        if (k < 0 || k >= notes.Count || !Finite(time) || HandBit(hand) == 4) return;
        var n = notes[k];
        if (!n.HasGeneration || n.Generation != generation || !n.Gold || !n.Long) return;
        candidates.Add(new CutCandidate { NoteIndex = k, Generation = generation, Hand = hand, Time = time, Frame = frame });
    }

    public void ResolveAt(int k, double time)
    {
        if (k < 0 || k >= notes.Count || !Finite(time)) { ActionsComplete = false; return; }
        notes[k].ResolvedTime = time;
    }

    static int HandBit(SaberHand hand) => hand == SaberHand.Left ? 1 : hand == SaberHand.Right ? 2 : 4;
    static SaberHand SingleHand(Note n) => n.HandMask == 1 ? SaberHand.Left : n.HandMask == 2 ? SaberHand.Right : SaberHand.Any;
    bool CompleteCuts(Note n) => ActionsComplete && n.Success && n.Accepted.Count == n.Cuts && n.CutsDone == n.Cuts
        && n.FirstCutTime.HasValue && n.LastCutTime.HasValue;

    public bool QuizSuccessRate(int numerator, int denominator)
    {
        int total = 0, success = 0;
        foreach (var n in notes)
        {
            if (!n.Quiz) continue;
            if (!n.Result.HasValue) return false;
            total++;
            if (n.Success) success++;
        }
        return total > 0 && denominator > 0 && (long)success * denominator >= (long)total * numerator;
    }

    public int HeldOppositeNotes()
    {
        if (!ActionsComplete) return 0;
        var found = new HashSet<int>();
        foreach (var holding in notes)
        {
            SaberHand hand = SingleHand(holding);
            if (!holding.Long || !CompleteCuts(holding) || hand == SaberHand.Any) continue;
            foreach (var n in notes)
            {
                if (ReferenceEquals(n, holding) || !CompleteCuts(n) || n.Accepted[0].Hand == SaberHand.Any
                    || n.Accepted[0].Hand == hand) continue;
                double tolerance = FinalNoteGroup.Tolerance + 1e-9;
                if (n.Time - holding.Time <= tolerance || holding.EndTime - n.Time <= tolerance
                    || n.FirstCutTime.Value - holding.FirstCutTime.Value <= tolerance
                    || holding.LastCutTime.Value - n.FirstCutTime.Value <= tolerance) continue;
                found.Add(n.Index);
            }
        }
        return found.Count;
    }

    // 両手の切りの順は、受け付けた切り(AcceptCut が候補にも入れる)だけで見る。判定を空回しして受け付けなかった手の候補を集めることはしない
    // (2026-10-29 9a: 演奏の判定を変えないため)。両手がほぼ同時に受け付けられたときは交互に数えない。
    public bool HasMochitsuki()
    {
        if (!ActionsComplete) return false;
        foreach (var n in notes)
        {
            if (!n.Gold || !n.Long || !CompleteCuts(n)) continue;
            int count = 0;
            SaberHand previous = SaberHand.Any;
            foreach (var cut in n.Accepted)
            {
                bool ambiguous = cut.Hand == SaberHand.Any
                    || CandidatesObservedUntil + 1e-9 < cut.ObservedAt + AchievementCatalog.MochitsukiAmbiguousSeconds;
                foreach (var candidate in candidates)
                    if (candidate.NoteIndex == cut.NoteIndex && candidate.Generation == cut.Generation && candidate.Hand != cut.Hand
                        && (candidate.Frame == cut.Frame
                            || Math.Abs(candidate.Time - cut.Time) <= AchievementCatalog.MochitsukiAmbiguousSeconds + 1e-9))
                        ambiguous = true;
                if (ambiguous) { count = 0; previous = SaberHand.Any; continue; }
                count = previous != SaberHand.Any && previous != cut.Hand ? count + 1 : 1;
                previous = cut.Hand;
                if (count >= AchievementCatalog.MochitsukiMinCuts) return true;
            }
        }
        return false;
    }

    public bool HasOneHandPerfect()
    {
        if (!ActionsComplete) return false;
        for (int start = 0; start < groups.Count; start++)
        {
            SaberHand hand = SaberHand.Any;
            int count = 0;
            double from = double.PositiveInfinity, to = double.NegativeInfinity;
            for (int end = start; end < groups.Count; end++)
            {
                bool good = true;
                foreach (int k in groups[end])
                {
                    var n = notes[k];
                    SaberHand current = SingleHand(n);
                    if (!n.Perfect || !CompleteCuts(n) || current == SaberHand.Any
                        || (hand != SaberHand.Any && hand != current)) { good = false; break; }
                    hand = current;
                    count++;
                    from = Math.Min(from, n.FirstCutTime.Value);
                    to = Math.Max(to, n.LastCutTime.Value);
                }
                if (!good) break;
                if (count < AchievementCatalog.OneHandPerfectNotes) continue;
                foreach (var cut in accepted)
                    if (cut.Time >= from && cut.Time <= to && cut.Hand != hand) { good = false; break; }
                if (good) return true;
                break;
            }
        }
        return false;
    }

    public bool HasRising()
    {
        if (!ActionsComplete) return false;
        int total = 0, up = 0;
        float cosine = Mathf.Cos(AchievementCatalog.RisingMaxAngleDegrees * Mathf.Deg2Rad);
        foreach (var n in notes)
        {
            if (n.Arrow) continue;
            if (!n.Result.HasValue) return false;
            if (!n.Success) continue;
            total++;
            if (!CompleteCuts(n)) return false;
            var first = n.Accepted[0];
            if (first.DirectionValid && first.Direction.sqrMagnitude > 1e-8f
                && Vector2.Dot(first.Direction.normalized, Vector2.up) + 1e-6f >= cosine) up++;
        }
        return total >= AchievementCatalog.RisingMinNotes
            && (long)up * AchievementCatalog.RisingDenominator >= (long)total * AchievementCatalog.RisingNumerator;
    }

    public int RevengeCount()
    {
        if (!ActionsComplete) return 0;
        int count = 0;
        var missed = new double?[AchievementCatalog.GridColumns * AchievementCatalog.GridRows];
        foreach (var group in groups)
        {
            var cells = new Dictionary<int, List<int>>();
            foreach (int k in group)
            {
                int cell = AchievementGrid.Cell(notes[k].X, notes[k].Y);
                if (cell < 0) continue;
                if (!cells.TryGetValue(cell, out var members)) cells[cell] = members = new List<int>();
                members.Add(k);
            }
            foreach (var pair in cells)
            {
                bool perfect = true, known = true;
                double first = double.PositiveInfinity;
                double? lastMiss = null;
                foreach (int k in pair.Value)
                {
                    var n = notes[k];
                    known &= n.Result.HasValue && n.ResolvedTime.HasValue;
                    perfect &= n.Perfect && CompleteCuts(n);
                    if (n.FirstCutTime.HasValue) first = Math.Min(first, n.FirstCutTime.Value);
                    if (n.Result == JudgmentTier.Miss && n.ResolvedTime.HasValue)
                        lastMiss = Math.Max(lastMiss ?? double.NegativeInfinity, n.ResolvedTime.Value);
                }
                if (known && perfect && missed[pair.Key].HasValue && first > missed[pair.Key].Value) count++;
                missed[pair.Key] = known ? lastMiss : null;
            }
        }
        return count;
    }

    sealed class SweepEvent
    {
        public double At;
        public AcceptedCut Cut;
    }

    public bool HasSweep()
    {
        if (!ActionsComplete) return false;
        foreach (var n in notes)
            if (!n.Result.HasValue || (n.Result == JudgmentTier.Miss && !n.ResolvedTime.HasValue)) return false;
        foreach (SaberHand hand in new[] { SaberHand.Left, SaberHand.Right })
        {
            var events = new List<SweepEvent>();
            foreach (var cut in accepted)
                if (cut.Hand == hand || cut.Hand == SaberHand.Any) events.Add(new SweepEvent { At = cut.Time, Cut = cut });
            foreach (var n in notes)
                if (n.Result == JudgmentTier.Miss && n.ResolvedTime.HasValue
                    && (n.Gold || (hand == SaberHand.Left ? n.Blue : n.Red)))
                    events.Add(new SweepEvent { At = n.ResolvedTime.Value });
            events.Sort((a, b) => a.At.CompareTo(b.At));
            int count = 0;
            ulong stroke = 0;
            double groupTime = double.NegativeInfinity;
            bool groupGood = false;
            foreach (var e in events)
            {
                var cut = e.Cut;
                var n = cut != null ? notes[cut.NoteIndex] : null;
                bool grouped = n != null && cut.Hand == hand && cut.StrokeId != 0 && !n.Long;
                bool valid = grouped && CompleteCuts(n);
                bool same = grouped && stroke == cut.StrokeId && Math.Abs(n.Time - groupTime) <= SaberCutJudge.SimultaneousSeconds;
                if (!same)
                {
                    if (groupGood && count >= AchievementCatalog.SweepRequiredGroups) return true;
                    if (!valid) { count = 0; stroke = 0; groupGood = false; continue; }
                    bool continued = groupGood && stroke == cut.StrokeId && cut.Sweep
                        && n.Time - groupTime > SaberCutJudge.SimultaneousSeconds;
                    count = continued ? count + 1 : 1;
                    stroke = cut.StrokeId;
                    groupTime = n.Time;
                    groupGood = true;
                }
                else groupGood &= valid;
            }
            if (groupGood && count >= AchievementCatalog.SweepRequiredGroups) return true;
        }
        return false;
    }

    public int OpeningFailures()
    {
        int total = 0, failed = 0;
        foreach (var group in groups)
        {
            foreach (int k in group)
            {
                var n = notes[k];
                if (!n.Result.HasValue) return -1;
                total++;
                if (n.Result == JudgmentTier.Bad || n.Result == JudgmentTier.Miss) failed++;
            }
            if (total >= AchievementCatalog.SlowStartOpeningNotes) return failed;
        }
        return -1;
    }

    public List<int> StrongSectionEntries()
    {
        var result = new List<int>();
        if (Stage?.sections == null || !Finite(PlayStartSeconds) || !Finite(PlayEndSeconds)
            || PlayEndSeconds <= PlayStartSeconds) return result;
        var starts = new SortedDictionary<double, double>();
        foreach (var s in Stage.sections)
        {
            if (s == null || !Finite(s.startSeconds) || !Finite(s.endSeconds) || !Finite(s.intensity)
                || s.startSeconds < PlayStartSeconds || s.startSeconds >= PlayEndSeconds
                || s.endSeconds <= s.startSeconds || s.intensity < StagePerformanceTimeline.StrongIntensity) continue;
            double end = Math.Min(s.endSeconds, PlayEndSeconds);
            if (!starts.ContainsKey(s.startSeconds) || end > starts[s.startSeconds]) starts[s.startSeconds] = end;
        }
        if (starts.Count < AchievementCatalog.SceneMasterMinSections) return result;
        foreach (var s in starts)
        {
            var group = GroupFrom(s.Key - AchievementCatalog.SceneMasterLeadSeconds, true);
            if (group == null || group.Count == 0 || result.Contains(group[0])
                || notes[group[0]].Time >= s.Value || notes[group[0]].Time > s.Key + AchievementCatalog.SceneMasterEntrySeconds)
                return new List<int>();
            AddGroup(result, group);
        }
        return result;
    }

    public sealed class RainbowCause
    {
        public int NoteIndex, CutIndex, Before, After;
        public bool OnlyFinalCutsRemained;
    }
    public RainbowCause FirstRainbowCause { get; private set; }
    public int? FinalVoltageLevel { get; set; }

    public void VoltageJudgment(int k, int cutIndex, int before, int after, int previousMaximum)
    {
        FinalVoltageLevel = after;
        if (FirstRainbowCause != null || previousMaximum >= VoltageMeter.MaximumLevel
            || before >= VoltageMeter.MaximumLevel || after != VoltageMeter.MaximumLevel) return;
        bool finalOnly = ActionsComplete && k >= 0 && k < notes.Count && notes[k].Final && cutIndex == notes[k].Cuts - 1;
        foreach (var n in notes)
            if (!n.Result.HasValue && (!n.Final || n.Cuts - n.CutsDone > 1)) finalOnly = false;
        FirstRainbowCause = new RainbowCause { NoteIndex = k, CutIndex = cutIndex, Before = before, After = after,
            OnlyFinalCutsRemained = finalOnly };
    }

    public bool HasLastRainbow() => ActionsComplete && FirstRainbowCause != null
        && FirstRainbowCause.OnlyFinalCutsRemained && AllSuccess(finalGroup) && FinalVoltageLevel == VoltageMeter.MaximumLevel;

    // D は通常ノーツの履歴とは別に、判定へ渡した照準を曲時計の区間で積算する。
    public sealed class AimObservation
    {
        public bool Complete = true, Resolved;
        public int Targets, Spikes;
        public double ActiveSeconds, RedSeconds, BlueSeconds;
        public bool MissingDanger;
        double previousSong = double.NaN, previousJudge;
        bool previousRed, previousBlue;

        public void Observe(double songTime, double judgeTime, double start, double end, AimDodgeSession session,
            AimHandSample red, AimHandSample blue)
        {
            if (!Finite(songTime) || !Finite(judgeTime) || session == null || !Finite(end) || end <= start)
            { Complete = false; return; }
            Targets = session.Targets.Count;
            Spikes = session.Spikes.Count;
            double now = Math.Max(start, Math.Min(end, songTime));
            if (!Finite(previousSong))
            {
                if (songTime > start + FinalNoteGroup.Tolerance) Complete = false;
                previousSong = now;
                previousJudge = judgeTime;
                previousRed = red.Valid;
                previousBlue = blue.Valid;
                return;
            }
            double elapsed = now - previousSong;
            if (elapsed < 0) Complete = false;
            if (elapsed > 0)
            {
                ActiveSeconds += elapsed;
                bool redValid = previousRed && red.Valid, blueValid = previousBlue && blue.Valid;
                if (redValid) RedSeconds += elapsed;
                if (blueValid) BlueSeconds += elapsed;
                if (!redValid || !blueValid)
                    foreach (var spike in session.Spikes)
                        if (previousJudge <= spike.Time + AimDodgeRules.SpikeWindow
                            && judgeTime >= spike.Time - AimDodgeRules.SpikeWindow) { MissingDanger = true; break; }
            }
            previousSong = now;
            previousJudge = judgeTime;
            previousRed = red.Valid;
            previousBlue = blue.Valid;
        }

        public bool Nothorn(int hit, int miss) => Complete && Resolved && Targets > 0 && Spikes > 0
            && hit >= 0 && miss >= 0 && hit + miss == Targets
            && (long)hit * AchievementCatalog.NothornHitDenominator >= (long)Targets * AchievementCatalog.NothornHitNumerator
            && ActiveSeconds > 0 && RedSeconds + 1e-9 >= ActiveSeconds * AchievementCatalog.NothornVisibleRate
            && BlueSeconds + 1e-9 >= ActiveSeconds * AchievementCatalog.NothornVisibleRate && !MissingDanger;
    }
}
