using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    public sealed partial class SaberChartEditorWindow
    {
        [Serializable] private sealed class DiskFile
        {
            public string path;
            public string hash;
        }
        // 編集中の内容と一緒に保存し、再コンパイル後も読込時点を忘れない。
        [SerializeField] private string diskBaselineKey;
        [SerializeField] private List<DiskFile> diskBaseline;
        [SerializeField] private string persistentWarning;
        private Func<string, bool> confirmExternalOverwrite = message => EditorUtility.DisplayDialog(
            "ディスク上の譜面が変わっています", message, "上書きする", "キャンセル");

        private static string FileHash(string path)
        {
            // File.Exists は権限エラーも false にするので、未作成と区別して扱う。
            try { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path))); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        private static List<DiskFile> CaptureDiskState(string song, string difficulty)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            paths.Add(SaberChartFileStore.ChartPath(song, difficulty));
            string loaded = SaberChartFileStore.FindChartToLoad(song, difficulty);
            if (loaded != null) paths.Add(loaded);
            // Normal の保存は共通の予備ファイルも置き換える。
            if (string.Equals(difficulty, "normal", StringComparison.OrdinalIgnoreCase))
                paths.Add(Path.Combine(SaberChartFileStore.SongFolderPath(song), "chart.json"));
            var result = new List<DiskFile>();
            foreach (string path in paths) result.Add(new DiskFile { path = path, hash = FileHash(path) });
            return result;
        }
        private static bool DiskStateMatches(List<DiskFile> snapshot)
        {
            if (snapshot == null) return false;
            foreach (var file in snapshot) if (FileHash(file.path) != file.hash) return false;
            return true;
        }
        private void RememberDiskState(string song, string difficulty, List<DiskFile> snapshot = null)
        {
            diskBaseline = snapshot ?? CaptureDiskState(song, difficulty);
            diskBaselineKey = TargetKey(song, difficulty);
        }
        private bool ConfirmDiskOverwrite(string song, string difficulty)
        {
            if (diskBaselineKey != TargetKey(song, difficulty) || diskBaseline == null) return true;
            if (DiskStateMatches(diskBaseline)) return true;
            persistentWarning = "ディスク上の譜面が変わっています。読込先を確認して読み直すか、保存時に上書きを選んでください。";
            return confirmExternalOverwrite(song + " / " + difficulty +
                " の譜面が読み込み後に変更・削除されています。共通の chart.json も確認対象です。\n" +
                "編集中の内容で上書きしますか？ キャンセルすると編集内容とディスクの両方を残します。");
        }
        private bool TextControlHasFocus() => EditorGUIUtility.editingTextField ||
            (GUI.GetNameOfFocusedControl() ?? "").StartsWith("ChartText:", StringComparison.Ordinal);
    }
}
