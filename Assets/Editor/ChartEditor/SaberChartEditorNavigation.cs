using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>
    /// モードによらないキーの決まり、方向キー、時刻・小節・目印への移動と全体の見取り図。
    /// </summary>
    public sealed partial class SaberChartEditorWindow
    {
        // 方向キーの並びは DirectionValues と同じ(↖↑↗ / ←・→ / ↙↓↘)。
        private static readonly KeyCode[] DirectionPadKeys =
        {
            KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9,
            KeyCode.Keypad4, KeyCode.Keypad5, KeyCode.Keypad6,
            KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3,
        };
        // 録音キー(D F G J K / H B)が入力に使われていないときだけ使う、テンキーの代わり。
        private static readonly KeyCode[] DirectionLetterKeys =
        {
            KeyCode.Q, KeyCode.W, KeyCode.E,
            KeyCode.A, KeyCode.S, KeyCode.D,
            KeyCode.Z, KeyCode.X, KeyCode.C,
        };
        private const float DirectionAssignWindowMs = 200f;
        private const float OverviewWidth = 10f;

        [SerializeField] private bool directionAdvance = true;
        [SerializeField] private bool showMarkers = true;
        [SerializeField] private string jumpMeasureText = string.Empty;
        [SerializeField] private string jumpTimeText = string.Empty;
        [SerializeField] private string newMarkerName = string.Empty;
        private readonly HashSet<KeyCode> directionKeysHeld = new HashSet<KeyCode>();
        // 聴きながら方向を付けた1回分(再生を止めるまで)を、1回の Undo で戻せるようにする。
        private string directionPassBefore;
        private readonly HashSet<SaberChartNote> directionPassAssigned = new HashSet<SaberChartNote>();
        // 重なったノーツを同じ場所のクリックで順に選ぶ。
        private Vector2 lastNoteClick = new Vector2(float.NaN, float.NaN);
        private bool draggingOverview;
        private float recordStepLastBeat;
        private int recordStepLastUndoCount = -1;
        private StagePerformanceTimeline overviewSections;
        private string overviewSectionsSong;

        // 録音・ステップの画面では D F G J K / H B が入力のキーなので、文字の方向キーを使わない(テンキーは使える)。
        private bool LettersAreNoteKeys => RecordingBusy || recordMode;
        private bool CanEditAtCursor => !RecordingBusy && !isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode &&
                                        recordStepHeld.Count == 0;

        private int DirectionKeyIndex(Event input)
        {
            if (input.control || input.command || input.alt) return -1;
            int index = Array.IndexOf(DirectionPadKeys, input.keyCode);
            if (index >= 0) return index;
            return LettersAreNoteKeys ? -1 : Array.IndexOf(DirectionLetterKeys, input.keyCode);
        }

        /// <summary>
        /// 方向キー。止まっているときは選んだノーツ(ステップでは今の拍)、再生中はその時刻にいちばん近いノーツ、
        /// 録音中は次に打つノーツの方向を変える。
        /// </summary>
        private bool HandleDirectionKey(Event input)
        {
            if (input.type == EventType.KeyUp)
            {
                directionKeysHeld.Remove(input.keyCode);
                return false;
            }
            if (input.type != EventType.KeyDown || EditorGUIUtility.editingTextField) return false;
            int index = DirectionKeyIndex(input);
            if (index < 0) return false;
            input.Use();
            if (!directionKeysHeld.Add(input.keyCode)) return true; // 押し続けのリピートでは繰り返さない
            string direction = DirectionValues[index];
            if (RecordingBusy)
            {
                SetPaletteDirection(direction);
                SetStatus($"次に打つノーツの方向: {DirectionGlyph(direction)}");
                return true;
            }
            if (isPlaying)
            {
                AssignDirectionNearPlayback(direction);
                return true;
            }
            AssignDirectionToTargets(direction);
            return true;
        }

        private void SetPaletteDirection(string direction)
        {
            paletteDirection = direction;
            if (direction != SaberChartUtility.DirectionNone) paletteType = SaberChartUtility.TypeDirection;
            else if (paletteType == SaberChartUtility.TypeDirection) paletteType = SaberChartUtility.TypeTap;
            Repaint();
        }

        private List<SaberChartNote> DirectionTargets()
        {
            if (recordMode && recordStepMode) return RecordingStepNotes();
            var selected = SelectedNotes();
            return selected;
        }

        private void AssignDirectionToTargets(string direction)
        {
            EndNoteDrag();
            List<SaberChartNote> targets = DirectionTargets();
            if (targets.Count == 0)
            {
                SetPaletteDirection(direction);
                SetStatus($"選択がないので、次に置くノーツの方向を {DirectionGlyph(direction)} にしました");
                return;
            }
            string before = CurrentJson();
            SaberChartNote last = targets.OrderBy(note => note.time).Last();
            foreach (SaberChartNote note in targets) SetNoteDirection(note, direction);
            if (CurrentJson() != before)
            {
                history.Record(before);
                MarkChanged();
            }
            SetStatus($"{targets.Count}ノーツを {DirectionGlyph(direction)} にしました");
            if (!directionAdvance) return;
            if (recordMode && recordStepMode) JumpToNote(1);
            else if (targets.Count == 1) SelectNextNoteAfter(last);
        }

        /// <summary>方向を付ける。TAP と方向ノーツは種類も合わせ、LONG は LONG のまま方向だけ変える。</summary>
        private static void SetNoteDirection(SaberChartNote note, string direction)
        {
            note.direction = direction;
            if (note.type == SaberChartUtility.TypeLong || note.count > 1) return;
            note.type = direction == SaberChartUtility.DirectionNone
                ? SaberChartUtility.TypeTap
                : SaberChartUtility.TypeDirection;
        }

        private void SelectNextNoteAfter(SaberChartNote note)
        {
            int index = document.notes.IndexOf(note);
            if (index < 0 || index + 1 >= document.notes.Count) return;
            selectedIndex = index + 1;
            SyncPalettePositionFromSelected();
            float beat = TimelineBeat(document.notes[selectedIndex]);
            currentBeat = Mathf.Clamp(beat, 0f, MaxBeat());
            Repaint();
        }

        // 録音と同じ音声の時計で、押した瞬間にいちばん近いノーツへ方向を付ける。
        private void AssignDirectionNearPlayback(string direction)
        {
            if (!TryRecordingPosition(out float seconds))
            {
                SetStatus("再生位置を読めませんでした");
                return;
            }
            float time = seconds * 1000f - document.offsetMs - recordInputOffsetMs;
            SaberChartNote nearest = null;
            float best = DirectionAssignWindowMs;
            foreach (SaberChartNote note in document.notes)
            {
                float distance = Mathf.Abs(note.time - time);
                if (distance > best) continue;
                best = distance;
                nearest = note;
            }
            if (nearest == null)
            {
                SetStatus($"{DirectionAssignWindowMs:0}ms 以内にノーツがありません");
                return;
            }
            // 同時打ちは左から順に、まだ付けていないノーツへ付ける。
            List<SaberChartNote> group = document.notes
                .Where(note => Mathf.Abs(note.time - nearest.time) < 1f)
                .OrderBy(note => note.x)
                .ToList();
            SaberChartNote target = group.FirstOrDefault(note => !directionPassAssigned.Contains(note)) ?? group[0];
            directionPassBefore ??= CurrentJson();
            SetNoteDirection(target, direction);
            directionPassAssigned.Add(target);
            MarkChanged();
            SetStatus($"{FormatClock(target.time + document.offsetMs)} のノーツを {DirectionGlyph(direction)} に（止めると1回の Undo で戻せます）");
        }

        private void FinishDirectionPass()
        {
            if (directionPassBefore != null && CurrentJson() != directionPassBefore)
            {
                history.Record(directionPassBefore);
                MarkChanged();
            }
            directionPassBefore = null;
            directionPassAssigned.Clear();
        }

        /// <summary>選んでいるノーツ。範囲を選んでいれば範囲のノーツ、なければ1個の選択。</summary>
        private List<SaberChartNote> SelectedNotes()
        {
            var result = new List<SaberChartNote>();
            if (HasRangeSelection) result.AddRange(RangeNotes());
            else if (SelectedNote != null) result.Add(SelectedNote);
            return result;
        }

        // ---- 使1: 矢印は常にシーク。XY は Alt+矢印 ----

        private bool HandleArrowKeys(Event current)
        {
            KeyCode key = current.keyCode;
            bool arrow = key == KeyCode.LeftArrow || key == KeyCode.RightArrow || key == KeyCode.UpArrow || key == KeyCode.DownArrow;
            if (!arrow || current.control || current.command) return false;
            if (current.alt)
            {
                NudgeSelection(key, current.shift);
                current.Use();
                return true;
            }
            bool forward = key == KeyCode.RightArrow || key == KeyCode.UpArrow;
            SeekToBeat(current.shift
                ? (float)new ChartMeterMap(document.timeSignatures).AdjacentBar(currentBeat, forward)
                : currentBeat + (forward ? 1f : -1f) * SaberChartUtility.SnapStep(CurrentSnap));
            current.Use();
            return true;
        }

        // 選んだノーツの横・高さを、列の間隔(Shift で 0.01)ずつ動かす。
        private void NudgeSelection(KeyCode key, bool fine)
        {
            List<SaberChartNote> targets = SelectedNotes();
            if (targets.Count == 0 || !CanEditAtCursor)
            {
                if (targets.Count == 0) SetStatus("Alt+矢印は選んだノーツの位置を動かします");
                return;
            }
            EndNoteDrag();
            float dx = key == KeyCode.LeftArrow ? -1f : key == KeyCode.RightArrow ? 1f : 0f;
            float dy = key == KeyCode.DownArrow ? -1f : key == KeyCode.UpArrow ? 1f : 0f;
            float stepX = fine ? .01f : (SaberChartUtility.DefaultXMax - SaberChartUtility.DefaultXMin) / (LaneCount - 1);
            float stepY = fine ? .01f : (SaberChartUtility.DefaultYMax - SaberChartUtility.DefaultYMin) / (LaneCount - 1);
            string before = CurrentJson();
            foreach (SaberChartNote note in targets)
            {
                note.x = Mathf.Clamp(note.x + dx * stepX, SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax);
                note.y = Mathf.Clamp(note.y + dy * stepY, SaberChartUtility.DefaultYMin, SaberChartUtility.DefaultYMax);
            }
            if (CurrentJson() == before) return;
            SaberChartNote keep = SelectedNote;
            SaberChartUtility.SortNotes(document);
            if (keep != null) selectedIndex = document.notes.IndexOf(keep);
            SyncPalettePositionFromSelected();
            history.Record(before);
            MarkChanged();
        }

        // ---- 使1: Ctrl+D・Ctrl+M・Shift+Delete・PageUp/Down は「選択があれば選択、無ければ今の拍」 ----

        private bool HandleSelectionOrBeatShortcut(Event current)
        {
            bool action = current.control || current.command;
            bool handled = action && !current.alt && (current.keyCode == KeyCode.D || current.keyCode == KeyCode.M) ||
                           !action && !current.alt && current.shift && current.keyCode == KeyCode.Delete ||
                           !action && !current.alt && !current.shift && (current.keyCode == KeyCode.PageUp || current.keyCode == KeyCode.PageDown);
            if (!handled) return false;
            // 押し続けのリピートで、複製や反転を何度も繰り返さない(離すと次の操作を受け付ける)。
            if (!recordStepActionKeys.Add(current.keyCode))
            {
                current.Use();
                return true;
            }
            if (action && !current.shift && !current.alt && current.keyCode == KeyCode.D)
            {
                if (HasRangeSelection) DuplicateRangeAfter();
                else if (SelectedNote != null) DuplicateSelected();
                else EditBeatNotes(0);
                current.Use();
                return true;
            }
            if (action && !current.alt && current.keyCode == KeyCode.M)
            {
                if (current.shift) SwapColorsOfSelectionOrBeat();
                else if (HasRangeSelection || SelectedNote != null) MirrorSelection();
                else EditBeatNotes(1);
                current.Use();
                return true;
            }
            if (!action && !current.alt && current.shift && current.keyCode == KeyCode.Delete)
            {
                EditBeatNotes(2);
                current.Use();
                return true;
            }
            if (!action && !current.alt && !current.shift &&
                (current.keyCode == KeyCode.PageUp || current.keyCode == KeyCode.PageDown))
            {
                JumpToNote(current.keyCode == KeyCode.PageUp ? -1 : 1);
                current.Use();
                return true;
            }
            return false;
        }

        // ---- 使4・使5: 再生中のクリックはシーク。重なったノーツは同じ場所のクリックで順に選ぶ ----

        private int FindNoteForClick(Vector2 mouse, Rect laneRect)
        {
            var hits = new List<int>();
            for (int index = document.notes.Count - 1; index >= 0; index--)
            {
                Rect hit = NoteRect(document.notes[index], laneRect);
                hit.xMin -= 3f;
                hit.xMax += 3f;
                hit.yMin -= 4f;
                hit.yMax += 4f;
                if (hit.Contains(mouse)) hits.Add(index);
            }
            bool sameSpot = !float.IsNaN(lastNoteClick.x) && (mouse - lastNoteClick).sqrMagnitude <= 9f;
            lastNoteClick = mouse;
            if (hits.Count == 0) return -1;
            int current = hits.IndexOf(selectedIndex);
            return sameSpot && current >= 0 ? hits[(current + 1) % hits.Count] : hits[0];
        }

        // 実際の横位置で描く。録音キーの G(0) と J(0.71) のように列の間にあるノーツも、別の位置に見える。
        private static float NoteCenterX(float x, Rect laneRect)
        {
            float laneWidth = laneRect.width / LaneCount;
            float normalized = Mathf.InverseLerp(SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax,
                Mathf.Clamp(x, SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax));
            return laneRect.x + (normalized * (LaneCount - 1) + .5f) * laneWidth;
        }

        // ---- 使6: ホイールで動く量を Snap によらず一定にする ----

        private float WheelSeekBeats(float deltaY, bool fine)
        {
            // Windows では1ノッチが ±3。1ノッチ=1拍、Shift で Snap 1つ分。
            float notches = deltaY / 3f;
            return notches * (fine ? SaberChartUtility.SnapStep(CurrentSnap) : 1f);
        }

        // ---- 速8: 小節・時刻へ移動、目印、全体の見取り図 ----

        private void DrawJumpControls()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("小節", GUILayout.Width(28f));
            jumpMeasureText = GUILayout.TextField(jumpMeasureText ?? string.Empty, GUILayout.Width(44f));
            if (GUILayout.Button("移動", EditorStyles.miniButton, GUILayout.Width(38f))) JumpToMeasureText();
            GUILayout.Space(6f);
            GUILayout.Label("時刻", GUILayout.Width(28f));
            jumpTimeText = GUILayout.TextField(jumpTimeText ?? string.Empty, GUILayout.MinWidth(54f));
            if (GUILayout.Button("移動", EditorStyles.miniButton, GUILayout.Width(38f))) JumpToTimeText();
            GUILayout.EndHorizontal();
            GUILayout.Label("時刻は 1:23.5 か 83.5（秒）。音源の先頭からの時刻です", smallMutedStyle);
        }

        private void JumpToMeasureText()
        {
            FinishTextEditing();
            if (!int.TryParse((jumpMeasureText ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int measure) || measure < 1)
            {
                SetStatus("小節は 1 以上の整数で入力してください");
                return;
            }
            float? beat = MeasureStartBeat(measure);
            if (beat == null)
            {
                SetStatus($"第{measure}小節は曲の範囲の外です");
                return;
            }
            SeekToBeat(beat.Value);
            SetStatus($"第{measure}小節へ移動しました");
        }

        private float? MeasureStartBeat(int measure)
        {
            var meter = new ChartMeterMap(document.timeSignatures);
            int count = 0;
            foreach (double start in meter.BarStarts(0, MaxBeat() + 64))
            {
                if (++count == measure) return (float)start;
            }
            return null;
        }

        private void JumpToTimeText()
        {
            FinishTextEditing();
            if (!TryParseClock(jumpTimeText, out float seconds))
            {
                SetStatus("時刻は 1:23.5 か 83.5 の形で入力してください");
                return;
            }
            SeekToBeat(BeatAtTime(seconds * 1000f - document.offsetMs));
            SetStatus($"{FormatClock(seconds * 1000f)} へ移動しました");
        }

        public static bool TryParseClock(string text, out float seconds)
        {
            seconds = 0f;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text.Trim();
            int colon = trimmed.LastIndexOf(':');
            if (colon < 0)
                return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) &&
                       seconds >= 0f && !float.IsInfinity(seconds);
            if (!int.TryParse(trimmed.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) ||
                !float.TryParse(trimmed.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float rest) ||
                minutes < 0 || rest < 0f || rest >= 60f)
                return false;
            seconds = minutes * 60f + rest;
            return true;
        }

        private static string FormatClock(float milliseconds)
        {
            float seconds = Mathf.Max(0f, milliseconds / 1000f);
            int minutes = Mathf.FloorToInt(seconds / 60f);
            return $"{minutes}:{seconds - minutes * 60f:00.000}";
        }

        private void DrawMarkerControls()
        {
            showMarkers = EditorGUILayout.Foldout(showMarkers, $"目印（{document.markers.Count}）", true);
            if (!showMarkers) return;
            GUILayout.BeginHorizontal();
            newMarkerName = GUILayout.TextField(newMarkerName ?? string.Empty, GUILayout.MinWidth(90f));
            if (GUILayout.Button("現在位置に追加", EditorStyles.miniButton, GUILayout.Width(92f))) AddMarkerAtCursor();
            GUILayout.EndHorizontal();
            for (int i = 0; i < document.markers.Count; i++)
            {
                SaberChartMarker marker = document.markers[i];
                GUILayout.BeginHorizontal();
                if (GUILayout.Button($"{FormatClock(marker.timeMs + document.offsetMs)}  {marker.name}", EditorStyles.miniButtonLeft))
                    SeekToBeat(BeatAtTime(marker.timeMs));
                if (GUILayout.Button("×", EditorStyles.miniButtonRight, GUILayout.Width(22f)))
                {
                    RemoveMarker(i);
                    GUILayout.EndHorizontal();
                    break;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("目印は譜面ファイルの editorMarkers に保存します（本編は読みません）。新規・複製で引き継ぎます", smallMutedStyle);
        }

        private void AddMarkerAtCursor()
        {
            FinishTextEditing();
            EndNoteDrag();
            string before = CurrentJson();
            string name = string.IsNullOrWhiteSpace(newMarkerName) ? $"目印{document.markers.Count + 1}" : newMarkerName.Trim();
            document.markers.Add(new SaberChartMarker { timeMs = TimeAtBeat(currentBeat), name = name });
            document.markers.Sort((a, b) => a.timeMs.CompareTo(b.timeMs));
            newMarkerName = string.Empty;
            history.Record(before);
            MarkChanged();
            SetStatus($"目印「{name}」を追加しました");
        }

        private void RemoveMarker(int index)
        {
            if (index < 0 || index >= document.markers.Count) return;
            EndNoteDrag();
            string before = CurrentJson();
            document.markers.RemoveAt(index);
            history.Record(before);
            MarkChanged();
        }

        private void DrawMarkersOnTimeline(Rect timelineRect, Rect laneRect)
        {
            if (document.markers == null) return;
            var color = new Color(1f, .62f, .2f, .85f);
            foreach (SaberChartMarker marker in document.markers)
            {
                float y = YForBeat(BeatAtTime(marker.timeMs), timelineRect);
                if (y < timelineRect.y - 20f || y > timelineRect.yMax + 20f) continue;
                EditorGUI.DrawRect(new Rect(laneRect.x, y, laneRect.width, 1f), color);
                GUI.Label(new Rect(laneRect.xMax - 160f, y - 17f, 156f, 16f), "◆ " + marker.name,
                    new GUIStyle(smallMutedStyle) { alignment = TextAnchor.MiddleRight, normal = { textColor = color } });
            }
        }

        // 右端の細い帯に曲全体のノーツの密度・サビ・目印・今見えている範囲を出す。クリックとドラッグで移動する。
        private void DrawOverview(Rect timelineRect, Event current)
        {
            Rect strip = new Rect(timelineRect.xMax - OverviewWidth - 1f, timelineRect.y + 2f, OverviewWidth, timelineRect.height - 4f);
            EditorGUI.DrawRect(strip, new Color(.05f, .07f, .1f));
            float total = Mathf.Max(1f, MaxBeat());
            float YAt(float beat) => strip.yMax - Mathf.Clamp01(beat / total) * strip.height;

            StagePerformanceTimeline sections = OverviewSections();
            if (sections?.sections != null)
            {
                foreach (var section in sections.sections)
                {
                    if (section == null || section.intensity < StagePerformanceTimeline.StrongIntensity) continue;
                    float top = YAt(BeatAtTime((float)section.endSeconds * 1000f - document.offsetMs));
                    float bottom = YAt(BeatAtTime((float)section.startSeconds * 1000f - document.offsetMs));
                    EditorGUI.DrawRect(new Rect(strip.x, top, strip.width, Mathf.Max(1f, bottom - top)), new Color(1f, .78f, .18f, .16f));
                }
            }

            int bins = Mathf.Max(1, Mathf.FloorToInt(strip.height / 2f));
            var counts = new int[bins];
            int peak = 1;
            foreach (SaberChartNote note in document.notes)
            {
                int bin = Mathf.Clamp(Mathf.FloorToInt(TimelineBeat(note) / total * bins), 0, bins - 1);
                peak = Mathf.Max(peak, ++counts[bin]);
            }
            for (int bin = 0; bin < bins; bin++)
            {
                if (counts[bin] == 0) continue;
                float y = strip.yMax - (bin + 1) * strip.height / bins;
                float strength = counts[bin] / (float)peak;
                EditorGUI.DrawRect(new Rect(strip.x + 1f, y, (strip.width - 2f) * (.35f + .65f * strength), strip.height / bins),
                    new Color(AccentColor.r, AccentColor.g, AccentColor.b, .35f + .55f * strength));
            }
            foreach (SaberChartMarker marker in document.markers)
                EditorGUI.DrawRect(new Rect(strip.x - 2f, YAt(BeatAtTime(marker.timeMs)), strip.width + 4f, 1f), new Color(1f, .62f, .2f));

            float viewTop = YAt(BeatAtY(timelineRect.y, timelineRect));
            float viewBottom = YAt(BeatAtY(timelineRect.yMax, timelineRect));
            DrawOutline(new Rect(strip.x - 1f, viewTop, strip.width + 2f, Mathf.Max(2f, viewBottom - viewTop)), Color.white, 1f);

            if (RecordingBusy) return;
            if (current.type == EventType.MouseDown && current.button == 0 && strip.Contains(current.mousePosition))
            {
                draggingOverview = true;
                SeekToBeat((strip.yMax - current.mousePosition.y) / strip.height * total);
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && draggingOverview)
            {
                SeekToBeat(Mathf.Clamp01((strip.yMax - current.mousePosition.y) / strip.height) * total);
                current.Use();
            }
            else if (current.rawType == EventType.MouseUp && draggingOverview)
            {
                draggingOverview = false;
            }
        }

        private StagePerformanceTimeline OverviewSections()
        {
            string song = EditingSongId;
            if (overviewSectionsSong != song)
            {
                overviewSectionsSong = song;
                overviewSections = SaberChartFileStore.IsValidSongId(song, out _) ? StagePerformanceTimeline.Load(song) : null;
            }
            return overviewSections;
        }

        // ---- 使1: いまの Snap と、モード別のキー一覧 ----

        private void DrawSnapBadge(Rect laneRect)
        {
            string label = $"Snap {SnapLabels[Mathf.Clamp(snapIndex, 0, SnapLabels.Length - 1)]}";
            var style = new GUIStyle(smallMutedStyle) { alignment = TextAnchor.MiddleLeft, normal = { textColor = AccentColor } };
            Rect rect = new Rect(laneRect.x + 4f, laneRect.y + 3f, 140f, 16f);
            EditorGUI.DrawRect(new Rect(rect.x - 2f, rect.y, style.CalcSize(new GUIContent(label)).x + 6f, rect.height),
                new Color(0f, 0f, 0f, .45f));
            GUI.Label(rect, label, style);
        }

        private string FooterKeyGuide()
        {
            if (RecordingBusy)
                return "D F G J K 単打 / H B 同時 / Backspace 1打戻す / Space・Esc 終了 / Alt+矢印 位置 / テンキー 次の方向";
            if (recordMode && recordStepMode)
                return "D F G J K・H B 配置 / Tab 次 / Backspace 1打戻す / ←→↑↓ シーク / Alt+矢印 位置 / テンキー 方向 / Ctrl+D コピー / Ctrl+M 反転";
            if (recordMode)
                return "R 録音 / Shift+R 録り直し / Enter 聴き直す / 1〜5 キー選択 / Alt+矢印 位置 / F1〜F3 配置 / テンキー 方向";
            return "Space 再生 / ←→↑↓ シーク / Alt+矢印 位置 / Del 削除 / Ctrl+D 複製 / Ctrl+M 反転 / QWE・ASD・ZXC・テンキー 方向 / PgUp・PgDn ノーツへ / [ ] 範囲";
        }

        // ---- ステップ入力の Backspace: 直前の1打を取り消し、その拍へ戻る ----

        private void UndoLastStepInput()
        {
            if (!CanStepInput || recordStepHeld.Count > 0 || !history.CanUndo) return;
            bool lastWasStep = history.UndoCount == recordStepLastUndoCount;
            float beat = recordStepLastBeat;
            Undo();
            if (lastWasStep) currentBeat = Mathf.Clamp(beat, 0f, MaxBeat());
            recordStepLastUndoCount = -1;
            SetStatus(lastWasStep ? "直前の1打を取り消し、その拍へ戻りました" : "直前の操作を取り消しました");
        }

        private void JumpToNote(int direction)
        {
            if (recordStepHeld.Count > 0) return;
            float target = direction > 0 ? float.PositiveInfinity : float.NegativeInfinity;
            foreach (SaberChartNote note in document.notes)
            {
                float beat = TimelineBeat(note);
                if (direction > 0 && beat > currentBeat + .0001f && beat < target ||
                    direction < 0 && beat < currentBeat - .0001f && beat > target) target = beat;
            }
            if (float.IsInfinity(target)) { SetStatus("この先にノーツはありません"); return; }
            SeekToBeat(target);
            FinishTextEditing();
        }
    }
}
