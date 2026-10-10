using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>
    /// 範囲選択と範囲の操作(削除・左右反転・色の入れ替え・時間の移動・コピーと貼り付け・一括変更)。
    /// 範囲は時刻で持ち、「開始以上・終了未満に始まるノーツ」を対象にする。
    /// </summary>
    public sealed partial class SaberChartEditorWindow
    {
        [SerializeField] private float rangeStartMs = -1f;
        [SerializeField] private float rangeEndMs = -1f;
        [SerializeField] private string rangeShiftMsText = "10";
        private float rangeAnchorMs = -1f;
        private bool draggingRange;

        // コピーした範囲。ウィンドウを閉じるまで残す。
        private static List<SaberChartNote> rangeClipboard;
        private static float rangeClipboardStartMs;

        private bool HasRangeSelection => rangeStartMs >= 0f && rangeEndMs > rangeStartMs;

        private List<SaberChartNote> RangeNotes() => SaberChartRangeOps.NotesIn(document, rangeStartMs, rangeEndMs);

        private void SetRange(float startMs, float endMs)
        {
            if (float.IsNaN(startMs) || float.IsNaN(endMs)) return;
            if (endMs < startMs) (startMs, endMs) = (endMs, startMs);
            rangeStartMs = Mathf.Max(0f, startMs);
            rangeEndMs = Mathf.Max(rangeStartMs, endMs);
            if (HasRangeSelection) selectedIndex = -1;
            Repaint();
        }

        private void ClearRange()
        {
            rangeStartMs = rangeEndMs = -1f;
            draggingRange = false;
            Repaint();
        }

        private void SetRangeStartAtCursor()
        {
            float time = TimeAtBeat(currentBeat);
            float end = rangeEndMs > time ? rangeEndMs : TimeAtBeat(currentBeat + 4f);
            SetRange(time, end);
            SetStatus($"範囲の始め: {FormatClock(time + document.offsetMs)}（] で終わりを決めます）");
        }

        private void SetRangeEndAtCursor()
        {
            float time = TimeAtBeat(currentBeat);
            float start = rangeStartMs >= 0f && rangeStartMs < time ? rangeStartMs : 0f;
            SetRange(start, time);
            SetStatus($"範囲: {FormatClock(rangeStartMs + document.offsetMs)} 〜 {FormatClock(rangeEndMs + document.offsetMs)}");
        }

        private void SelectFromCursorToEnd()
        {
            float start = TimeAtBeat(currentBeat);
            float last = document.notes.Count > 0 ? document.notes.Max(note => note.time) : start;
            SetRange(start, Mathf.Max(start + 1f, last + 1f));
            SetStatus($"{FormatClock(start + document.offsetMs)} から最後までを選びました（{RangeNotes().Count}ノーツ）");
        }

        private void SelectWholeChart()
        {
            float last = document.notes.Count > 0 ? document.notes.Max(note => note.time) : 0f;
            SetRange(0f, last + 1f);
        }

        // [ と ] で範囲を決める。JIS 配列でも同じ文字が入るよう、文字でも判定する。
        private bool HandleRangeKeys(Event current)
        {
            bool plain = !current.control && !current.command && !current.alt;
            bool action = (current.control || current.command) && !current.alt;
            if (plain && (current.keyCode == KeyCode.LeftBracket || current.character == '['))
            {
                SetRangeStartAtCursor();
                current.Use();
                return true;
            }
            if (plain && (current.keyCode == KeyCode.RightBracket || current.character == ']'))
            {
                SetRangeEndAtCursor();
                current.Use();
                return true;
            }
            if (plain && !current.shift && current.keyCode == KeyCode.Escape && HasRangeSelection)
            {
                ClearRange();
                current.Use();
                return true;
            }
            if (action && !current.shift && current.keyCode == KeyCode.C)
            {
                CopySelection();
                current.Use();
                return true;
            }
            if (action && !current.shift && current.keyCode == KeyCode.V)
            {
                PasteAtCursor();
                current.Use();
                return true;
            }
            if (action && (current.keyCode == KeyCode.UpArrow || current.keyCode == KeyCode.DownArrow))
            {
                ShiftSelectionBySnap(current.keyCode == KeyCode.UpArrow ? 1 : -1);
                current.Use();
                return true;
            }
            return false;
        }

        private void DrawRangePanel()
        {
            SectionLabel(HasRangeSelection ? $"範囲  {RangeNotes().Count}ノーツ" : "範囲の編集");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("始め [", "今の位置を範囲の始めにします"), EditorStyles.miniButtonLeft)) SetRangeStartAtCursor();
            if (GUILayout.Button(new GUIContent("終わり ]", "今の位置を範囲の終わりにします"), EditorStyles.miniButtonMid)) SetRangeEndAtCursor();
            if (GUILayout.Button(new GUIContent("ここから最後", "今の位置より後ろを全部選びます（後ろをまとめて動かすとき）"), EditorStyles.miniButtonMid)) SelectFromCursorToEnd();
            if (GUILayout.Button("全体", EditorStyles.miniButtonRight)) SelectWholeChart();
            GUILayout.EndHorizontal();
            if (!HasRangeSelection)
            {
                GUILayout.Label("左の目盛りを Shift+ドラッグでも選べます", smallMutedStyle);
                return;
            }
            GUILayout.Label($"{FormatClock(rangeStartMs + document.offsetMs)} 〜 {FormatClock(rangeEndMs + document.offsetMs)}（Esc で解除）", smallMutedStyle);
            using (new EditorGUI.DisabledScope(!CanEditAtCursor))
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("左右反転", "横位置・赤青・矢印の左右を反転（Ctrl+M）"))) MirrorSelection();
                if (GUILayout.Button(new GUIContent("色だけ入替", "赤と青だけを入れ替え（Ctrl+Shift+M）"))) SwapColorsOfSelectionOrBeat();
                if (GUILayout.Button(new GUIContent("削除", "範囲のノーツを削除（Delete）"))) DeleteSelection();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("コピー", "Ctrl+C"))) CopySelection();
                if (GUILayout.Button(new GUIContent("今の位置へ貼付", "Ctrl+V。貼り付け先の同じノーツは残します"))) PasteAtCursor();
                if (GUILayout.Button(new GUIContent("後ろへ複製", "範囲の直後に同じ並びを置き、範囲をそこへ移します（Ctrl+D）"))) DuplicateRangeAfter();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("時間", GUILayout.Width(28f));
                if (GUILayout.Button(new GUIContent("−1 Snap", "Ctrl+↓"), EditorStyles.miniButtonLeft)) ShiftSelectionBySnap(-1);
                if (GUILayout.Button(new GUIContent("+1 Snap", "Ctrl+↑"), EditorStyles.miniButtonRight)) ShiftSelectionBySnap(1);
                rangeShiftMsText = GUILayout.TextField(rangeShiftMsText ?? "10", GUILayout.Width(42f));
                GUILayout.Label("ms", GUILayout.Width(20f));
                if (GUILayout.Button("−", EditorStyles.miniButtonLeft, GUILayout.Width(22f))) ShiftSelectionByMsText(-1f);
                if (GUILayout.Button("+", EditorStyles.miniButtonRight, GUILayout.Width(22f))) ShiftSelectionByMsText(1f);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("色", GUILayout.Width(28f));
                for (int i = 0; i < ColorValues.Length; i++)
                    if (GUILayout.Button(ColorLabels[i].Split(' ')[0], EditorStyles.miniButton)) SetSelectionColor(ColorValues[i]);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("種類", GUILayout.Width(28f));
                for (int i = 0; i < TypeValues.Length; i++)
                    if (GUILayout.Button(TypeLabels[i], EditorStyles.miniButton)) SetSelectionType(TypeValues[i]);
                GUILayout.EndHorizontal();
                GUILayout.Label("方向はキー（QWE・ASD・ZXC / テンキー）で範囲のノーツにまとめて付きます", smallMutedStyle);
            }
        }

        private void DrawRangeOnTimeline(Rect timelineRect, Rect laneRect)
        {
            if (!HasRangeSelection) return;
            float top = YForBeat(BeatAtTime(rangeEndMs), timelineRect);
            float bottom = YForBeat(BeatAtTime(rangeStartMs), timelineRect);
            Rect band = new Rect(laneRect.x, Mathf.Min(top, bottom), laneRect.width, Mathf.Abs(bottom - top));
            EditorGUI.DrawRect(band, new Color(.95f, .85f, .3f, .08f));
            EditorGUI.DrawRect(new Rect(timelineRect.x, bottom, laneRect.xMax - timelineRect.x, 2f), new Color(.95f, .85f, .3f, .8f));
            EditorGUI.DrawRect(new Rect(timelineRect.x, top, laneRect.xMax - timelineRect.x, 2f), new Color(.95f, .85f, .3f, .8f));
        }

        // 左の目盛りの Shift+ドラッグで範囲を選ぶ。
        private bool HandleRangeDrag(Rect timelineRect, Rect laneRect, Event current)
        {
            Rect gutter = new Rect(timelineRect.x, timelineRect.y, laneRect.x - timelineRect.x, timelineRect.height);
            if (current.type == EventType.MouseDown && current.button == 0 && current.shift && gutter.Contains(current.mousePosition))
            {
                rangeAnchorMs = TimeAtBeat(SaberChartUtility.QuantizeBeat(BeatAtY(current.mousePosition.y, timelineRect), CurrentSnap, document));
                draggingRange = true;
                SetRange(rangeAnchorMs, rangeAnchorMs);
                current.Use();
                return true;
            }
            if (current.type == EventType.MouseDrag && draggingRange)
            {
                float time = TimeAtBeat(SaberChartUtility.QuantizeBeat(BeatAtY(current.mousePosition.y, timelineRect), CurrentSnap, document));
                SetRange(rangeAnchorMs, time);
                current.Use();
                return true;
            }
            if (current.rawType == EventType.MouseUp && draggingRange)
            {
                draggingRange = false;
                if (!HasRangeSelection) ClearRange();
                else SetStatus($"範囲: {RangeNotes().Count}ノーツ");
                current.Use();
                return true;
            }
            return false;
        }

        // ---- 操作 ----

        private bool ApplySelectionEdit(string label, Func<List<SaberChartNote>, bool> edit)
        {
            if (!CanEditAtCursor) return false;
            EndNoteDrag();
            List<SaberChartNote> targets = SelectedNotes();
            if (targets.Count == 0)
            {
                SetStatus("ノーツか範囲を選んでください");
                return false;
            }
            string before = CurrentJson();
            SaberChartNote keep = SelectedNote;
            if (!edit(targets)) return false;
            SaberChartUtility.SortNotes(document);
            selectedIndex = keep != null ? document.notes.IndexOf(keep) : -1;
            if (CurrentJson() == before) return false;
            history.Record(before);
            MarkChanged();
            SetStatus(label);
            return true;
        }

        private void MirrorSelection()
        {
            ApplySelectionEdit("左右反転しました（横位置・赤青・矢印）。Ctrl+Z で戻せます", targets =>
            {
                SaberChartRangeOps.Mirror(targets);
                return true;
            });
        }

        private void SwapColorsOfSelectionOrBeat()
        {
            if (!HasRangeSelection && SelectedNote == null)
            {
                List<SaberChartNote> beatNotes = RecordingStepNotes();
                if (beatNotes.Count == 0 || !CanEditAtCursor) return;
                string before = CurrentJson();
                SaberChartRangeOps.SwapColors(beatNotes);
                if (CurrentJson() == before) return;
                history.Record(before);
                MarkChanged();
                SetStatus("この拍の赤と青を入れ替えました");
                return;
            }
            ApplySelectionEdit("赤と青を入れ替えました", targets =>
            {
                SaberChartRangeOps.SwapColors(targets);
                return true;
            });
        }

        private void SetSelectionColor(string color)
        {
            ApplySelectionEdit("色を変えました", targets =>
            {
                foreach (SaberChartNote note in targets) note.color = color;
                return true;
            });
        }

        private void SetSelectionType(string type)
        {
            ApplySelectionEdit("種類を変えました", targets =>
            {
                SaberChartRangeOps.SetType(targets, type, paletteDirection, paletteCount);
                return true;
            });
        }

        private void DeleteSelection()
        {
            if (HasRangeSelection)
            {
                ApplySelectionEdit("範囲のノーツを削除しました。Ctrl+Z で戻せます", targets =>
                {
                    foreach (SaberChartNote note in targets) document.notes.Remove(note);
                    return true;
                });
                return;
            }
            DeleteSelected();
        }

        private void ShiftSelectionBySnap(int steps)
        {
            float step = SaberChartUtility.SnapStep(CurrentSnap);
            ShiftSelection(note => GridTimeAt(GridBeatAt(note.time) + steps * step), $"{(steps > 0 ? "+" : "−")}1 Snap 動かしました");
        }

        private void ShiftSelectionByMsText(float sign)
        {
            if (!float.TryParse(rangeShiftMsText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float ms) || float.IsNaN(ms) || float.IsInfinity(ms))
            {
                SetStatus("ms は数値で入力してください");
                return;
            }
            float delta = sign * Mathf.Abs(ms);
            ShiftSelection(note => note.time + delta, $"{delta:+0.##;-0.##}ms 動かしました");
        }

        /// <summary>選んだノーツの時刻を動かす。範囲を選んでいれば範囲も一緒に動かす(続けて動かせるように)。</summary>
        private void ShiftSelection(Func<SaberChartNote, float> newTime, string label)
        {
            if (!CanEditAtCursor) return;
            List<SaberChartNote> targets = SelectedNotes();
            if (targets.Count == 0)
            {
                SetStatus("ノーツか範囲を選んでください");
                return;
            }
            if (targets.Any(note => newTime(note) < 0f))
            {
                SetStatus("曲の頭より前へは動かせません");
                return;
            }
            float startBefore = rangeStartMs, endBefore = rangeEndMs;
            float firstBefore = targets.Min(note => note.time);
            bool changed = ApplySelectionEdit(label, list =>
            {
                foreach (SaberChartNote note in list) SetNoteTime(note, newTime(note));
                return true;
            });
            if (!changed || !HasRangeSelection) return;
            float delta = targets.Min(note => note.time) - firstBefore;
            SetRange(startBefore + delta, endBefore + delta);
        }

        private void CopySelection()
        {
            List<SaberChartNote> targets = HasRangeSelection || SelectedNote != null ? SelectedNotes() : RecordingStepNotes();
            if (targets.Count == 0)
            {
                SetStatus("コピーするノーツがありません");
                return;
            }
            rangeClipboard = targets.Select(note => note.Clone()).ToList();
            rangeClipboardStartMs = HasRangeSelection ? rangeStartMs : targets.Min(note => note.time);
            SetStatus($"{targets.Count}ノーツをコピーしました。Ctrl+V で今の位置へ貼り付けます");
        }

        private void PasteAtCursor()
        {
            if (rangeClipboard == null || rangeClipboard.Count == 0)
            {
                SetStatus("コピーした範囲がありません");
                return;
            }
            PasteClip(rangeClipboard, rangeClipboardStartMs, TimeAtBeat(currentBeat), moveRange: false);
        }

        // 範囲の直後(終わりの位置)へ同じ並びを置く。サビの繰り返しなどに使う。
        private void DuplicateRangeAfter()
        {
            if (!HasRangeSelection) return;
            List<SaberChartNote> clip = RangeNotes().Select(note => note.Clone()).ToList();
            if (clip.Count == 0)
            {
                SetStatus("範囲にノーツがありません");
                return;
            }
            PasteClip(clip, rangeStartMs, rangeEndMs, moveRange: true);
        }

        // 拍の上の相対位置で貼る(テンポ地図があっても、拍の並びをそのまま写す)。
        private void PasteClip(List<SaberChartNote> clip, float sourceStartMs, float targetStartMs, bool moveRange)
        {
            if (!CanEditAtCursor) return;
            EndNoteDrag();
            float sourceBeat = GridBeatAt(sourceStartMs);
            float targetBeat = GridBeatAt(targetStartMs);
            string before = CurrentJson();
            List<SaberChartNote> added = SaberChartRangeOps.Paste(document, clip,
                note => GridTimeAt(targetBeat + (GridBeatAt(note.time) - sourceBeat)), BeatAtTime);
            if (added.Count == 0)
            {
                SetStatus("貼り付け先に同じノーツがあるので、何も足しませんでした");
                return;
            }
            history.Record(before);
            selectedIndex = -1;
            MarkChanged();
            if (moveRange)
            {
                float length = rangeEndMs - rangeStartMs;
                float newStart = targetStartMs;
                SetRange(newStart, GridTimeAt(targetBeat + (GridBeatAt(rangeEndMs) - sourceBeat)));
                if (!HasRangeSelection) SetRange(newStart, newStart + length);
            }
            SetStatus($"{added.Count}ノーツを貼り付けました（同じノーツがある所は残しました）");
        }

        /// <summary>今の拍のノーツをまとめて編集する(0=次の Snap へコピー、1=左右反転、2=削除)。</summary>
        private void EditBeatNotes(int action)
        {
            if (!CanEditAtCursor || action < 0 || action > 2) return;
            var notes = RecordingStepNotes();
            if (notes.Count == 0) { SetStatus("この拍は空です。PageUp / PageDownで配置済みノーツへ移動できます"); return; }
            EndNoteDrag();
            string before = CurrentJson();
            int changed = 0;
            if (action == 0)
            {
                float next = RecordingStepNeighbor(currentBeat, 1);
                float sourceBeat = currentBeat;
                List<SaberChartNote> added = SaberChartRangeOps.Paste(document, notes,
                    note => GridTimeAt(next + (GridBeatAt(note.time) - sourceBeat)), BeatAtTime);
                changed = added.Count;
                // コピー先へ移るので、Ctrl+Dを押し直すだけで同じパターンを繰り返せる。
                currentBeat = next;
            }
            else if (action == 1)
            {
                SaberChartRangeOps.Mirror(notes);
                changed = notes.Count;
            }
            else
            {
                foreach (var note in notes) document.notes.Remove(note);
                changed = notes.Count;
            }
            SaberChartUtility.SortNotes(document);
            if (CurrentJson() != before)
            {
                history.Record(before);
                selectedIndex = -1;
                MarkChanged();
            }
            FinishTextEditing();
            SetStatus(action == 0 ? $"{changed}ノーツを次の拍へコピー（既存ノーツは保持）。Ctrl+Dで繰り返し" :
                action == 1 ? $"この拍の{changed}ノーツを左右反転しました。Ctrl+Zで戻せます" : $"この拍の{changed}ノーツを削除しました。Ctrl+Zで戻せます");
        }
    }
}
