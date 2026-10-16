using System;
using System.Collections.Generic;
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
        // 返信の操作のうち、採用しないもの(返信JSONの operations の添字)。変更案はこれを除いて作り直す。
        [SerializeField] List<int> rejectedOperations = new List<int>();
        [SerializeField] bool showChecks;
        [SerializeField] int chartCheckPage, candidateCheckPage, chartCheckFilter, candidateCheckFilter;
        const int ChecksPerPage = 8;
        static readonly string[] CheckFilters = { "エラー・注意", "エラー", "注意" };
        // 譜面チェックの結果。変更前・変更案・音源の長さが同じなら、描画のたびに検査し直さない。
        List<SaberChartIssue> documentIssues = new List<SaberChartIssue>(), candidateIssues;
        List<SaberChartIssue> addedCandidateIssues = new List<SaberChartIssue>();
        string checkKey;
        bool audition;
        float auditionStart, auditionEnd;
        readonly SaberChartWaveform reviewWaveform = new SaberChartWaveform();
        string cachedReplyJson;
        SaberReviewPatch cachedReply;
        // AI10: 拍に合わせる刻み。譜面エディターと同じ音符の分母(3連の12・24分を含む)で、小節の頭から数える。
        // beatSnap の値は前からの下書きと同じ意味を保つ(0=OFF・1=4分・2=8分・3=16分)。足した刻みは後ろに置き、表示だけ音符の順に並べる。
        static readonly int[] SnapDenominators = { 0, 4, 8, 16, 12, 24, 32 };
        static readonly int[] SnapMenuValues = { 0, 1, 2, 4, 3, 5, 6 };
        static readonly string[] SnapMenuLabels = { "OFF", "4分（1拍）", "8分", "12分（3連）", "16分", "24分（3連）", "32分" };
        const int ChangesPerPage = 6;

        // AIの説明(変更のない返信でも残す)。
        [SerializeField] string replyExplanation = "";

        SaberReviewPatch Reply
        {
            get
            {
                if (string.IsNullOrEmpty(replyJson) || candidate == null) return null;
                if (replyJson != cachedReplyJson)
                {
                    try { cachedReply = SaberPlaybackReviewData.ParseReply(replyJson); }
                    catch (InvalidOperationException) { cachedReply = null; }
                    cachedReplyJson = replyJson;
                }
                return cachedReply;
            }
        }

        // 変更が、どの指摘の区間(前後0.5秒)にも入っていないか。AIに頼んだ範囲の外を黙って変えていないかを見る。
        bool OutsideComments(SaberReviewOperation op)
        {
            if (comments.Count == 0) return false;
            Vector2 range = SaberPlaybackReviewData.ChangeRange(document, op);
            return !comments.Any(c => range.y >= c.fromSeconds - .5f && range.x <= c.toSeconds + .5f);
        }

        int SnapDenominator => SnapDenominators[Mathf.Clamp(beatSnap, 0, SnapDenominators.Length - 1)];

        float PointerTime(float raw) => SnapDenominator == 0 ? SafeTime(raw)
            : SaberPlaybackReviewData.SnapAudioTime(document, raw, SnapDenominator / 4, Duration);

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
                GUILayout.Label(new GUIContent("拍に合わせる", "タイムラインのクリック・ドラッグと打点を、この刻みの格子へ寄せます（譜面エディターの Snap と同じ。小節の頭から数えます）"),
                    GUILayout.Width(82));
                beatSnap = EditorGUILayout.IntPopup(Mathf.Clamp(beatSnap, 0, SnapDenominators.Length - 1), SnapMenuLabels, SnapMenuValues, GUILayout.Width(92));
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
            var meter = new ChartMeterMap(document.timeSignatures);
            float first = Mathf.Max(0, grid.BeatAt(start * 1000 - document.offsetMs));
            float last = grid.BeatAt((start + span) * 1000 - document.offsetMs);
            if (last < first) return;
            // 刻みは小節の頭から数える(エディターと同じ格子)。数が多すぎるときは拍の線だけにする。
            double step = 4.0 / Math.Max(4, SnapDenominator == 0 ? 4 : SnapDenominator);
            if ((last - first) / step > 150) step = 1;
            int count = 0;
            foreach (double barStart in meter.BarStarts(meter.At(first).BarStart, last))
            {
                var bar = meter.At(barStart);
                for (double beat = barStart; beat < bar.BarEnd - ChartMeterMap.Epsilon && beat <= last && count < 300; beat += step)
                {
                    if (beat < first - ChartMeterMap.Epsilon) continue;
                    float time = (grid.TimeAt((float)beat) + document.offsetMs) / 1000f;
                    float x = rect.x + (time - start) / span * rect.width;
                    double pulses = (beat - barStart) * bar.Denominator / 4.0;
                    bool onPulse = Math.Abs(pulses - Math.Round(pulses)) < 1e-4;
                    float alpha = beat - barStart < ChartMeterMap.Epsilon ? .38f : onPulse ? .22f : .09f;
                    EditorGUI.DrawRect(new Rect(x, rect.y + 17, 1, 46), new Color(1, 1, 1, alpha));
                    count++;
                }
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
            GUILayout.Label($"追加 {patch.operations.Count(op => op.action == "add")} / 変更 {patch.operations.Count(op => op.action == "replace")} / 削除 {patch.operations.Count(op => op.action == "delete")}" +
                $" · 採用 {count - rejectedOperations.Count}/{count}件", EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(changeIndex <= 0)) if (GUILayout.Button("◀ 前の変更")) VisitChange(changeIndex - 1);
                using (new EditorGUI.DisabledScope(changeIndex >= count - 1)) if (GUILayout.Button("次の変更 ▶")) VisitChange(changeIndex + 1);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(rejectedOperations.Count == 0)) if (GUILayout.Button("すべて採用")) SetAllOperationsAccepted(true);
                using (new EditorGUI.DisabledScope(rejectedOperations.Count >= count)) if (GUILayout.Button("すべて外す")) SetAllOperationsAccepted(false);
            }
            GUILayout.Label("Bキーで同じ位置の変更前 / 変更案を比較", EditorStyles.wordWrappedMiniLabel);
            int pageCount = Math.Max(1, (count + ChangesPerPage - 1) / ChangesPerPage);
            changePage = Mathf.Clamp(changePage, 0, pageCount - 1);
            for (int i = changePage * ChangesPerPage; i < Math.Min(count, (changePage + 1) * ChangesPerPage); i++)
            {
                var op = patch.operations[i];
                float offset = document.offsetMs;
                string label = op.action == "delete" ? $"削除 #{op.index}  {SaberPlaybackReviewData.DescribeNote(document.notes[op.index], offset)}"
                    : op.action == "add" ? $"追加  {SaberPlaybackReviewData.DescribeNote(op.note, offset)}"
                    : $"変更 #{op.index}  {SaberPlaybackReviewData.DescribeNote(document.notes[op.index], offset)}\n" +
                      $"→ {SaberPlaybackReviewData.DescribeChange(document.notes[op.index], op.note, offset)}";
                if (!string.IsNullOrWhiteSpace(op.reason)) label += "\n理由: " + op.reason.Trim();
                if (OutsideComments(op)) label += "\n【区間外】どの指摘の区間にも入っていない変更です。頼んだ範囲か確かめてください。";
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    bool accepted = !rejectedOperations.Contains(i);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label($"{i + 1}.", EditorStyles.miniBoldLabel, GUILayout.Width(28));
                        bool next = GUILayout.Toggle(accepted, accepted ? "採用する" : "採用しない（変更案から外す）");
                        if (next != accepted) SetOperationAccepted(i, next);
                    }
                    GUILayout.Label(label, EditorStyles.wordWrappedMiniLabel);
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

        bool AnyOperationAccepted => Reply == null || rejectedOperations.Count < Reply.operations.Count;

        void SetOperationAccepted(int index, bool accepted)
        {
            if (accepted) rejectedOperations.Remove(index);
            else if (!rejectedOperations.Contains(index)) rejectedOperations.Add(index);
            RebuildCandidate();
        }

        void SetAllOperationsAccepted(bool accepted)
        {
            var patch = Reply; if (patch == null) return;
            rejectedOperations.Clear();
            if (!accepted) rejectedOperations.AddRange(Enumerable.Range(0, patch.operations.Count));
            RebuildCandidate();
        }

        // 採用する操作だけで変更案を作り直す。返信は読み込み時に全件を検査済みなので、一部だけでも同じ規則で適用できる。
        void RebuildCandidate()
        {
            var patch = Reply; if (patch == null) return;
            var chosen = SaberPlaybackReviewData.Selected(patch, rejectedOperations);
            candidate = chosen.operations.Count == 0 ? SaberChartUtility.Clone(document)
                : SaberPlaybackReviewData.Apply(JsonUtility.FromJson<SaberReviewRequest>(requestJson), document, chosen, Duration);
            candidateJson = SaberChartUtility.ToJson(candidate, false);
            cachedReplyJson = null;
            status = $"変更案を作り直しました（採用 {chosen.operations.Count}/{patch.operations.Count}件）。";
            SaveDraft(); Repaint();
        }

        void RefreshChecks()
        {
            float audioMs = clip != null ? clip.length * 1000f : 0f;
            string key = SaberChartLint.Fingerprint(document) + "|" + (candidate == null ? "-" : SaberChartLint.Fingerprint(candidate).ToString()) +
                "|" + difficulty + "|" + audioMs;
            if (key == checkKey) return;
            checkKey = key;
            documentIssues = SaberChartLint.Check(document, difficulty, audioMs);
            candidateIssues = candidate == null ? null : SaberChartLint.Check(candidate, difficulty, audioMs);
            addedCandidateIssues = candidateIssues == null ? new List<SaberChartIssue>()
                : SaberPlaybackReviewData.NewIssues(documentIssues, candidateIssues, document, candidate);
        }

        static int CountLevel(List<SaberChartIssue> issues, SaberChartIssueLevel level) => issues?.Count(i => i.level == level) ?? 0;

        List<SaberChartIssue> FilteredChecks(bool proposal)
        {
            int filter = proposal ? candidateCheckFilter : chartCheckFilter;
            return (proposal ? addedCandidateIssues : documentIssues).Where(issue =>
                issue.level != SaberChartIssueLevel.Info && (filter == 0 || (int)issue.level == filter - 1)).ToList();
        }

        // 全件をたどれる一覧。候補の採否で件数が減った場合も、存在する最後のページへ戻す。
        List<SaberChartIssue> CheckPage(bool proposal)
        {
            var issues = FilteredChecks(proposal);
            int last = Math.Max(0, (issues.Count - 1) / ChecksPerPage);
            int page = Mathf.Clamp(proposal ? candidateCheckPage : chartCheckPage, 0, last);
            if (proposal) candidateCheckPage = page; else chartCheckPage = page;
            return issues.Skip(page * ChecksPerPage).Take(ChecksPerPage).ToList();
        }

        void DrawCheckPageControls(bool proposal)
        {
            int filter = proposal ? candidateCheckFilter : chartCheckFilter;
            int next = GUILayout.Toolbar(filter, CheckFilters);
            if (next != filter)
            {
                if (proposal) { candidateCheckFilter = next; candidateCheckPage = 0; }
                else { chartCheckFilter = next; chartCheckPage = 0; }
            }
            CheckPage(proposal);
            int count = FilteredChecks(proposal).Count;
            int page = proposal ? candidateCheckPage : chartCheckPage;
            int pages = Math.Max(1, (count + ChecksPerPage - 1) / ChecksPerPage);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(page == 0)) if (GUILayout.Button("前の項目")) page--;
                GUILayout.Label($"{page + 1}/{pages}（{count}件）", EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(page + 1 >= pages)) if (GUILayout.Button("次の項目")) page++;
            }
            if (proposal) candidateCheckPage = page; else chartCheckPage = page;
            if (count == 0) GUILayout.Label("この絞り込みに該当する項目はありません。", EditorStyles.wordWrappedMiniLabel);
        }

        // 今の譜面のチェック。本編で取れない・取りにくい配置を見つけて、そのまま指摘にできる。
        void DrawChartChecks()
        {
            RefreshChecks();
            var shown = documentIssues.Where(i => i.level != SaberChartIssueLevel.Info).ToList();
            showChecks = EditorGUILayout.Foldout(showChecks,
                $"譜面チェック（エラー {CountLevel(documentIssues, SaberChartIssueLevel.Error)}・注意 {CountLevel(documentIssues, SaberChartIssueLevel.Warning)}）", true);
            if (!showChecks) return;
            if (shown.Count == 0) { GUILayout.Label("本編で取れない・取りにくい配置はありません。", EditorStyles.wordWrappedMiniLabel); return; }
            DrawCheckPageControls(false);
            foreach (var issue in CheckPage(false))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(SaberChartLint.Describe(document, issue), EditorStyles.wordWrappedMiniLabel)) VisitIssue(document, issue, false);
                    using (new EditorGUI.DisabledScope(candidate != null))
                        if (GUILayout.Button(new GUIContent("指摘に", "この箇所を選び、直してほしい内容を指摘の欄に入れます。"), GUILayout.Width(52))) AddIssueToInstruction(issue);
                }
            }
        }

        // 変更案で新しく出たエラー・注意。採用する前に確かめられるよう、移動できるようにする。
        void DrawCandidateChecks()
        {
            RefreshChecks();
            if (candidateIssues == null) return;
            GUILayout.Label($"変更案のチェック: エラー {CountLevel(documentIssues, SaberChartIssueLevel.Error)}→{CountLevel(candidateIssues, SaberChartIssueLevel.Error)}" +
                $" / 注意 {CountLevel(documentIssues, SaberChartIssueLevel.Warning)}→{CountLevel(candidateIssues, SaberChartIssueLevel.Warning)}", EditorStyles.miniBoldLabel);
            var added = addedCandidateIssues.Where(i => i.level != SaberChartIssueLevel.Info).ToList();
            if (added.Count == 0) { GUILayout.Label("変更案で新しく増えたエラー・注意はありません。", EditorStyles.wordWrappedMiniLabel); return; }
            EditorGUILayout.HelpBox($"変更案で新しく出たエラー・注意 {added.Count}件。採用する前に確かめてください。",
                added.Any(i => i.level == SaberChartIssueLevel.Error) ? MessageType.Error : MessageType.Warning);
            DrawCheckPageControls(true);
            foreach (var issue in CheckPage(true))
                if (GUILayout.Button(SaberChartLint.Describe(candidate, issue), EditorStyles.wordWrappedMiniLabel)) VisitIssue(candidate, issue, true);
        }

        void VisitIssue(SaberChartDocument chart, SaberChartIssue issue, bool showCandidate)
        {
            var notes = issue.notes.Where(i => i >= 0 && i < chart.notes.Count).ToArray();
            float from = (issue.timeMs + chart.offsetMs) / 1000f;
            float to = notes.Length == 0 ? from : notes.Max(i => (chart.notes[i].time + chart.offsetMs + SaberChartUtility.EffectiveLongLengthMs(chart.notes[i])) / 1000f);
            Stop(); rangeA = SafeTime(from - .25f); rangeB = SafeTime(to + .25f); Seek(Mathf.Max(0, from - .2f));
            after = showCandidate && candidate != null; hasPoint = false; selected.Clear();
            if (!showCandidate && candidate == null) selected.UnionWith(notes);
            timelineZoom = Mathf.Max(1, timelineZoom); timelineStart = Mathf.Max(0, seconds - TimelineSpans[timelineZoom] / 2);
        }

        void AddIssueToInstruction(SaberChartIssue issue)
        {
            if (candidate != null) return;
            if (!string.IsNullOrWhiteSpace(instruction) && !string.IsNullOrEmpty(editingCommentId))
            { status = "編集中の指摘を先に更新してください。"; return; }
            VisitIssue(document, issue, false);
            ReleaseInstructionFocus();
            instruction = (string.IsNullOrWhiteSpace(instruction) ? "" : instruction.TrimEnd() + "\n") +
                $"譜面チェックの{SaberChartLint.Label(issue.level)}を直してください: {issue.message}";
            FocusInstructionSoon();
            status = "指摘の欄に入れました。必要なら書き足して「指摘を追加」を押してください。";
        }
    }
}
