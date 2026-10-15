using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    public sealed partial class SaberPlaybackReviewWindow
    {
        [SerializeField] int beatSnap, changeIndex, changePage;
        [SerializeField] bool showWaveform = true;
        [SerializeField] float reviewLeadIn = 1;
        [SerializeField] string replyJson = "";
        bool audition;
        float auditionStart, auditionEnd;
        readonly SaberChartWaveform reviewWaveform = new SaberChartWaveform();
        string cachedReplyJson;
        SaberReviewPatch cachedReply;
        static readonly int[] BeatDivisions = { 0, 1, 2, 4 };
        const int ChangesPerPage = 6;

        SaberReviewPatch Reply
        {
            get
            {
                if (string.IsNullOrEmpty(replyJson) || candidate == null) return null;
                if (replyJson != cachedReplyJson) { cachedReply = JsonUtility.FromJson<SaberReviewPatch>(replyJson); cachedReplyJson = replyJson; }
                return cachedReply;
            }
        }

        float PointerTime(float raw) => beatSnap == 0 ? SafeTime(raw)
            : SaberPlaybackReviewData.SnapAudioTime(document, raw, BeatDivisions[beatSnap], Duration);

        void SelectTimelineNote(int index, bool additive)
        {
            if (candidate != null || index < 0 || index >= document.notes.Count) return;
            Stop();
            if (!additive) selected.Clear();
            if (!selected.Add(index)) selected.Remove(index);
            var note = document.notes[index]; float hit = (note.time + document.offsetMs) / 1000f;
            Seek(Mathf.Max(0, hit - .2f)); hasPoint = false;
            // 対象の到達時刻とロング末尾が、報告する区間から外れないようにする。
            var targets = selected.Count > 0 ? selected.ToArray() : new[] { index };
            rangeA = SafeTime(targets.Min(i => (document.notes[i].time + document.offsetMs) / 1000f) - .25f);
            rangeB = SafeTime(targets.Max(i => (document.notes[i].time + document.offsetMs + SaberChartUtility.EffectiveLongLengthMs(document.notes[i])) / 1000f) + .25f);
            status = $"ノーツ #{index:D4} を選択。Ctrl/Cmd+クリックで選択を追加・解除できます。";
        }

        void StartAudition()
        {
            if (document == null || rangeB <= rangeA) return;
            Stop(); auditionStart = Mathf.Max(0, rangeA - reviewLeadIn); auditionEnd = Mathf.Min(Duration, rangeB + .35f);
            seconds = auditionStart; audition = true; Play(); Repaint();
        }

        // 音声時計が区間末尾に達したときの処理を共通化する。前奏を含めてもA/B自体は変えない。
        void UpdatePlaybackBoundary()
        {
            if (audition && seconds >= auditionEnd)
            {
                float start = auditionStart, end = auditionEnd;
                Stop();
                if (loop) { seconds = start; auditionStart = start; auditionEnd = end; audition = true; Play(); }
                else seconds = end;
            }
            else if (!audition && loop && seconds >= rangeB && rangeB > rangeA) { Stop(); seconds = rangeA; Play(); }
            else if (seconds >= Duration) { seconds = Duration; Stop(); }
        }

        void DrawInspectionControls()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(audition && playing ? "区間の試聴を止める" : "▶ 区間を聴き直す", GUILayout.Width(145)))
                { if (audition && playing) Stop(); else StartAudition(); }
                GUILayout.Label("手前", GUILayout.Width(30));
                int lead = Array.IndexOf(new[] { 0f, .5f, 1f, 2f }, reviewLeadIn);
                int next = EditorGUILayout.Popup(Math.Max(0, lead), new[] { "0秒", "0.5秒", "1秒", "2秒" }, GUILayout.Width(58));
                reviewLeadIn = new[] { 0f, .5f, 1f, 2f }[next];
                showWaveform = GUILayout.Toggle(showWaveform, "波形", GUILayout.Width(55));
                GUILayout.Label("拍に合わせる", GUILayout.Width(82));
                beatSnap = EditorGUILayout.Popup(beatSnap, new[] { "OFF", "1拍", "1/2拍", "1/4拍" }, GUILayout.Width(75));
                GUILayout.FlexibleSpace();
            }
        }

        void DrawAudioGuide(Rect rect, float start, float span)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (showWaveform && clip != null)
            {
                if (reviewWaveform.Clip != clip) reviewWaveform.Build(clip);
                if (reviewWaveform.Peaks != null)
                {
                    for (float x = 0; x < rect.width; x += 2)
                    {
                        float peak = reviewWaveform.Sample((start + x / rect.width * span) / clip.length);
                        float height = peak * 21;
                        EditorGUI.DrawRect(new Rect(rect.x + x, rect.y + 39 - height, 1, height * 2), new Color(.45f, .7f, .85f, .20f));
                    }
                }
                else GUI.Label(new Rect(rect.x + 4, rect.y + 46, rect.width - 8, 16), "波形を取得できません（音源の読込設定を確認）", EditorStyles.miniLabel);
            }
            if (timelineZoom == 0) return;
            var grid = new SaberChartGrid(document.bpm, document.beatZeroMs, document.tempoMap);
            int division = beatSnap > 0 ? BeatDivisions[beatSnap] : 1;
            float first = grid.BeatAt(start * 1000 - document.offsetMs);
            float last = grid.BeatAt((start + span) * 1000 - document.offsetMs);
            // 極端なテンポのファイルでも描画数を制限する。
            int step = Math.Max(1, Mathf.CeilToInt((last - first) * division / 150));
            for (int tick = Mathf.CeilToInt(first * division), count = 0; tick <= last * division && count < 151; tick += step, count++)
            {
                float time = (grid.TimeAt(tick / (float)division) + document.offsetMs) / 1000f;
                float x = rect.x + (time - start) / span * rect.width;
                EditorGUI.DrawRect(new Rect(x, rect.y + 17, 1, 46), new Color(1, 1, 1, tick % division == 0 ? .22f : .09f));
            }
        }

        void VisitChange(int index)
        {
            var patch = Reply;
            if (patch == null || index < 0 || index >= patch.operations.Count) return;
            changeIndex = index; changePage = index / ChangesPerPage;
            var range = SaberPlaybackReviewData.ChangeRange(document, patch.operations[index]);
            Stop(); rangeA = SafeTime(range.x - .25f); rangeB = SafeTime(range.y + .25f);
            Seek(Mathf.Max(0, range.x - .2f)); selected.Clear(); hasPoint = false;
            timelineZoom = 1; timelineStart = Mathf.Max(0, seconds - 1);
        }

        void ToggleComparison()
        {
            if (candidate == null) return;
            after = !after; Repaint();
        }

        void DrawChanges()
        {
            var patch = Reply;
            if (patch == null) { GUILayout.Label(changeSummary ?? "", EditorStyles.wordWrappedMiniLabel); return; }
            int count = patch.operations.Count;
            GUILayout.Label($"追加 {patch.operations.Count(op => op.action == "add")} / 変更 {patch.operations.Count(op => op.action == "replace")} / 削除 {patch.operations.Count(op => op.action == "delete")}", EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(changeIndex <= 0)) if (GUILayout.Button("◀ 前の変更")) VisitChange(changeIndex - 1);
                using (new EditorGUI.DisabledScope(changeIndex >= count - 1)) if (GUILayout.Button("次の変更 ▶")) VisitChange(changeIndex + 1);
            }
            GUILayout.Label("Bキーで同じ位置の変更前 / 変更案を比較", EditorStyles.wordWrappedMiniLabel);
            int pageCount = Math.Max(1, (count + ChangesPerPage - 1) / ChangesPerPage);
            changePage = Mathf.Clamp(changePage, 0, pageCount - 1);
            for (int i = changePage * ChangesPerPage; i < Math.Min(count, (changePage + 1) * ChangesPerPage); i++)
            {
                var op = patch.operations[i];
                string label = op.action == "delete" ? $"削除 #{op.index}\n{Describe(document.notes[op.index])}"
                    : op.action == "add" ? $"追加\n{Describe(op.note)}"
                    : $"変更 #{op.index}\n{Describe(document.notes[op.index])}\n→ {Describe(op.note)}";
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label($"{i + 1}. {label}", EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button(changeIndex == i ? "この変更を聴き直す" : "この変更へ移動"))
                    { bool replay = changeIndex == i; VisitChange(i); if (replay) StartAudition(); }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(changePage == 0)) if (GUILayout.Button("前ページ")) changePage--;
                GUILayout.Label($"{changePage + 1} / {pageCount}", GUILayout.Width(70));
                using (new EditorGUI.DisabledScope(changePage == pageCount - 1)) if (GUILayout.Button("次ページ")) changePage++;
            }
        }
    }
}
