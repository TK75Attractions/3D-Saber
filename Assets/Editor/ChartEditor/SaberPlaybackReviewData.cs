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
    }
    [Serializable] public sealed class SaberReviewRequest
    {
        public int version = 1;
        public string songId, difficulty, chartPath, baseHash, sourceDiskHash, sourceLegacyHash;
        public string timeBasis = "音源先頭からの秒。ノーツ到達秒=(note.time+chart.offsetMs)/1000。screenPointは画像左上=(0,0)、右下=(1,1)。";
        public SaberChartDocument chart;
        public List<SaberReviewComment> comments = new List<SaberReviewComment>();
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
        public static SaberReviewRequest Request(SaberChartDocument chart, string song, string difficulty, string path, IEnumerable<SaberReviewComment> comments)
        {
            var copy = SaberChartUtility.Clone(chart);
            var list = JsonUtility.FromJson<SaberReviewRequest>(JsonUtility.ToJson(new SaberReviewRequest { comments = comments.ToList() })).comments;
            return new SaberReviewRequest { songId = song, difficulty = difficulty, chartPath = path, chart = copy, baseHash = ChartHash(copy), comments = list };
        }
        public static SaberChartDocument Apply(SaberReviewRequest request, SaberChartDocument current, SaberReviewPatch patch, float duration)
        {
            if (patch == null || patch.version != 1 || patch.songId != request.songId || patch.difficulty != request.difficulty)
                throw new InvalidOperationException("別の曲・難易度、または未対応の返信形式です。");
            if (patch.baseHash != request.baseHash || ChartHash(current) != request.baseHash)
                throw new InvalidOperationException("依頼後に譜面が変わっています。現在の譜面で依頼を作り直してください。");
            if (patch.operations == null || patch.operations.Count == 0 || patch.operations.Count > 10000)
                throw new InvalidOperationException("変更操作は1〜10000件必要です。");
            var result = SaberChartUtility.Clone(current);
            var touched = new HashSet<int>(); var removed = new HashSet<int>(); var added = new List<SaberChartNote>();
            foreach (var operation in patch.operations)
            {
                if (operation == null || !(operation.action == "replace" || operation.action == "delete" || operation.action == "add"))
                    throw new InvalidOperationException("actionはreplace / delete / addのみです。");
                if (operation.action != "add" && (operation.index < 0 || operation.index >= current.notes.Count || !touched.Add(operation.index)))
                    throw new InvalidOperationException("ノーツの添字が範囲外、または同じノーツへの変更が重複しています。");
                if (operation.action == "delete") { removed.Add(operation.index); continue; }
                var n = operation.note;
                ValidateNote(n, current.offsetMs, duration);
                var copy = n.Clone();
                // 時刻が変わったノーツの格子位置は既存のテンポ地図から計算する。
                if (operation.action == "add" || current.notes[operation.index].time != copy.time)
                    copy.beat = new SaberChartGrid(current.bpm, current.beatZeroMs, current.tempoMap).BeatAt(copy.time);
                if (operation.action == "add") added.Add(copy); else result.notes[operation.index] = copy;
            }
            result.notes = result.notes.Where((_, i) => !removed.Contains(i)).Concat(added).OrderBy(n => n.time).ToList();
            SaberChartUtility.Normalize(result);
            return result;
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static void ValidateNote(SaberChartNote n, float offset, float duration)
        {
            if (n == null || !Finite(n.time) || !Finite(n.x) || !Finite(n.y) || !Finite(n.lengthMs) || !Finite(n.beat)
                || n.time < 0 || n.lengthMs < 0 || n.lengthMs > 600000 || Math.Abs(n.x) > 2.5f || Math.Abs(n.y) > 1.5f || n.count < 1 || n.count > 99)
                throw new InvalidOperationException("ノーツの数値・座標・回数が不正です。");
            if (!new[] { "tap", "direction", "long" }.Contains(n.type) || !new[] { "red", "blue", "gold", "default" }.Contains(n.color)
                || !new[] { "none", "up", "down", "left", "right", "upleft", "upright", "downleft", "downright" }.Contains(n.direction))
                throw new InvalidOperationException("ノーツの種類・色・方向が不正です。");
            if ((n.type == "long") != (n.count > 1) || (n.type != "long" && n.lengthMs != 0)
                || (n.type == "direction" && n.direction == "none") || (n.type == "tap" && n.direction != "none")) throw new InvalidOperationException("ノーツの種類と回数・長さ・方向が一致しません。");
            float start = (n.time + offset) / 1000f, end = start + SaberChartUtility.EffectiveLongLengthMs(n) / 1000f;
            if (start < 0 || (duration > 0 && end > duration + .001f)) throw new InvalidOperationException("変更したノーツが音源の範囲を越えています。");
        }
        public static string Prompt(SaberReviewRequest request, string folder)
        {
            var text = new StringBuilder();
            text.AppendLine("3D-Saberのプレイレビューを修正してください。").AppendLine("依頼フォルダー: " + folder);
            text.AppendLine("request.json、chart-before.jsonと各frame-*.pngを読んでください。音源は含みません。");
            foreach (var c in request.comments)
                text.AppendLine($"- {c.fromSeconds:0.000}〜{c.toSeconds:0.000}秒 / ノーツ[{string.Join(",", c.noteIndices)}]: {c.instruction}");
            text.AppendLine("譜面修正はpatch-template.jsonの形式でreply.jsonに出力。baseHash・songId・difficultyを保持し、operationsにはadd/replace/deleteを指定してください。");
            text.AppendLine("indexはrequest.chart.notesの0始まりの元添字。replace/addのnoteは全フィールドを記述。timeとlengthMsはミリ秒。範囲外のノーツや曲設定は変更しないでください。");
            text.AppendLine("背景や演出の指示は対象コードの変更として扱い、譜面パッチへ混ぜず、変更内容を説明してください。");
            return text.ToString();
        }
        public static void Export(string folder, SaberReviewRequest request)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "request.json"), JsonUtility.ToJson(request, true), Encoding.UTF8);
            File.WriteAllText(Path.Combine(folder, "chart-before.json"), SaberChartUtility.ToFileJson(request.chart), Encoding.UTF8);
            File.WriteAllText(Path.Combine(folder, "prompt.txt"), Prompt(request, folder), Encoding.UTF8);
            File.WriteAllText(Path.Combine(folder, "patch-template.json"), JsonUtility.ToJson(new SaberReviewPatch
            { songId = request.songId, difficulty = request.difficulty, baseHash = request.baseHash, explanation = "変更内容", operations = new List<SaberReviewOperation>() }, true), Encoding.UTF8);
        }
    }
}
