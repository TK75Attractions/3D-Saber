using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Saber.ChartEditor
{
    public enum SaberChartIssueLevel { Error, Warning, Info }

    // 譜面チェックの1件。notes は chart.notes の添字(エディターの移動先・AI依頼の対象)。timeMs は最初のノーツの譜面時刻。
    [Serializable]
    public sealed class SaberChartIssue
    {
        public string rule;
        public SaberChartIssueLevel level;
        public int[] notes = Array.Empty<int>();
        public float timeMs;
        public string message;
    }

    // 本編で遊べるかを見る譜面チェック。エディターの「診断」と、プレイレビューのAI依頼・変更案の比較が同じ規則を使う。
    // 規則は本編の判定(SaberCutJudge)に合わせ、しきい値は 2026-10-16 時点の全43譜面での件数から決めた
    // (エラーにした規則は、今の譜面では0件。Hard で意図して使っている短い間隔は、難易度で扱いを変える)。
    public static class SaberChartLint
    {
        // 同時とみなす時刻の差(本編の SaberCutJudge.SimultaneousSeconds)。
        public const float SimultaneousMs = SaberCutJudge.SimultaneousSeconds * 1000f;
        // 刃の線からノーツの中心まで、これ以内なら当たる(本編の刃 0.32 + ノーツ 0.88)。
        public const float HitRange = 1.2f;
        // 本編が同時押しの線(同時線)を引く時刻の差(NoteSpawner.SimultaneousEpsilonSeconds)。
        public const float GuideLineMs = (float)(NoteSpawner.SimultaneousEpsilonSeconds * 1000.0);
        // ほぼ同じ時刻で、中心がこれより近いノーツは見た目が重なる。
        public const float OverlapDistance = .8f;
        // 同じ手の続きがこれより短いと、振り返す時間がない(なぞりで取れる並びは除く)。
        public const float FastSameHandMs = 150f;
        // Easy・Normal では、同じ手の続きがこれより短いと注意する(Hard は意図して使うので出さない)。
        public const float EasySameHandMs = 250f;
        // 同じ向きの矢印が同じ手で続くとき、これより短いと振り直す(戻す)時間がない。
        public const float ResetMs = 400f;
        // 同じ手で矢印がこの数だけ続くと知らせる。
        public const int ArrowRun = 4;
        // ロングを切る回数の速さの上限(1秒あたり)。今の譜面の最大は 3.0(RushE Hard)。
        public const float MaxLongCutsPerSecond = 4f;
        // 2秒の窓に入るノーツの数の上限(今の譜面の最大と同じ)。これを越える山を知らせる。
        static readonly Dictionary<string, int> PeakPerTwoSeconds = new Dictionary<string, int>
        { { "easy", 6 }, { "normal", 9 }, { "hard", 13 } };

        // 同時(SimultaneousMs 以内)をまとめた時刻順の組。エディターの「同時打ち」の操作と派生も同じ幅を使う。
        public static List<List<SaberChartNote>> GroupSimultaneous(IEnumerable<SaberChartNote> notes)
        {
            var result = new List<List<SaberChartNote>>();
            foreach (SaberChartNote note in notes.Where(n => n != null).OrderBy(n => n.time))
            {
                if (result.Count > 0 && note.time - result[result.Count - 1][0].time <= SimultaneousMs) result[result.Count - 1].Add(note);
                else result.Add(new List<SaberChartNote> { note });
            }
            return result;
        }

        public static string Label(SaberChartIssueLevel level) =>
            level == SaberChartIssueLevel.Error ? "エラー" : level == SaberChartIssueLevel.Warning ? "注意" : "情報";

        public static List<SaberChartIssue> Check(SaberChartDocument chart, string difficulty, float audioLengthMs = 0f)
        {
            var issues = new List<SaberChartIssue>();
            if (chart?.notes == null || chart.notes.Count == 0) return issues;
            string level = (difficulty ?? "").Trim().ToLowerInvariant();
            bool easier = level == "easy" || level == "normal";
            CheckFormat(chart, audioLengthMs, issues);
            foreach (string hand in new[] { SaberChartUtility.ColorBlue, SaberChartUtility.ColorRed })
                CheckHand(chart, hand, easier, issues);
            CheckBothHands(chart, easier, issues);
            CheckDensity(chart, level, issues);
            return issues.OrderBy(i => i.timeMs).ThenBy(i => i.level).ThenBy(i => i.rule, StringComparer.Ordinal).ToList();
        }

        // 内容が同じなら同じ値。毎描画で検査をやり直さないためのキャッシュの鍵。
        public static int Fingerprint(SaberChartDocument chart)
        {
            if (chart?.notes == null) return 0;
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + chart.notes.Count;
                hash = hash * 31 + chart.offsetMs.GetHashCode();
                hash = hash * 31 + chart.coordScale.GetHashCode();
                foreach (SaberChartNote n in chart.notes)
                {
                    if (n == null) { hash = hash * 31; continue; }
                    hash = hash * 31 + n.time.GetHashCode();
                    hash = hash * 31 + n.x.GetHashCode();
                    hash = hash * 31 + n.y.GetHashCode();
                    hash = hash * 31 + n.count;
                    hash = hash * 31 + n.lengthMs.GetHashCode();
                    hash = hash * 31 + (n.type ?? "").GetHashCode();
                    hash = hash * 31 + (n.color ?? "").GetHashCode();
                    hash = hash * 31 + (n.direction ?? "").GetHashCode();
                }
                return hash;
            }
        }

        // 下の難易度は、上の難易度から間引いて作る決まり。上に同じ時刻(25ms以内、OFFSET込み)がないノーツを1件にまとめて知らせる。
        public static SaberChartIssue NotInUpper(SaberChartDocument chart, SaberChartDocument upper, string upperLabel)
        {
            if (chart?.notes == null || upper?.notes == null || upper.notes.Count == 0) return null;
            float[] times = upper.notes.Where(n => n != null).Select(n => n.time + upper.offsetMs).OrderBy(t => t).ToArray();
            var missing = new List<int>();
            for (int i = 0; i < chart.notes.Count; i++)
            {
                if (chart.notes[i] == null) continue;
                float time = chart.notes[i].time + chart.offsetMs;
                int k = Array.BinarySearch(times, time - SimultaneousMs);
                if (k < 0) k = ~k;
                if (k >= times.Length || times[k] > time + SimultaneousMs) missing.Add(i);
            }
            if (missing.Count == 0) return null;
            return new SaberChartIssue
            {
                rule = "not-in-upper", level = SaberChartIssueLevel.Info, notes = missing.ToArray(),
                timeMs = missing.Min(i => chart.notes[i].time),
                message = $"{upperLabel} に同じ時刻がないノーツ {missing.Count}個（下の難易度は上から間引いて作る決まり）"
            };
        }

        // 1行の説明(AIへの依頼・一覧の表示用)。時刻は音源上の秒。
        public static string Describe(SaberChartDocument chart, SaberChartIssue issue)
        {
            float seconds = (issue.timeMs + (chart?.offsetMs ?? 0f)) / 1000f;
            string notes = issue.notes.Length == 0 ? "" : " / ノーツ[" + string.Join(",", issue.notes) + "]";
            return FormattableString.Invariant($"{Label(issue.level)} {seconds:0.000}秒: {issue.message}{notes}");
        }

        static void CheckFormat(SaberChartDocument chart, float audioLengthMs, List<SaberChartIssue> issues)
        {
            CheckOverlaps(chart, issues);
            for (int i = 0; i < chart.notes.Count; i++)
            {
                SaberChartNote n = chart.notes[i];
                if (n == null) continue;
                float audio = n.time + chart.offsetMs;
                if (audio < 0f) Add(issues, SaberChartIssueLevel.Error, "before-start", chart, "曲が始まる前のノーツ", i);
                if (audioLengthMs > 0f && audio + SaberChartUtility.EffectiveLongLengthMs(n) > audioLengthMs)
                    Add(issues, SaberChartIssueLevel.Error, "after-end", chart, "音源の終わりを越えるノーツ・ロング", i);
                if (n.type == SaberChartUtility.TypeDirection && n.direction == SaberChartUtility.DirectionNone)
                    Add(issues, SaberChartIssueLevel.Error, "no-arrow", chart, "矢印ノーツの向きが未指定", i);
                float longMs = SaberChartUtility.EffectiveLongLengthMs(n);
                if (n.count > 1 && longMs > 0f && (n.count - 1) / (longMs / 1000f) > MaxLongCutsPerSecond)
                    Add(issues, SaberChartIssueLevel.Warning, "long-rate", chart,
                        $"ロングの回数に対して長さが短い（1秒に{(n.count - 1) / (longMs / 1000f):0.0}回切る）", i);
                float x = n.x * Scale(chart), y = n.y * Scale(chart);
                if (x < SaberChartUtility.DefaultXMin || x > SaberChartUtility.DefaultXMax ||
                    y < SaberChartUtility.DefaultYMin || y > SaberChartUtility.DefaultYMax)
                    Add(issues, SaberChartIssueLevel.Warning, "outside", chart, "推奨XY範囲の外", i);
            }
        }

        // 重なりと、時刻のそろっていない同時押し。完全に同じものはエラー、ほぼ同じ時刻・近い位置は注意。
        // 同時に切れる幅(25ms)の中でも、本編の同時線は10ms以内にしか出ないので、ずれた和音は知らせる。
        static void CheckOverlaps(SaberChartDocument chart, List<SaberChartIssue> issues)
        {
            var order = Enumerable.Range(0, chart.notes.Count).Where(i => chart.notes[i] != null).OrderBy(i => chart.notes[i].time).ToList();
            for (int a = 0; a < order.Count; a++)
            for (int b = a + 1; b < order.Count && chart.notes[order[b]].time - chart.notes[order[a]].time <= SimultaneousMs; b++)
            {
                SaberChartNote first = chart.notes[order[a]], second = chart.notes[order[b]];
                float gap = second.time - first.time;
                float distance = Vector2.Distance(new Vector2(first.x, first.y), new Vector2(second.x, second.y)) * Scale(chart);
                if (distance >= OverlapDistance) continue;
                if (gap <= 1f && distance < .001f)
                    Add(issues, SaberChartIssueLevel.Error, "duplicate", chart, "同じ時刻・同じ位置に重なったノーツ", order[a], order[b]);
                else
                    Add(issues, SaberChartIssueLevel.Warning, "overlap", chart,
                        $"ほぼ同じ時刻（{gap:0}ms差）・近い位置（{distance:0.00}）で重なって見えるノーツ", order[a], order[b]);
            }
            foreach (List<int> group in Groups(chart, order))
            {
                if (group.Count < 2) continue;
                float spread = chart.notes[group[group.Count - 1]].time - chart.notes[group[0]].time;
                if (spread > GuideLineMs)
                    Add(issues, SaberChartIssueLevel.Info, "chord-offset", chart,
                        $"同時押しの時刻が{spread:0}msずれている（本編では同時に切れるが、同時線は{GuideLineMs:0}ms以内にしか出ない）", group.ToArray());
            }
        }

        // 同じ手の流れ。金(どちらの手でも切れる)は手の列に入れない。
        static void CheckHand(SaberChartDocument chart, string color, bool easier, List<SaberChartIssue> issues)
        {
            string handName = color == SaberChartUtility.ColorBlue ? "左手(青)" : "右手(赤)";
            List<List<int>> groups = Groups(chart, Enumerable.Range(0, chart.notes.Count)
                .Where(i => chart.notes[i] != null && chart.notes[i].color == color));
            float longEnd = float.NegativeInfinity;
            int longIndex = -1;
            var arrowRun = new List<int>();
            for (int g = 0; g < groups.Count; g++)
            {
                List<int> group = groups[g];
                float time = chart.notes[group[0]].time;
                if (group.Count > 1 && Spread(chart, group) > 2f * HitRange)
                    Add(issues, SaberChartIssueLevel.Warning, "same-hand-far", chart,
                        $"{handName}の同時ノーツが離れすぎ(1振りで届きにくい)", group.ToArray());
                if (time < longEnd - SimultaneousMs)
                    Add(issues, SaberChartIssueLevel.Error, "long-overlap", chart,
                        $"{handName}のロングの途中に同じ手のノーツ", new[] { longIndex }.Concat(group).ToArray());
                foreach (int i in group)
                {
                    float end = chart.notes[i].time + SaberChartUtility.EffectiveLongLengthMs(chart.notes[i]);
                    if (SaberChartUtility.EffectiveLongLengthMs(chart.notes[i]) > 0f && end > longEnd) { longEnd = end; longIndex = i; }
                }

                if (group.Any(i => chart.notes[i].type == SaberChartUtility.TypeDirection)) arrowRun.AddRange(group);
                else FlushArrowRun(chart, handName, arrowRun, issues);

                if (g == 0) continue;
                List<int> previous = groups[g - 1];
                float gap = time - chart.notes[previous[0]].time;
                bool sweep = Sweepable(chart, previous, group);
                if (gap < FastSameHandMs)
                {
                    if (sweep)
                        Add(issues, SaberChartIssueLevel.Info, "sweep", chart,
                            $"{handName}の続きが{gap:0}ms。隣へなぞれば1振りで続けて切れる並び", previous.Concat(group).ToArray());
                    else
                        Add(issues, SaberChartIssueLevel.Warning, "fast-same-hand", chart,
                            $"{handName}の続きが{gap:0}ms(振り返す時間がない)", previous.Concat(group).ToArray());
                }
                else if (easier && gap < EasySameHandMs)
                    Add(issues, SaberChartIssueLevel.Warning, "fast-same-hand", chart,
                        $"{handName}の続きが{gap:0}ms(Easy・Normal には速い)", previous.Concat(group).ToArray());

                string a = FirstArrow(chart, previous), b = FirstArrow(chart, group);
                if (a != null && b != null && gap < ResetMs && !sweep && Angle(a, b) <= 45f)
                    Add(issues, SaberChartIssueLevel.Warning, "reset", chart,
                        $"{handName}で同じ向きの矢印が{gap:0}ms で続く(振り直す時間がない)", previous.Concat(group).ToArray());
            }
            FlushArrowRun(chart, handName, arrowRun, issues);
        }

        static void FlushArrowRun(SaberChartDocument chart, string handName, List<int> run, List<SaberChartIssue> issues)
        {
            int groups = CountGroups(chart, run);
            if (groups >= ArrowRun)
                Add(issues, SaberChartIssueLevel.Info, "arrow-run", chart, $"{handName}で矢印が{groups}回続く", run.ToArray());
            run.Clear();
        }

        static int CountGroups(SaberChartDocument chart, List<int> indices) => Groups(chart, indices).Count;

        // 赤と青が同じ時刻にあるとき。内向き(互いへ振る)は手がぶつかる。左右の交差は Easy・Normal では注意。
        static void CheckBothHands(SaberChartDocument chart, bool easier, List<SaberChartIssue> issues)
        {
            foreach (List<int> group in Groups(chart, Enumerable.Range(0, chart.notes.Count).Where(i => chart.notes[i] != null)))
            {
                foreach (int blue in group.Where(i => chart.notes[i].color == SaberChartUtility.ColorBlue))
                foreach (int red in group.Where(i => chart.notes[i].color == SaberChartUtility.ColorRed))
                {
                    SaberChartNote l = chart.notes[blue], r = chart.notes[red];
                    Vector2 dl = Arrow(l.direction), dr = Arrow(r.direction);
                    if (dl.x > .5f && dr.x < -.5f && l.x < r.x)
                        Add(issues, SaberChartIssueLevel.Error, "handclap", chart, "内向きの同時押し(左右の手がぶつかる)", blue, red);
                    else if (l.x * Scale(chart) > r.x * Scale(chart) + .25f)
                        Add(issues, easier ? SaberChartIssueLevel.Warning : SaberChartIssueLevel.Info, "crossed", chart,
                            "青が赤より右にある同時押し(左右の手が交差)", blue, red);
                }
            }
        }

        static void CheckDensity(SaberChartDocument chart, string difficulty, List<SaberChartIssue> issues)
        {
            if (!PeakPerTwoSeconds.TryGetValue(difficulty, out int limit)) return;
            var order = Enumerable.Range(0, chart.notes.Count).Where(i => chart.notes[i] != null)
                .OrderBy(i => chart.notes[i].time).ToList();
            int start = 0, bestCount = 0, bestStart = 0, bestEnd = 0;
            for (int end = 0; end < order.Count; end++)
            {
                while (chart.notes[order[end]].time - chart.notes[order[start]].time > 2000f) start++;
                if (end - start + 1 > bestCount) { bestCount = end - start + 1; bestStart = start; bestEnd = end; }
            }
            if (bestCount > limit)
                Add(issues, SaberChartIssueLevel.Info, "density", chart,
                    $"2秒に{bestCount}個の山(この難易度の今の譜面は最大{limit}個)",
                    order.GetRange(bestStart, bestEnd - bestStart + 1).ToArray());
        }

        // 同時(25ms以内)をまとめた時刻順の組。
        static List<List<int>> Groups(SaberChartDocument chart, IEnumerable<int> indices)
        {
            var result = new List<List<int>>();
            foreach (int i in indices.OrderBy(i => chart.notes[i].time).ThenBy(i => i))
            {
                if (result.Count > 0 && chart.notes[i].time - chart.notes[result[result.Count - 1][0]].time <= SimultaneousMs)
                    result[result.Count - 1].Add(i);
                else result.Add(new List<int> { i });
            }
            return result;
        }

        // 1振りで隣へなぞって続けて切れる並びか(本編の「なぞり」と同じ考え)。
        // 同じ位置の連打はなぞれない。矢印があれば、なぞる向きと±45°以内で合っていること。
        static bool Sweepable(SaberChartDocument chart, List<int> from, List<int> to)
        {
            Vector2 a = Center(chart, from), b = Center(chart, to), d = b - a;
            float distance = d.magnitude;
            if (distance < .4f || distance > 2f * HitRange) return false;
            foreach (int i in from.Concat(to))
            {
                Vector2 arrow = Arrow(chart.notes[i].direction);
                if (arrow != Vector2.zero && Vector2.Dot(arrow, d / distance) < Mathf.Cos(45f * Mathf.Deg2Rad)) return false;
            }
            return true;
        }

        static Vector2 Center(SaberChartDocument chart, List<int> group)
        {
            Vector2 sum = Vector2.zero;
            foreach (int i in group) sum += new Vector2(chart.notes[i].x, chart.notes[i].y) * Scale(chart);
            return sum / group.Count;
        }

        static float Spread(SaberChartDocument chart, List<int> group)
        {
            float max = 0f;
            foreach (int i in group)
            foreach (int j in group)
                max = Mathf.Max(max, Vector2.Distance(new Vector2(chart.notes[i].x, chart.notes[i].y),
                    new Vector2(chart.notes[j].x, chart.notes[j].y)) * Scale(chart));
            return max;
        }

        static string FirstArrow(SaberChartDocument chart, List<int> group) => group
            .Select(i => chart.notes[i].direction)
            .FirstOrDefault(d => !string.IsNullOrEmpty(d) && d != SaberChartUtility.DirectionNone);

        static Vector2 Arrow(string direction) => CutDirectionHelper.ToVector(CutDirectionHelper.Parse(direction));

        static float Angle(string a, string b) => Vector2.Angle(Arrow(a), Arrow(b));

        static float Scale(SaberChartDocument chart) => chart.coordScale == 0f ? 1f : chart.coordScale;

        static void Add(List<SaberChartIssue> issues, SaberChartIssueLevel level, string rule, SaberChartDocument chart,
            string message, params int[] notes)
        {
            int[] sorted = notes.Where(i => i >= 0).Distinct().OrderBy(i => i).ToArray();
            issues.Add(new SaberChartIssue
            {
                rule = rule, level = level, notes = sorted, message = message,
                timeMs = sorted.Length == 0 ? 0f : sorted.Min(i => chart.notes[i].time)
            });
        }
    }
}
