using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>
    /// 上の難易度から、下の難易度の候補を作る(StepMania の Little・NoJumps と同じ考え方)。
    /// 候補は足すだけで、人が置いたノーツは消さない。最後は人が耳と目で直す前提の下書き。
    /// </summary>
    public static class SaberChartDerive
    {
        public sealed class Rules
        {
            // 残す拍の位置(1=表拍だけ、2=8分まで)。
            public int beatDivision = 1;
            // 同じ手の次のノーツまでの最短(拍と ms の両方を満たす)。
            public float minSameHandBeats = 1f;
            public float minSameHandMs = 500f;
            // 同時に置く数の上限。
            public int maxSimultaneous = 1;
            // 方向ノーツを TAP にする(Easy)。
            public bool arrowsToTaps = true;
            // この長さより短い LONG は TAP にする。
            public float minLongBeats = 1f;
            public float minLongMs = 1000f;

            public static Rules For(string targetDifficulty)
            {
                if (string.Equals(targetDifficulty, "normal", StringComparison.OrdinalIgnoreCase))
                    return new Rules
                    {
                        beatDivision = 2, minSameHandBeats = .5f, minSameHandMs = 250f, maxSimultaneous = 2,
                        arrowsToTaps = false, minLongBeats = 1f, minLongMs = 500f,
                    };
                return new Rules();
            }
        }

        /// <summary>
        /// 候補を作る。beatAt は時刻(ms)から今の格子の拍を返す。existing にある同じノーツは候補にしない。
        /// </summary>
        public static List<SaberChartNote> Candidates(IEnumerable<SaberChartNote> upper, Rules rules,
            Func<float, float> beatAt, SaberChartDocument existing)
        {
            var result = new List<SaberChartNote>();
            if (upper == null || rules == null || beatAt == null) return result;
            var lastByHand = new Dictionary<string, float>();
            var lastBeatByHand = new Dictionary<string, float>();

            foreach (var group in upper.Where(note => note != null)
                         .OrderBy(note => note.time)
                         .GroupBy(note => Mathf.Round(note.time)))
            {
                float time = group.First().time;
                float beat = beatAt(time);
                // 拍頭(Normal は8分まで)だけを残す。
                float scaled = beat * rules.beatDivision;
                if (Mathf.Abs(scaled - Mathf.Round(scaled)) > .04f) continue;

                // 同時は金を優先し、次に外側(中央から遠い)を残す。
                List<SaberChartNote> picks = group
                    .OrderByDescending(note => note.color == SaberChartUtility.ColorGold)
                    .ThenByDescending(note => Mathf.Abs(note.x))
                    .ToList();
                int kept = 0;
                foreach (SaberChartNote source in picks)
                {
                    if (kept >= Mathf.Max(1, rules.maxSimultaneous)) break;
                    string[] hands = HandsOf(source.color);
                    if (hands.Any(hand => TooClose(hand, time, beat, lastByHand, lastBeatByHand, rules))) continue;
                    SaberChartNote candidate = Simplify(source, rules, beatAt);
                    candidate.beat = beat;
                    if (existing?.notes != null && existing.notes.Any(note => SaberChartRangeOps.SameSlot(note, candidate))) continue;
                    result.Add(candidate);
                    kept++;
                    float end = candidate.time + SaberChartUtility.EffectiveLongLengthMs(candidate);
                    float endBeat = beatAt(end);
                    foreach (string hand in hands)
                    {
                        lastByHand[hand] = end;
                        lastBeatByHand[hand] = endBeat;
                    }
                }
            }
            return result;
        }

        private static bool TooClose(string hand, float time, float beat,
            Dictionary<string, float> lastByHand, Dictionary<string, float> lastBeatByHand, Rules rules)
        {
            if (!lastByHand.TryGetValue(hand, out float lastTime)) return false;
            float lastBeat = lastBeatByHand[hand];
            return time - lastTime < rules.minSameHandMs - .5f || beat - lastBeat < rules.minSameHandBeats - .001f;
        }

        // 赤=右手、青=左手、金・自由=どちらの手でもよいので、両手の間隔を見る。
        private static string[] HandsOf(string color)
        {
            if (color == SaberChartUtility.ColorRed) return new[] { "right" };
            if (color == SaberChartUtility.ColorBlue) return new[] { "left" };
            return new[] { "left", "right" };
        }

        private static SaberChartNote Simplify(SaberChartNote source, Rules rules, Func<float, float> beatAt)
        {
            SaberChartNote note = source.Clone();
            if (note.type == SaberChartUtility.TypeLong)
            {
                float length = SaberChartUtility.EffectiveLongLengthMs(note);
                float lengthBeats = beatAt(note.time + length) - beatAt(note.time);
                if (length < rules.minLongMs || lengthBeats < rules.minLongBeats - .001f)
                {
                    note.type = note.direction == SaberChartUtility.DirectionNone
                        ? SaberChartUtility.TypeTap
                        : SaberChartUtility.TypeDirection;
                    note.count = 1;
                    note.lengthMs = 0f;
                }
            }
            if (rules.arrowsToTaps && note.direction != SaberChartUtility.DirectionNone)
            {
                note.direction = SaberChartUtility.DirectionNone;
                if (note.type == SaberChartUtility.TypeDirection) note.type = SaberChartUtility.TypeTap;
            }
            return note;
        }

        /// <summary>難易度の1つ上(Easy→Normal、Normal→Hard)。Hard は無し。</summary>
        public static string UpperDifficulty(string difficulty)
        {
            switch ((difficulty ?? string.Empty).ToLowerInvariant())
            {
                case "easy": return "normal";
                case "normal": return "hard";
                default: return null;
            }
        }
    }
}
