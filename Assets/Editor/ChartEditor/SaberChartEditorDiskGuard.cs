using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // ---- 守1: ほかのツールが開いている譜面を書き換えたら、保存を待たずに気付く ----

        private const string ExternalChangeWarning =
            "開いている譜面が、ほかのツール（プレイレビュー・AI・Codex など）で書き換えられました。保存すると上書きの確認が出ます。" +
            "ディスクの最新を使うなら、編集を保存せずに読み直してください。";
        private double nextDiskCheckAt;

        private void OnFocus()
        {
            nextDiskCheckAt = 0;
            CheckDiskForExternalChanges();
        }

        /// <summary>
        /// 2秒ごとと窓に戻ったときに、読み込んだ時点のファイルと比べる。未編集なら黙って最新を読み直し(表示位置は保つ)、
        /// 編集中なら上書きの危険をすぐ知らせる(保存時の確認まで待たない)。
        /// </summary>
        private void CheckDiskForExternalChanges()
        {
            if (EditorApplication.timeSinceStartup < nextDiskCheckAt) return;
            nextDiskCheckAt = EditorApplication.timeSinceStartup + 2;
            if (diskBaseline == null || string.IsNullOrEmpty(loadedSongId) || string.IsNullOrEmpty(loadedDifficulty)) return;
            if (diskBaselineKey != TargetKey(loadedSongId, loadedDifficulty) || RecordingBusy || draggingNote || isPlaying) return;
            bool same;
            try { same = DiskStateMatches(diskBaseline); }
            catch (IOException) { return; }
            if (same) return;
            UpdateDirtyState();
            if (!hasUnsavedChanges) ReloadEditingDocument("ほかのツールで保存された譜面を読み直しました");
            else if (persistentWarning != ExternalChangeWarning)
            {
                persistentWarning = ExternalChangeWarning;
                SetStatus("ディスクの譜面が書き換えられました（診断・復元を見てください）");
                Repaint();
            }
        }

        /// <summary>編集中の譜面をディスクから読み直す。表示位置は保ち、選択と Undo の履歴は読込と同じく捨てる。</summary>
        private bool ReloadEditingDocument(string message)
        {
            string song = loadedSongId, difficulty = loadedDifficulty;
            try
            {
                var nextDiskState = CaptureDiskState(song, difficulty);
                SaberChartDocument next = SaberChartFileStore.Load(song, difficulty, out _);
                // 書き込みの途中に読んだら、次の確認で読み直す。
                if (!DiskStateMatches(nextDiskState)) return false;
                float keepBeat = currentBeat;
                StopPreview(false);
                EndNoteDrag();
                document = next;
                beatZeroMs = SaberChartUtility.EstimateBeatZeroMs(next);
                selectedIndex = -1;
                ResetDerivedViews();
                history.Clear();
                savedJson = SaberChartUtility.ToJson(next, false);
                RememberDiskState(song, difficulty, nextDiskState);
                persistentWarning = null;
                hasUnsavedChanges = false;
                currentBeat = Mathf.Clamp(keepBeat, 0f, MaxBeat());
                SetStatus(message);
                Repaint();
                return true;
            }
            catch (Exception exception)
            {
                persistentWarning = "譜面を読み直せません: " + exception.Message;
                return false;
            }
        }

        // ---- プレイレビューとのやり取り(同じ譜面を開いている窓を探す) ----

        internal static SaberChartEditorWindow FindOpenFor(string song, string difficulty) =>
            Resources.FindObjectsOfTypeAll<SaberChartEditorWindow>().FirstOrDefault(window => window.IsEditing(song, difficulty));

        internal bool IsEditing(string song, string difficulty) =>
            string.Equals(loadedSongId, song?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(loadedDifficulty, difficulty, StringComparison.OrdinalIgnoreCase);

        internal bool HasUnsavedChartEdits
        {
            get { UpdateDirtyState(); return hasUnsavedChanges; }
        }

        /// <summary>ほかのツールが同じ譜面を保存した直後に呼ぶ。未編集ならすぐ読み直し、編集中なら知らせる。</summary>
        internal void NotifyExternalSave()
        {
            nextDiskCheckAt = 0;
            CheckDiskForExternalChanges();
        }
    }
}
