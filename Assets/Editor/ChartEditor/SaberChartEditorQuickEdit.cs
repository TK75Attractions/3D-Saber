using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>選択したノーツを、曲設定までスクロールせずに確認・修正する。</summary>
    public sealed partial class SaberChartEditorWindow
    {
        [SerializeField] private bool showQuickNoteDetails;
        [SerializeField] private bool showQuickNotePosition;
        private SaberChartNote inspectorNote;

        private void RevealSelectedNote()
        {
            if (ReferenceEquals(inspectorNote, SelectedNote)) return;
            inspectorNote = SelectedNote;
            if (inspectorNote != null) rightScroll = Vector2.zero;
        }

        private void DrawFocusedNotePanel()
        {
            SectionLabel(SelectedNote != null ? $"選択ノーツ  {selectedIndex + 1} / {document.notes.Count}" : "ノーツ編集");
            using (new EditorGUI.DisabledScope(!CanEditAtCursor))
            {
                GUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(AdjacentNoteIndex(-1) < 0))
                    if (GUILayout.Button(new GUIContent("◀ 前のノーツ", "同時刻のノーツも1個ずつ選びます"))) SelectAdjacentNote(-1);
                using (new EditorGUI.DisabledScope(AdjacentNoteIndex(1) < 0))
                    if (GUILayout.Button(new GUIContent("次のノーツ ▶", "選択がなければ現在位置以降のノーツを選びます"))) SelectAdjacentNote(1);
                GUILayout.EndHorizontal();

                if (SelectedNote != null)
                {
                    SaberChartNote note = SelectedNote;
                    int color = System.Array.IndexOf(ColorValues, note.color);
                    GUILayout.Label($"音源上 {(note.time + document.offsetMs) / 1000f:0.000}s（OFFSET込み） /  {(color >= 0 ? ColorLabels[color] : note.color)}  /  {DirectionGlyph(note.direction)}", smallMutedStyle);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("10ms早く")) NudgeSelectedNoteTime(-10f);
                    if (GUILayout.Button("10ms遅く")) NudgeSelectedNoteTime(10f);
                    if (GUILayout.Button(new GUIContent("拍にそろえる", "現在のSnapとテンポ地図に合わせます。長さ・位置は変えません"))) SnapSelectedNote();
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("選択位置へ")) SeekSelectedNote(0f);
                    using (new EditorGUI.DisabledScope(audioClip == null || !SaberChartAudioPreview.IsSupported))
                        if (GUILayout.Button("1拍前から聴く")) PreviewSelectedNote();
                    GUILayout.EndHorizontal();

                    showQuickNoteDetails = EditorGUILayout.Foldout(showQuickNoteDetails, "詳細を編集（種類・色・方向・長さ）", true);
                    if (showQuickNoteDetails) DrawSelectedInspector();
                }
                else
                {
                    GUILayout.Label(HasRangeSelection ? "範囲を編集中。下の「範囲」でまとめて変更できます" : "タイムラインか前後のボタンでノーツを選択", smallMutedStyle);
                }

                showQuickNotePosition = EditorGUILayout.Foldout(showQuickNotePosition,
                    SelectedNote != null ? "選択ノーツのXY位置" : "次に置くXY位置", true);
                if (showQuickNotePosition)
                {
                    Rect pad = GUILayoutUtility.GetRect(220f, 180f, GUILayout.ExpandWidth(true));
                    DrawSpatialPad(pad);
                    GUILayout.Label("上が +Y / 右が +X", centeredSmallStyle);
                }
            }
            GUILayout.Space(10f);
        }

        // 同時刻の左右ノーツを飛ばさないように、時刻でなく一覧内の順序で移る。
        private int AdjacentNoteIndex(int direction)
        {
            if (document.notes.Count == 0) return -1;
            if (SelectedNote != null)
            {
                int next = selectedIndex + (direction > 0 ? 1 : -1);
                return next >= 0 && next < document.notes.Count ? next : -1;
            }
            float time = TimeAtBeat(currentBeat);
            if (direction > 0) return document.notes.FindIndex(note => note.time >= time - .01f);
            return document.notes.FindLastIndex(note => note.time <= time + .01f);
        }

        private void SelectAdjacentNote(int direction)
        {
            if (!CanEditAtCursor) return;
            EndNoteDrag();
            int next = AdjacentNoteIndex(direction);
            if (next < 0) return;
            FinishTextEditing();
            ClearRange();
            selectedIndex = next;
            SyncPalettePositionFromSelected();
            SeekSelectedNote(0f);
        }

        private void SeekSelectedNote(float leadBeats)
        {
            if (!CanEditAtCursor || SelectedNote == null) return;
            FinishTextEditing();
            SeekToBeat(TimelineBeat(SelectedNote) - Mathf.Max(0f, leadBeats));
        }

        private void PreviewSelectedNote()
        {
            if (!CanEditAtCursor || SelectedNote == null || audioClip == null) return;
            SeekSelectedNote(1f);
            TogglePreview();
        }

        private void NudgeSelectedNoteTime(float milliseconds)
        {
            if (!CanEditAtCursor || HasRangeSelection || SelectedNote == null) return;
            ShiftSelection(note => note.time + milliseconds, $"{milliseconds:+0;-0}ms 動かしました。Ctrl+Z で戻せます");
            SeekSelectedNote(0f);
        }

        private void SnapSelectedNote()
        {
            if (!CanEditAtCursor || HasRangeSelection || SelectedNote == null) return;
            ApplySelectionEdit("選択ノーツを拍にそろえました。Ctrl+Z で戻せます", targets =>
            {
                SaberChartNote note = targets[0];
                float beat = SaberChartUtility.QuantizeBeat(TimelineBeat(note), CurrentSnap, document);
                float time = TimeAtBeat(beat);
                // 既に格子上なら、古い譜面の拍情報を不用意に更新しない。
                if (Mathf.Abs(time - note.time) < .01f) return false;
                SetNoteTime(note, time);
                return true;
            });
            SeekSelectedNote(0f);
        }
    }
}
