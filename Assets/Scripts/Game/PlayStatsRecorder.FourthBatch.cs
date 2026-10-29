using System;
using System.Collections.Generic;
using UnityEngine;

public sealed partial class PlayStatsRecorder
{
    readonly Dictionary<CuttableNote, (int index, uint generation)> cutIndices =
        new Dictionary<CuttableNote, (int, uint)>();
    AimDodgeMode observedAim;

    double JudgmentClock => manager != null && manager.songPlayer != null
        ? spawner.JudgeClock(manager.songPlayer.SongTime) : double.NaN;

    bool Current(PlayStats stats, int k, CuttableNote note, uint generation) =>
        bound && !Stale && ReferenceEquals(Stats, stats) && note != null && note.SpawnVersion == generation
        && stats.Notes[k].Generation == generation;

    void PartialRecorded(PlayStats stats, int k, CuttableNote cut, uint generation, int index, int total)
    {
        if (!Current(stats, k, cut, generation)) return;
        stats.AcceptCut(k, index, generation, cut.LastCutterHand, cut.LastCutSongTime ?? JudgmentClock, cut.LastCutRecord);
        if (stats.Notes[k].Final && index < total - 1) stats.FinalJudgment(scoreBefore, scoreAfter);
    }

    void WatchFourthBatch()
    {
        if (Stale || manager == null) return;
        var song = manager.songPlayer;
        if (song != null && song.IsScheduled)
        {
            Stats.PlayStartSeconds = song.PlayStartSeconds;
            Stats.PlayEndSeconds = song.ProgressEndSeconds > 0 ? song.ProgressEndSeconds : song.Duration;
        }
        Stats.CandidatesObservedUntil = Time.realtimeSinceStartupAsDouble;
        var next = manager.AimMode;
        if (observedAim == next) return;
        UnwatchAim();
        observedAim = next;
        if (observedAim == null) return;
        Stats.AimObserved = new PlayStats.AimObservation { Complete = Stats.Complete
            && (!CameraSaberHistory.Finite(observedAim.LastAchievementSongTime)
                || observedAim.LastAchievementSongTime <= Stats.PlayStartSeconds) };
        observedAim.AchievementSamples += AimSamples;
    }

    void UnwatchAim()
    {
        if (observedAim != null) observedAim.AchievementSamples -= AimSamples;
        observedAim = null;
    }

    void AimSamples(double songTime, double judgeTime, AimHandSample red, AimHandSample blue)
    {
        if (!bound || Stale || observedAim == null || Stats.AimObserved == null) return;
        Stats.AimObserved.Observe(songTime, judgeTime, Stats.PlayStartSeconds, Stats.PlayEndSeconds, observedAim.Session, red, blue);
        Stats.AimObserved.Resolved = observedAim.Session.Completed || AllDone(observedAim.Session);
    }

    // 最後の的・トゲはそのコマで Done になるが、Completed のカーソルは次のコマの初めに進む。曲の最後のコマで終わっても取れるように、ここで全部を見る。
    static bool AllDone(AimDodgeSession session)
    {
        foreach (var target in session.Targets) if (!target.Done) return false;
        foreach (var spike in session.Spikes) if (!spike.Done) return false;
        return true;
    }

    void VoltageJudgment(CuttableNote note, int cutIndex, int before, int after, int previousMaximum)
    {
        if (!bound || Stale) return;
        int k = -1;
        if (note != null && cutIndices.TryGetValue(note, out var match) && match.generation == note.SpawnVersion) k = match.index;
        Stats.VoltageJudgment(k, cutIndex, before, after, previousMaximum);
    }
}
