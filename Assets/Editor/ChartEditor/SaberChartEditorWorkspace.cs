using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>編集機能を残したまま、常時操作する帯と目的別の編集欄を分ける。</summary>
    public sealed partial class SaberChartEditorWindow
    {
        private enum InspectorSection { Notes, Recording, Navigation, Chart, Song, Diagnostics }
        private static readonly string[] InspectorLabels = { "ノーツ", "録音", "移動・表示", "曲設定", "曲情報", "診断・復元" };
        [SerializeField] private InspectorSection inspectorSection;
        [SerializeField] private Vector2[] inspectorScrolls = new Vector2[6];
        [SerializeField] private bool showPalette = true, showInspector = true, showDocumentPicker;
        [SerializeField] private float workspaceLeftWidth = 224f, workspaceRightWidth = 330f;
        [SerializeField] private string lastDraftSavedAt, lastDraftTarget;
        private bool workspacePreferencesChanged;
        private float resizeStartMouse, resizeStartWidth;
        private int diagnosticPage;

        [Serializable]
        private sealed class WorkspacePreferences
        {
            public float left = 224f, right = 330f;
            public bool palette = true, inspector = true;
        }

        private struct WorkspaceRects
        {
            public Rect header, transport, picker, left, center, right, leftResize, rightResize, footer;
        }

        // 最小サイズでも中央を確保する。設定値を狭い窓に合わせて書き換えず、広げた時に元の幅へ戻す。
        private WorkspaceRects CalculateWorkspace(Vector2 size)
        {
            float width = Mathf.Max(1050f, size.x);
            float pickerHeight = showDocumentPicker ? DocumentPickerHeight : 0f;
            float top = HeaderHeight + TransportHeight + pickerHeight + PanelGap;
            float height = Mathf.Max(100f, size.y - top - FooterHeight - PanelGap);
            float leftWidth = showPalette ? Mathf.Clamp(workspaceLeftWidth, 210f, 320f) : 0f;
            float rightWidth = showInspector ? Mathf.Clamp(workspaceRightWidth, 286f, 460f) : 0f;
            float spacing = PanelGap * (2 + (showPalette ? 1 : 0) + (showInspector ? 1 : 0));
            float excess = Mathf.Max(0f, leftWidth + rightWidth + spacing + 350f - width);
            float reduceRight = Mathf.Min(excess, Mathf.Max(0f, rightWidth - 286f));
            rightWidth -= reduceRight;
            leftWidth -= Mathf.Min(excess - reduceRight, Mathf.Max(0f, leftWidth - 210f));
            float centerX = PanelGap + (showPalette ? leftWidth + PanelGap : 0);
            float rightX = width - PanelGap - rightWidth;
            return new WorkspaceRects
            {
                header = new Rect(0, 0, width, HeaderHeight),
                transport = new Rect(0, HeaderHeight, width, TransportHeight),
                picker = new Rect(0, HeaderHeight + TransportHeight, width, pickerHeight),
                left = new Rect(PanelGap, top, leftWidth, height),
                center = new Rect(centerX, top, rightX - centerX - (showInspector ? PanelGap : 0), height),
                right = new Rect(rightX, top, rightWidth, height),
                leftResize = new Rect(centerX - PanelGap, top, PanelGap, height),
                rightResize = new Rect(rightX - PanelGap, top, PanelGap, height),
                footer = new Rect(0, size.y - FooterHeight, width, FooterHeight),
            };
        }

        private void DrawWorkspaceHeader(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, headerStyle);
            GUILayout.BeginArea(new Rect(rect.x + 10, rect.y + 9, rect.width - 20, 28));
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent($"編集中  {EditingSongId} / {EditingDifficultyLabel}",
                $"保存先: {EditingSongId} / {EditingDifficultyLabel}。開く・作るの選択先へは自動で保存しません。"),
                new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = Color.white }, clipping = TextClipping.Clip },
                GUILayout.MinWidth(120), GUILayout.ExpandWidth(true));
            GUILayout.Label($"{document.notes.Count:N0} NOTES", smallMutedStyle, GUILayout.Width(82));
            DrawCheckBadge();
            using (new EditorGUI.DisabledScope(RecordingBusy))
            {
                if (GUILayout.Button(showDocumentPicker ? "開く・作る ▲" : "開く・作る ▼", GUILayout.Width(94)))
                {
                    showDocumentPicker = !showDocumentPicker;
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("フォルダ", GUILayout.Width(62))) RevealSongFolder();
                int mode = recordMode ? recordStepMode ? 2 : 1 : 0;
                int next = GUILayout.Toolbar(mode, new[] { "編集", "録音", "ステップ" }, GUILayout.Width(186));
                if (next != mode) { SetRecordingInputMode(next); GUIUtility.ExitGUI(); }
            }
            showPlaybackPreview = GUILayout.Toggle(showPlaybackPreview, "3D表示", EditorStyles.miniButton, GUILayout.Width(65));
            bool palette = GUILayout.Toggle(showPalette, "パレット", EditorStyles.miniButton, GUILayout.Width(68));
            bool inspector = GUILayout.Toggle(showInspector, "編集欄", EditorStyles.miniButton, GUILayout.Width(58));
            if (palette != showPalette || inspector != showInspector)
            {
                showPalette = palette; showInspector = inspector; workspacePreferencesChanged = true;
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button(new GUIContent("配置を戻す", "パネル幅と表示を初期状態に戻します。譜面は変えません。"), GUILayout.Width(76)))
            {
                ResetWorkspaceLayout(); GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawTransportBar(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, headerStyle);
            GUILayout.BeginArea(new Rect(rect.x + 10, rect.y + 3, rect.width - 20, rect.height - 4));
            GUILayout.BeginHorizontal(GUILayout.Height(27));
            using (new EditorGUI.DisabledScope(RecordingBusy))
                if (GUILayout.Button(new GUIContent("|◀", "曲頭へ"), GUILayout.Width(32))) SeekToBeat(0f);
            if (GUILayout.Button(RecordingBusy ? "録音終了" : isPlaying ? "一時停止" : "▶ 再生", GUILayout.Width(78))) TogglePreview();
            if (GUILayout.Button(new GUIContent("■ 停止", "録音を確定して停止し、曲頭へ戻ります"), GUILayout.Width(61))) StopPreview(true);
            using (new EditorGUI.DisabledScope(!RecordingBusy && (audioClip == null || !SaberChartAudioPreview.IsSupported || EditorApplication.isPlayingOrWillChangePlaymode)))
                if (GUILayout.Button(RecordingBusy ? "■ 録音終了" : "● 録音開始", GUILayout.Width(94))) ToggleWorkspaceRecording();
            GUILayout.Space(5);
            using (new EditorGUI.DisabledScope(RecordingBusy))
            {
                placementSnap = GUILayout.Toggle(placementSnap,
                    new GUIContent("吸着", "OFFで自由配置。Shiftを押している間だけ拍・列に吸着します。右の分解能は格子表示・ステップ入力にも使います。"), GUILayout.Width(48));
                snapIndex = EditorGUILayout.Popup(snapIndex, SnapLabels, GUILayout.Width(102));
            }
            using (new EditorGUI.DisabledScope(RecordingBusy || !history.CanUndo))
                if (GUILayout.Button("元に戻す", GUILayout.Width(67))) Undo();
            using (new EditorGUI.DisabledScope(RecordingBusy || !history.CanRedo))
                if (GUILayout.Button("やり直し", GUILayout.Width(67))) Redo();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(RecordingBusy || !SaberChartFileStore.IsValidSongId(EditingSongId, out _)))
            {
                if (GUILayout.Button(new GUIContent("保存", "編集中の譜面と曲情報を保存 (Ctrl+S)"), GUILayout.Width(58))) SaveDocument();
                if (GUILayout.Button(hasUnsavedChanges ? "保存して本編でテスト" : "本編でテスト（保存なし）", GUILayout.Width(185))) TestInGame();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(3);
            GUILayout.BeginHorizontal(GUILayout.Height(22));
            GUILayout.Label($"{SaberChartUtility.FormatMusicalPosition(currentBeat, document, CurrentSnap)} / {BeatToAudioSeconds(currentBeat):0.000}s",
                smallMutedStyle, GUILayout.Width(198));
            using (new EditorGUI.DisabledScope(RecordingBusy))
            {
                EditorGUI.BeginChangeCheck();
                float maxBeat = MaxBeat();
                float sought = GUILayout.HorizontalSlider(currentBeat, 0f, maxBeat, GUILayout.MinWidth(90));
                GUILayout.Label(new GUIContent("拍", "拍番号を直接入力して移動できます"), GUILayout.Width(18));
                sought = EditorGUILayout.DelayedFloatField(sought, GUILayout.Width(64));
                if (EditorGUI.EndChangeCheck() && !float.IsNaN(sought) && !float.IsInfinity(sought))
                    SeekToBeat(Mathf.Clamp(sought, 0f, maxBeat));
            }
            GUILayout.BeginVertical(GUILayout.Width(188)); DrawClickToggles(); GUILayout.EndVertical();
            string state = hasUnsavedChanges ? "● 未保存" : "本編へ保存済み";
            if (hasUnsavedChanges && lastDraftTarget == TargetKey(EditingSongId, EditingDifficulty) && !string.IsNullOrEmpty(lastDraftSavedAt))
                state += " / 下書き " + lastDraftSavedAt;
            GUILayout.Label(new GUIContent(state, "下書きは譜面だけの復元用です。曲情報や本編のファイルへの保存は「保存」で行います。"),
                smallMutedStyle, GUILayout.Width(204));
            if (GUILayout.Button(pendingDraft != null ? "下書きを復元…" : "診断・復元", EditorStyles.miniButton, GUILayout.Width(108)))
            {
                SetInspectorSection(InspectorSection.Diagnostics); showInspector = true; GUIUtility.ExitGUI();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void ToggleWorkspaceRecording()
        {
            if (RecordingBusy) { StopPreview(false); return; }
            if (audioClip == null || !SaberChartAudioPreview.IsSupported || EditorApplication.isPlayingOrWillChangePlaymode) return;
            SetRecordingInputMode(1);
            StartRecording();
        }

        private void DrawDocumentPicker(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, panelStyle);
            GUILayout.BeginArea(new Rect(rect.x + 12, rect.y + 6, rect.width - 24, rect.height - 12));
            using (new EditorGUI.DisabledScope(RecordingBusy))
            {
                GUILayout.Label("別の譜面を開く・作る（選択だけでは編集中の保存先は変わりません）", smallMutedStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label("曲フォルダ", GUILayout.Width(74));
                GUI.SetNextControlName("ChartText:string_nextSongId");
                songId = EditorGUILayout.TextField(songId ?? string.Empty, GUILayout.MinWidth(120));
                if (GUILayout.Button("曲一覧 ▾", GUILayout.Width(75))) ShowSongMenu();
                difficultyIndex = EditorGUILayout.Popup(difficultyIndex, DifficultyLabels, GUILayout.Width(78));
                if (GUILayout.Button("読込", GUILayout.Width(58))) LoadDocument();
                if (GUILayout.Button("新規", GUILayout.Width(58))) NewDocument();
                if (GUILayout.Button("ここへ複製", GUILayout.Width(87))) DuplicateToSelection();
                if (GUILayout.Button("選択を戻す", GUILayout.Width(87))) ResetSelectionToEditing();
                GUILayout.EndHorizontal();
                GUILayout.Label(SelectionDiffersFromEditing()
                    ? $"未読込: {songId?.Trim()} / {DifficultyLabels[Mathf.Clamp(difficultyIndex, 0, 2)]}   |   保存先: {EditingSongId} / {EditingDifficultyLabel}"
                    : $"編集中と同じ選択です  /  {document.notes.Count:N0} NOTES", smallMutedStyle);
            }
            GUILayout.EndArea();
        }

        private void SetInspectorSection(InspectorSection section)
        {
            if (inspectorScrolls == null || inspectorScrolls.Length != InspectorLabels.Length) inspectorScrolls = new Vector2[InspectorLabels.Length];
            if (inspectorSection == section) return;
            inspectorScrolls[Mathf.Clamp((int)inspectorSection, 0, InspectorLabels.Length - 1)] = rightScroll;
            inspectorSection = section;
            rightScroll = inspectorScrolls[(int)section];
            if (section == InspectorSection.Song) showSongInfo = true;
            Repaint();
        }

        private void DrawInspectorWorkspace(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, panelStyle);
            GUILayout.BeginArea(new Rect(rect.x + 9, rect.y + 8, rect.width - 18, rect.height - 16));
            int section = GUILayout.SelectionGrid((int)inspectorSection, InspectorLabels, 3, GUILayout.Height(46));
            if (section != (int)inspectorSection)
            {
                SetInspectorSection((InspectorSection)section);
                GUIUtility.ExitGUI();
            }
            GUILayout.Space(6);
            rightScroll = GUILayout.BeginScrollView(rightScroll, false, false);
            // ラベル幅はインスペクターに合わせ、数値欄が狭い窓でも潰れないようにする。
            float oldLabel = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Clamp(rect.width * .47f, 120f, 178f);
            try
            {
                switch (inspectorSection)
                {
                    case InspectorSection.Notes:
                        if (HasRangeSelection)
                        {
                            using (new EditorGUI.DisabledScope(RecordingBusy)) DrawRangePanel();
                            DrawFocusedNotePanel();
                        }
                        else
                        {
                            DrawFocusedNotePanel();
                            using (new EditorGUI.DisabledScope(RecordingBusy)) DrawRangePanel();
                        }
                        using (new EditorGUI.DisabledScope(RecordingBusy)) DrawDifficultyPanel();
                        break;
                    case InspectorSection.Recording:
                        DrawRecordingSettings();
                        break;
                    case InspectorSection.Navigation:
                        using (new EditorGUI.DisabledScope(RecordingBusy))
                        {
                            DrawJumpControls();
                            DrawMarkerControls();
                        }
                        GUILayout.Space(12); SectionLabel("3Dプレビューの表示");
                        DrawPreviewOptions();
                        using (new EditorGUI.DisabledScope(RecordingBusy)) DrawGameTimingToggle();
                        break;
                    case InspectorSection.Chart:
                        using (new EditorGUI.DisabledScope(RecordingBusy))
                        {
                            DrawWorkspaceAudio();
                            SectionLabel("曲 / グリッド設定");
                            DrawChartSettings();
                            DrawTimingExplanation();
                            DrawSettingsMismatch();
                            DrawTempoMapPanel();
                            GUILayout.Space(10); DrawTimeSignatures();
                        }
                        break;
                    case InspectorSection.Song:
                        using (new EditorGUI.DisabledScope(RecordingBusy)) DrawSongInfoPanel();
                        break;
                    case InspectorSection.Diagnostics:
                        using (new EditorGUI.DisabledScope(RecordingBusy))
                        {
                            DrawDraftBanner();
                            DrawDraftHistory();
                            if (!string.IsNullOrEmpty(persistentWarning)) EditorGUILayout.HelpBox(persistentWarning, MessageType.Warning);
                            DrawValidationSummary();
                            DrawWorkspaceDiagnosticNotes();
                        }
                        break;
                }
            }
            finally { EditorGUIUtility.labelWidth = oldLabel; }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawGameTimingToggle()
        {
            useGameTiming = EditorGUILayout.ToggleLeft(new GUIContent(
                $"ゲームと同じ表示補正（{GameSession.JudgmentOffsetMs:+0;-0;0}ms）",
                "再生カーソルだけを補正します。波形と保存するノーツ時刻は変えません。"), useGameTiming);
        }

        private void DrawWorkspaceAudio()
        {
            SectionLabel("音源");
            EditorGUI.BeginChangeCheck();
            AudioClip next = (AudioClip)EditorGUILayout.ObjectField(audioClip, typeof(AudioClip), false);
            if (EditorGUI.EndChangeCheck()) HandleAudioClipSelection(next);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("曲フォルダから取得")) EnsureAudioForSong(true);
            if (GUILayout.Button("音源を取り込む")) ImportAudio();
            GUILayout.EndHorizontal();
            if (!SaberChartAudioPreview.IsSupported)
                EditorGUILayout.HelpBox("音源プレビューAPIを利用できません。保存・編集は可能です。", MessageType.Warning);
            else if (!string.IsNullOrEmpty(waveform.Error)) GUILayout.Label("波形: " + waveform.Error, smallMutedStyle);
            GUILayout.Space(10);
        }

        private void DrawTimingExplanation()
        {
            EditorGUILayout.HelpBox("音源上のノーツ時刻 = ノーツの時刻 + OFFSET\n" +
                "OFFSET → 全ノーツの鳴る位置\n拍の始まり・BPM・テンポ地図 → 編集の格子\n" +
                "録音の入力補正 → 新しく打つノーツ\nゲームの表示補正 → プレビューのカーソルだけ", MessageType.None);
        }

        private sealed class WorkspaceNoteIssue
        {
            public int index;
            public string reason;
            public SaberChartIssueLevel level;
        }

        // 譜面チェック(SaberChartLint)の結果。内容・難易度・音源の長さが同じなら、描画のたびに検査し直さない。
        private List<SaberChartIssue> lintCache;
        private string lintCacheKey;
        // 診断の一覧に出す重さ(0=すべて、1=エラー、2=注意、3=情報)。
        [SerializeField] private int diagnosticFilter;
        private static readonly string[] DiagnosticFilterLabels = { "すべて", "エラー", "注意", "情報" };

        private List<SaberChartIssue> LintIssues(float audioLengthMs)
        {
            SaberChartDocument upper = UpperDocument();
            string key = SaberChartLint.Fingerprint(document) + "|" + EditingDifficultyLabel + "|" + audioLengthMs + "|" + upperDocumentKey;
            if (lintCache == null || key != lintCacheKey)
            {
                lintCache = SaberChartLint.Check(document, EditingDifficultyLabel, audioLengthMs);
                // 下の難易度が上の難易度の一部になっているか(上にない時刻のノーツをまとめて1件)。
                string upperDifficulty = SaberChartDerive.UpperDifficulty(EditingDifficulty);
                SaberChartIssue nesting = upperDifficulty == null ? null : SaberChartLint.NotInUpper(document, upper,
                    DifficultyLabels[Array.IndexOf(DifficultyValues, upperDifficulty)]);
                if (nesting != null) lintCache.Add(nesting);
                lintCacheKey = key;
            }
            return lintCache;
        }

        // ノーツから推定した難易度(本編の ChartDifficultyRater。表示レベルが0のとき選曲画面に出る値)。内容が同じなら計算し直さない。
        private int estimatedLevelKey = int.MinValue, estimatedLevel;
        private int EstimatedLevel()
        {
            int key = SaberChartLint.Fingerprint(document);
            if (key == estimatedLevelKey) return estimatedLevel;
            estimatedLevelKey = key;
            try { estimatedLevel = ChartDifficultyRater.Rate(JsonUtility.FromJson<ChartData>(SaberChartUtility.ToFileJson(document))); }
            catch (Exception) { estimatedLevel = 0; }
            return estimatedLevel;
        }

        // 本編用の音源の長さ。描画のたびにファイルを調べないよう、曲ごとに2秒だけ覚える。
        private string packagedAudioSong;
        private double packagedAudioCheckedAt;
        private float packagedAudioLengthMs;
        private float PackagedAudioLengthMs()
        {
            if (packagedAudioSong != EditingSongId || EditorApplication.timeSinceStartup >= packagedAudioCheckedAt + 2)
            {
                AudioClip packaged = SaberChartFileStore.LoadAudioClip(EditingSongId);
                packagedAudioLengthMs = packaged != null ? packaged.length * 1000f : 0f;
                packagedAudioSong = EditingSongId;
                packagedAudioCheckedAt = EditorApplication.timeSinceStartup;
            }
            return packagedAudioLengthMs;
        }

        // 使1: 本編で取れない配置や、ほかのツールによる書き換えを、診断の欄を開かなくても見えるようにする。押すと診断へ移る。
        private void DrawCheckBadge()
        {
            List<SaberChartIssue> issues = LintIssues(PackagedAudioLengthMs());
            int errors = issues.Count(issue => issue.level == SaberChartIssueLevel.Error);
            int cautions = issues.Count(issue => issue.level == SaberChartIssueLevel.Warning);
            bool warning = !string.IsNullOrEmpty(persistentWarning);
            if (errors == 0 && cautions == 0 && !warning)
            {
                GUILayout.Label(new GUIContent("チェックOK", "本編で取れない・取りにくい配置はありません（診断・復元で詳しく見られます）"),
                    smallMutedStyle, GUILayout.Width(70));
                return;
            }
            string text = warning ? "要確認" : errors > 0 ? $"エラー {errors}" + (cautions > 0 ? $"・注意 {cautions}" : "") : $"注意 {cautions}";
            var style = new GUIStyle(EditorStyles.miniButton)
            { normal = { textColor = errors > 0 || warning ? new Color(1f, .45f, .45f) : new Color(1f, .82f, .35f) } };
            string tip = warning ? persistentWarning : "本編で取れない（エラー）・取りにくい（注意）配置があります。押すと「診断・復元」で一覧を開きます。";
            if (GUILayout.Button(new GUIContent(text, tip), style, GUILayout.Width(110)))
            {
                showInspector = true;
                SetInspectorSection(InspectorSection.Diagnostics);
                GUIUtility.ExitGUI();
            }
        }

        private List<WorkspaceNoteIssue> WorkspaceNoteIssues(float audioLengthMs)
        {
            var result = new List<WorkspaceNoteIssue>();
            foreach (SaberChartIssue issue in LintIssues(audioLengthMs))
            {
                if (issue.notes.Length == 0) continue;
                if (diagnosticFilter > 0 && (int)issue.level != diagnosticFilter - 1) continue;
                result.Add(new WorkspaceNoteIssue { index = issue.notes[0], reason = issue.message, level = issue.level });
            }
            return result;
        }

        private void DrawWorkspaceDiagnosticNotes()
        {
            var issues = WorkspaceNoteIssues(PackagedAudioLengthMs());
            GUILayout.Space(10);
            SectionLabel($"対象ノーツへ移動（{issues.Count}件）");
            int filter = GUILayout.Toolbar(diagnosticFilter, DiagnosticFilterLabels);
            if (filter != diagnosticFilter) { diagnosticFilter = filter; diagnosticPage = 0; GUIUtility.ExitGUI(); }
            GUILayout.Label("エラーは本編で取れない・鳴らない配置、注意は取りにくい配置、情報は目安です。意図した配置なら直さなくてかまいません。",
                new GUIStyle(smallMutedStyle) { wordWrap = true });
            const int pageSize = 30;
            int pages = Mathf.Max(1, Mathf.CeilToInt(issues.Count / (float)pageSize));
            diagnosticPage = Mathf.Clamp(diagnosticPage, 0, pages - 1);
            if (pages > 1)
            {
                GUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(diagnosticPage == 0))
                    if (GUILayout.Button("前の30件")) { diagnosticPage--; GUIUtility.ExitGUI(); }
                GUILayout.Label($"{diagnosticPage + 1}/{pages}", centeredSmallStyle);
                using (new EditorGUI.DisabledScope(diagnosticPage + 1 >= pages))
                    if (GUILayout.Button("次の30件")) { diagnosticPage++; GUIUtility.ExitGUI(); }
                GUILayout.EndHorizontal();
            }
            using (new EditorGUI.DisabledScope(!CanEditAtCursor))
            {
                int end = Mathf.Min(issues.Count, (diagnosticPage + 1) * pageSize);
                for (int i = diagnosticPage * pageSize; i < end; i++)
                {
                    WorkspaceNoteIssue issue = issues[i];
                    SaberChartNote note = document.notes[issue.index];
                    string label = $"{SaberChartLint.Label(issue.level)}  #{issue.index + 1}  {(note.time + document.offsetMs) / 1000f:0.000}s\n{issue.reason}";
                    if (GUILayout.Button(label, new GUIStyle(GUI.skin.button) { wordWrap = true, alignment = TextAnchor.MiddleLeft }, GUILayout.MinHeight(44)))
                    {
                        FocusDiagnosticNote(issue.index); GUIUtility.ExitGUI();
                    }
                }
            }
        }

        private void FocusDiagnosticNote(int index)
        {
            if (!CanEditAtCursor || index < 0 || index >= document.notes.Count) return;
            EndNoteDrag(); FinishTextEditing(); ClearRange();
            selectedIndex = index;
            SyncPalettePositionFromSelected(); SeekSelectedNote(0f);
            SetInspectorSection(InspectorSection.Notes); showInspector = true;
            rightScroll = Vector2.zero;
        }

        private void ResetWorkspaceLayout()
        {
            workspaceLeftWidth = 224f; workspaceRightWidth = 330f;
            showPalette = showInspector = true;
            workspacePreferencesChanged = true;
            Repaint();
        }

        private void DrawPanelResizers(WorkspaceRects layout)
        {
            if (showPalette) DrawPanelResizer(layout.leftResize, true);
            if (showInspector) DrawPanelResizer(layout.rightResize, false);
        }

        private void DrawPanelResizer(Rect rect, bool left)
        {
            int id = GUIUtility.GetControlID(left ? 91231 : 91232, FocusType.Passive);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal, id);
            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                if (e.clickCount == 2) { ResetWorkspaceLayout(); e.Use(); return; }
                GUIUtility.hotControl = id;
                resizeStartMouse = e.mousePosition.x;
                resizeStartWidth = left ? workspaceLeftWidth : workspaceRightWidth;
                e.Use();
            }
            if (GUIUtility.hotControl != id) return;
            if (e.type == EventType.MouseDrag)
            {
                float delta = e.mousePosition.x - resizeStartMouse;
                if (left) workspaceLeftWidth = Mathf.Clamp(resizeStartWidth + delta, 210f, 320f);
                else workspaceRightWidth = Mathf.Clamp(resizeStartWidth - delta, 286f, 460f);
                workspacePreferencesChanged = true; e.Use(); Repaint();
            }
            if (e.type == EventType.MouseUp) { GUIUtility.hotControl = 0; e.Use(); }
        }

        private void LoadWorkspacePreferences()
        {
            string json = EditorPrefs.GetString(PrefPrefix + "Workspace", "");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                WorkspacePreferences prefs = JsonUtility.FromJson<WorkspacePreferences>(json);
                if (prefs == null) return;
                workspaceLeftWidth = float.IsNaN(prefs.left) || float.IsInfinity(prefs.left) ? 224 : Mathf.Clamp(prefs.left, 210, 320);
                workspaceRightWidth = float.IsNaN(prefs.right) || float.IsInfinity(prefs.right) ? 330 : Mathf.Clamp(prefs.right, 286, 460);
                showPalette = prefs.palette; showInspector = prefs.inspector;
            }
            catch (ArgumentException) { /* 壊れた表示設定でも譜面を開けるようにする。 */ }
        }

        private void SaveWorkspacePreferences()
        {
            if (!workspacePreferencesChanged) return;
            EditorPrefs.SetString(PrefPrefix + "Workspace", JsonUtility.ToJson(new WorkspacePreferences
            {
                left = workspaceLeftWidth, right = workspaceRightWidth, palette = showPalette, inspector = showInspector,
            }));
            workspacePreferencesChanged = false;
        }
    }
}
