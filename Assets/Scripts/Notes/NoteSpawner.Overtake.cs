using System;
using UnityEngine;

// 判定用のchart.notesは時刻順のまま保ち、Pの出現順だけを別に持つ。
public partial class NoteSpawner
{
    int[] overtakeSpawnOrder;
    bool[] overtakeSpawned;
    int overtakeCursor;
    double overtakeApproach;

    public bool HasOvertakeNotes { get; private set; }
    public double OvertakeApproach => overtakeApproach;

    // 別の飛来方式を上書きしない。生成道具では併用そのものを検査で拒否する。
    bool UsesOvertake(NoteData data) =>
        data != null && data.HasOvertake && !HasFall && scrollMap == null
        && !data.HasWind && !LongSlideRules.Enabled(data)
        && VanishRules.Parse(data.vanish) == VanishMode.None;

    void ResetOvertakeSchedule()
    {
        overtakeSpawnOrder = null;
        overtakeSpawned = null;
        overtakeCursor = 0;
        HasOvertakeNotes = false;
        overtakeApproach = OvertakeRules.BaseApproach(approachTime);
        if (chart?.notes == null) return;

        foreach (var data in chart.notes)
            if (UsesOvertake(data)) { HasOvertakeNotes = true; break; }
        if (!HasOvertakeNotes) return;

        int count = chart.notes.Count;
        overtakeSpawnOrder = new int[count];
        overtakeSpawned = new bool[count];
        for (int i = 0; i < count; i++) overtakeSpawnOrder[i] = i;
        Array.Sort(overtakeSpawnOrder, (a, b) =>
        {
            int comparison = SpawnTimeFor(chart.notes[a]).CompareTo(SpawnTimeFor(chart.notes[b]));
            // 同じ出現時刻では元の譜面順を保つ。
            return comparison != 0 ? comparison : a.CompareTo(b);
        });
    }

    public double SpawnTimeFor(NoteData data)
    {
        double hit = EffectiveTime(data);
        if (!UsesOvertake(data)) return SpawnTimeFor(hit);
        float scale = OvertakeRules.FlightScale(data.approachScale, overtakeApproach);
        return hit - OvertakeRules.Duration(overtakeApproach, scale);
    }

    // 最初の到着ノーツより、後のノーツが先に見え始める場合も含む。
    public double EarliestSpawnTime
    {
        get
        {
            if (chart?.notes == null || chart.notes.Count == 0)
                return double.PositiveInfinity;
            return SpawnTimeFor(chart.notes[
                overtakeSpawnOrder != null ? overtakeSpawnOrder[0] : 0]);
        }
    }

    bool SpawnOvertakeDue(double songTime)
    {
        if (overtakeSpawnOrder == null) return false;
        while (overtakeCursor < overtakeSpawnOrder.Length)
        {
            int index = overtakeSpawnOrder[overtakeCursor];
            var data = chart.notes[index];
            if (songTime < SpawnTimeFor(data)) break;
            SpawnOne(data, songTime: songTime);
            overtakeSpawned[index] = true;
            overtakeCursor++;
            // NextIndexは時刻順で最初の未生成ノーツ。生成済みの件数とは別。
            while (nextIndex < overtakeSpawned.Length && overtakeSpawned[nextIndex])
                nextIndex++;
        }
        return true;
    }

    // 剣の受信監視にも、個別の出現時刻を渡す。
    public bool UpcomingNeeds(double songTime, SaberHand hand)
    {
        if (overtakeSpawnOrder == null)
            return chart != null && PlayTrackingWatch.UpcomingNeeds(
                chart.notes, nextIndex, EffectiveTime, approachTime, songTime, hand);
        for (int i = overtakeCursor; i < overtakeSpawnOrder.Length; i++)
        {
            var data = chart.notes[overtakeSpawnOrder[i]];
            if (SpawnTimeFor(data) - songTime > PlayTrackingWatch.ComingWithinSeconds) break;
            if (hand == SaberHand.Any || SaberHandHelper.FromColor(data.color) == hand)
                return true;
        }
        return false;
    }

    void UpdateOvertakeNote(CuttableNote note, double songTime)
    {
        if (!note.HasOvertake) return;
        double remaining = note.HitTime - songTime;
        float speed = (spawnZ - judgeZ) / (float)note.OvertakeApproach;
        Vector3 position = OvertakeRules.Position(note.LandingPosition,
            note.OvertakeLaneX, remaining, note.OvertakeApproach,
            note.OvertakeScale, spawnZ, judgeZ);
        // 到着後のロングの滞留と、ミス後の退場は従来の時計で計算する。
        position.z = ComputeNoteZ(note,
            OvertakeRules.Remaining(remaining, note.OvertakeScale), speed);
        note.transform.position = position;
        note.FlowNoteVelocity = note.RequiredCutCount > 1 ? Vector3.zero
            : Vector3.back * (remaining > OvertakeRules.CommonSeconds
                ? speed / note.OvertakeScale : speed);
    }
}
