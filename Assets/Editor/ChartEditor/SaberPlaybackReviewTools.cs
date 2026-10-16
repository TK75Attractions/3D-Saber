using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    // レビューの下書きは譜面と別に保存する。元ファイルのハッシュごとに分け、古い指摘を混ぜない。
    [Serializable] sealed class SaberReviewDraft
    {
        public int version = 1;
        public string song, difficulty, diskHash, legacyHash, chartJson, candidateJson, requestJson, lastFolder;
        public string instruction, editingCommentId, changeSummary;
        public float seconds, rangeA, rangeB;
        public bool hasPoint, loop, after, unsaved;
        public Vector2 point;
        public int[] selected;
        public string[] undo;
        public List<SaberReviewComment> comments;
        public string replyJson;
        public int beatSnap, timelineZoom, changeIndex = -1, changePage;
        public float timelineStart, reviewLeadIn = 1;
        public bool showWaveform = true;
    }

    public sealed partial class SaberPlaybackReviewWindow
    {
        [SerializeField] string editingCommentId = "", loadedSong, loadedDifficulty;
        [SerializeField] int timelineZoom;
        [SerializeField] float timelineStart;
        SaberReviewComment deletedComment;
        string lastDraftJson, draftMessage = "指摘と入力中の文章を、このPCへ自動保存します。";
        double nextDraftSave;
        bool timelineDragging, resumeAfterDrag, rangeDragging;
        float dragAnchor;
        Rect timelineRect;
        string checkedRequestJson, checkedChartJson, checkedChartHash;
        SaberReviewRequest checkedRequest;
        static readonly float[] TimelineSpans = { 0, 10, 5, 2 };

        string DraftPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "3DSaberPlaybackReview",
            SaberPlaybackReviewData.Hash(loadedSong + "\n" + loadedDifficulty + "\n" + diskHash) + ".json"));

        bool ExportIsCurrent
        {
            get
            {
                if (string.IsNullOrEmpty(requestJson) || !string.IsNullOrWhiteSpace(instruction)) return false;
                // 毎描画で数千ノーツの依頼JSONを読み直さない。
                if (checkedRequestJson != requestJson) { checkedRequest = JsonUtility.FromJson<SaberReviewRequest>(requestJson); checkedRequestJson = requestJson; }
                if (checkedChartJson != chartJson) { checkedChartHash = SaberPlaybackReviewData.Hash(chartJson); checkedChartJson = chartJson; }
                return checkedRequest != null && checkedRequest.baseHash == checkedChartHash
                    && JsonUtility.ToJson(new SaberReviewRequest { comments = checkedRequest.comments })
                    == JsonUtility.ToJson(new SaberReviewRequest { comments = comments });
            }
        }

        void SaveDraft()
        {
            if (document == null || string.IsNullOrEmpty(loadedSong) || string.IsNullOrEmpty(diskHash)) return;
            try
            {
                string json = JsonUtility.ToJson(new SaberReviewDraft {
                    song = loadedSong, difficulty = loadedDifficulty, diskHash = diskHash, legacyHash = legacyHash,
                    chartJson = chartJson, candidateJson = candidateJson, requestJson = requestJson, lastFolder = lastFolder,
                    instruction = instruction, editingCommentId = editingCommentId, changeSummary = changeSummary,
                    seconds = seconds, rangeA = rangeA, rangeB = rangeB, hasPoint = hasPoint, point = point,
                    loop = loop, after = after, unsaved = hasUnsavedChanges, selected = selected.OrderBy(i => i).ToArray(),
                    undo = undo.ToArray(), comments = comments, replyJson = replyJson,
                    beatSnap = beatSnap, timelineZoom = timelineZoom, timelineStart = timelineStart,
                    reviewLeadIn = reviewLeadIn, showWaveform = showWaveform, changeIndex = changeIndex, changePage = changePage
                });
                if (json == lastDraftJson && File.Exists(DraftPath)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(DraftPath));
                string temp = DraftPath + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(DraftPath)) File.Replace(temp, DraftPath, null); else File.Move(temp, DraftPath);
                lastDraftJson = json; draftMessage = "下書き保存済み · 同じ曲・難易度を開くと復元します。";
            }
            catch (Exception e) { draftMessage = "下書きを保存できません: " + e.Message; }
        }

        void RestoreDraft()
        {
            lastDraftJson = null;
            if (!File.Exists(DraftPath)) return;
            try
            {
                var draft = JsonUtility.FromJson<SaberReviewDraft>(File.ReadAllText(DraftPath));
                if (draft == null || draft.version != 1 || draft.song != song || draft.difficulty != difficulty || draft.diskHash != diskHash)
                    throw new InvalidOperationException("曲・難易度または元譜面が一致しません。");
                var restored = SaberChartUtility.FromJson(draft.chartJson);
                var restoredCandidate = string.IsNullOrEmpty(draft.candidateJson) ? null : SaberChartUtility.FromJson(draft.candidateJson);
                SaberPlaybackReviewData.ValidateComments(restored, draft.comments, Duration);
                document = restored; chartJson = draft.chartJson; candidate = restoredCandidate; candidateJson = draft.candidateJson;
                legacyHash = draft.legacyHash; requestJson = draft.requestJson; lastFolder = draft.lastFolder;
                comments = draft.comments; instruction = draft.instruction ?? ""; editingCommentId = draft.editingCommentId ?? "";
                changeSummary = draft.changeSummary; seconds = SafeTime(draft.seconds); rangeA = SafeTime(draft.rangeA);
                replyJson = draft.replyJson ?? ""; beatSnap = Mathf.Clamp(draft.beatSnap, 0, 3);
                timelineZoom = Mathf.Clamp(draft.timelineZoom, 0, 3); timelineStart = SafeTime(draft.timelineStart);
                reviewLeadIn = new[] { 0f, .5f, 1f, 2f }.Contains(draft.reviewLeadIn) ? draft.reviewLeadIn : 1;
                showWaveform = draft.showWaveform; changeIndex = draft.changeIndex; changePage = draft.changePage;
                rangeB = Mathf.Max(rangeA, SafeTime(draft.rangeB)); hasPoint = draft.hasPoint; point = draft.point;
                loop = draft.loop; after = draft.after; hasUnsavedChanges = draft.unsaved;
                selected.Clear(); foreach (int i in draft.selected ?? Array.Empty<int>()) if (i >= 0 && i < document.notes.Count) selected.Add(i);
                undo.Clear(); foreach (string json in (draft.undo ?? Array.Empty<string>()).Reverse()) undo.Push(json);
                status = "前回の指摘・入力中の文章・再生位置を復元しました。";
                draftMessage = "このPCの下書きから復元済み。";
            }
            catch (Exception e)
            {
                // 読めない下書きは退避してから新しく保存する。
                try { File.Copy(DraftPath, DraftPath + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmssfff")); }
                catch (IOException) { }
                status = "下書きを復元できません: " + e.Message;
            }
        }

        float SafeTime(float time) => float.IsNaN(time) || float.IsInfinity(time) ? 0 : Mathf.Clamp(time, 0, Duration);

        void VisitComment(SaberReviewComment comment)
        {
            Stop(); Seek(comment.frameSeconds); rangeA = comment.fromSeconds; rangeB = comment.toSeconds;
            point = comment.screenPoint; hasPoint = comment.hasPoint; selected.Clear();
            foreach (int i in comment.noteIndices ?? Array.Empty<int>()) if (i >= 0 && i < document.notes.Count) selected.Add(i);
            timelineStart = Mathf.Max(0, seconds - TimelineSpans[timelineZoom] / 2);
        }
        void EditComment(SaberReviewComment comment)
        {
            // 書きかけの別の指摘を、一覧の操作で黙って捨てない。
            if (!string.IsNullOrWhiteSpace(instruction) && (editingCommentId != comment.id || instruction != comment.instruction))
            { status = "入力中の指摘を先に追加・更新してください。"; return; }
            VisitComment(comment); instruction = comment.instruction; editingCommentId = comment.id;
            EditorGUI.FocusTextInControl("ReviewInstruction");
        }
        void DeleteComment(SaberReviewComment comment)
        {
            deletedComment = comment; comments.Remove(comment);
            if (editingCommentId == comment.id) { editingCommentId = ""; instruction = ""; }
            SaveDraft();
        }
        void RestoreDeletedComment()
        {
            if (deletedComment == null) return;
            comments.Add(deletedComment); deletedComment = null; SaveDraft();
        }
        void AppendTemplate(string text)
        {
            Stop(); instruction += (string.IsNullOrWhiteSpace(instruction) ? "" : "\n") + text;
            EditorGUI.FocusTextInControl("ReviewInstruction");
        }
        void MarkCurrent()
        {
            Stop(); rangeA = Mathf.Max(0, seconds - 1); rangeB = Mathf.Min(Duration, seconds + 1);
            selected.Clear(); hasPoint = false;
            EditorGUI.FocusTextInControl("ReviewInstruction");
        }
        void JumpNote(int direction)
        {
            float target = SaberPlaybackReviewData.AdjacentNoteTime(VisibleDocument, seconds, direction);
            if (target < 0) return;
            Stop(); Seek(target); rangeA = Mathf.Max(0, target - .25f); rangeB = Mathf.Min(Duration, target + .25f);
            selected.Clear(); hasPoint = false;
            timelineStart = Mathf.Max(0, seconds - TimelineSpans[timelineZoom] / 2);
        }
        void DrawNavigation()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("◀ 前のノーツ")) JumpNote(-1);
                if (GUILayout.Button("次のノーツ ▶")) JumpNote(1);
                using (new EditorGUI.DisabledScope(candidate != null))
                {
                    if (GUILayout.Button("区間を全選択")) { selected.Clear(); selected.UnionWith(SaberPlaybackReviewData.NotesInRange(document, rangeA, rangeB)); }
                    if (GUILayout.Button("選択解除")) selected.Clear();
                    if (GUILayout.Button("ここを指摘  N")) MarkCurrent();
                }
            }
            GUILayout.Label("Space 再生/停止 · ← → 1秒 · Shift+← → 1コマ · [ ] 区間A/B", EditorStyles.miniLabel);
        }
        void HandleShortcuts()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if ((e.control || e.command) && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            { AddComment(); e.Use(); return; }
            if (EditorGUIUtility.editingTextField || e.control || e.command || e.alt) return;
            switch (e.keyCode)
            {
                case KeyCode.Space: if (playing) Stop(); else Play(); break;
                case KeyCode.LeftArrow: Stop(); Seek(seconds - (e.shift ? 1f / 60 : 1)); break;
                case KeyCode.RightArrow: Stop(); Seek(seconds + (e.shift ? 1f / 60 : 1)); break;
                case KeyCode.LeftBracket: rangeA = seconds; rangeB = Mathf.Max(rangeB, rangeA); selected.Clear(); break;
                case KeyCode.RightBracket: rangeB = seconds; rangeA = Mathf.Min(rangeA, rangeB); selected.Clear(); break;
                case KeyCode.N: if (candidate == null) MarkCurrent(); break;
                case KeyCode.B: ToggleComparison(); break;
                default: return;
            }
            e.Use(); Repaint();
        }

        void DrawTimeline()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("譜面 / 指摘", EditorStyles.boldLabel, GUILayout.Width(85));
                int zoom = GUILayout.Toolbar(timelineZoom, new[] { "曲全体", "10秒", "5秒", "2秒" }, GUILayout.Width(230));
                if (zoom != timelineZoom) { timelineZoom = zoom; timelineStart = seconds - TimelineSpans[zoom] / 2; }
                if (GUILayout.Button("現在位置", GUILayout.Width(68))) timelineStart = seconds - TimelineSpans[timelineZoom] / 2;
                GUILayout.FlexibleSpace();
                GUILayout.Label("Shift+ドラッグで区間", EditorStyles.miniLabel);
            }
            float span = timelineZoom == 0 ? Duration : Mathf.Min(Duration, TimelineSpans[timelineZoom]);
            if (playing && !timelineDragging && (seconds < timelineStart || seconds > timelineStart + span)) timelineStart = seconds - span * .2f;
            timelineStart = Mathf.Clamp(timelineStart, 0, Mathf.Max(0, Duration - span));
            if (timelineZoom == 0) timelineStart = 0;
            Rect rect = GUILayoutUtility.GetRect(100, 84, GUILayout.ExpandWidth(true));
            timelineRect = rect;
            EditorGUI.DrawRect(rect, new Color(.07f, .09f, .13f));
            float X(float time) => rect.x + Mathf.Clamp01((time - timelineStart) / span) * rect.width;
            if (rangeB >= timelineStart && rangeA <= timelineStart + span)
                EditorGUI.DrawRect(new Rect(X(rangeA), rect.y, Mathf.Max(1, X(rangeB) - X(rangeA)), rect.height), new Color(.25f, .55f, .7f, .18f));
            DrawAudioGuide(rect, timelineStart, span);
            for (int tick = 0; tick <= 4; tick++)
            {
                float time = timelineStart + span * tick / 4;
                float x = X(time);
                EditorGUI.DrawRect(new Rect(x, rect.y + 17, 1, 49), new Color(1, 1, 1, .1f));
                GUI.Label(new Rect(Mathf.Min(x + 2, rect.xMax - 50), rect.y, 50, 17), time.ToString("0.00"), EditorStyles.miniLabel);
            }
            var chart = VisibleDocument;
            foreach (int i in SaberPlaybackReviewData.NotesInRange(chart, timelineStart, timelineStart + span))
            {
                var n = chart.notes[i]; float time = (n.time + chart.offsetMs) / 1000f;
                float end = time + SaberChartUtility.EffectiveLongLengthMs(n) / 1000f;
                bool blue = n.color == "blue", gold = n.color == "gold";
                float y = rect.y + (blue ? 20 : gold ? 48 : 34);
                Color color = blue ? new Color(.2f, .65f, 1) : gold ? new Color(1, .78f, .2f) : new Color(1, .3f, .4f);
                float noteX = Mathf.Min(X(time), rect.xMax - 3), noteWidth = Mathf.Max(3, X(end) - X(time));
                if (candidate == null && selected.Contains(i)) EditorGUI.DrawRect(new Rect(noteX - 1, y - 2, noteWidth + 2, 13), Color.white);
                EditorGUI.DrawRect(new Rect(noteX, y, noteWidth, 9), color);
            }
            int marker = 0;
            foreach (var comment in comments.OrderBy(c => c.fromSeconds).ThenBy(c => c.frameSeconds))
            {
                marker++;
                if (comment.frameSeconds < timelineStart || comment.frameSeconds > timelineStart + span) continue;
                Rect button = new Rect(Mathf.Clamp(X(comment.frameSeconds) - 10, rect.x, rect.xMax - 24), rect.y + 64, 24, 19);
                if (GUI.Button(button, new GUIContent(marker.ToString(), comment.instruction), EditorStyles.miniButton)) VisitComment(comment);
            }
            if (seconds >= timelineStart && seconds <= timelineStart + span)
                EditorGUI.DrawRect(new Rect(Mathf.Min(X(seconds), rect.xMax - 2), rect.y, 2, 62), Color.white);
            var e = Event.current;
            int control = GUIUtility.GetControlID(FocusType.Passive, rect);
            if (e.type == EventType.ScrollWheel && rect.Contains(e.mousePosition) && !timelineDragging)
            {
                float ratio = Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width);
                float anchor = timelineStart + ratio * span;
                timelineZoom = Mathf.Clamp(timelineZoom + (e.delta.y < 0 ? 1 : -1), 0, TimelineSpans.Length - 1);
                float nextSpan = timelineZoom == 0 ? Duration : Mathf.Min(Duration, TimelineSpans[timelineZoom]);
                timelineStart = Mathf.Clamp(anchor - ratio * nextSpan, 0, Mathf.Max(0, Duration - nextSpan));
                e.Use(); Repaint();
            }
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                float raw = timelineStart + Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width) * span;
                float row = e.mousePosition.y - rect.y;
                string color = row >= 18 && row < 32 ? "blue" : row >= 32 && row < 46 ? "red" : row >= 46 && row < 61 ? "gold" : null;
                int picked = !e.shift && candidate == null && color != null ? SaberPlaybackReviewData.PickTimelineNote(document, raw, color, span * 6 / rect.width) : -1;
                if (picked >= 0)
                {
                    GUI.FocusControl(null); SelectTimelineNote(picked, e.control || e.command); e.Use(); Repaint();
                }
                else
                {
                    GUIUtility.hotControl = control; GUI.FocusControl(null);
                    timelineDragging = true; resumeAfterDrag = playing; rangeDragging = e.shift; Stop();
                    dragAnchor = PointerTime(raw);
                    Seek(dragAnchor); if (rangeDragging) { rangeA = rangeB = dragAnchor; selected.Clear(); }
                    e.Use();
                }
            }
            if (timelineDragging && GUIUtility.hotControl == control)
            {
                if (e.type == EventType.MouseDrag || e.type == EventType.MouseUp)
                {
                    float target = PointerTime(timelineStart + Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width) * span);
                    Seek(target);
                    if (rangeDragging) { rangeA = Mathf.Min(dragAnchor, target); rangeB = Mathf.Max(dragAnchor, target); }
                    if (e.type == EventType.MouseUp) { GUIUtility.hotControl = 0; timelineDragging = false; if (resumeAfterDrag && !rangeDragging) Play(); }
                    e.Use();
                }
            }
            if (timelineZoom != 0) timelineStart = GUILayout.HorizontalSlider(timelineStart, 0, Mathf.Max(0, Duration - span));
        }
    }
}
