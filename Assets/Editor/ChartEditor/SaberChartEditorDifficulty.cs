using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>
    /// 難易度の派生: 別の難易度への複製、上の難易度を重ねて見る・下ろす候補、曲の設定の食い違いの警告とコピー。
    /// </summary>
    public sealed partial class SaberChartEditorWindow
    {
        [SerializeField] private bool showUpperOverlay = true;
        private List<SaberChartNote> deriveCandidates;
        private SaberChartDocument upperDocument;
        private string upperDocumentKey;
        private readonly List<(string difficulty, List<string> differences)> settingsMismatch =
            new List<(string, List<string>)>();
        private string settingsMismatchKey;
        private double settingsMismatchCheckedAt;

        // ---- 速2: 別の難易度へ複製して開く ----

        private void DrawDifficultyPanel()
        {
            SectionLabel("難易度");
            GUILayout.BeginHorizontal();
            GUILayout.Label("複製して開く", GUILayout.Width(70f));
            for (int i = 0; i < DifficultyValues.Length; i++)
            {
                if (i == EditingDifficultyIndex) continue;
                if (GUILayout.Button(new GUIContent(DifficultyLabels[i], $"今の内容を {DifficultyLabels[i]} の譜面として開きます（保存するまで書きません）"),
                        EditorStyles.miniButton))
                {
                    songId = EditingSongId;
                    difficultyIndex = i;
                    DuplicateToSelection();
                }
            }
            GUILayout.EndHorizontal();

            string upper = SaberChartDerive.UpperDifficulty(EditingDifficulty);
            if (upper == null) return;
            string upperLabel = DifficultyLabels[Array.IndexOf(DifficultyValues, upper)];
            SaberChartDocument upperChart = UpperDocument();
            if (upperChart == null)
            {
                GUILayout.Label($"{upperLabel} がまだ無いので、下ろす候補は使えません", smallMutedStyle);
                return;
            }
            showUpperOverlay = EditorGUILayout.ToggleLeft($"{upperLabel} を薄く重ねる（{upperChart.notes.Count}ノーツ）", showUpperOverlay);
            using (new EditorGUI.DisabledScope(!CanEditAtCursor))
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent(HasRangeSelection ? "範囲の候補を出す" : "全体の候補を出す",
                        $"{upperLabel} から、拍頭・同じ手の間隔・同時の数などの決まりで候補を選びます"))) MakeDeriveCandidates();
                using (new EditorGUI.DisabledScope(deriveCandidates == null || deriveCandidates.Count == 0))
                {
                    if (GUILayout.Button($"採用（{deriveCandidates?.Count ?? 0}）")) AdoptDeriveCandidates();
                    if (GUILayout.Button("消す", GUILayout.Width(40f))) ClearDeriveCandidates();
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("候補は足すだけで、置いてあるノーツは消しません。採用後に耳と目で直してください", smallMutedStyle);
        }

        private SaberChartDocument UpperDocument()
        {
            string upper = SaberChartDerive.UpperDifficulty(EditingDifficulty);
            if (upper == null || !SaberChartFileStore.IsValidSongId(EditingSongId, out _)) return null;
            string path = SaberChartFileStore.ChartPath(EditingSongId, upper);
            string key = path + "|" + (File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0);
            if (key != upperDocumentKey)
            {
                upperDocumentKey = key;
                upperDocument = SaberChartFileStore.TryLoadExact(EditingSongId, upper);
            }
            return upperDocument;
        }

        // ---- 速3: 上の難易度から下ろす候補 ----

        private void MakeDeriveCandidates()
        {
            SaberChartDocument upperChart = UpperDocument();
            if (upperChart == null) return;
            IEnumerable<SaberChartNote> source = HasRangeSelection
                ? SaberChartRangeOps.NotesIn(upperChart, rangeStartMs, rangeEndMs)
                : upperChart.notes;
            deriveCandidates = SaberChartDerive.Candidates(source, SaberChartDerive.Rules.For(EditingDifficulty), BeatAtTime, document);
            SetStatus(deriveCandidates.Count == 0
                ? "候補はありません（もう同じノーツがあるか、決まりに合うノーツがありません）"
                : $"{deriveCandidates.Count}個の候補を点線で表示しました。「採用」で足します");
            Repaint();
        }

        private void AdoptDeriveCandidates()
        {
            if (deriveCandidates == null || deriveCandidates.Count == 0 || !CanEditAtCursor) return;
            EndNoteDrag();
            string before = CurrentJson();
            int added = 0;
            foreach (SaberChartNote candidate in deriveCandidates)
            {
                if (document.notes.Any(note => SaberChartRangeOps.SameSlot(note, candidate))) continue;
                document.notes.Add(candidate.Clone());
                added++;
            }
            deriveCandidates = null;
            if (added == 0) { SetStatus("足す候補がありませんでした"); return; }
            SaberChartUtility.SortNotes(document);
            selectedIndex = -1;
            history.Record(before);
            MarkChanged();
            SetStatus($"{added}ノーツを足しました。Ctrl+Z でまとめて戻せます");
        }

        private void ClearDeriveCandidates()
        {
            deriveCandidates = null;
            Repaint();
        }

        private void DrawUpperOverlay(Rect laneRect)
        {
            if (!showUpperOverlay || recordMode) return;
            SaberChartDocument upperChart = UpperDocument();
            if (upperChart?.notes == null) return;
            foreach (SaberChartNote note in upperChart.notes)
            {
                Rect rect = NoteRect(note, laneRect);
                if (rect.yMax < laneRect.y || rect.y > laneRect.yMax) continue;
                Color color = NoteColor(note.color);
                EditorGUI.DrawRect(rect, new Color(color.r, color.g, color.b, .14f));
                DrawOutline(rect, new Color(color.r, color.g, color.b, .32f), 1f);
            }
        }

        private void DrawDeriveCandidates(Rect laneRect)
        {
            if (deriveCandidates == null) return;
            foreach (SaberChartNote note in deriveCandidates)
            {
                Rect rect = NoteRect(note, laneRect);
                if (rect.yMax < laneRect.y || rect.y > laneRect.yMax) continue;
                Color color = NoteColor(note.color);
                // 点線の枠で、まだ足していない候補だと分かるようにする。
                for (float x = rect.x; x < rect.xMax; x += 6f)
                {
                    EditorGUI.DrawRect(new Rect(x, rect.y, Mathf.Min(3f, rect.xMax - x), 2f), color);
                    EditorGUI.DrawRect(new Rect(x, rect.yMax - 2f, Mathf.Min(3f, rect.xMax - x), 2f), color);
                }
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, 2f, rect.height), color);
                EditorGUI.DrawRect(new Rect(rect.xMax - 2f, rect.y, 2f, rect.height), color);
            }
        }

        // ---- 取6: 曲の設定の食い違いを知らせ、他の難易度へコピーする ----

        private List<(string difficulty, List<string> differences)> SettingsMismatch()
        {
            string key = EditingSongId + "|" + EditingDifficulty + "|" + document.bpm + "|" + document.offsetMs + "|" +
                         document.beatZeroMs + "|" + document.coordScale + "|" + JsonUtility.ToJson(new SignatureList { items = document.timeSignatures });
            if (key == settingsMismatchKey && EditorApplication.timeSinceStartup - settingsMismatchCheckedAt < 3.0)
                return settingsMismatch;
            settingsMismatchKey = key;
            settingsMismatchCheckedAt = EditorApplication.timeSinceStartup;
            settingsMismatch.Clear();
            if (!SaberChartFileStore.IsValidSongId(EditingSongId, out _)) return settingsMismatch;
            foreach (string difficulty in DifficultyValues)
            {
                if (difficulty == EditingDifficulty) continue;
                SaberChartDocument other = SaberChartFileStore.TryLoadExact(EditingSongId, difficulty);
                if (other == null) continue;
                List<string> differences = SaberChartUtility.SongSettingDifferences(document, other);
                if (differences.Count > 0) settingsMismatch.Add((difficulty, differences));
            }
            return settingsMismatch;
        }

        [Serializable]
        private sealed class SignatureList
        {
            public List<ChartTimeSignature> items;
        }

        private void DrawSettingsMismatch()
        {
            var mismatch = SettingsMismatch();
            if (mismatch.Count == 0) return;
            string lines = string.Join("\n", mismatch.Select(item =>
                $"{DifficultyLabels[Array.IndexOf(DifficultyValues, item.difficulty)]}: {string.Join("、", item.differences)}（この譜面 / 相手）"));
            EditorGUILayout.HelpBox("曲の設定が他の難易度と違います。カウントインや小節線が難易度でずれます。\n" + lines, MessageType.Warning);
            using (new EditorGUI.DisabledScope(!CanEditAtCursor))
            {
                if (GUILayout.Button(new GUIContent("この譜面の設定を他の難易度へコピー",
                        "BPM・OFFSET・原点・拍子・座標倍率・目印を、保存済みの他の難易度のファイルへ書きます（ノーツの時刻は変えません）")))
                    CopySongSettingsToOtherDifficulties();
                GUILayout.BeginHorizontal();
                foreach (var item in mismatch)
                {
                    string label = DifficultyLabels[Array.IndexOf(DifficultyValues, item.difficulty)];
                    if (GUILayout.Button(new GUIContent($"{label} の設定を読み込む", $"{label} の曲の設定をこの譜面へ写します（保存するまでファイルは変わりません）"),
                            EditorStyles.miniButton))
                        LoadSongSettingsFrom(item.difficulty);
                }
                GUILayout.EndHorizontal();
            }
        }

        private Func<string, string, bool> confirmSettingsCopy = (title, message) =>
            EditorUtility.DisplayDialog(title, message, "コピーする", "キャンセル");

        private void CopySongSettingsToOtherDifficulties()
        {
            EndNoteDrag();
            var targets = DifficultyValues
                .Where(difficulty => difficulty != EditingDifficulty && SaberChartFileStore.ChartExists(EditingSongId, difficulty))
                .ToList();
            if (targets.Count == 0) return;
            string names = string.Join("・", targets.Select(d => DifficultyLabels[Array.IndexOf(DifficultyValues, d)]));
            if (!confirmSettingsCopy("曲の設定をコピーしますか？",
                    $"{names} のファイルへ、この譜面の BPM・OFFSET・原点・拍子・座標倍率・目印を書きます。\n" +
                    "ノーツの時刻は変えません。書く前のファイルはバックアップされます。")) return;
            try
            {
                foreach (string difficulty in targets)
                {
                    SaberChartDocument other = SaberChartFileStore.TryLoadExact(EditingSongId, difficulty);
                    if (other == null) continue;
                    bool followed = SaberChartUtility.BeatsFollowSingleGrid(other);
                    SaberChartUtility.CopySongSettings(document, other);
                    // 1つの格子に乗っていた拍の値だけ、新しい格子に合わせる(テンポの変わる曲の拍は保つ)。
                    if (followed) SaberChartUtility.RecalculateBeatsFromTimes(other, beatZeroMs);
                    SaberChartFileStore.Save(other, EditingSongId, difficulty);
                }
                settingsMismatchKey = null;
                upperDocumentKey = null;
                SetStatus($"{names} へ曲の設定をコピーしました");
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("曲の設定をコピーできません", exception.Message, "OK");
            }
        }

        private void LoadSongSettingsFrom(string difficulty)
        {
            SaberChartDocument other = SaberChartFileStore.TryLoadExact(EditingSongId, difficulty);
            if (other == null) return;
            EndNoteDrag();
            string before = CurrentJson();
            bool followed = SaberChartUtility.BeatsFollowSingleGrid(document);
            SaberChartUtility.CopySongSettings(other, document);
            beatZeroMs = SaberChartUtility.EstimateBeatZeroMs(other);
            if (followed) SaberChartUtility.RecalculateBeatsFromTimes(document, beatZeroMs);
            if (CurrentJson() == before) return;
            history.Record(before);
            MarkChanged();
            SetStatus($"{DifficultyLabels[Array.IndexOf(DifficultyValues, difficulty)]} の曲の設定を読み込みました（保存するとファイルに書きます）");
        }
    }
}
