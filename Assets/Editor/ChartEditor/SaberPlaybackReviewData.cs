using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Saber.ChartEditor
{
    [Serializable] public sealed class SaberReviewComment
    {
        public string id = Guid.NewGuid().ToString("N");
        public float fromSeconds, toSeconds, frameSeconds;
        public bool hasPoint;
        public Vector2 screenPoint;
        public string instruction;
        public int[] noteIndices = Array.Empty<int>();
        // ノーツが到達してほしい音源時刻。色・座標はAIへの配置依頼で決める。
        public float[] requestedNoteSeconds = Array.Empty<float>();
        // 打点から差し引いた、押す遅れ(ms。譜面エディターの録音で測った値)。
        public float inputOffsetMs;
    }
    [Serializable] public sealed class SaberReviewRequest
    {
        public int version = 1;
        public string songId, difficulty, chartPath, baseHash, sourceDiskHash, sourceLegacyHash;
        public string timeBasis = "音源先頭からの秒。ノーツ到達秒=(note.time+chart.offsetMs)/1000。screenPointは画像左上=(0,0)、右下=(1,1)。";
        public SaberChartDocument chart;
        public List<SaberReviewComment> comments = new List<SaberReviewComment>();
        // 依頼時点の譜面チェック(SaberChartLint)。AIが本編の決まりに合わない箇所を知るため。
        public List<SaberChartIssue> checks = new List<SaberChartIssue>();
    }
    [Serializable] public sealed class SaberReviewPatch
    {
        public int version = 1;
        public string songId, difficulty, baseHash, explanation;
        public List<SaberReviewOperation> operations = new List<SaberReviewOperation>();
    }
    [Serializable] public sealed class SaberReviewOperation
    {
        public string action;
        // 全操作で、依頼JSON内の元のnotes配列に対する0始まりの添字を使う。
        public int index = -1;
        public SaberChartNote note;
        // replace/delete で、変更前のノーツの time・color・x・y(任意)。添字の数え違いで別のノーツを変えないよう照合する。
        public SaberReviewExpect expect;
        // 任意。どの指摘への対応か(指摘の id)と理由。変更の一覧に表示する。
        public string commentId, reason;
    }
    [Serializable] public sealed class SaberReviewExpect
    {
        public float time, x, y;
        public string color;
    }

    // AIへの依頼と返信はデータだけ。コードやコマンドは実行しない。
    public static class SaberPlaybackReviewData
    {
        public static string Hash(string text)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "").ToLowerInvariant();
        }
        public static string ChartHash(SaberChartDocument chart) => Hash(SaberChartUtility.ToJson(chart, false));
        // duration が0以下(音源の長さが分からない)なら、終わりは確かめない(ValidateComments と同じ扱い)。
        public static void ValidateTimingSeconds(SaberChartDocument chart, IEnumerable<float> times, float duration)
        {
            if ((times ?? Array.Empty<float>()).Any(t => !Finite(t) || t < 0 || t * 1000 - chart.offsetMs < 0 || (duration > 0 && t > duration)))
                throw new InvalidOperationException("追加タイミングが音源または譜面の範囲外です。");
        }
        public static SaberReviewComment TimingComment(SaberChartDocument chart, IEnumerable<float> times, float duration)
        {
            var sorted = times.OrderBy(t => t).Distinct().ToArray();
            if (sorted.Length == 0) throw new InvalidOperationException("追加タイミングを記録してください。");
            ValidateTimingSeconds(chart, sorted, duration);
            return new SaberReviewComment {
                requestedNoteSeconds = sorted, fromSeconds = Mathf.Max(0, sorted[0] - .25f),
                toSeconds = Mathf.Min(duration, sorted[sorted.Length - 1] + .25f), frameSeconds = Mathf.Max(0, sorted[0] - .2f),
                instruction = "記録した各タイミングにノーツが来るように配置してください。色・位置・種類は前後の流れに合わせてお任せします。既存ノーツで満たせる時刻は再利用し、必要なノーツを追加してください。"
            };
        }
        public static float SnapAudioTime(SaberChartDocument chart, float seconds, int divisions, float duration)
        {
            if (!Finite(seconds) || divisions < 1 || !Finite(duration) || duration < 0) throw new ArgumentException("拍の刻みと時刻を確認してください。");
            var grid = new SaberChartGrid(chart.bpm, chart.beatZeroMs, chart.tempoMap);
            float beat = grid.BeatAt(seconds * 1000 - chart.offsetMs);
            return Mathf.Clamp((grid.TimeAt(Mathf.Round(beat * divisions) / divisions) + chart.offsetMs) / 1000f, 0, duration);
        }
        // 横幅から求めた許容秒で、同じ色の行にあるノーツを選ぶ。短いノーツの近くではロングの胴より開始位置を優先する。
        public static int PickTimelineNote(SaberChartDocument chart, float seconds, string color, float tolerance)
        {
            int closest = -1, tail = -1; float distance = float.PositiveInfinity, tailStart = float.NegativeInfinity;
            for (int i = 0; i < chart.notes.Count; i++)
            {
                var note = chart.notes[i]; string lane = note.color == "blue" ? "blue" : note.color == "gold" ? "gold" : "red";
                if (lane != color) continue;
                float start = (note.time + chart.offsetMs) / 1000f, delta = Mathf.Abs(start - seconds);
                if (delta <= tolerance && delta < distance) { closest = i; distance = delta; }
                float end = start + SaberChartUtility.EffectiveLongLengthMs(note) / 1000f;
                if (seconds >= start && seconds <= end && start > tailStart) { tail = i; tailStart = start; }
            }
            return closest >= 0 ? closest : tail;
        }
        public static Vector2 ChangeRange(SaberChartDocument chart, SaberReviewOperation operation)
        {
            var notes = new List<SaberChartNote>();
            if (operation.action != "add") notes.Add(chart.notes[operation.index]);
            if (operation.action != "delete") notes.Add(operation.note);
            float from = notes.Min(n => (n.time + chart.offsetMs) / 1000f);
            float to = notes.Max(n => (n.time + chart.offsetMs + SaberChartUtility.EffectiveLongLengthMs(n)) / 1000f);
            return new Vector2(from, to);
        }
        // 同時押しは一つの到達時刻として移動し、オフセットを含む音源時刻で比較する。
        public static float AdjacentNoteTime(SaberChartDocument chart, float seconds, int direction)
        {
            if (!Finite(seconds) || direction == 0) throw new ArgumentException("移動方向と現在時刻を確認してください。");
            var times = chart.notes.Select(n => (n.time + chart.offsetMs) / 1000f).Where(t => t >= 0);
            return direction > 0 ? times.Where(t => t > seconds + .0005f).DefaultIfEmpty(-1).Min()
                : times.Where(t => t < seconds - .0005f).DefaultIfEmpty(-1).Max();
        }
        public static void ValidateComments(SaberChartDocument chart, List<SaberReviewComment> comments, float duration)
        {
            if (comments == null) throw new InvalidOperationException("指摘の一覧がありません。");
            var ids = new HashSet<string>();
            foreach (var comment in comments)
            {
                if (comment == null || !Guid.TryParseExact(comment.id, "N", out _) || !ids.Add(comment.id)
                    || !Finite(comment.fromSeconds) || !Finite(comment.toSeconds) || !Finite(comment.frameSeconds)
                    || comment.fromSeconds < 0 || comment.toSeconds < comment.fromSeconds || comment.frameSeconds < 0
                    || (duration > 0 && (comment.toSeconds > duration + .001f || comment.frameSeconds > duration + .001f))
                    || string.IsNullOrWhiteSpace(comment.instruction) || comment.noteIndices == null
                    || comment.noteIndices.Any(i => i < 0 || i >= chart.notes.Count)
                    || (comment.hasPoint && (!Finite(comment.screenPoint.x) || !Finite(comment.screenPoint.y)
                        || comment.screenPoint.x < 0 || comment.screenPoint.x > 1 || comment.screenPoint.y < 0 || comment.screenPoint.y > 1)))
                    throw new InvalidOperationException("指摘の時刻・対象ノーツ・文章・画像位置を確認してください。");
                ValidateTimingSeconds(chart, comment.requestedNoteSeconds, duration);
                if ((comment.requestedNoteSeconds ?? Array.Empty<float>()).Any(t => t < comment.fromSeconds || t > comment.toSeconds))
                    throw new InvalidOperationException("追加タイミングを指摘区間に含めてください。");
            }
        }
        public static int[] NotesInRange(SaberChartDocument chart, float from, float to)
        {
            if (!Finite(from) || !Finite(to) || to < from) throw new ArgumentException("区間の開始・終了を確認してください。");
            return Enumerable.Range(0, chart.notes.Count).Where(i =>
            {
                var n = chart.notes[i]; float start = (n.time + chart.offsetMs) / 1000f;
                float end = start + SaberChartUtility.EffectiveLongLengthMs(n) / 1000f;
                return start <= to && end >= from;
            }).ToArray();
        }
        public static SaberReviewRequest Request(SaberChartDocument chart, string song, string difficulty, string path,
            IEnumerable<SaberReviewComment> comments, float audioLengthMs = 0f)
        {
            var copy = SaberChartUtility.Clone(chart);
            var list = JsonUtility.FromJson<SaberReviewRequest>(JsonUtility.ToJson(new SaberReviewRequest { comments = comments.ToList() })).comments;
            return new SaberReviewRequest { songId = song, difficulty = difficulty, chartPath = path, chart = copy, baseHash = ChartHash(copy), comments = list,
                checks = SaberChartLint.Check(copy, difficulty, audioLengthMs) };
        }
        // AIの返信を読む。```json の囲みを外し、必須の項目と型を操作の番号つきで確かめてから JsonUtility で読む
        // (JsonUtility は欠けた項目を既定値で埋めるので、色や種類を書き忘れた青の矢印が赤のタップに化けるのを防ぐ)。
        // 向き・色・種類・操作の綴りは、本編と同じく大文字や「-」「_」の違いを許す。
        public static SaberReviewPatch ParseReply(string text)
        {
            string json = StripCodeFence(text);
            if (!SaberJson.TryParseObject(json, out SaberJsonObject root))
                throw new InvalidOperationException("返信をJSONとして読めません。reply.json の中身を確かめてください（```json の囲みは付いていてもかまいません）。");
            var errors = new List<string>();
            var hasExpect = new List<bool>();
            if (!(root.Get("operations") is SaberJsonArray operations))
                errors.Add("operations（配列）がありません。譜面を変えない返信は空の配列 [] にしてください。");
            else
            {
                for (int k = 0; k < operations.Items.Count; k++)
                {
                    var operation = operations.Items[k] as SaberJsonObject;
                    // JsonUtility で書いた返信は、空の expect(色なし)を必ず含むので、色のあるものだけを照合に使う。
                    hasExpect.Add(operation?.Get("expect") is SaberJsonObject expect
                        && SaberJson.TryGetString(expect.Get("color"), out string expectColor) && !string.IsNullOrWhiteSpace(expectColor));
                    string where = $"操作{k + 1}";
                    if (operation == null) { errors.Add(where + ": { } のオブジェクトにしてください。"); continue; }
                    if (!SaberJson.TryGetString(operation.Get("action"), out string action)) { errors.Add(where + ": action（文字列）がありません。"); continue; }
                    action = action.Trim().ToLowerInvariant();
                    if (action != "add" && !SaberJson.TryGetNumber(operation.Get("index"), out _))
                        errors.Add(where + ": index（数）がありません。");
                    if (action != "add" && action != "replace") continue;
                    if (!(operation.Get("note") is SaberJsonObject note)) { errors.Add(where + ": note（オブジェクト）がありません。"); continue; }
                    var missing = new[] { "time", "x", "y" }.Where(key => !SaberJson.TryGetNumber(note.Get(key), out _)).Select(key => key + "（数）")
                        .Concat(new[] { "type", "color" }.Where(key => !SaberJson.TryGetString(note.Get(key), out _)).Select(key => key + "（文字列）")).ToList();
                    if (missing.Count > 0) errors.Add(where + ": note に " + string.Join("・", missing) + " がありません。");
                }
            }
            if (errors.Count > 0)
                throw new InvalidOperationException($"返信JSONの {errors.Count} か所を直してください。何も適用していません。\n" +
                    string.Join("\n", errors.Take(12)) + (errors.Count > 12 ? $"\nほか {errors.Count - 12} か所" : ""));
            var patch = JsonUtility.FromJson<SaberReviewPatch>(json);
            patch.operations ??= new List<SaberReviewOperation>();
            for (int k = 0; k < patch.operations.Count; k++)
            {
                var operation = patch.operations[k];
                operation.action = (operation.action ?? "").Trim().ToLowerInvariant();
                if (k >= hasExpect.Count || !hasExpect[k]) operation.expect = null;
                if (operation.note == null) continue;
                operation.note.type = Canonical(operation.note.type);
                operation.note.color = Canonical(operation.note.color);
                operation.note.direction = string.IsNullOrWhiteSpace(operation.note.direction) ? "none" : Canonical(operation.note.direction);
            }
            return patch;
        }
        static string Canonical(string value) =>
            (value ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        static string StripCodeFence(string text)
        {
            string trimmed = (text ?? "").Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
            int start = trimmed.IndexOf('\n'), end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            return start < 0 || end <= start ? trimmed : trimmed.Substring(start + 1, end - start - 1).Trim();
        }
        // 返信が、この依頼・今の譜面に対するものか。
        public static void ValidateTarget(SaberReviewRequest request, SaberChartDocument current, SaberReviewPatch patch)
        {
            if (patch == null || patch.version != 1 || patch.songId != request.songId || patch.difficulty != request.difficulty)
                throw new InvalidOperationException("別の曲・難易度、または未対応の返信形式です。");
            if (patch.baseHash != request.baseHash || ChartHash(current) != request.baseHash)
                throw new InvalidOperationException("依頼後に譜面が変わっています。現在の譜面で依頼を作り直してください。");
        }
        public static SaberChartDocument Apply(SaberReviewRequest request, SaberChartDocument current, SaberReviewPatch patch, float duration)
        {
            ValidateTarget(request, current, patch);
            if (patch.operations == null || patch.operations.Count == 0 || patch.operations.Count > 10000)
                throw new InvalidOperationException("変更操作は1〜10000件必要です。");
            var result = SaberChartUtility.Clone(current);
            var touched = new HashSet<int>(); var removed = new HashSet<int>(); var added = new List<SaberChartNote>();
            // 1件目で止めず、直す必要のある操作をすべて番号つきで示す(AIへそのまま返せるように)。どれか1件でも不正なら何も変えない。
            var errors = new List<string>();
            for (int k = 0; k < patch.operations.Count; k++)
            {
                var operation = patch.operations[k];
                string where = operation == null ? $"操作{k + 1}" : $"操作{k + 1}（{operation.action} index {operation.index}）";
                if (operation == null || !(operation.action == "replace" || operation.action == "delete" || operation.action == "add"))
                { errors.Add(where + ": action は replace / delete / add のどれかにしてください。"); continue; }
                if (operation.action != "add")
                {
                    if (operation.index < 0 || operation.index >= current.notes.Count)
                    { errors.Add($"{where}: index が範囲外です（0〜{current.notes.Count - 1}）。"); continue; }
                    if (!touched.Add(operation.index)) { errors.Add(where + ": 同じノーツへの変更が重複しています。"); continue; }
                    string mismatch = ExpectMismatch(operation.expect, current.notes[operation.index]);
                    if (mismatch != null) { errors.Add(where + ": " + mismatch); continue; }
                }
                if (operation.action == "delete") { removed.Add(operation.index); continue; }
                var n = operation.note;
                var problems = NoteProblems(n, current.offsetMs, duration);
                if (problems.Count > 0) { errors.Add(where + ": " + string.Join(" / ", problems)); continue; }
                var copy = n.Clone();
                // 時刻が変わったノーツの格子位置は既存のテンポ地図から計算する。
                if (operation.action == "add" || current.notes[operation.index].time != copy.time)
                    copy.beat = new SaberChartGrid(current.bpm, current.beatZeroMs, current.tempoMap).BeatAt(copy.time);
                if (operation.action == "add") added.Add(copy); else result.notes[operation.index] = copy;
            }
            if (errors.Count > 0)
                throw new InvalidOperationException($"返信JSONの操作 {errors.Count} 件を直してください。何も適用していません。\n" +
                    string.Join("\n", errors.Take(12)) + (errors.Count > 12 ? $"\nほか {errors.Count - 12} 件" : ""));
            result.notes = result.notes.Where((_, i) => !removed.Contains(i)).Concat(added).OrderBy(n => n.time).ToList();
            SaberChartUtility.Normalize(result);
            return result;
        }
        // expect と、添字が指すノーツが合わなければ、その違い(合えば null)。
        static string ExpectMismatch(SaberReviewExpect expect, SaberChartNote actual)
        {
            if (expect == null) return null;
            bool same = Mathf.Abs(expect.time - actual.time) <= 1f && Mathf.Abs(expect.x - actual.x) <= .01f && Mathf.Abs(expect.y - actual.y) <= .01f
                && (string.IsNullOrEmpty(expect.color) || Canonical(expect.color) == actual.color);
            return same ? null : FormattableString.Invariant(
                $"index のノーツが expect と違います（添字の数え違い?）。実際は time {actual.time:0.###}・{actual.color}・x {actual.x:0.###}・y {actual.y:0.###}。");
        }
        // 採用しない操作を除いた返信。操作は元の譜面の添字で書かれ、重複もないので、一部だけでもそのまま適用できる。
        // JSON を通して複製しない(JsonUtility は入れ子の null を保てず、空の expect を作ってしまう)。
        public static SaberReviewPatch Selected(SaberReviewPatch patch, ICollection<int> rejected) => new SaberReviewPatch
        {
            version = patch.version, songId = patch.songId, difficulty = patch.difficulty, baseHash = patch.baseHash, explanation = patch.explanation,
            operations = patch.operations.Where((_, i) => rejected == null || !rejected.Contains(i)).ToList()
        };
        // 変更案で新しく出た譜面チェックの項目(同じ規則・同じ時刻のものは、変更前からあったとみなす)。
        public static List<SaberChartIssue> NewIssues(List<SaberChartIssue> before, List<SaberChartIssue> after)
        {
            var known = new HashSet<string>(before.Select(IssueKey));
            return after.Where(issue => !known.Contains(IssueKey(issue))).ToList();
        }
        static string IssueKey(SaberChartIssue issue) => issue.rule + "@" + Mathf.RoundToInt(issue.timeMs);
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static readonly string[] Types = { "tap", "direction", "long" };
        static readonly string[] Colors = { "red", "blue", "gold", "default" };
        static readonly string[] Directions = { "none", "up", "down", "left", "right", "upleft", "upright", "downleft", "downright" };
        // ノーツの直す点(空なら有効)。AIが直せるよう、項目と許される値を書く。
        static List<string> NoteProblems(SaberChartNote n, float offset, float duration)
        {
            var problems = new List<string>();
            if (n == null) { problems.Add("note がありません（add / replace は note の全項目が必要）"); return problems; }
            if (!Finite(n.time) || n.time < 0) problems.Add("time はミリ秒の0以上の数");
            if (!Finite(n.x) || Math.Abs(n.x) > 2.5f) problems.Add("x は -2.5〜2.5");
            if (!Finite(n.y) || Math.Abs(n.y) > 1.5f) problems.Add("y は -1.5〜1.5");
            if (!Finite(n.beat)) problems.Add("beat は数（時刻から計算し直します）");
            if (!Finite(n.lengthMs) || n.lengthMs < 0 || n.lengthMs > 600000) problems.Add("lengthMs は 0〜600000");
            if (n.count < 1 || n.count > 99) problems.Add("count は 1〜99");
            if (!Types.Contains(n.type)) problems.Add("type は " + string.Join(" / ", Types));
            if (!Colors.Contains(n.color)) problems.Add("color は " + string.Join(" / ", Colors));
            if (!Directions.Contains(n.direction)) problems.Add("direction は " + string.Join(" / ", Directions));
            if (problems.Count > 0) return problems;
            if ((n.type == "long") != (n.count > 1)) problems.Add("long は count 2以上、tap・direction は count 1");
            if (n.type != "long" && n.lengthMs != 0) problems.Add("lengthMs はロングだけ（ほかは 0）");
            if (n.type == "direction" && n.direction == "none") problems.Add("type direction には向き（up など）が必要");
            if (n.type == "tap" && n.direction != "none") problems.Add("tap に向きは付けない（向きを付けるなら type を direction に）");
            float start = (n.time + offset) / 1000f, end = start + SaberChartUtility.EffectiveLongLengthMs(n) / 1000f;
            if (start < 0) problems.Add("曲の開始前（time + offsetMs が負）");
            else if (duration > 0 && end > duration + .001f) problems.Add($"音源の終わり（{duration:0.000}秒）を越える");
            return problems;
        }
        // 返信の書き方。操作の3種類の例と、許される値を示す(このツールは同じ規則で検査する)。
        public static string ReplyFormat() => string.Join("\n", new[]
        {
            "返信(reply.json)の書き方:",
            "{ \"version\": 1, \"songId\": \"…\", \"difficulty\": \"…\", \"baseHash\": \"…\", \"explanation\": \"変更の要約\", \"operations\": [",
            "  { \"action\": \"replace\", \"index\": 12, \"expect\": { \"time\": 45230, \"color\": \"red\", \"x\": 1.0, \"y\": 0.0 }, \"commentId\": \"指摘のid\", \"reason\": \"理由\",",
            "    \"note\": { \"time\": 45230, \"x\": 1.5, \"y\": 0.0, \"type\": \"direction\", \"color\": \"red\", \"direction\": \"down\", \"count\": 1, \"lengthMs\": 0 } },",
            "  { \"action\": \"add\", \"reason\": \"理由\", \"note\": { \"time\": 46000, \"x\": -1.0, \"y\": 0.0, \"type\": \"tap\", \"color\": \"blue\", \"direction\": \"none\", \"count\": 1, \"lengthMs\": 0 } },",
            "  { \"action\": \"delete\", \"index\": 40, \"expect\": { \"time\": 47100, \"color\": \"blue\", \"x\": -1.5, \"y\": 0.5 } } ] }",
            "- type: tap / direction / long。direction: up / down / left / right / upleft / upright / downleft / downright（tap と long は none）。color: red / blue / gold / default。",
            "- long は count 2以上（切る回数）と lengthMs（ミリ秒。0 なら count から自動）。tap・direction は count 1・lengthMs 0。",
            "- replace・delete の expect には、index が指す変更前のノーツの time・color・x・y を写してください（添字の数え違いを見つけるため）。",
            "- 譜面を変えない返信は operations を [] にして、explanation に説明を書いてください。",
        });
        // AIが配置を決めるときに守る本編の決まり。数値は SaberChartLint と同じものを使う。
        public static string GameRules() => string.Join("\n", new[]
        {
            "本編の決まり（このツールが変更案に同じチェックをかけます）:",
            "- 青=左手、赤=右手、金=どちらの手でも切れる。x は -2.5〜2.5、y は -1.5〜1.5（coordScale 倍で配置）。",
            FormattableString.Invariant($"- 刃の線からノーツの中心まで {SaberChartLint.HitRange} 以内で当たる。時刻差 {SaberChartLint.SimultaneousMs:0}ms 以内は同時で、1振りでまとめて切れる。"),
            FormattableString.Invariant($"- 同じ手の続きは、隣へなぞる並び（間 0.4〜{2 * SaberChartLint.HitRange}、矢印はなぞる向きと±45°）なら1振りで続けて切れる。そうでなければ振り返す間を空ける（Hard {SaberChartLint.FastSameHandMs:0}ms・Easy/Normal {SaberChartLint.EasySameHandMs:0}ms 以上）。"),
            FormattableString.Invariant($"- 同じ手のロングの途中に同じ手のノーツを置かない。内向きの同時押し（青が右向き・赤が左向きで近づく）を置かない。同じ手で同じ向きの矢印を {SaberChartLint.ResetMs:0}ms 未満で続けない（なぞりを除く）。"),
            "- Easy・Normal では左右の手を交差させない。下の難易度は上の難易度から間引いて作る（上にない時刻を足さない）。",
        });
        public static string Prompt(SaberReviewRequest request, string folder)
        {
            var text = new StringBuilder();
            text.AppendLine("3D-Saberのプレイレビューを修正してください。").AppendLine("依頼フォルダー: " + folder);
            text.AppendLine("request.json、chart-before.jsonと各frame-*.pngを読んでください。音源は含みません。");
            text.AppendLine(FormattableString.Invariant($"停止画像は本番と同じ見え方です（ノーツは {GameSession.NoteApproachTimeDefault:0.0} 秒かけて飛んでくる既定の速さ）。譜面の座標 x -2.5〜2.5・y -1.5〜1.5 が画面の左右・上下に対応します。"));
            text.AppendLine($"曲: {request.songId} / 難易度: {request.difficulty} / 指摘: {request.comments.Count}件");
            int number = 0;
            foreach (var c in request.comments.OrderBy(c => c.fromSeconds).ThenBy(c => c.frameSeconds))
            {
                text.AppendLine(FormattableString.Invariant($"{++number}. {c.fromSeconds:0.000}〜{c.toSeconds:0.000}秒 / ノーツ[{string.Join(",", c.noteIndices)}]: {c.instruction}"));
                text.AppendLine(FormattableString.Invariant($"   停止画像: frame-{c.id}.png ({c.frameSeconds:0.000}秒)"));
                if (c.requestedNoteSeconds != null && c.requestedNoteSeconds.Length > 0)
                {
                    text.AppendLine("   ノーツ到達の希望時刻（音源先頭からの秒）: " + string.Join(", ", c.requestedNoteSeconds.Select(t => FormattableString.Invariant($"{t:0.000}"))));
                    if (c.inputOffsetMs != 0f) text.AppendLine(FormattableString.Invariant($"   （押す遅れ {c.inputOffsetMs:+0;-0}ms を差し引き済み）"));
                    text.AppendLine("   requestedNoteSecondsの各時刻を優先し、note.time = 秒 × 1000 − chart.offsetMsで変換してください。色・座標・種類は指示と前後の譜面に合わせて配置してください。クリック位置は配置座標の指定ではありません。停止画像は区間の参考です。");
                }
                else if (c.noteIndices.Length == 0) text.AppendLine("   個別ノーツの選択なし。区間と画像から対象を確認してください。");
            }
            text.AppendLine("譜面修正はpatch-template.jsonの形式でreply.jsonに出力。baseHash・songId・difficultyを保持し、operationsにはadd/replace/deleteを指定してください。");
            text.AppendLine("indexはrequest.chart.notesの0始まりの元添字。replace/addのnoteは全フィールドを記述。timeとlengthMsはミリ秒。範囲外のノーツや曲設定は変更しないでください。");
            text.AppendLine("背景や演出の指示は対象コードの変更として扱い、譜面パッチへ混ぜず、変更内容を説明してください。");
            text.AppendLine().AppendLine(ReplyFormat());
            text.AppendLine().AppendLine(GameRules());
            var checks = (request.checks ?? new List<SaberChartIssue>()).Where(i => i.level != SaberChartIssueLevel.Info).ToList();
            if (checks.Count > 0)
            {
                text.AppendLine().AppendLine($"依頼時点の譜面チェック（エラー・注意 {checks.Count}件。指摘の区間にあるものは一緒に直してください）:");
                foreach (var issue in checks.Take(20)) text.AppendLine("- " + SaberChartLint.Describe(request.chart, issue));
                if (checks.Count > 20) text.AppendLine($"- ほか {checks.Count - 20} 件（request.json の checks）");
            }
            return text.ToString();
        }
        public static void Export(string folder, SaberReviewRequest request)
        {
            Directory.CreateDirectory(folder);
            // BOM なしの UTF-8(Python の json.load などは BOM 付きを読めない。譜面本体も BOM なし)。
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(folder, "request.json"), JsonUtility.ToJson(request, true), utf8);
            File.WriteAllText(Path.Combine(folder, "chart-before.json"), SaberChartUtility.ToFileJson(request.chart), utf8);
            File.WriteAllText(Path.Combine(folder, "prompt.txt"), Prompt(request, folder), utf8);
            File.WriteAllText(Path.Combine(folder, "patch-template.json"), JsonUtility.ToJson(new SaberReviewPatch
            { songId = request.songId, difficulty = request.difficulty, baseHash = request.baseHash, explanation = "変更内容", operations = new List<SaberReviewOperation>() }, true), utf8);
        }
    }
}
