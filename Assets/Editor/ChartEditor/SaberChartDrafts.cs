using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>
    /// 未保存の編集を Library へ定期的に退避する下書き。Unity が落ちても作業を失わないためのもので、
    /// 本編の譜面ファイルには触れない。譜面ごとに新しいものから一定数だけ残す
    /// (1つの譜面を長く編集しても、ほかの譜面の下書きを押し出さない)。中身が前と同じなら書かない。
    /// </summary>
    internal static class SaberChartDrafts
    {
        public const int KeepPerChart = 5;
        public const int Keep = 60;
        // テストでは利用者の下書きフォルダに触れないよう差し替える。
        internal static string rootOverride;

        [Serializable]
        public sealed class Draft
        {
            public string songId;
            public string difficulty;
            public string savedAtUtc;
            public int noteCount;
            public string document;
            // 編集のもとになった譜面ファイルのハッシュ(読み込んだ時点)。開くときに、そのあと外で書き換えられていないかを見る。
            public string sourceHash;
            [NonSerialized] public string filePath;

            public DateTime SavedAtUtc =>
                DateTime.TryParse(savedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime value)
                    ? value : DateTime.MinValue;
        }

        public static string Root => rootOverride ??
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "3DSaberChartDrafts"));

        public static string Write(string songId, string difficulty, SaberChartDocument document, string sourceHash = null)
        {
            Directory.CreateDirectory(Root);
            string json = SaberChartUtility.ToJson(document, false);
            // いちばん新しい下書きと同じ中身なら書かない(放っておいても同じ下書きが増えて、ほかを押し出さない)。
            Draft latest = LatestFor(songId, difficulty, out string latestPath);
            if (latest != null && latest.document == json && (latest.sourceHash ?? "") == (sourceHash ?? "") && latestPath != null) return latestPath;
            DateTime now = DateTime.UtcNow;
            var draft = new Draft
            {
                songId = songId ?? string.Empty,
                difficulty = difficulty ?? string.Empty,
                savedAtUtc = now.ToString("o", CultureInfo.InvariantCulture),
                noteCount = document?.notes?.Count ?? 0,
                document = json,
                sourceHash = sourceHash,
            };
            string safeSong = Sanitize(songId);
            string unique = Guid.NewGuid().ToString("N").Substring(0, 6);
            string path = Path.Combine(Root, $"{safeSong}_{Sanitize(difficulty)}_{now.ToLocalTime():yyyyMMdd_HHmmss_fff}_{unique}.json");
            File.WriteAllText(path, JsonUtility.ToJson(draft), new UTF8Encoding(false));
            Prune();
            return path;
        }

        /// <summary>その曲・難易度のいちばん新しい下書き。無ければ null。</summary>
        public static Draft LatestFor(string songId, string difficulty, out string path)
        {
            path = null;
            Draft latest = null;
            foreach (var (file, draft) in ReadAll())
            {
                if (!Same(draft.songId, songId) || !Same(draft.difficulty, difficulty)) continue;
                if (latest != null && draft.SavedAtUtc <= latest.SavedAtUtc) continue;
                latest = draft;
                path = file;
            }
            return latest;
        }

        public static void DeleteFor(string songId, string difficulty)
        {
            foreach (var (file, draft) in ReadAll())
            {
                if (!Same(draft.songId, songId) || !Same(draft.difficulty, difficulty)) continue;
                TryDelete(file);
            }
        }

        // 自動案内より古い下書きも、診断・復元から明示的に選べる。
        public static List<Draft> ListFor(string songId, string difficulty) => ReadAll()
            .Where(item => Same(item.draft.songId, songId) && Same(item.draft.difficulty, difficulty))
            .Select(item => item.draft).OrderByDescending(draft => draft.SavedAtUtc).ToList();

        // 読めなかった1件だけを控えへ移す。他の復旧候補をまとめて捨てない。
        public static void KeepUnreadable(Draft draft)
        {
            if (draft == null || string.IsNullOrEmpty(draft.filePath)) return;
            string path = Path.GetFullPath(draft.filePath);
            if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase))
                throw new IOException("下書きの保存先が一致しません。");
            if (File.Exists(path)) File.Move(path, path + ".unreadable-" + Guid.NewGuid().ToString("N"));
        }

        /// <summary>この窓が書いた下書きだけを消す(保存したとき。前の起動で残った下書きは、案内を見るまで残す)。</summary>
        public static void Delete(IEnumerable<string> paths)
        {
            foreach (string path in paths ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(path) && File.Exists(path) &&
                    string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase))
                    TryDelete(path);
        }

        // この窓の所有分と保存対象の両方に一致する下書きだけを消す。
        public static void DeleteSessionChart(List<string> paths, string songId, string difficulty)
        {
            if (paths == null) return;
            var owned = new HashSet<string>(paths.Where(path => !string.IsNullOrEmpty(path))
                .Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            foreach (var (file, draft) in ReadAll())
            {
                if (!owned.Contains(Path.GetFullPath(file))
                    || !Same(draft.songId, songId) || !Same(draft.difficulty, difficulty)) continue;
                TryDelete(file);
                // 削除できなかったものは、次の保存で再試行できるよう所有履歴に残す。
                if (!File.Exists(file))
                    paths.RemoveAll(path => !string.IsNullOrEmpty(path)
                        && string.Equals(Path.GetFullPath(path), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));
            }
        }

        public static int Count() => Directory.Exists(Root) ? Directory.GetFiles(Root, "*.json").Length : 0;

        private static IEnumerable<(string file, Draft draft)> ReadAll()
        {
            if (!Directory.Exists(Root)) yield break;
            foreach (string file in Directory.GetFiles(Root, "*.json"))
            {
                Draft draft = null;
                try { draft = JsonUtility.FromJson<Draft>(File.ReadAllText(file, Encoding.UTF8)); }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                                                  exception is ArgumentException) { }
                if (draft != null && !string.IsNullOrEmpty(draft.document))
                {
                    draft.filePath = file;
                    yield return (file, draft);
                }
            }
        }

        private static void Prune()
        {
            if (!Directory.Exists(Root)) return;
            var files = Directory.GetFiles(Root, "*.json")
                .Select(file => new FileInfo(file))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ThenByDescending(info => info.Name, StringComparer.Ordinal)
                .ToList();
            // 譜面ごとに新しい KeepPerChart 件、全体で Keep 件まで残す。譜面はファイル名の「曲_難易度」で分ける。
            var kept = new List<FileInfo>();
            foreach (var chart in files.GroupBy(info => ChartKey(info.Name)))
            {
                int index = 0;
                foreach (FileInfo info in chart)
                {
                    if (index++ < KeepPerChart) kept.Add(info);
                    else TryDelete(info.FullName);
                }
            }
            foreach (FileInfo info in kept.OrderByDescending(info => info.LastWriteTimeUtc).Skip(Keep)) TryDelete(info.FullName);
        }

        // 「曲_難易度_日付_時刻_ミリ秒_識別子.json」から「曲_難易度」を取り出す(曲の名前に _ があってもよい)。
        private static string ChartKey(string fileName)
        {
            string[] parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
            return parts.Length > 4 ? string.Join("_", parts.Take(parts.Length - 4)) : fileName;
        }

        private static void TryDelete(string file)
        {
            try { File.Delete(file); }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                Debug.LogWarning("譜面の下書きを削除できませんでした: " + file + "\n" + exception.Message);
            }
        }

        private static bool Same(string a, string b) =>
            string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

        private static string Sanitize(string value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "Song" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) text = text.Replace(invalid, '_');
            return text;
        }
    }
}
