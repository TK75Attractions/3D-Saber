using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>
    /// Playモード不要で使える、3D-Saber専用の縦型譜面エディター。
    /// 横軸にX、縦軸に時間を置き、右側の空間パッドでYも同時に扱う。
    /// </summary>
    public sealed partial class SaberChartEditorWindow : EditorWindow
    {
        private enum EditTool
        {
            Select,
            Draw,
            Erase,
        }

        private const float HeaderHeight = 88f;
        private const float FooterHeight = 28f;
        private const float LeftPanelWidth = 238f;
        private const float RightPanelWidth = 286f;
        private const float PanelGap = 7f;
        private const float TimelineGutter = 62f;
        private const int LaneCount = SaberChartUtility.DefaultLaneCount;

        private const string PrefPrefix = "3DSaber.ChartEditor.";
        private static readonly int[] SnapDenominators = { 4, 8, 12, 16, 24, 32 };
        private static readonly string[] SnapLabels = { "4分（表拍）", "8分", "12分", "16分", "24分", "32分" };
        private static readonly string[] DifficultyValues = { "easy", "normal", "hard" };
        private static readonly string[] DifficultyLabels = { "Easy", "Normal", "Hard" };
        private static readonly string[] TypeValues =
        {
            SaberChartUtility.TypeTap,
            SaberChartUtility.TypeDirection,
            SaberChartUtility.TypeLong,
        };
        private static readonly string[] TypeLabels = { "TAP", "方向", "LONG" };
        private static readonly string[] ColorValues =
        {
            SaberChartUtility.ColorRed,
            SaberChartUtility.ColorBlue,
            SaberChartUtility.ColorGold,
            SaberChartUtility.ColorDefault,
        };
        private static readonly string[] ColorLabels = { "赤 / 右手", "青 / 左手", "金 / 両手", "自由" };
        private static readonly string[] DirectionValues =
        {
            "upleft", "up", "upright",
            "left", "none", "right",
            "downleft", "down", "downright",
        };
        private static readonly string[] DirectionLabels =
        {
            "↖", "↑", "↗",
            "←", "・", "→",
            "↙", "↓", "↘",
        };

        private static readonly Color BackgroundColor = new Color(0.035f, 0.045f, 0.065f);
        private static readonly Color PanelColor = new Color(0.065f, 0.085f, 0.12f);
        private static readonly Color HeaderColor = new Color(0.045f, 0.065f, 0.095f);
        private static readonly Color AccentColor = new Color(0.16f, 0.88f, 0.95f);
        private static readonly Color MutedTextColor = new Color(0.58f, 0.67f, 0.75f);
        private static readonly Color RedColor = new Color(1f, 0.24f, 0.32f);
        private static readonly Color BlueColor = new Color(0.22f, 0.55f, 1f);
        private static readonly Color GoldColor = new Color(1f, 0.78f, 0.18f);

        [SerializeField] private SaberChartDocument document = new SaberChartDocument();
        [SerializeField] private string songId = "NewSong";
        [SerializeField] private int difficultyIndex = 1;
        [SerializeField] private AudioClip audioClip;
        [SerializeField] private float beatZeroMs;
        [SerializeField] private float currentBeat;
        [SerializeField] private float pixelsPerBeat = 82f;
        [SerializeField] private int snapIndex = 3;
        [SerializeField] private EditTool editTool = EditTool.Draw;
        [SerializeField] private int paletteXLane = 3;
        [SerializeField] private int paletteYLane = 3;
        [SerializeField] private string paletteType = SaberChartUtility.TypeTap;
        [SerializeField] private string paletteColor = SaberChartUtility.ColorRed;
        [SerializeField] private string paletteDirection = SaberChartUtility.DirectionNone;
        [SerializeField] private int paletteCount = 2;
        [SerializeField] private int selectedIndex = -1;
        [SerializeField] private string loadedSongId;
        [SerializeField] private string loadedDifficulty;
        [SerializeField] private string savedJson;
        [SerializeField] private Vector2 leftScroll;
        [SerializeField] private Vector2 rightScroll;

        private readonly SaberChartHistory history = new SaberChartHistory();
        private readonly SaberChartWaveform waveform = new SaberChartWaveform();
        private GUIStyle panelStyle;
        private GUIStyle headerStyle;
        private GUIStyle sectionStyle;
        private GUIStyle titleStyle;
        private GUIStyle smallMutedStyle;
        private GUIStyle centeredSmallStyle;
        private GUIStyle noteLabelStyle;

        private bool isPlaying;
        [SerializeField] private bool showPlaybackPreview = true;
        [SerializeField] private bool expandPlaybackPreview;
        // プレイ画面を本編に近づける表示(本編のカメラ、着地の枠、本編の小節線、当たりの円)。
        [SerializeField] private bool previewGameCamera = true;
        [SerializeField] private bool previewLandingFrame = true;
        [SerializeField] private bool previewBarLines = true;
        [SerializeField] private bool previewHitRadius;
        private SaberChartPlaybackPreview playbackPreview;
        private string playbackPreviewError;
        private float playbackAudioStartSeconds;
        [SerializeField] private bool useGameTiming = true;
        private bool draggingNote;
        private bool dragRecorded;
        private int dragNoteIndex = -1;
        private string dragSnapshot;
        // クリックの手ぶれでノーツを動かさない。この距離を超えてから初めて移動として扱う。
        private const float DragThresholdPixels = 4f;
        private bool dragStarted;
        private Vector2 dragStartMouse;
        private float dragOriginalTime;
        private float dragOriginalX;
        private float dragOriginalBeat;
        private string statusMessage = "準備完了";
        private double statusUntil;

        // 未保存の間だけ、5分ごとに Library へ下書きを残す。
        private const double AutosaveIntervalSeconds = 300.0;
        private double nextAutosaveAt;
        private SaberChartDrafts.Draft pendingDraft;
        // 新規・複製の譜面が既存ファイルを置き換えることを、利用者が確認済みの保存先。
        private string confirmedOverwriteTarget;

        // 確認の選択とファイル操作を分離し、失敗時の編集状態も検証できるようにする。
        private Func<string, string, string, string, string, int> confirmChangesDialog = EditorUtility.DisplayDialogComplex;
        private Action<string> reportLoadError = message => EditorUtility.DisplayDialog("譜面を読み込めません", message, "OK");

        private int CurrentSnap => SnapDenominators[Mathf.Clamp(snapIndex, 0, SnapDenominators.Length - 1)];
        private string CurrentDifficulty => DifficultyValues[Mathf.Clamp(difficultyIndex, 0, DifficultyValues.Length - 1)];
        // 保存先は「開いた場所」。画面上部の選択は、次に開く・作る先を選ぶだけ。
        private string EditingSongId => !string.IsNullOrEmpty(loadedSongId) ? loadedSongId : songId?.Trim();
        private string EditingDifficulty => !string.IsNullOrEmpty(loadedDifficulty) ? loadedDifficulty : CurrentDifficulty;
        private int EditingDifficultyIndex => Mathf.Max(0, Array.IndexOf(DifficultyValues, EditingDifficulty));
        private string EditingDifficultyLabel => DifficultyLabels[EditingDifficultyIndex];
        private string HistoryStateKey => "3DSaber.ChartEditor.History." + GetInstanceID();
        private SaberChartNote SelectedNote =>
            document?.notes != null && selectedIndex >= 0 && selectedIndex < document.notes.Count
                ? document.notes[selectedIndex]
                : null;

        [MenuItem("3D Saber/譜面エディター", priority = 10)]
        public static void Open()
        {
            SaberChartEditorWindow window = GetWindow<SaberChartEditorWindow>();
            window.titleContent = new GUIContent("3D Saber 譜面");
            window.minSize = new Vector2(1050f, 650f);
            // 出力機器ごとの入力補正は、メニューから開いたときに読み込む(テストの窓では個人設定を使わない)。
            window.LoadInputOffsetProfile();
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("3D Saber 譜面");
            minSize = new Vector2(1050f, 650f);
            saveChangesMessage = "譜面に未保存の変更があります。保存しますか？";
            document ??= new SaberChartDocument();
            SaberChartUtility.Normalize(document);
            savedJson ??= SaberChartUtility.ToJson(document, false);
            LoadPreferences();
            LoadRecordingLayout();
            EnsureAudioForSong(false);
            RestoreHistoryState();
            // Unity の「未保存」の印は再コンパイルをまたいで残らない。開き直すたびに内容から計算し直す。
            UpdateDirtyState();
            CheckForDraft();
            EditorApplication.update += EditorTick;
        }

        private void OnDisable()
        {
            EditorApplication.update -= EditorTick;
            StopPreview(false);
            DisposePlaybackPreview();
            SavePreferences();
            SaveRecordingLayout();
            SaveHistoryState();
            FinishLatencyCalibration(true);
            DisposeClickMix();
        }

        private void OnDestroy()
        {
            // 閉じたウィンドウの履歴は持ち越さない(再コンパイルでは OnDestroy は呼ばれない)。
            SessionState.EraseString(HistoryStateKey);
        }

        // 再コンパイルや Play 開始で Undo の履歴を失わないよう、エディターのセッション内に退避する。
        private void SaveHistoryState()
        {
            if (!history.CanUndo && !history.CanRedo)
            {
                SessionState.EraseString(HistoryStateKey);
                return;
            }
            SessionState.SetString(HistoryStateKey, history.Export(CurrentJson()));
        }

        private void RestoreHistoryState()
        {
            string exported = SessionState.GetString(HistoryStateKey, string.Empty);
            SessionState.EraseString(HistoryStateKey);
            if (!string.IsNullOrEmpty(exported)) history.Import(exported, CurrentJson());
        }

        public override void SaveChanges()
        {
            if (SaveDocument()) base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            hasUnsavedChanges = false;
            base.DiscardChanges();
        }

        private void OnGUI()
        {
            EnsureStyles();
            HandleKeyboardShortcuts(Event.current);
            EditorGUI.DrawRect(new Rect(Vector2.zero, position.size), BackgroundColor);

            Rect headerRect = new Rect(0f, 0f, position.width, HeaderHeight);
            Rect footerRect = new Rect(0f, position.height - FooterHeight, position.width, FooterHeight);
            float contentY = HeaderHeight + PanelGap;
            float contentHeight = Mathf.Max(100f, position.height - contentY - FooterHeight - PanelGap);
            Rect leftRect = new Rect(PanelGap, contentY, LeftPanelWidth, contentHeight);
            Rect rightRect = new Rect(position.width - RightPanelWidth - PanelGap, contentY, RightPanelWidth, contentHeight);
            Rect timelineRect = new Rect(
                leftRect.xMax + PanelGap,
                contentY,
                Mathf.Max(120f, rightRect.xMin - leftRect.xMax - PanelGap * 2f),
                contentHeight);

            using (new EditorGUI.DisabledScope(RecordingBusy))
            {
                DrawHeader(headerRect);
                DrawLeftPanel(leftRect);
            }
            DrawCenterPanel(timelineRect);
            DrawRightPanel(rightRect);
            DrawFooter(footerRect);
        }

        private void DrawHeader(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, headerStyle);
            GUILayout.BeginArea(new Rect(rect.x + 14f, rect.y + 8f, rect.width - 28f, rect.height - 12f));

            GUILayout.BeginHorizontal();
            GUILayout.Label("3D SABER  /  CHART STUDIO", titleStyle, GUILayout.Width(360f));
            int mode = GUILayout.Toolbar(recordMode ? recordStepMode ? 2 : 1 : 0, new[] { "編集", "録音", "ステップ" }, GUILayout.Width(210f));
            if (mode != (recordMode ? recordStepMode ? 2 : 1 : 0)) SetRecordingInputMode(mode);
            GUILayout.FlexibleSpace();
            if (SelectionDiffersFromEditing())
            {
                // 選んだだけでは開かない。保存先は「編集中」のまま。
                GUILayout.Label($"選択中の {songId?.Trim()} / {DifficultyLabels[Mathf.Clamp(difficultyIndex, 0, 2)]} は未読込",
                    new GUIStyle(smallMutedStyle) { normal = { textColor = GoldColor } });
                if (GUILayout.Button(new GUIContent("ここへ複製", "今の内容を、選択中の曲・難易度の新しい譜面として開きます（保存するまで書きません）"),
                        EditorStyles.miniButton, GUILayout.Width(70f))) DuplicateToSelection();
                if (GUILayout.Button(new GUIContent("選択を戻す", "選択を編集中の曲・難易度へ戻します"),
                        EditorStyles.miniButton, GUILayout.Width(70f))) ResetSelectionToEditing();
                GUILayout.Space(8f);
            }
            showPlaybackPreview = GUILayout.Toggle(showPlaybackPreview, "プレイ画面", EditorStyles.miniButton, GUILayout.Width(90f));
            if (hasUnsavedChanges)
                GUILayout.Label("● 未保存", new GUIStyle(smallMutedStyle) { normal = { textColor = GoldColor } });
            else
                GUILayout.Label("保存済み", smallMutedStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent($"編集中  {EditingSongId} / {EditingDifficultyLabel}", "保存先です。上書きするのはこのファイルだけです。"),
                new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = Color.white } }, GUILayout.MaxWidth(230f));
            GUI.enabled = !RecordingBusy && SaberChartFileStore.IsValidSongId(EditingSongId, out _);
            if (GUILayout.Button("保存", GUILayout.Width(52f))) SaveDocument();
            GUI.enabled = !RecordingBusy;
            if (GUILayout.Button("フォルダ", GUILayout.Width(62f))) RevealSongFolder();
            GUILayout.Space(10f);
            GUILayout.Label("開く・作る", smallMutedStyle, GUILayout.Width(52f));
            string nextSongId = GUILayout.TextField(songId ?? string.Empty, GUILayout.MinWidth(90f), GUILayout.MaxWidth(200f));
            if (nextSongId != songId) songId = nextSongId;
            if (GUILayout.Button("▾", EditorStyles.miniButton, GUILayout.Width(24f))) ShowSongMenu();

            int nextDifficulty = EditorGUILayout.Popup(difficultyIndex, DifficultyLabels, GUILayout.Width(72f));
            if (nextDifficulty != difficultyIndex) difficultyIndex = nextDifficulty;

            if (GUILayout.Button(new GUIContent("読込", "選択中の曲・難易度を開きます"), GUILayout.Width(48f))) LoadDocument();
            if (GUILayout.Button(new GUIContent("新規", "選択中の曲・難易度に空の譜面を作ります"), GUILayout.Width(48f))) NewDocument();
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{document.notes.Count:N0} NOTES", smallMutedStyle, GUILayout.Width(80f));
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // 開いている譜面が変わったら、上の難易度・候補・設定の比較を読み直す。
        private void ResetDerivedViews()
        {
            deriveCandidates = null;
            upperDocumentKey = null;
            settingsMismatchKey = null;
            overviewSectionsSong = null;
            ClearRange();
        }

        private bool SelectionDiffersFromEditing()
        {
            if (string.IsNullOrEmpty(loadedSongId) || string.IsNullOrEmpty(loadedDifficulty)) return false;
            return !string.Equals(loadedSongId, songId?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                   !string.Equals(loadedDifficulty, CurrentDifficulty, StringComparison.OrdinalIgnoreCase);
        }

        private void ResetSelectionToEditing()
        {
            FinishTextEditing();
            songId = EditingSongId;
            difficultyIndex = EditingDifficultyIndex;
            Repaint();
        }

        /// <summary>
        /// 今の内容を、画面上部で選んだ曲・難易度の新しい譜面として開く。ファイルは保存するまで書かない。
        /// 編集中の譜面に未保存の変更があれば、先に保存するかを選ばせる。
        /// </summary>
        private void DuplicateToSelection()
        {
            EndNoteDrag();
            string targetSong = songId?.Trim();
            string targetDifficulty = CurrentDifficulty;
            if (!SaberChartFileStore.IsValidSongId(targetSong, out string reason))
            {
                EditorUtility.DisplayDialog("複製できません", reason, "OK");
                return;
            }
            if (hasUnsavedChanges)
            {
                int choice = confirmChangesDialog(
                    "未保存の変更",
                    $"編集中の {EditingSongId} / {EditingDifficultyLabel} を保存してから複製しますか？",
                    "保存して複製",
                    "キャンセル",
                    "保存せずに複製");
                if (choice == 1) return;
                if (choice == 0 && !SaveDocument()) return;
            }
            string targetLabel = DifficultyLabels[Mathf.Clamp(difficultyIndex, 0, DifficultyLabels.Length - 1)];
            SaberChartDocument existing = SaberChartFileStore.TryLoadExact(targetSong, targetDifficulty);
            if (SaberChartFileStore.ChartExists(targetSong, targetDifficulty) &&
                !confirmDuplicateOverwrite($"{targetSong} / {targetLabel} は既にあります。",
                    "複製した譜面を保存すると、このファイルを置き換えます（保存前のファイルはバックアップされます）。続けますか？"))
                return;

            SaberChartDocument copy = SaberChartUtility.Clone(document);
            // 表示レベルと制作メモは難易度ごとの値。複製先に既存の譜面があればその値を引き継ぐ。
            copy.displayLevel = existing?.displayLevel ?? 0;
            copy.extraFields = existing?.extraFields ?? new List<SaberChartExtraField>();
            StopPreview(false);
            document = copy;
            loadedSongId = targetSong;
            loadedDifficulty = targetDifficulty;
            confirmedOverwriteTarget = TargetKey(targetSong, targetDifficulty);
            ResetDerivedViews();
            selectedIndex = -1;
            history.Clear();
            savedJson = null;
            UpdateDirtyState();
            EnsureAudioForSong(false);
            SetStatus($"{targetSong} / {targetLabel} として開きました。保存するとファイルを書きます");
        }

        private Func<string, string, bool> confirmDuplicateOverwrite = (title, message) =>
            EditorUtility.DisplayDialog(title, message, "続ける", "キャンセル");

        private static string TargetKey(string song, string difficulty) =>
            (song ?? string.Empty).Trim().ToLowerInvariant() + "/" + (difficulty ?? string.Empty).Trim().ToLowerInvariant();

        private void DrawLeftPanel(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, panelStyle);
            GUILayout.BeginArea(new Rect(rect.x + 10f, rect.y + 10f, rect.width - 20f, rect.height - 20f));
            leftScroll = GUILayout.BeginScrollView(leftScroll, false, false);

            SectionLabel("編集ツール");
            GUILayout.BeginHorizontal();
            DrawToolToggle(EditTool.Select, "選択");
            DrawToolToggle(EditTool.Draw, "配置");
            DrawToolToggle(EditTool.Erase, "消去");
            GUILayout.EndHorizontal();

            GUILayout.Space(10f);
            SectionLabel("ノーツ種類");
            GUILayout.BeginHorizontal();
            for (int i = 0; i < TypeValues.Length; i++)
            {
                bool active = paletteType == TypeValues[i];
                if (DrawChoiceButton(TypeLabels[i], active, NoteTypeColor(TypeValues[i])))
                {
                    paletteType = TypeValues[i];
                    if (paletteType == SaberChartUtility.TypeDirection &&
                        paletteDirection == SaberChartUtility.DirectionNone)
                        paletteDirection = "up";
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10f);
            SectionLabel("担当セーバー / 色");
            for (int row = 0; row < 2; row++)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < 2; column++)
                {
                    int index = row * 2 + column;
                    bool active = paletteColor == ColorValues[index];
                    if (DrawChoiceButton(ColorLabels[index], active, NoteColor(ColorValues[index])))
                        paletteColor = ColorValues[index];
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(10f);
            SectionLabel("カット方向");
            for (int row = 0; row < 3; row++)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < 3; column++)
                {
                    int index = row * 3 + column;
                    bool active = paletteDirection == DirectionValues[index];
                    Color tint = paletteType == SaberChartUtility.TypeDirection ? AccentColor : MutedTextColor;
                    if (DrawChoiceButton(DirectionLabels[index], active, tint, 34f))
                    {
                        paletteDirection = DirectionValues[index];
                        if (paletteDirection != SaberChartUtility.DirectionNone)
                            paletteType = SaberChartUtility.TypeDirection;
                    }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("キー: QWE / ASD / ZXC（S=なし）・テンキー", smallMutedStyle);
            directionAdvance = EditorGUILayout.ToggleLeft(new GUIContent("方向を付けたら次のノーツへ",
                "キーで方向を付けたあと、次のノーツを選びます（ステップでは次の配置済みの拍へ）"), directionAdvance);

            GUILayout.Space(10f);
            SectionLabel("LONG カット回数");
            paletteCount = EditorGUILayout.IntSlider(paletteCount, 2, 12);

            GUILayout.Space(10f);
            SectionLabel("分解能 / SNAP");
            snapIndex = EditorGUILayout.Popup(snapIndex, SnapLabels);
            GUILayout.Label($"1ステップ = {SaberChartUtility.SnapStep(CurrentSnap):0.###} 拍", smallMutedStyle);

            GUILayout.Space(10f);
            SectionLabel("履歴");
            GUILayout.BeginHorizontal();
            GUI.enabled = !RecordingBusy && history.CanUndo;
            if (GUILayout.Button("↶ 元に戻す")) Undo();
            GUI.enabled = !RecordingBusy && history.CanRedo;
            if (GUILayout.Button("↷ やり直し")) Redo();
            GUI.enabled = !RecordingBusy;
            GUILayout.EndHorizontal();

            GUILayout.Space(10f);
            EditorGUILayout.HelpBox(
                "中央: 横=X / 縦=時間\n右の8×8パッド: X・Y空間位置\n右クリック: ノーツ削除",
                MessageType.None);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawRightPanel(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, panelStyle);
            GUILayout.BeginArea(new Rect(rect.x + 10f, rect.y + 10f, rect.width - 20f, rect.height - 20f));
            rightScroll = GUILayout.BeginScrollView(rightScroll, false, false);

            DrawDraftBanner();
            SectionLabel("再生 / シーク");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("|◀", GUILayout.Width(42f))) SeekToBeat(0f);
            if (GUILayout.Button(isPlaying ? "一時停止" : "▶ 再生", GUILayout.Height(28f))) TogglePreview();
            if (GUILayout.Button("■", GUILayout.Width(36f))) StopPreview(true);
            GUILayout.EndHorizontal();
            DrawClickToggles();
            DrawPreviewOptions();

            EditorGUI.BeginDisabledGroup(RecordingBusy);
            float maxBeat = MaxBeat();
            EditorGUI.BeginChangeCheck();
            float soughtBeat = EditorGUILayout.Slider(currentBeat, 0f, maxBeat);
            if (EditorGUI.EndChangeCheck()) SeekToBeat(soughtBeat);
            GUILayout.Label(
                $"{SaberChartUtility.FormatMusicalPosition(currentBeat, document, CurrentSnap)}   /   {BeatToAudioSeconds(currentBeat):0.000}s",
                centeredSmallStyle);
            useGameTiming = EditorGUILayout.ToggleLeft(new GUIContent(
                $"ゲームと同じ表示補正（{GameSession.JudgmentOffsetMs:+0;-0;0}ms）",
                "ゲームの判定調整と同じ量だけ再生カーソルを補正します。波形と保存する譜面時刻は変わりません。"), useGameTiming);
            DrawJumpControls();
            DrawMarkerControls();
            EditorGUI.EndDisabledGroup();

            if (!recordMode)
            {
                GUILayout.Space(10f);
                EditorGUI.BeginDisabledGroup(RecordingBusy);
                DrawRangePanel();
                GUILayout.Space(10f);
                DrawDifficultyPanel();
                GUILayout.Space(10f);
                DrawSongInfoPanel();
                EditorGUI.EndDisabledGroup();
            }

            DrawRecordingSettings();

            EditorGUI.BeginDisabledGroup(RecordingBusy);
            GUILayout.Space(10f);
            SectionLabel("音源");
            EditorGUI.BeginChangeCheck();
            AudioClip nextClip = (AudioClip)EditorGUILayout.ObjectField(audioClip, typeof(AudioClip), false);
            if (EditorGUI.EndChangeCheck()) HandleAudioClipSelection(nextClip);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("曲フォルダから取得")) EnsureAudioForSong(true);
            if (GUILayout.Button("音源を取り込む")) ImportAudio();
            GUILayout.EndHorizontal();
            if (!SaberChartAudioPreview.IsSupported)
                EditorGUILayout.HelpBox("音源プレビューAPIを利用できません。保存・編集は可能です。", MessageType.Warning);
            else if (!string.IsNullOrEmpty(waveform.Error))
                GUILayout.Label("波形: " + waveform.Error, smallMutedStyle);

            GUILayout.Space(10f);
            SectionLabel("曲 / グリッド設定");
            DrawChartSettings();
            DrawSettingsMismatch();
            DrawTempoMapPanel();
            GUILayout.Space(10f);
            DrawTimeSignatures();

            GUILayout.Space(10f);
            SectionLabel(SelectedNote != null ? "選択ノーツのXY位置" : "次に置くXY位置");
            Rect padRect = GUILayoutUtility.GetRect(220f, 220f, GUILayout.ExpandWidth(true));
            DrawSpatialPad(padRect);
            GUILayout.Label("上が +Y / 右が +X", centeredSmallStyle);

            if (SelectedNote != null)
            {
                GUILayout.Space(10f);
                SectionLabel("選択ノーツ詳細");
                DrawSelectedInspector();
            }

            GUILayout.Space(10f);
            DrawValidationSummary();

            GUILayout.Space(8f);
            GUI.enabled = !RecordingBusy && SaberChartFileStore.IsValidSongId(EditingSongId, out _);
            // 変更がなければ保存せずに本編を開く。開いて試すだけでファイルを書き換えない。
            if (GUILayout.Button(hasUnsavedChanges ? "保存して本編でテスト" : "本編でテスト（保存なし）", GUILayout.Height(32f))) TestInGame();
            GUI.enabled = !RecordingBusy;
            EditorGUI.EndDisabledGroup();

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawChartSettings()
        {
            // 数値は Enter か欄を離れたときに確定する。1文字ごとに履歴を増やさない。
            EditorGUI.BeginChangeCheck();
            float nextBpm = EditorGUILayout.DelayedFloatField("BPM", document.bpm);
            float nextOffset = EditorGUILayout.DelayedFloatField("全体OFFSET (ms)", document.offsetMs);
            float nextBeatZero = EditorGUILayout.DelayedFloatField("譜面グリッド原点 (ms / 0以上)", beatZeroMs);
            float nextScale = EditorGUILayout.DelayedFloatField("座標倍率", document.coordScale);
            bool changed = EditorGUI.EndChangeCheck();
            if (!Mathf.Approximately(document.beatZeroMs, beatZeroMs))
            {
                // 推定した原点は表示にだけ使う。本編の小節線はファイルの値のまま。
                GUILayout.Label($"ファイルの原点は {document.beatZeroMs:0.#}ms のまま（本編の小節線はこちら）", smallMutedStyle);
                if (GUILayout.Button(new GUIContent("表示中の原点をファイルにも書く",
                        $"本編の小節線とカウントインが {beatZeroMs:0.#}ms 基準に変わります")))
                    WriteDisplayedOriginToFile();
            }
            if (changed) ApplyChartSettings(nextBpm, nextOffset, nextBeatZero, nextScale);
        }

        private void ApplyChartSettings(float nextBpm, float nextOffset, float nextBeatZero, float nextScale)
        {
            if (float.IsNaN(nextBpm) || float.IsInfinity(nextBpm) || float.IsNaN(nextOffset) || float.IsInfinity(nextOffset) ||
                float.IsNaN(nextBeatZero) || float.IsInfinity(nextBeatZero) || float.IsNaN(nextScale) || float.IsInfinity(nextScale))
                return;
            EndNoteDrag();
            string before = CurrentJson();
            float safeBpm = Mathf.Max(1f, nextBpm);
            float safeBeatZero = Mathf.Max(0f, nextBeatZero);
            bool bpmChanged = !Mathf.Approximately(safeBpm, document.bpm);
            bool originChanged = !Mathf.Approximately(safeBeatZero, beatZeroMs);
            // 拍の値が1つの格子に乗っていない譜面(テンポが変わる曲など)は、拍の値に曲のテンポの情報がある。
            // そのときは格子を変えても拍の値を計算し直さない。
            bool beatsFollowGrid = SaberChartUtility.BeatsFollowSingleGrid(document);
            document.bpm = safeBpm;
            document.offsetMs = nextOffset;
            document.coordScale = Mathf.Max(0.0001f, nextScale);
            if (originChanged)
            {
                // 原点の欄を直接変えたときだけ、ファイルの原点も変える(本編の小節線も動く)。
                beatZeroMs = safeBeatZero;
                document.beatZeroMs = safeBeatZero;
            }
            // time が本編の判定時刻。BPM変更でも時刻は動かさず、補助値 beat だけ更新する。
            if ((bpmChanged || originChanged) && beatsFollowGrid && (document.tempoMap == null || document.tempoMap.Count == 0))
                SaberChartUtility.RecalculateBeatsFromTimes(document, beatZeroMs);
            if (CurrentJson() == before) return;
            history.Record(before);
            MarkChanged();
            RestartPreviewIfPlaying();
        }

        private void WriteDisplayedOriginToFile()
        {
            EndNoteDrag();
            string before = CurrentJson();
            document.beatZeroMs = beatZeroMs;
            if (CurrentJson() == before) return;
            history.Record(before);
            MarkChanged();
            SetStatus($"ファイルの原点を {beatZeroMs:0.#}ms にしました（保存すると本編の小節線が変わります）");
        }

        private void DrawCenterPanel(Rect rect)
        {
            rect = DrawRecordingPad(rect);
            if (!showPlaybackPreview)
            {
                DisposePlaybackPreview();
                if (recordMode)
                {
                    if (GUI.Button(new Rect(rect.xMax - 94, rect.y, 86, 24), "3Dを見る")) showPlaybackPreview = true;
                    rect = new Rect(rect.x, rect.y + 28, rect.width, rect.height - 28);
                }
                DrawTimeline(rect);
                return;
            }

            float previewHeight = recordMode || expandPlaybackPreview ? rect.height : Mathf.Min(rect.width * 9f / 16f + 30f, rect.height * .46f);
            Rect previewRect = new Rect(rect.x, rect.y, rect.width, previewHeight);
            GUI.Box(previewRect, GUIContent.none, panelStyle);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 5f, rect.width - 110f, 22f),
                isPlaying ? "プレイ画面  /  再生中" : "プレイ画面  /  一時停止", smallMutedStyle);
            if (GUI.Button(new Rect(rect.xMax - 94f, rect.y + 4f, 86f, 22f), recordMode ? "譜面を見る" : expandPlaybackPreview ? "編集に戻る" : "拡大"))
            {
                if (recordMode) showPlaybackPreview = false;
                else expandPlaybackPreview = !expandPlaybackPreview;
            }
            Rect viewport = new Rect(previewRect.x + 4f, previewRect.y + 30f, previewRect.width - 8f, previewRect.height - 34f);
            // 縦横比は固定。拡大してもノーツの画角を変えない。
            float width = Mathf.Min(viewport.width, viewport.height * 16f / 9f);
            viewport = new Rect(viewport.center.x - width / 2f, viewport.center.y - width * 9f / 32f, width, width * 9f / 16f);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                DisposePlaybackPreview();
                GUI.Label(viewport, "編集モードでプレイ画面を表示します。", centeredSmallStyle);
            }
            else if (Event.current.type == EventType.Repaint && playbackPreviewError == null)
            {
                try
                {
                    playbackPreview ??= new SaberChartPlaybackPreview();
                    playbackPreview.UseGameCamera = previewGameCamera;
                    playbackPreview.ShowLandingFrame = previewLandingFrame;
                    playbackPreview.ShowBarLines = previewBarLines;
                    playbackPreview.ShowHitRadius = previewHitRadius;
                    playbackPreview.Tick(document, BeatToAudioSeconds(currentBeat));
                    Texture frame = playbackPreview.Render(viewport);
                    if (frame != null) GUI.DrawTexture(viewport, frame, ScaleMode.ScaleToFit, false);
                }
                catch (Exception exception)
                {
                    DisposePlaybackPreview();
                    playbackPreviewError = "プレイ画面を表示できません: " + exception.Message;
                    Debug.LogException(exception);
                }
            }
            if (playbackPreviewError != null)
            {
                GUI.Label(viewport, playbackPreviewError, EditorStyles.wordWrappedLabel);
                if (GUI.Button(new Rect(viewport.x, viewport.yMax - 26f, 88f, 24f), "再試行")) playbackPreviewError = null;
            }
            if (!recordMode && !expandPlaybackPreview)
                DrawTimeline(new Rect(rect.x, previewRect.yMax + PanelGap, rect.width, rect.height - previewHeight - PanelGap));
        }

        // プレイ画面の見え方を本編に近づける切り替え。
        private void DrawPreviewOptions()
        {
            GUILayout.BeginHorizontal();
            previewGameCamera = GUILayout.Toggle(previewGameCamera, new GUIContent("本編の視点",
                "本編と同じカメラ（高さ2.35・12°見下ろし）。オフで以前の正面寄りの視点"), EditorStyles.miniButtonLeft);
            previewLandingFrame = GUILayout.Toggle(previewLandingFrame, new GUIContent("着地の枠",
                "判定面に、推奨の XY 範囲（±2.5 × ±1.5）の枠を出します"), EditorStyles.miniButtonMid);
            previewBarLines = GUILayout.Toggle(previewBarLines, new GUIContent("小節線",
                "本編の小節線（ファイルの BPM・原点・OFFSET・拍子）を、ノーツと同じ速さで流します"), EditorStyles.miniButtonMid);
            previewHitRadius = GUILayout.Toggle(previewHitRadius, new GUIContent("当たりの円",
                "判定の前後0.25秒のノーツに、刃が届けば切れる範囲（半径1.2）の円を出します"), EditorStyles.miniButtonRight);
            GUILayout.EndHorizontal();
        }

        private void DisposePlaybackPreview()
        {
            playbackPreview?.Dispose();
            playbackPreview = null;
        }

        private static readonly int[] MeterDenominators = { 1, 2, 4, 8, 16, 32, 64 };
        private static readonly string[] MeterDenominatorLabels = { "1", "2", "4", "8", "16", "32", "64" };

        private void DrawTimeSignatures()
        {
            SectionLabel("拍子 / 途中の拍子変更");
            var meter = new ChartMeterMap(document.timeSignatures);
            var initial = meter.At(0);
            GUILayout.BeginHorizontal();
            GUILayout.Label("曲頭", GUILayout.Width(50));
            EditorGUI.BeginChangeCheck();
            int numerator = EditorGUILayout.DelayedIntField(initial.Numerator, GUILayout.Width(48));
            GUILayout.Label("/", GUILayout.Width(12));
            int denominator = EditorGUILayout.Popup(Array.IndexOf(MeterDenominators, initial.Denominator),
                MeterDenominatorLabels, GUILayout.Width(58));
            if (EditorGUI.EndChangeCheck()) SetTimeSignature(0, numerator, MeterDenominators[denominator]);
            GUILayout.EndHorizontal();
            GUILayout.Label("開始拍（四分音符単位） / 拍子", smallMutedStyle);
            for (int i = 0; i < document.timeSignatures.Count; i++)
            {
                var signature = document.timeSignatures[i];
                if (signature.beat == 0) continue;
                var position = meter.At(signature.beat);
                GUILayout.Label($"第{position.Measure}小節  /  {BeatToAudioSeconds(signature.beat):0.000}秒", smallMutedStyle);
                GUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                float beat = EditorGUILayout.DelayedFloatField(signature.beat, GUILayout.Width(66));
                numerator = EditorGUILayout.DelayedIntField(signature.numerator, GUILayout.Width(36));
                GUILayout.Label("/", GUILayout.Width(10));
                denominator = EditorGUILayout.Popup(Array.IndexOf(MeterDenominators, signature.denominator),
                    MeterDenominatorLabels, GUILayout.Width(48));
                bool changed = EditorGUI.EndChangeCheck();
                bool remove = GUILayout.Button("×", GUILayout.Width(24));
                GUILayout.EndHorizontal();
                if (changed || remove)
                {
                    EditTimeSignature(i, beat, numerator, MeterDenominators[denominator], remove);
                    break;
                }
            }
            var active = meter.At(currentBeat);
            if (GUILayout.Button($"現在位置に追加（{currentBeat:0.###}拍）"))
                SetTimeSignature(currentBeat, active.Numerator, active.Denominator);
            EditorGUILayout.HelpBox("変更位置が新しい小節の先頭になります。\n拍子を変えてもノーツの時刻は動きません。", MessageType.None);
        }

        private void PutTimeSignature(float beat, int numerator, int denominator)
        {
            if (float.IsNaN(beat) || float.IsInfinity(beat)) return;
            beat = Mathf.Max(0, beat);
            document.timeSignatures.RemoveAll(item => Mathf.Abs(item.beat - beat) < ChartMeterMap.Epsilon);
            document.timeSignatures.Add(new ChartTimeSignature
                { beat = beat, numerator = Mathf.Clamp(numerator, 1, 64), denominator = denominator });
            document.timeSignatures = ChartMeterMap.Normalize(document.timeSignatures);
        }

        private void SetTimeSignature(float beat, int numerator, int denominator)
        {
            EndNoteDrag();
            string before = CurrentJson();
            PutTimeSignature(beat, numerator, denominator);
            history.Record(before);
            MarkChanged();
        }

        private void EditTimeSignature(int index, float beat, int numerator, int denominator, bool remove)
        {
            if (index < 0 || index >= document.timeSignatures.Count ||
                (!remove && (float.IsNaN(beat) || float.IsInfinity(beat)))) return;
            EndNoteDrag();
            string before = CurrentJson();
            document.timeSignatures.RemoveAt(index);
            if (!remove) PutTimeSignature(beat, numerator, denominator);
            history.Record(before);
            MarkChanged();
        }

        private void DrawSelectedInspector()
        {
            SaberChartNote note = SelectedNote;
            if (note == null) return;

            float beat = TimelineBeat(note);
            float time = note.time;
            float x = note.x;
            float y = note.y;
            int typeIndex = Mathf.Max(0, Array.IndexOf(TypeValues, note.type));
            int colorIndex = Mathf.Max(0, Array.IndexOf(ColorValues, note.color));
            int directionIndex = Mathf.Max(0, Array.IndexOf(DirectionValues, note.direction));
            int count = note.count;

            // 数値は確定してから1回だけ履歴に残す(1文字ごとに Undo を増やさない)。
            EditorGUI.BeginChangeCheck();
            float nextBeat = EditorGUILayout.DelayedFloatField("拍", beat);
            float nextTime = EditorGUILayout.DelayedFloatField("時刻 (ms)", time);
            float nextX = EditorGUILayout.DelayedFloatField("X", x);
            float nextY = EditorGUILayout.DelayedFloatField("Y", y);
            int nextTypeIndex = EditorGUILayout.Popup("種類", typeIndex, TypeLabels);
            int nextColorIndex = EditorGUILayout.Popup("色 / 手", colorIndex, ColorLabels);
            int nextDirectionIndex = EditorGUILayout.Popup("方向", directionIndex, DirectionLabels);
            int nextCount = nextTypeIndex == 2
                ? EditorGUILayout.IntSlider("カット回数", count, 2, 99)
                : 1;
            // Long の長さ。自動 = 本編既定の (回数-1)×0.7秒。手動なら拍数で直接指定できる。
            bool autoLength = note.lengthMs <= 0f;
            bool nextAutoLength = autoLength;
            float nextLengthBeats = 0f;
            if (nextTypeIndex == 2)
            {
                nextAutoLength = EditorGUILayout.Toggle("長さ自動 (回数×0.7s)", autoLength);
                float effectiveMs = note.lengthMs > 0f
                    ? note.lengthMs
                    : (Mathf.Max(2, nextCount) - 1) * SaberChartUtility.DefaultSecondsPerLongCut * 1000f;
                // テンポ地図があっても、その場所のテンポで拍数を出す。
                float shownBeats = GridBeatAt(note.time + effectiveMs) - GridBeatAt(note.time);
                using (new EditorGUI.DisabledScope(nextAutoLength))
                {
                    nextLengthBeats = EditorGUILayout.DelayedFloatField("長さ (拍)", shownBeats);
                }
                GUILayout.Label($"実効: {effectiveMs / 1000f:0.00}s", smallMutedStyle);
            }
            if (EditorGUI.EndChangeCheck())
            {
                string before = CurrentJson();
                SaberChartNote selected = note;
                bool beatEdited = !Mathf.Approximately(nextBeat, beat);
                bool timeEdited = !Mathf.Approximately(nextTime, time);
                // 触った値だけを変える。拍も時刻も変えていなければ、保存済みの拍の値を保つ。
                if (beatEdited && !timeEdited)
                    SetNoteTime(selected, TimeAtBeat(Mathf.Max(0f, nextBeat)));
                else if (timeEdited)
                    SetNoteTime(selected, Mathf.Max(0f, nextTime));
                selected.x = nextX;
                selected.y = nextY;
                selected.type = TypeValues[Mathf.Clamp(nextTypeIndex, 0, TypeValues.Length - 1)];
                selected.color = ColorValues[Mathf.Clamp(nextColorIndex, 0, ColorValues.Length - 1)];
                string nextType = TypeValues[Mathf.Clamp(nextTypeIndex, 0, TypeValues.Length - 1)];
                bool changedAwayFromDirection = nextTypeIndex != typeIndex && nextType != SaberChartUtility.TypeDirection;
                string chosenDirection = DirectionValues[Mathf.Clamp(nextDirectionIndex, 0, DirectionValues.Length - 1)];
                if (nextType == SaberChartUtility.TypeDirection && chosenDirection == SaberChartUtility.DirectionNone)
                    chosenDirection = "up";
                selected.direction = changedAwayFromDirection ? SaberChartUtility.DirectionNone : chosenDirection;
                selected.count = selected.type == SaberChartUtility.TypeLong ? Mathf.Max(2, nextCount) : 1;
                if (selected.type == SaberChartUtility.TypeLong && !nextAutoLength)
                {
                    float startBeat = GridBeatAt(selected.time);
                    selected.lengthMs = Mathf.Max(60f, GridTimeAt(startBeat + Mathf.Max(0f, nextLengthBeats)) - selected.time);
                }
                else
                {
                    selected.lengthMs = 0f; // 自動(回数×0.7s)へ戻す
                }
                SaberChartUtility.SortNotes(document);
                selectedIndex = document.notes.IndexOf(selected);
                paletteXLane = SaberChartUtility.LaneForCoordinate(selected.x, LaneCount,
                    SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax);
                paletteYLane = SaberChartUtility.LaneForCoordinate(selected.y, LaneCount,
                    SaberChartUtility.DefaultYMin, SaberChartUtility.DefaultYMax);
                if (CurrentJson() != before)
                {
                    history.Record(before);
                    MarkChanged();
                }
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("複製")) DuplicateSelected();
            GUI.backgroundColor = new Color(0.8f, 0.2f, 0.25f);
            if (GUILayout.Button("削除")) DeleteSelected();
            GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();
        }

        private void DrawSpatialPad(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.025f, 0.035f, 0.055f));
            const float gap = 2f;
            float cellWidth = (rect.width - gap * (LaneCount + 1)) / LaneCount;
            float cellHeight = (rect.height - gap * (LaneCount + 1)) / LaneCount;
            SaberChartNote selected = SelectedNote;
            int xLane = selected != null
                ? SaberChartUtility.LaneForCoordinate(selected.x, LaneCount, SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax)
                : paletteXLane;
            int yLane = selected != null
                ? SaberChartUtility.LaneForCoordinate(selected.y, LaneCount, SaberChartUtility.DefaultYMin, SaberChartUtility.DefaultYMax)
                : paletteYLane;

            Event current = Event.current;
            for (int visualRow = 0; visualRow < LaneCount; visualRow++)
            {
                int y = LaneCount - 1 - visualRow;
                for (int x = 0; x < LaneCount; x++)
                {
                    Rect cell = new Rect(
                        rect.x + gap + x * (cellWidth + gap),
                        rect.y + gap + visualRow * (cellHeight + gap),
                        cellWidth,
                        cellHeight);
                    bool active = x == xLane && y == yLane;
                    Color baseColor = active
                        ? NoteColor(selected != null ? selected.color : paletteColor)
                        : new Color(0.11f, 0.15f, 0.20f);
                    EditorGUI.DrawRect(cell, baseColor);
                    if (active) DrawOutline(cell, Color.white, 2f);

                    if (current.type == EventType.MouseDown && current.button == 0 && cell.Contains(current.mousePosition))
                    {
                        SetSpatialPosition(x, y);
                        current.Use();
                    }
                }
            }
        }

        private void SetSpatialPosition(int xLane, int yLane)
        {
            if (RecordingBusy) return;
            FinishTextEditing();
            EndNoteDrag();
            paletteXLane = xLane;
            paletteYLane = yLane;
            SaberChartNote selected = SelectedNote;
            if (selected == null) return;

            string before = CurrentJson();
            selected.x = SaberChartUtility.CoordinateForLane(xLane, LaneCount,
                SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax);
            selected.y = SaberChartUtility.CoordinateForLane(yLane, LaneCount,
                SaberChartUtility.DefaultYMin, SaberChartUtility.DefaultYMax);
            SaberChartUtility.SortNotes(document);
            selectedIndex = document.notes.IndexOf(selected);
            history.Record(before);
            MarkChanged();
        }

        private void DrawTimeline(Rect rect)
        {
            // 長いLONGや画面外の小節線を、隣のXYパッドまで描かない。
            GUI.BeginGroup(rect);
            try
            {
                rect = new Rect(Vector2.zero, rect.size);
                EditorGUI.DrawRect(rect, new Color(0.018f, 0.026f, 0.044f));
                Rect laneRect = new Rect(rect.x + TimelineGutter, rect.y, rect.width - TimelineGutter - 12f, rect.height);
                if (laneRect.width < 80f) return;

                DrawLaneBackgrounds(laneRect);
                DrawWaveform(rect, laneRect);
                DrawBeatGrid(rect, laneRect);
                DrawRangeOnTimeline(rect, laneRect);
                DrawMarkersOnTimeline(rect, laneRect);
                DrawUpperOverlay(laneRect);
                DrawNotes(laneRect);
                DrawDeriveCandidates(laneRect);
                DrawPlayhead(rect, laneRect);
                DrawSnapBadge(laneRect);
                DrawOverview(rect, Event.current);
                HandleTimelineInput(rect, laneRect, Event.current);
                DrawOutline(rect, new Color(0.12f, 0.2f, 0.28f), 1f);
            }
            finally { GUI.EndGroup(); }
        }

        private void DrawLaneBackgrounds(Rect laneRect)
        {
            float laneWidth = laneRect.width / LaneCount;
            for (int lane = 0; lane < LaneCount; lane++)
            {
                Rect background = new Rect(laneRect.x + lane * laneWidth, laneRect.y, laneWidth, laneRect.height);
                if (lane % 2 == 0) EditorGUI.DrawRect(background, new Color(1f, 1f, 1f, 0.018f));
            }

            for (int lane = 0; lane <= LaneCount; lane++)
            {
                float x = laneRect.x + lane * laneWidth;
                EditorGUI.DrawRect(new Rect(x, laneRect.y, 1f, laneRect.height), new Color(0.48f, 0.68f, 0.78f, 0.28f));
            }
        }

        private void DrawWaveform(Rect timelineRect, Rect laneRect)
        {
            if (audioClip == null || waveform.Peaks == null) return;
            float centerX = timelineRect.x + TimelineGutter * 0.54f;
            float maxHalfWidth = TimelineGutter * 0.34f;
            Color waveColor = new Color(0.20f, 0.88f, 0.96f, 0.30f);
            for (float y = timelineRect.y; y < timelineRect.yMax; y += 2f)
            {
                float beat = BeatAtY(y, timelineRect);
                float seconds = BeatToAudioSeconds(beat);
                float normalized = audioClip.length > 0f ? seconds / audioClip.length : 0f;
                float half = waveform.Sample(normalized) * maxHalfWidth;
                EditorGUI.DrawRect(new Rect(centerX - half, y, half * 2f, 1f), waveColor);
            }
        }

        private void DrawBeatGrid(Rect timelineRect, Rect laneRect)
        {
            float topBeat = BeatAtY(timelineRect.y, timelineRect);
            float bottomBeat = BeatAtY(timelineRect.yMax, timelineRect);
            float minBeat = Mathf.Max(0f, Mathf.Min(topBeat, bottomBeat));
            float maxBeat = Mathf.Max(topBeat, bottomBeat);
            float step = SaberChartUtility.SnapStep(CurrentSnap);
            if (pixelsPerBeat * step < 5f)
                step *= Mathf.Ceil(5f / Mathf.Max(0.01f, pixelsPerBeat * step));

            var meter = new ChartMeterMap(document.timeSignatures);
            foreach (double start in meter.BarStarts(meter.At(minBeat).BarStart, maxBeat))
            {
                var bar = meter.At(start);
                for (double beat = start; beat < Math.Min(bar.BarEnd, maxBeat + step) - ChartMeterMap.Epsilon; beat += step)
                {
                    float y = YForBeat((float)beat, timelineRect);
                    EditorGUI.DrawRect(new Rect(laneRect.x, y, laneRect.width, 1), new Color(.65f, .75f, .84f, .10f));
                }
                // 分母の拍と小節線は Snap の間引きとは独立して描く。
                for (int pulse = 0; pulse < bar.Numerator; pulse++)
                {
                    double beat = start + pulse * 4.0 / bar.Denominator;
                    if (beat >= bar.BarEnd - ChartMeterMap.Epsilon || beat > maxBeat) break;
                    float y = YForBeat((float)beat, timelineRect);
                    bool measure = pulse == 0;
                    EditorGUI.DrawRect(new Rect(laneRect.x, y, laneRect.width, measure ? 2 : 1),
                        measure ? new Color(.28f, .92f, 1, .72f) : new Color(.8f, .9f, 1, .28f));
                    if (measure || pixelsPerBeat * 4f / bar.Denominator >= 18)
                        GUI.Label(new Rect(timelineRect.x + 2, y - 9, TimelineGutter - 6, 18),
                            $"{bar.Measure}:{pulse + 1}", smallMutedStyle);
                }
                if (document.timeSignatures.Any(item => Math.Abs(item.beat - start) < ChartMeterMap.Epsilon))
                    GUI.Label(new Rect(laneRect.x + 4, YForBeat((float)start, timelineRect) - 19, 90, 18),
                        $"{bar.Numerator}/{bar.Denominator}", smallMutedStyle);
            }
        }

        private void DrawNotes(Rect laneRect)
        {
            if (document?.notes == null) return;
            for (int index = 0; index < document.notes.Count; index++)
            {
                SaberChartNote note = document.notes[index];
                Rect noteRect = NoteRect(note, laneRect);
                float endY = noteRect.center.y;
                if (note.type == SaberChartUtility.TypeLong)
                    endY = YForBeat(BeatAtTime(note.time + SaberChartUtility.EffectiveLongLengthMs(note)), laneRect);
                if (Mathf.Max(noteRect.yMax, endY) < laneRect.y || Mathf.Min(noteRect.y, endY) > laneRect.yMax) continue;

                Color color = NoteColor(note.color);
                if (note.type == SaberChartUtility.TypeLong)
                {
                    Rect tail = new Rect(noteRect.center.x - noteRect.width * 0.28f,
                        Mathf.Min(endY, noteRect.center.y), noteRect.width * 0.56f,
                        Mathf.Abs(endY - noteRect.center.y));
                    EditorGUI.DrawRect(tail, new Color(color.r, color.g, color.b, 0.30f));
                    DrawOutline(tail, new Color(color.r, color.g, color.b, 0.65f), 1f);
                }

                EditorGUI.DrawRect(noteRect, new Color(color.r, color.g, color.b, 0.92f));
                if (note.type == SaberChartUtility.TypeDirection)
                {
                    GUI.Label(noteRect, DirectionGlyph(note.direction), noteLabelStyle);
                }
                else if (note.type == SaberChartUtility.TypeLong)
                {
                    string longLabel = note.direction == SaberChartUtility.DirectionNone
                        ? "×" + note.count
                        : DirectionGlyph(note.direction) + "×" + note.count;
                    GUI.Label(noteRect, longLabel, noteLabelStyle);
                }
                else
                {
                    int yLane = SaberChartUtility.LaneForCoordinate(note.y, LaneCount,
                        SaberChartUtility.DefaultYMin, SaberChartUtility.DefaultYMax);
                    GUI.Label(noteRect, "Y" + (yLane + 1), noteLabelStyle);
                }

                if (index == selectedIndex)
                {
                    DrawOutline(new Rect(noteRect.x - 2f, noteRect.y - 2f, noteRect.width + 4f, noteRect.height + 4f),
                        Color.white, 2f);
                }
            }
        }

        private void DrawPlayhead(Rect timelineRect, Rect laneRect)
        {
            float y = PlayheadY(timelineRect);
            EditorGUI.DrawRect(new Rect(timelineRect.x, y - 1f, timelineRect.width, 3f), AccentColor);
            GUI.Label(
                new Rect(laneRect.x + 4f, y - 21f, 220f, 20f),
                SaberChartUtility.FormatMusicalPosition(currentBeat, document, CurrentSnap),
                new GUIStyle(smallMutedStyle) { normal = { textColor = AccentColor } });
        }

        private void HandleTimelineInput(Rect timelineRect, Rect laneRect, Event current)
        {
            if (RecordingBusy) return;
            // 範囲のドラッグは枠の外で離しても終える。
            if (HandleRangeDrag(timelineRect, laneRect, current)) return;
            if (!timelineRect.Contains(current.mousePosition))
            {
                if (current.type == EventType.MouseUp) EndNoteDrag();
                return;
            }

            if (current.type == EventType.MouseDown)
            {
                // 手描きの操作領域でも、数値欄から編集フォーカスを受け取る。
                FinishTextEditing();
                EndNoteDrag();
            }

            if (current.type == EventType.ScrollWheel)
            {
                if (current.control || current.command)
                {
                    pixelsPerBeat = Mathf.Clamp(pixelsPerBeat * (current.delta.y > 0f ? 0.88f : 1.14f), 30f, 240f);
                    SetStatus($"ズーム {pixelsPerBeat:0} px/拍");
                }
                else
                {
                    // Snap によらず1ノッチ=1拍。Shift で Snap 1つ分。
                    SeekToBeat(currentBeat + WheelSeekBeats(current.delta.y, current.shift));
                }
                current.Use();
                return;
            }

            if (current.type == EventType.MouseDown && HandleMouseDownWhilePlaying(current.button, current.mousePosition, timelineRect))
            {
                current.Use();
                return;
            }

            if (current.type == EventType.MouseDown && current.button == 1)
            {
                int hit = FindNoteAt(current.mousePosition, laneRect);
                if (hit >= 0) DeleteNoteAt(hit);
                current.Use();
                return;
            }

            if (current.type == EventType.MouseDown && current.button == 0)
            {
                int hit = FindNoteForClick(current.mousePosition, laneRect);
                if (hit >= 0 && HasRangeSelection && !current.shift) ClearRange();
                if (hit >= 0)
                {
                    selectedIndex = hit;
                    SyncPalettePositionFromSelected();
                    if (editTool == EditTool.Erase)
                    {
                        DeleteNoteAt(hit);
                    }
                    else
                    {
                        BeginNoteDrag(hit, current.mousePosition);
                    }
                }
                else if (editTool == EditTool.Draw && laneRect.Contains(current.mousePosition))
                {
                    AddNoteAtMouse(current.mousePosition, timelineRect, laneRect);
                }
                else
                {
                    selectedIndex = -1;
                    SeekToBeat(SaberChartUtility.QuantizeBeat(BeatAtY(current.mousePosition.y, timelineRect), CurrentSnap, document));
                }
                current.Use();
                return;
            }

            if (current.type == EventType.MouseDrag && draggingNote && dragNoteIndex >= 0 &&
                dragNoteIndex < document.notes.Count)
            {
                DragNoteTo(current.mousePosition, current.shift, timelineRect, laneRect);
                current.Use();
                return;
            }

            if (current.type == EventType.MouseUp && current.button == 0)
            {
                EndNoteDrag();
                current.Use();
            }
        }

        // 再生中のクリックはシークだけにする(聴きながらクリックしてノーツが増えないように)。
        private bool HandleMouseDownWhilePlaying(int button, Vector2 mouse, Rect timelineRect)
        {
            if (!isPlaying) return false;
            if (button == 0)
                SeekToBeat(SaberChartUtility.QuantizeBeat(BeatAtY(mouse.y, timelineRect), CurrentSnap, document));
            return true;
        }

        private void BeginNoteDrag(int index, Vector2 mouse)
        {
            SaberChartNote note = document.notes[index];
            draggingNote = true;
            dragNoteIndex = index;
            dragRecorded = false;
            dragStarted = false;
            dragStartMouse = mouse;
            dragOriginalTime = note.time;
            dragOriginalX = note.x;
            dragOriginalBeat = note.beat;
            dragSnapshot = CurrentJson();
        }

        private void DragNoteTo(Vector2 mouse, bool snapToGrid, Rect timelineRect, Rect laneRect)
        {
            if (!draggingNote || dragNoteIndex < 0 || dragNoteIndex >= document.notes.Count) return;
            if (!dragStarted)
            {
                // 4px までのぶれは「選んだだけ」。位置も時刻も変えない。
                if ((mouse - dragStartMouse).sqrMagnitude < DragThresholdPixels * DragThresholdPixels) return;
                dragStarted = true;
            }

            SaberChartNote note = document.notes[dragNoteIndex];
            (float time, float x) = DragTarget(mouse, snapToGrid, timelineRect, laneRect);
            if (Mathf.Approximately(time, note.time) && Mathf.Approximately(x, note.x)) return;
            if (!dragRecorded)
            {
                history.Record(dragSnapshot);
                dragRecorded = true;
            }
            note.x = x;
            if (Mathf.Approximately(time, dragOriginalTime))
            {
                // 元の位置へ戻したときは、保存されていた拍の値もそのまま戻す。
                note.time = dragOriginalTime;
                note.beat = dragOriginalBeat;
            }
            else
            {
                SetNoteTime(note, time);
            }
            paletteXLane = SaberChartUtility.LaneForCoordinate(note.x, LaneCount,
                SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax);
            // ドラッグ中にJSON化すると時刻ソートでindexが変わるため、確定までは並べ替えない。
            hasUnsavedChanges = true;
            Repaint();
        }

        /// <summary>
        /// ドラッグ先の時刻と横位置。元の値からの相対移動で、時刻は Snap の整数倍、横は列の間隔の整数倍だけ動かす。
        /// 録音した細かな時刻や位置の端数は保つ。Shift を押したときだけ、格子と列の中心へ吸い付ける。
        /// </summary>
        private (float time, float x) DragTarget(Vector2 mouse, bool snapToGrid, Rect timelineRect, Rect laneRect)
        {
            const float xMin = SaberChartUtility.DefaultXMin;
            const float xMax = SaberChartUtility.DefaultXMax;
            if (snapToGrid)
            {
                float beat = SaberChartUtility.QuantizeBeat(BeatAtY(mouse.y, timelineRect), CurrentSnap, document);
                return (TimeAtBeat(beat),
                    SaberChartUtility.CoordinateForLane(LaneAtX(mouse.x, laneRect), LaneCount, xMin, xMax));
            }

            float step = SaberChartUtility.SnapStep(CurrentSnap);
            int steps = Mathf.RoundToInt((dragStartMouse.y - mouse.y) / Mathf.Max(1f, pixelsPerBeat) / step);
            float time = steps == 0
                ? dragOriginalTime
                : Mathf.Max(0f, GridTimeAt(GridBeatAt(dragOriginalTime) + steps * step));
            float laneWidth = Mathf.Max(1f, laneRect.width / LaneCount);
            int lanes = Mathf.RoundToInt((mouse.x - dragStartMouse.x) / laneWidth);
            float spacing = (xMax - xMin) / (LaneCount - 1);
            float x = lanes == 0 ? dragOriginalX : Mathf.Clamp(dragOriginalX + lanes * spacing, xMin, xMax);
            return (time, x);
        }

        private void EndNoteDrag()
        {
            if (draggingNote && dragRecorded && dragNoteIndex >= 0 && dragNoteIndex < document.notes.Count)
            {
                SaberChartNote note = document.notes[dragNoteIndex];
                SaberChartUtility.SortNotes(document);
                selectedIndex = document.notes.IndexOf(note);
                MarkChanged();
            }
            draggingNote = false;
            dragRecorded = false;
            dragStarted = false;
            dragNoteIndex = -1;
            dragSnapshot = null;
        }

        private void AddNoteAtMouse(Vector2 mouse, Rect timelineRect, Rect laneRect)
        {
            int xLane = LaneAtX(mouse.x, laneRect);
            float x = SaberChartUtility.CoordinateForLane(xLane, LaneCount,
                SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax);
            float y = SaberChartUtility.CoordinateForLane(paletteYLane, LaneCount,
                SaberChartUtility.DefaultYMin, SaberChartUtility.DefaultYMax);
            float beat = SaberChartUtility.QuantizeBeat(BeatAtY(mouse.y, timelineRect), CurrentSnap, document);
            SaberChartNote existing = document.notes.FirstOrDefault(note =>
                Mathf.Abs(TimelineBeat(note) - beat) < 0.0001f &&
                Mathf.Abs(note.x - x) < 0.0001f && Mathf.Abs(note.y - y) < 0.0001f);
            if (existing != null)
            {
                selectedIndex = document.notes.IndexOf(existing);
                return;
            }

            string before = CurrentJson();
            var note = new SaberChartNote
            {
                beat = beat,
                time = TimeAtBeat(beat),
                x = x,
                y = y,
                type = paletteType,
                color = paletteColor,
                direction = paletteType == SaberChartUtility.TypeDirection
                    ? paletteDirection
                    : SaberChartUtility.DirectionNone,
                count = paletteType == SaberChartUtility.TypeLong ? Mathf.Max(2, paletteCount) : 1,
            };
            document.notes.Add(note);
            SaberChartUtility.SortNotes(document);
            selectedIndex = document.notes.IndexOf(note);
            paletteXLane = xLane;
            history.Record(before);
            MarkChanged();
            SetStatus($"{beat:0.###}拍に配置");
        }

        private Rect NoteRect(SaberChartNote note, Rect laneRect)
        {
            // 8列へ丸めず、実際の横位置で描く(列の間にあるノーツも見分けられるように)。
            float centerX = NoteCenterX(note.x, laneRect);
            float width = Mathf.Clamp(laneRect.width / LaneCount - 7f, 20f, 56f);
            float height = note.type == SaberChartUtility.TypeLong ? 25f : 21f;
            float centerY = YForBeat(TimelineBeat(note), laneRect);
            return new Rect(centerX - width * 0.5f, centerY - height * 0.5f, width, height);
        }

        private int FindNoteAt(Vector2 mouse, Rect laneRect)
        {
            for (int index = document.notes.Count - 1; index >= 0; index--)
            {
                Rect hit = NoteRect(document.notes[index], laneRect);
                hit.xMin -= 3f;
                hit.xMax += 3f;
                hit.yMin -= 4f;
                hit.yMax += 4f;
                if (hit.Contains(mouse)) return index;
            }
            return -1;
        }

        private void HandleKeyboardShortcuts(Event current)
        {
            // 遅れの測定中は、キーを全部測定に使う。
            if (HandleCalibrationKey(current)) return;
            // 方向キーは録音の処理より先に見る(録音中のテンキーは次に打つノーツの方向)。
            if (HandleDirectionKey(current)) return;
            if (HandleRecordingKeyboard(current)) return;
            if (current.type != EventType.KeyDown || EditorGUIUtility.editingTextField) return;
            bool action = current.control || current.command;

            if (action && current.keyCode == KeyCode.S)
            {
                SaveDocument();
                current.Use();
            }
            else if (action && current.keyCode == KeyCode.Z && current.shift)
            {
                Redo();
                current.Use();
            }
            else if (action && current.keyCode == KeyCode.Z)
            {
                Undo();
                current.Use();
            }
            else if (action && current.keyCode == KeyCode.Y)
            {
                Redo();
                current.Use();
            }
            else if (HandleSelectionOrBeatShortcut(current) || HandleRangeKeys(current))
            {
                // 「選択があれば選択、無ければ今の拍」の操作と、範囲の操作。
            }
            else if (current.keyCode == KeyCode.Space)
            {
                TogglePreview();
                current.Use();
            }
            else if (!action && (current.keyCode == KeyCode.Delete || current.keyCode == KeyCode.Backspace))
            {
                DeleteSelection();
                current.Use();
            }
            else if (HandleArrowKeys(current))
            {
                // 矢印は常にシーク、Alt+矢印は位置。
            }
            else if (current.keyCode == KeyCode.Alpha1)
            {
                editTool = EditTool.Select;
                current.Use();
            }
            else if (current.keyCode == KeyCode.Alpha2)
            {
                editTool = EditTool.Draw;
                current.Use();
            }
            else if (current.keyCode == KeyCode.Alpha3)
            {
                editTool = EditTool.Erase;
                current.Use();
            }
        }

        private void NewDocument()
        {
            EndNoteDrag();
            if (!ConfirmAbandonChanges()) return;
            StopPreview(false);
            string targetSong = songId?.Trim();
            document = NewDocumentFor(targetSong, CurrentDifficulty, out string inheritedFrom, out float displayOrigin);
            // 格子は引き継ぎ元と同じ位置に出す(ファイルの原点の値は引き継ぎ元のファイルと同じ)。
            beatZeroMs = displayOrigin;
            currentBeat = 0f;
            // Easyの新規配置は表拍から始める。既存ノーツの時刻は変えない。
            if (CurrentDifficulty == "easy") snapIndex = 0;
            selectedIndex = -1;
            // 新しい譜面の保存先は、作ったときに選んでいた曲・難易度。既存ファイルがあれば保存時に確認する。
            loadedSongId = SaberChartFileStore.IsValidSongId(targetSong, out _) ? targetSong : null;
            loadedDifficulty = loadedSongId != null ? CurrentDifficulty : null;
            confirmedOverwriteTarget = null;
            ResetDerivedViews();
            history.Clear();
            savedJson = null;
            UpdateDirtyState();
            SetStatus(inheritedFrom == null
                ? "新しい譜面を作成しました"
                : $"新しい譜面を作成しました（BPM・原点・拍子などは {inheritedFrom} から引き継ぎ）");
        }

        /// <summary>
        /// 新しい空の譜面。同じ曲の他の難易度があれば、曲の設定(BPM・OFFSET・原点・拍子・座標倍率)を引き継ぐ。
        /// ノーツ・表示レベル・制作メモは難易度ごとの値なので引き継がない。
        /// </summary>
        private static SaberChartDocument NewDocumentFor(string targetSong, string difficulty,
            out string inheritedFrom, out float displayOrigin)
        {
            inheritedFrom = null;
            displayOrigin = 0f;
            var fresh = new SaberChartDocument();
            if (!SaberChartFileStore.IsValidSongId(targetSong, out _)) return fresh;
            foreach (string source in new[] { "normal", "hard", "easy" })
            {
                if (source == difficulty) continue;
                SaberChartDocument other = SaberChartFileStore.TryLoadExact(targetSong, source);
                if (other == null) continue;
                SaberChartUtility.CopySongSettings(other, fresh);
                displayOrigin = SaberChartUtility.EstimateBeatZeroMs(other);
                inheritedFrom = DifficultyLabels[Array.IndexOf(DifficultyValues, source)];
                break;
            }
            return fresh;
        }

        private void LoadDocument()
        {
            LoadDocumentForSong(songId);
        }

        private void LoadDocumentForSong(string targetSongId)
        {
            EndNoteDrag();
            if (!ConfirmAbandonChanges()) return;
            try
            {
                // 読込に失敗した場合は、元の譜面・履歴・未保存状態・保存先を残す。
                SaberChartDocument nextDocument = SaberChartFileStore.Load(targetSongId, CurrentDifficulty, out string loadedPath);
                float nextBeatZeroMs = SaberChartUtility.EstimateBeatZeroMs(nextDocument);
                string nextSavedJson = SaberChartUtility.ToJson(nextDocument, false);
                StopPreview(false);
                document = nextDocument;
                songId = targetSongId.Trim();
                beatZeroMs = nextBeatZeroMs;
                currentBeat = 0f;
                selectedIndex = -1;
                loadedSongId = songId.Trim();
                loadedDifficulty = CurrentDifficulty;
                confirmedOverwriteTarget = null;
                ResetDerivedViews();
                history.Clear();
                savedJson = nextSavedJson;
                hasUnsavedChanges = false;
                nextAutosaveAt = 0;
                EnsureAudioForSong(false);
                CheckForDraft();
                SetStatus(loadedPath == null ? "空の譜面を開きました" : $"読込: {Path.GetFileName(loadedPath)}");
            }
            catch (Exception exception)
            {
                reportLoadError(exception.Message);
            }
        }

        /// <summary>
        /// 開いた場所(編集中の曲・難易度)へ保存する。画面上部で別の曲・難易度を選んでいても、そちらには書かない。
        /// 変更がなければファイルを書かない(開いて保存しただけで書式や値が変わらないように)。
        /// </summary>
        private bool SaveDocument()
        {
            if (RecordingBusy) StopPreview(false);
            EndNoteDrag();
            FinishDirectionPass();
            string targetSong = EditingSongId;
            string targetDifficulty = EditingDifficulty;
            if (!SaberChartFileStore.IsValidSongId(targetSong, out string reason))
            {
                EditorUtility.DisplayDialog("譜面を保存できません", reason, "OK");
                return false;
            }
            bool exists = SaberChartFileStore.ChartExists(targetSong, targetDifficulty);
            if (exists && !hasUnsavedChanges && savedJson != null)
            {
                SetStatus("変更はありません（ファイルは書き換えていません）");
                return true;
            }
            string targetLabel = DifficultyLabels[Mathf.Max(0, Array.IndexOf(DifficultyValues, targetDifficulty))];
            if (exists && savedJson == null && confirmedOverwriteTarget != TargetKey(targetSong, targetDifficulty))
            {
                // 新規の譜面で、既にある譜面を黙って置き換えない。
                if (!confirmDuplicateOverwrite($"{targetSong} / {targetLabel} は既にあります。",
                        "新しく作った譜面で、このファイルを置き換えますか？（置き換える前のファイルはバックアップされます）"))
                    return false;
            }

            try
            {
                SaberChartUtility.Normalize(document);
                string destination = SaberChartFileStore.Save(document, targetSong, targetDifficulty);
                loadedSongId = targetSong.Trim();
                loadedDifficulty = targetDifficulty;
                confirmedOverwriteTarget = null;
                savedJson = CurrentJson();
                hasUnsavedChanges = false;
                nextAutosaveAt = 0;
                settingsMismatchKey = null;
                SaberChartDrafts.DeleteFor(loadedSongId, loadedDifficulty);
                pendingDraft = null;
                if (targetDifficulty != "normal" && SaberChartFileStore.MissingNormalChart(targetSong))
                    SetStatus($"保存: {Path.GetFileName(destination)}。Normal がまだ無いため、本編の Easy / Normal は選べません（chart.json は Normal から作ります）");
                else
                    SetStatus($"保存: {Path.GetFileName(destination)}");
                return true;
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("譜面を保存できません", exception.Message, "OK");
                return false;
            }
        }

        private bool ConfirmAbandonChanges()
        {
            if (!hasUnsavedChanges) return true;
            int choice = confirmChangesDialog(
                "未保存の変更",
                $"編集中の {EditingSongId} / {EditingDifficultyLabel} を保存してから続けますか？",
                "保存",
                "キャンセル",
                "保存しない");
            if (choice == 0) return SaveDocument();
            // 「保存しない」は次の操作への許可。置き換え成功までは未保存のまま保つ。
            return choice == 2;
        }

        private void ShowSongMenu()
        {
            var menu = new GenericMenu();
            List<string> songs = SaberChartFileStore.ExistingSongIds();
            if (songs.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("曲フォルダがありません"));
            }
            else
            {
                foreach (string id in songs)
                {
                    string captured = id;
                    menu.AddItem(new GUIContent(captured), captured == songId, () =>
                    {
                        LoadSongFromMenu(captured);
                    });
                }
            }
            menu.ShowAsContext();
        }

        private void LoadSongFromMenu(string selectedSongId)
        {
            LoadDocumentForSong(selectedSongId);
        }

        private void RevealSongFolder()
        {
            string folder = SaberChartFileStore.SongFolderPath(EditingSongId);
            if (folder == null)
            {
                ShowNotification(new GUIContent("曲フォルダ名を確認してください"));
                return;
            }
            Directory.CreateDirectory(folder);
            EditorUtility.RevealInFinder(folder);
        }

        private void EnsureAudioForSong(bool notify)
        {
            AudioClip found = SaberChartFileStore.LoadAudioClip(EditingSongId);
            SetAudioClip(found);
            if (notify) SetStatus(found != null ? "曲フォルダの音源を読み込みました" : "音源が見つかりません");
        }

        private void ImportAudio()
        {
            if (!SaberChartFileStore.IsValidSongId(EditingSongId, out string reason))
            {
                EditorUtility.DisplayDialog("音源を取り込めません", reason, "OK");
                return;
            }

            string source = EditorUtility.OpenFilePanelWithFilters(
                "音源を選択",
                string.Empty,
                new[] { "対応音源", "ogg,wav,mp3", "すべてのファイル", "*" });
            if (string.IsNullOrEmpty(source)) return;
            bool replace = EditorUtility.DisplayDialog(
                "音源を取り込む",
                "曲フォルダ内の既存 audio.ogg / wav / mp3 を整理し、この音源を使用しますか？",
                "取り込む",
                "キャンセル");
            if (!replace) return;

            try
            {
                SetAudioClip(SaberChartFileStore.ImportAudio(source, EditingSongId, true));
                SetStatus("音源を取り込みました");
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("音源を取り込めません", exception.Message, "OK");
            }
        }

        private void HandleAudioClipSelection(AudioClip clip)
        {
            if (clip == null || SaberChartFileStore.IsAudioClipForSong(clip, EditingSongId))
            {
                SetAudioClip(clip);
                return;
            }

            int choice = EditorUtility.DisplayDialogComplex(
                "本編用音源へ取り込みますか？",
                "選択したAudioClipは現在の曲フォルダの audio.ogg / wav / mp3 ではありません。\n" +
                "本編と同じ音源で確認するには、曲フォルダへ取り込んでください。",
                "取り込む",
                "キャンセル",
                "プレビューのみ");
            if (choice == 1) return;
            if (choice == 2)
            {
                SetAudioClip(clip);
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(clip);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string sourcePath = string.IsNullOrEmpty(assetPath)
                ? null
                : Path.GetFullPath(Path.Combine(projectRoot, assetPath));
            try
            {
                SetAudioClip(SaberChartFileStore.ImportAudio(sourcePath, EditingSongId, true));
                SetStatus("選択した音源を曲フォルダへ取り込みました");
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("音源を取り込めません", exception.Message, "OK");
            }
        }

        private void SetAudioClip(AudioClip clip)
        {
            if (audioClip == clip && waveform.Clip == clip) return;
            StopPreview(false);
            DisposeClickMix();
            audioClip = clip;
            waveform.Build(clip);
            Repaint();
        }

        private void TogglePreview()
        {
            if (RecordingBusy) { StopPreview(false); return; }
            ClearRecordingStepInput();
            if (isPlaying)
            {
                UpdatePlaybackPosition();
                StopPreview(false);
                return;
            }
            if (audioClip == null)
            {
                ShowNotification(new GUIContent("先に音源を選んでください"));
                return;
            }

            float seconds = Mathf.Clamp(BeatToAudioSeconds(currentBeat), 0f, Mathf.Max(0f, audioClip.length - 0.01f));
            playingClip = PlaybackClipFor(audioClip);
            suppressClicksForNextPlay = false;
            if (!SaberChartAudioPreview.Play(playingClip, seconds))
            {
                EditorUtility.DisplayDialog("音源を再生できません", SaberChartAudioPreview.LastError ?? "不明なエラー", "OK");
                return;
            }
            playbackAudioStartSeconds = seconds;
            isPlaying = true;
            showPlaybackPreview = true;
            Repaint();
        }

        private void StopPreview(bool resetToStart)
        {
            // 音声時計がまだ有効な間に、押し続けているノーツと録音の履歴を確定する。
            FinishRecording();
            FinishDirectionPass();
            SaberChartAudioPreview.Stop();
            DisposeCountIn();
            isPlaying = false;
            playingClip = null;
            if (resetToStart) currentBeat = 0f;
            Repaint();
        }

        private void RestartPreviewIfPlaying()
        {
            if (!isPlaying) return;
            SaberChartAudioPreview.Stop();
            isPlaying = false;
            TogglePreview();
        }

        private void EditorTick()
        {
            if (RecordingBusy && EditorApplication.isPlayingOrWillChangePlaymode)
            {
                StopPreview(false);
                return;
            }
            TickAutosave();
            TickLatencyCalibration();
            if (countingIn)
            {
                if (EditorApplication.timeSinceStartup >= countInEndsAt) BeginRecordingSong();
                Repaint();
                return;
            }
            if (!isPlaying) return;
            UpdatePlaybackPosition();
            Repaint();
        }

        private void UpdatePlaybackPosition()
        {
            if (!isPlaying) return;
            if (!SaberChartAudioPreview.TryGetPosition(playingClip ?? audioClip, out float audioSeconds))
            {
                StopPreview(false);
                return;
            }
            if (recorder != null) lastRecordingSeconds = audioSeconds;
            // 個人の表示補正を譜面や波形に焼き込まず、試聴中のカーソルだけへ適用する。
            if (useGameTiming) audioSeconds -= GameSession.JudgmentOffsetMs / 1000f;
            audioSeconds = Mathf.Max(playbackAudioStartSeconds, audioSeconds);
            currentBeat = BeatAtTime(audioSeconds * 1000f - document.offsetMs);
        }

        private void SeekToBeat(float beat)
        {
            if (RecordingBusy) StopPreview(false);
            ClearRecordingStepInput();
            currentBeat = Mathf.Clamp(beat, 0f, MaxBeat());
            if (isPlaying) RestartPreviewIfPlaying();
            Repaint();
        }

        private float BeatToAudioSeconds(float beat)
        {
            return (TimeAtBeat(beat) + document.offsetMs) / 1000f;
        }

        private float MaxBeat()
        {
            float notesMax = document?.notes != null && document.notes.Count > 0
                ? document.notes.Max(TimelineBeat) + 8f
                : 32f;
            if (audioClip == null) return Mathf.Max(32f, notesMax);
            float audioBeat = BeatAtTime(audioClip.length * 1000f - document.offsetMs);
            return Mathf.Max(4f, notesMax, audioBeat);
        }

        // 譜面の時刻(ms)と編集用の格子の拍の変換。拍は 0 未満にしない。
        private float TimeAtBeat(float beat) => Mathf.Max(0f, GridTimeAt(Mathf.Max(0f, beat)));
        private float BeatAtTime(float timeMs) => Mathf.Max(0f, GridBeatAt(timeMs));

        // 格子の上の変換(負の拍も扱う)。ドラッグなど、元の値からの相対移動に使う。
        private float GridTimeAt(float beat) => Grid.TimeAt(beat);
        private float GridBeatAt(float timeMs) => Grid.BeatAt(timeMs);

        /// <summary>時刻を変えたノーツだけ、拍の値を今の格子に合わせ直す。触っていないノーツの拍の値は保つ。</summary>
        private void SetNoteTime(SaberChartNote note, float timeMs)
        {
            note.time = Mathf.Max(0f, timeMs);
            note.beat = BeatAtTime(note.time);
        }

        private void Undo()
        {
            EndNoteDrag();
            FinishDirectionPass();
            if (RecordingBusy) StopPreview(false);
            if (!history.CanUndo) return;
            StopPreview(false);
            bool retainReview = recordReviewDocument == document;
            document = history.Undo(document);
            if (retainReview) recordReviewDocument = document;
            beatZeroMs = SaberChartUtility.EstimateBeatZeroMs(document);
            selectedIndex = -1;
            UpdateDirtyState();
            SetStatus("元に戻しました");
        }

        private void Redo()
        {
            EndNoteDrag();
            FinishDirectionPass();
            if (RecordingBusy) StopPreview(false);
            if (!history.CanRedo) return;
            StopPreview(false);
            bool retainReview = recordReviewDocument == document;
            document = history.Redo(document);
            if (retainReview) recordReviewDocument = document;
            beatZeroMs = SaberChartUtility.EstimateBeatZeroMs(document);
            selectedIndex = -1;
            UpdateDirtyState();
            SetStatus("やり直しました");
        }

        private void DeleteSelected()
        {
            EndNoteDrag();
            if (SelectedNote == null) return;
            DeleteNoteAt(selectedIndex);
        }

        private void DeleteNoteAt(int index)
        {
            if (index < 0 || index >= document.notes.Count) return;
            string before = CurrentJson();
            document.notes.RemoveAt(index);
            selectedIndex = -1;
            history.Record(before);
            MarkChanged();
            SetStatus("ノーツを削除しました");
        }

        private void DuplicateSelected()
        {
            EndNoteDrag();
            SaberChartNote selected = SelectedNote;
            if (selected == null) return;
            string before = CurrentJson();
            SaberChartNote copy = selected.Clone();
            float step = SaberChartUtility.SnapStep(CurrentSnap);
            copy.beat = TimelineBeat(selected);
            do
            {
                copy.beat += step;
            }
            while (document.notes.Any(note =>
                       Mathf.Abs(TimelineBeat(note) - copy.beat) < 0.0001f &&
                       Mathf.Abs(note.x - copy.x) < 0.0001f &&
                       Mathf.Abs(note.y - copy.y) < 0.0001f));
            copy.time = TimeAtBeat(copy.beat);
            document.notes.Add(copy);
            SaberChartUtility.SortNotes(document);
            selectedIndex = document.notes.IndexOf(copy);
            history.Record(before);
            MarkChanged();
            SetStatus("ノーツを複製しました");
        }

        // 本編はファイルを読むので、変更があるときだけ保存する。開いて試すだけなら書き換えない。
        private bool SaveForTestIfNeeded()
        {
            if (!hasUnsavedChanges && SaberChartFileStore.ChartExists(EditingSongId, EditingDifficulty)) return true;
            return SaveDocument();
        }

        private void TestInGame()
        {
            if (!SaveForTestIfNeeded()) return;
            string testSong = EditingSongId;
            AudioClip packagedClip = SaberChartFileStore.LoadAudioClip(testSong);
            if (packagedClip == null)
            {
                bool continueSilent = EditorUtility.DisplayDialog(
                    "本編用音源がありません",
                    "曲フォルダに audio.ogg / wav / mp3 がないため、無音でテストしますか？",
                    "無音で続ける",
                    "キャンセル");
                if (!continueSilent) return;
            }
            else if (audioClip != null && audioClip != packagedClip)
            {
                bool continueWithPackaged = EditorUtility.DisplayDialog(
                    "プレビュー音源と本編音源が異なります",
                    "本編では曲フォルダ内の音源が再生されます。その音源でテストを続けますか？",
                    "続ける",
                    "キャンセル");
                if (!continueWithPackaged) return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            const string gameScenePath = "Assets/Scenes/Game.unity";
            if (!File.Exists(gameScenePath))
            {
                EditorUtility.DisplayDialog("本編を開けません", gameScenePath + " が見つかりません。", "OK");
                return;
            }

            SaberChartTestPlayBridge.Queue(testSong.Trim(), EditingDifficultyLabel);
            EditorSceneManager.OpenScene(gameScenePath);
            EditorApplication.EnterPlaymode();
        }

        private void DrawValidationSummary()
        {
            List<string> warnings = ValidationWarnings();
            if (warnings.Count == 0)
            {
                EditorGUILayout.HelpBox("保存形式チェック: 問題なし", MessageType.Info);
                return;
            }

            string message = string.Join("\n", warnings.Take(4).Select(warning => "• " + warning));
            if (warnings.Count > 4) message += $"\n• ほか {warnings.Count - 4} 件";
            EditorGUILayout.HelpBox(message, MessageType.Warning);
        }

        private List<string> ValidationWarnings()
        {
            var warnings = new List<string>();
            int outside = document.notes.Count(note =>
                note.x * document.coordScale < SaberChartUtility.DefaultXMin ||
                note.x * document.coordScale > SaberChartUtility.DefaultXMax ||
                note.y * document.coordScale < SaberChartUtility.DefaultYMin ||
                note.y * document.coordScale > SaberChartUtility.DefaultYMax);
            if (outside > 0) warnings.Add($"推奨XY範囲外のノーツ: {outside}個");

            int beforeStart = document.notes.Count(note => note.time + document.offsetMs < 0f);
            if (beforeStart > 0) warnings.Add($"実効時刻が曲開始前のノーツ: {beforeStart}個");

            int missingDirection = document.notes.Count(note =>
                note.type == SaberChartUtility.TypeDirection && note.direction == SaberChartUtility.DirectionNone);
            if (missingDirection > 0) warnings.Add($"方向未指定のDirection: {missingDirection}個");

            int duplicates = 0;
            var keys = new HashSet<string>();
            foreach (SaberChartNote note in document.notes)
            {
                string key = $"{Mathf.RoundToInt(note.time)}:{note.x:F3}:{note.y:F3}";
                if (!keys.Add(key)) duplicates++;
            }
            if (duplicates > 0) warnings.Add($"同時刻・同位置の重複: {duplicates}個");

            AudioClip packagedClip = SaberChartFileStore.LoadAudioClip(EditingSongId);
            if (packagedClip == null)
            {
                warnings.Add("曲フォルダに本編用音源がありません");
            }
            else
            {
                if (audioClip != null && audioClip != packagedClip)
                    warnings.Add("プレビュー音源と本編用音源が異なります");
                float audioEndMs = packagedClip.length * 1000f;
                int beyond = document.notes.Count(note =>
                    note.time + document.offsetMs + SaberChartUtility.EffectiveLongLengthMs(note) > audioEndMs);
                if (beyond > 0) warnings.Add($"音源末尾を越えるノーツ/Long: {beyond}個");
            }
            return warnings;
        }

        private void DrawFooter(Rect rect)
        {
            EditorGUI.DrawRect(rect, HeaderColor);
            string visibleStatus = EditorApplication.timeSinceStartup <= statusUntil ? statusMessage : "準備完了";
            GUI.Label(new Rect(rect.x + 10f, rect.y + 4f, rect.width * 0.30f, 20f), visibleStatus, smallMutedStyle);
            // モードごとに、いま効くキーだけを出す。
            GUI.Label(
                new Rect(rect.x + rect.width * 0.30f, rect.y + 4f, rect.width * 0.69f - 10f, 20f),
                FooterKeyGuide(),
                new GUIStyle(smallMutedStyle) { alignment = TextAnchor.MiddleRight });
        }

        private void MarkChanged()
        {
            UpdateDirtyState();
            Repaint();
        }

        private static void FinishTextEditing()
        {
            GUIUtility.keyboardControl = 0;
            EditorGUIUtility.editingTextField = false;
        }

        private void UpdateDirtyState()
        {
            hasUnsavedChanges = string.IsNullOrEmpty(savedJson) || CurrentJson() != savedJson;
            if (!hasUnsavedChanges) nextAutosaveAt = 0;
            else if (nextAutosaveAt <= 0) nextAutosaveAt = EditorApplication.timeSinceStartup + AutosaveIntervalSeconds;
        }

        // 未保存の間だけ、一定間隔で下書きを残す。録音中・ドラッグ中は作業を止めないよう後回しにする。
        private void TickAutosave()
        {
            if (!hasUnsavedChanges || nextAutosaveAt <= 0 || RecordingBusy || draggingNote) return;
            if (EditorApplication.timeSinceStartup < nextAutosaveAt) return;
            WriteDraftNow();
        }

        private bool WriteDraftNow()
        {
            nextAutosaveAt = EditorApplication.timeSinceStartup + AutosaveIntervalSeconds;
            if (!SaberChartFileStore.IsValidSongId(EditingSongId, out _)) return false;
            try
            {
                SaberChartDrafts.Write(EditingSongId, EditingDifficulty, document);
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // 下書きの失敗で作業を止めない。保存の操作はいつもどおりできる。
                Debug.LogWarning("譜面の下書きを保存できませんでした: " + exception.Message);
                return false;
            }
        }

        /// <summary>
        /// 開いた譜面より新しい下書きがあれば、復元を案内する(Unity が落ちた後など)。
        /// 編集中に未保存の変更があるときは案内しない(今の作業を優先する)。
        /// </summary>
        private void CheckForDraft()
        {
            pendingDraft = null;
            if (hasUnsavedChanges || !SaberChartFileStore.IsValidSongId(EditingSongId, out _)) return;
            SaberChartDrafts.Draft draft = SaberChartDrafts.LatestFor(EditingSongId, EditingDifficulty, out _);
            if (draft == null) return;
            string chartPath = SaberChartFileStore.ChartPath(EditingSongId, EditingDifficulty);
            if (!string.IsNullOrEmpty(chartPath) && File.Exists(chartPath) &&
                File.GetLastWriteTimeUtc(chartPath) >= draft.SavedAtUtc) return;
            if (draft.document == CurrentJson()) return;
            pendingDraft = draft;
        }

        private void DrawDraftBanner()
        {
            if (pendingDraft == null) return;
            DateTime local = pendingDraft.SavedAtUtc.ToLocalTime();
            EditorGUILayout.HelpBox($"保存していない下書きがあります（{local:M/d HH:mm}・{pendingDraft.noteCount}ノーツ）。\n" +
                                    "Unity が終了する前の編集かもしれません。", MessageType.Warning);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("下書きを開く")) RestoreDraft();
            if (GUILayout.Button("下書きを捨てる")) DiscardDraft();
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);
        }

        private void RestoreDraft()
        {
            if (pendingDraft == null) return;
            EndNoteDrag();
            StopPreview(false);
            SaberChartDocument restored;
            try { restored = SaberChartUtility.FromJson(pendingDraft.document); }
            catch (FormatException exception)
            {
                EditorUtility.DisplayDialog("下書きを開けません", exception.Message, "OK");
                DiscardDraft();
                return;
            }
            string before = CurrentJson();
            document = restored;
            beatZeroMs = SaberChartUtility.EstimateBeatZeroMs(document);
            selectedIndex = -1;
            // 下書きの前の状態へ Undo で戻れるようにする。
            history.Record(before);
            pendingDraft = null;
            UpdateDirtyState();
            SetStatus("下書きを開きました。保存するまでファイルは変わりません");
        }

        private void DiscardDraft()
        {
            if (pendingDraft != null) SaberChartDrafts.DeleteFor(pendingDraft.songId, pendingDraft.difficulty);
            pendingDraft = null;
            Repaint();
        }

        private string CurrentJson()
        {
            return SaberChartUtility.ToJson(document, false);
        }

        private float TimelineBeat(SaberChartNote note)
        {
            return note == null
                ? 0f
                : BeatAtTime(note.time);
        }

        private void SyncPalettePositionFromSelected()
        {
            SaberChartNote selected = SelectedNote;
            if (selected == null) return;
            paletteXLane = SaberChartUtility.LaneForCoordinate(selected.x, LaneCount,
                SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax);
            paletteYLane = SaberChartUtility.LaneForCoordinate(selected.y, LaneCount,
                SaberChartUtility.DefaultYMin, SaberChartUtility.DefaultYMax);
        }

        private int LaneAtX(float x, Rect laneRect)
        {
            float normalized = Mathf.InverseLerp(laneRect.x, laneRect.xMax, x);
            return Mathf.Clamp(Mathf.FloorToInt(normalized * LaneCount), 0, LaneCount - 1);
        }

        private float PlayheadY(Rect timelineRect)
        {
            return timelineRect.y + timelineRect.height * 0.72f;
        }

        private float BeatAtY(float y, Rect timelineRect)
        {
            return currentBeat + (PlayheadY(timelineRect) - y) / Mathf.Max(1f, pixelsPerBeat);
        }

        private float YForBeat(float beat, Rect timelineRect)
        {
            return PlayheadY(timelineRect) - (beat - currentBeat) * pixelsPerBeat;
        }

        private void SetStatus(string message)
        {
            statusMessage = message;
            statusUntil = EditorApplication.timeSinceStartup + 3.0;
            Repaint();
        }

        private void DrawToolToggle(EditTool tool, string label)
        {
            bool active = editTool == tool;
            Color old = GUI.backgroundColor;
            if (active) GUI.backgroundColor = AccentColor;
            if (GUILayout.Button(label, GUILayout.Height(28f))) editTool = tool;
            GUI.backgroundColor = old;
        }

        private bool DrawChoiceButton(string label, bool active, Color tint, float height = 30f)
        {
            Color old = GUI.backgroundColor;
            GUI.backgroundColor = active ? tint : new Color(0.33f, 0.38f, 0.45f);
            bool clicked = GUILayout.Button(label, GUILayout.Height(height));
            GUI.backgroundColor = old;
            return clicked;
        }

        private void SectionLabel(string label)
        {
            GUILayout.Label(label, sectionStyle);
        }

        private static Color NoteColor(string color)
        {
            switch (color)
            {
                case SaberChartUtility.ColorRed: return RedColor;
                case SaberChartUtility.ColorBlue: return BlueColor;
                case SaberChartUtility.ColorGold: return GoldColor;
                default: return new Color(0.75f, 0.82f, 0.88f);
            }
        }

        private static Color NoteTypeColor(string type)
        {
            switch (type)
            {
                case SaberChartUtility.TypeDirection: return new Color(0.72f, 0.4f, 1f);
                case SaberChartUtility.TypeLong: return new Color(0.25f, 1f, 0.68f);
                default: return AccentColor;
            }
        }

        private static string DirectionGlyph(string direction)
        {
            int index = Array.IndexOf(DirectionValues, direction);
            return index >= 0 ? DirectionLabels[index] : "・";
        }

        private static void DrawOutline(Rect rect, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private void EnsureStyles()
        {
            if (panelStyle != null) return;
            panelStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTexture(PanelColor) },
                border = new RectOffset(1, 1, 1, 1),
            };
            headerStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTexture(HeaderColor) },
                border = new RectOffset(0, 0, 0, 1),
            };
            sectionStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                normal = { textColor = AccentColor },
                margin = new RectOffset(1, 1, 3, 4),
            };
            titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 17,
                normal = { textColor = Color.white },
            };
            smallMutedStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = MutedTextColor },
            };
            centeredSmallStyle = new GUIStyle(smallMutedStyle)
            {
                alignment = TextAnchor.MiddleCenter,
            };
            noteLabelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                normal = { textColor = Color.white },
            };
        }

        private static Texture2D MakeTexture(Color color)
        {
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private void LoadPreferences()
        {
            songId = EditorPrefs.GetString(PrefPrefix + "SongId", songId);
            difficultyIndex = EditorPrefs.GetInt(PrefPrefix + "Difficulty", difficultyIndex);
            snapIndex = EditorPrefs.GetInt(PrefPrefix + "Snap", snapIndex);
            pixelsPerBeat = EditorPrefs.GetFloat(PrefPrefix + "Zoom", pixelsPerBeat);
            difficultyIndex = Mathf.Clamp(difficultyIndex, 0, DifficultyValues.Length - 1);
            snapIndex = Mathf.Clamp(snapIndex, 0, SnapDenominators.Length - 1);
            pixelsPerBeat = Mathf.Clamp(pixelsPerBeat, 30f, 240f);
        }

        private void SavePreferences()
        {
            EditorPrefs.SetString(PrefPrefix + "SongId", songId ?? string.Empty);
            EditorPrefs.SetInt(PrefPrefix + "Difficulty", difficultyIndex);
            EditorPrefs.SetInt(PrefPrefix + "Snap", snapIndex);
            EditorPrefs.SetFloat(PrefPrefix + "Zoom", pixelsPerBeat);
        }
    }

    /// <summary>
    /// Play開始時のDomain Reloadをまたいでテスト対象曲をGameSessionへ渡す。
    /// SessionStateはEditorセッション内だけに残り、ビルドや通常プレイには混入しない。
    /// </summary>
    [InitializeOnLoad]
    internal static class SaberChartTestPlayBridge
    {
        private const string PendingKey = "3DSaber.ChartEditor.TestPlay.Pending";
        private const string SongKey = "3DSaber.ChartEditor.TestPlay.Song";
        private const string DifficultyKey = "3DSaber.ChartEditor.TestPlay.Difficulty";

        static SaberChartTestPlayBridge()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static void Queue(string songId, string difficulty)
        {
            SessionState.SetString(SongKey, songId);
            SessionState.SetString(DifficultyKey, difficulty);
            SessionState.SetBool(PendingKey, true);

            // Domain Reloadを無効にしている設定でも同じ値で開始できるよう、先にも設定する。
            Apply(songId, difficulty);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(PendingKey, false)) return;
            Apply(
                SessionState.GetString(SongKey, string.Empty),
                SessionState.GetString(DifficultyKey, "Normal"));
            SessionState.EraseBool(PendingKey);
            SessionState.EraseString(SongKey);
            SessionState.EraseString(DifficultyKey);
        }

        private static void Apply(string songId, string difficulty)
        {
            GameSession.SelectedSongId = songId;
            // 選曲画面から始めたときと同じ曲名(stage.json の表示名)を出す。
            GameSession.SelectedSongTitle = SongSelectController.DisplaySongTitle(songId);
            GameSession.SelectedDifficulty = difficulty;
            GameSession.IsCalibrationMode = false;
        }
    }
}
