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
    /// 本編の譜面ファイルには触れない。新しいものから一定数だけ残す。
    /// </summary>
    internal static class SaberChartDrafts
    {
        public const int Keep = 15;
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

            public DateTime SavedAtUtc =>
                DateTime.TryParse(savedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime value)
                    ? value : DateTime.MinValue;
        }

        public static string Root => rootOverride ??
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "3DSaberChartDrafts"));

        public static string Write(string songId, string difficulty, SaberChartDocument document)
        {
            Directory.CreateDirectory(Root);
            DateTime now = DateTime.UtcNow;
            var draft = new Draft
            {
                songId = songId ?? string.Empty,
                difficulty = difficulty ?? string.Empty,
                savedAtUtc = now.ToString("o", CultureInfo.InvariantCulture),
                noteCount = document?.notes?.Count ?? 0,
                document = SaberChartUtility.ToJson(document, false),
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
                if (draft != null && !string.IsNullOrEmpty(draft.document)) yield return (file, draft);
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
            for (int i = Keep; i < files.Count; i++) TryDelete(files[i].FullName);
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
