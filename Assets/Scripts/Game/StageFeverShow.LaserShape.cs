using System.Collections.Generic;
using UnityEngine;

// 89A: 強度で平行と扇を分け、区間の境目から1拍で形を変える。
public sealed partial class StageFeverShow
{
    internal const float ClimaxIntensity = .95f;
    struct ShapeChange { public double time, beat; public float from, target; }
    readonly List<ShapeChange> laserShapes = new List<ShapeChange>();

    internal static bool IsClimax(float intensity) => Finite(intensity) && intensity >= ClimaxIntensity;

    void BuildLaserShapes()
    {
        laserShapes.Clear();
        var sections = new List<StagePerformanceTimeline.Section>();
        var edges = new SortedSet<double>();
        if (timeline.sections != null)
            foreach (var s in timeline.sections)
            {
                if (s == null || !Finite(s.startSeconds) || !Finite(s.endSeconds) || !Finite(s.intensity)
                    || s.startSeconds < 0 || s.endSeconds <= s.startSeconds
                    || s.intensity < StagePerformanceTimeline.StrongIntensity) continue;
                sections.Add(s); edges.Add(s.startSeconds); edges.Add(s.endSeconds);
            }
        bool wasActive = false;
        float previousTarget = 0;
        foreach (double time in edges)
        {
            float intensity = 0;
            foreach (var s in sections)
                if (s.startSeconds <= time && time < s.endSeconds) intensity = Mathf.Max(intensity, s.intensity);
            bool active = intensity >= StagePerformanceTimeline.StrongIntensity;
            float target = IsClimax(intensity) ? 1f : 0f;
            // 完全に終わったときは変更を足さず、巻き取りの向きを維持する。
            if (active && (!wasActive || target != previousTarget))
            {
                double beat = motionBeats.BeatAt(time);
                float from = wasActive && laserShapes.Count > 0
                    ? ShapeAt(laserShapes[laserShapes.Count - 1], beat) : 0f;
                laserShapes.Add(new ShapeChange { time = time, beat = beat, from = from, target = target });
            }
            wasActive = active; previousTarget = target;
        }
    }

    static float ShapeAt(ShapeChange change, double beat)
        => Mathf.Lerp(change.from, change.target, Mathf.SmoothStep(0f, 1f, (float)(beat - change.beat)));

    // 曲時刻だけで決める。巻き取り時はLaserAtのposeTimeを渡す。
    internal float LaserFanAt(double poseTime)
    {
        if (!Finite(poseTime)) return 0;
        int lo = 0, hi = laserShapes.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (laserShapes[mid].time <= poseTime) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found < 0 ? 0 : ShapeAt(laserShapes[found], motionBeats.BeatAt(poseTime));
    }

    // 同じ側の3本は同じ揺れ。正面から見た角度を平行24度から外向き12・24・36度へ移す。
    internal static Vector3 LaserDirection(float fan, float sway, int index, int side)
    {
        float angle = (Mathf.Lerp(24f, 12f * (index + 1), Mathf.Clamp01(fan))
            + Mathf.Clamp(sway, -.2f, .2f) * 30f) * Mathf.Deg2Rad;
        return new Vector3(side * Mathf.Sin(angle), Mathf.Cos(angle), .65f).normalized;
    }
}
