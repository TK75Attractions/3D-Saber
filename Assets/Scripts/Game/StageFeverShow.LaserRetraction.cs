using System;
using UnityEngine;

// 33A: 区間を読み込み時に結合し、曲時計だけからレーザーの巻き取りを再現する。
public sealed partial class StageFeverShow
{
    internal struct LaserState { public float length, width; public double poseTime; }

    void MergeStrongSections()
    {
        strong.Sort((a, b) => a.start.CompareTo(b.start));
        for (int i = 1; i < strong.Count;)
        {
            var previous = strong[i - 1];
            var current = strong[i];
            if (current.start <= previous.end + .001)
            {
                strong[i - 1] = (previous.start, Math.Max(previous.end, current.end));
                strong.RemoveAt(i);
            }
            else i++;
        }
    }

    internal LaserState LaserAt(double songTime)
    {
        var value = new LaserState { poseTime = songTime };
        if (!Finite(songTime)) return value;
        foreach (var section in strong)
        {
            if (songTime < section.start) break;
            if (songTime < section.end)
                return new LaserState {
                    length = 18f, width = Mathf.Clamp01((float)((songTime - section.start) / FeverInSeconds)),
                    poseTime = songTime
                };
            double age = songTime - section.end;
            if (age >= FeverOutSeconds) continue;
            value = new LaserState {
                length = 18f * Mathf.Clamp01(1f - (float)(age / FeverOutSeconds)),
                width = Mathf.Clamp01((float)((section.end - section.start) / FeverInSeconds))
                    * Mathf.Clamp01(1f - (float)((age - .3) / .2)),
                poseTime = section.end
            };
        }
        return value;
    }
}
