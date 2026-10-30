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

        private const float HeaderHeight = 44f;
        private const float TransportHeight = 68f;
        private const float DocumentPickerHeight = 90f;
        private const float FooterHeight = 28f;
        private const float PanelGap = 7f;
        private const float TimelineGutter = 62f;
        private const int LaneCount = SaberChartUtility.DefaultLaneCount;

        private const string PrefPrefix = "3DSaber.ChartEditor.";
        private static readonly int[] SnapDenominators = { 4, 8, 12, 16, 24, 32 };
        private static readonly string[] SnapLabels = { "4分（表拍）", "8分", "12分", "16分", "24分", "32分" };
        private static readonly string[] DifficultyValues = { "easy", "normal", "hard" };
        private static readonly string[] DifficultyLabels = { "Easy", "Normal", "Hard" };
        // 「開く・作る」の選択とヘッダーの札で、E N H の次の番号はその曲の裏譜面(SaberChartSecrets)。
        private const int SecretDifficultyIndex = 3;
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
        // 金は「両手で同時に」ではなく「どちらの手でも」切れる(SaberHandHelper.FromColor)。
        private static readonly string[] ColorLabels = { "赤 / 右手", "青 / 左手", "金 / どちらでも", "自由" };
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
        // 格子の分解能とマウス操作の吸着は別。既存のSnap設定があっても自由配置から始める。
        [SerializeField] private bool placementSnap;
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
        // 今の譜面を読み込んだ裏譜面(曲/難易度。読み込みに成功したときだけ決まる)。裏譜面へはこれと同じ所にだけ保存する。
        [SerializeField] private string secretSourceKey;
        [SerializeField] private string savedJson;
        [SerializeField] private bool documentInitialized;
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
        // プレイ画面を本編に近づける表示(本編のカメラ、着地の枠、本編の小節線、当たりの円、Perfect で切る)。
        [SerializeField] private bool previewGameCamera = true;
        [SerializeField] private bool previewLandingFrame = true;
        [SerializeField] private bool previewBarLines = true;
        [SerializeField] private bool previewHitRadius;
        [SerializeField] private bool previewPerfectCuts = true;
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
        // 裏譜面の番号で、選んだ曲に裏譜面が無いときは Normal。
        private string CurrentDifficulty => DifficultyFor(songId, difficultyIndex) ?? DifficultyValues[1];
        // 保存先は「開いた場所」。画面上部の選択は、次に開く・作る先を選ぶだけ。
        private string EditingSongId => !string.IsNullOrEmpty(loadedSongId) ? loadedSongId : songId?.Trim();
        private string EditingDifficulty => !string.IsNullOrEmpty(loadedDifficulty) ? loadedDifficulty : CurrentDifficulty;
        private int EditingDifficultyIndex => DifficultyIndexOf(EditingDifficulty);
        private string EditingDifficultyLabel => DifficultyLabelFor(EditingDifficulty);
        // 裏譜面はノーツの配置を直すだけにする(新規・複製・難易度の派生・曲の設定の写しには使わない)。
        private bool EditingIsSecret => SaberChartSecrets.IsSecret(EditingDifficulty);
        // 本編テストで GameSession.SelectedDifficulty に入れる名前(裏譜面は表の文字。例 "R")。
        private string EditingGameDifficulty => EditingIsSecret ? SaberChartSecrets.Letter(EditingDifficulty) : EditingDifficultyLabel;
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
            // マウスを乗せたノーツの中身を出すため(変わったときだけ描き直す)。
            wantsMouseMove = true;
            wantsMouseEnterLeaveWindow = true;
            saveChangesMessage = "譜面または曲情報に未保存の変更があります。保存しますか？";
            document ??= new SaberChartDocument();
            SaberChartUtility.Normalize(document);
            if (!documentInitialized) savedJson ??= SaberChartUtility.ToJson(document, false);
            LoadPreferences();
            LoadWorkspacePreferences();
            RestoreLastDocument();
            RestoreSongInfoDraft();
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
            CacheSongInfoDraft();
            SavePreferences();
            SaveWorkspacePreferences();
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
            if (songInfo != null) LoadSongInfo(songInfoSong);
            CacheSongInfoDraft();
            hasUnsavedChanges = false;
            base.DiscardChanges();
        }

        private void OnGUI()
        {
            EnsureStyles();
            HandleKeyboardShortcuts(Event.current);
            RevealSelectedNote();
            EditorGUI.DrawRect(new Rect(Vector2.zero, position.size), BackgroundColor);

            WorkspaceRects layout = CalculateWorkspace(position.size);
            DrawWorkspaceHeader(layout.header);
            DrawTransportBar(layout.transport);
            if (showDocumentPicker) DrawDocumentPicker(layout.picker);
            if (showPalette)
            {
                using (new EditorGUI.DisabledScope(RecordingBusy)) DrawLeftPanel(layout.left);
            }
            DrawCenterPanel(layout.center);
            if (showInspector) DrawRightPanel(layout.right);
            DrawPanelResizers(layout);
            DrawFooter(layout.footer);
            CacheSongInfoDraft();
            UpdateDirtyStateCheaply();
            if (saveAfterTextCommit && Event.current.type == EventType.Repaint && !TextControlHasFocus())
            {
                saveAfterTextCommit = false;
                EditorApplication.delayCall += () => { if (this != null) SaveDocument(); };
            }
        }

        // 入力欄で Ctrl/Cmd+S を押したとき、欄の値が確定してから保存する。
        private bool saveAfterTextCommit;

        // 開いている譜面が変わったら、上の難易度・候補・設定の比較を読み直す。
        private void ResetDerivedViews()
        {
            deriveCandidates = null;
            upperDocumentKey = null;
            settingsMismatchKey = null;
            overviewSectionsSong = null;
            pendingDraft = null;
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
        private bool DuplicateToSelection()
        {
            EndNoteDrag();
            string targetSong = songId?.Trim();
            string targetDifficulty = CurrentDifficulty;
            // 裏譜面から表の譜面へも、表の譜面から裏譜面へも複製しない。
            if (EditingIsSecret || SaberChartSecrets.IsSecret(targetDifficulty))
            {
                SetStatus(SecretDuplicateReason);
                return false;
            }
            if (!SaberChartFileStore.IsValidSongId(targetSong, out string reason))
            {
                EditorUtility.DisplayDialog("複製できません", reason, "OK");
                return false;
            }
            if (hasUnsavedChanges)
            {
                int choice = confirmChangesDialog(
                    "未保存の変更",
                    $"編集中の {EditingSongId} / {EditingDifficultyLabel} を保存してから複製しますか？",
                    "保存して複製",
                    "キャンセル",
                    "保存せずに複製");
                if (choice == 1) return false;
                if (choice == 0 && !SaveDocument()) return false;
            }
            string targetLabel = DifficultyLabelFor(targetDifficulty);
            SaberChartDocument existing = SaberChartFileStore.TryLoadExact(targetSong, targetDifficulty);
            if (SaberChartFileStore.ChartExists(targetSong, targetDifficulty) &&
                !confirmDuplicateOverwrite($"{targetSong} / {targetLabel} は既にあります。",
                    "複製した譜面を保存すると、このファイルを置き換えます（保存前のファイルはバックアップされます）。続けますか？"))
                return false;

            SaberChartDocument copy = SaberChartUtility.Clone(document);
            // 表示レベルと制作メモは難易度ごとの値。複製先に既存の譜面があればその値を引き継ぐ。
            copy.displayLevel = existing?.displayLevel ?? 0;
            copy.extraFields = existing?.extraFields ?? new List<SaberChartExtraField>();
            StopPreview(false);
            document = copy;
            loadedSongId = targetSong;
            loadedDifficulty = targetDifficulty;
            confirmedOverwriteTarget = TargetKey(targetSong, targetDifficulty);
            secretSourceKey = null;
            RememberDiskState(targetSong, targetDifficulty);
            persistentWarning = null;
            ResetDerivedViews();
            selectedIndex = -1;
            history.Clear();
            savedJson = null;
            UpdateDirtyState();
            EnsureAudioForSong(false);
            SetStatus($"{targetSong} / {targetLabel} として開きました。保存するとファイルを書きます");
            return true;
        }

        private Func<string, string, bool> confirmDuplicateOverwrite = (title, message) =>
            EditorUtility.DisplayDialog(title, message, "続ける", "キャンセル");

        private static string TargetKey(string song, string difficulty) =>
            (song ?? string.Empty).Trim().ToLowerInvariant() + "/" + (difficulty ?? string.Empty).Trim().ToLowerInvariant();

        // 選択の番号の難易度。裏譜面の番号は、その曲に裏譜面のファイルがあるときだけ(無ければ null)。
        private static string DifficultyFor(string song, int index) =>
            index == SecretDifficultyIndex
                ? SaberChartSecrets.Value(SaberChartSecrets.For(song))
                : DifficultyValues[Mathf.Clamp(index, 0, DifficultyValues.Length - 1)];

        private static int DifficultyIndexOf(string difficulty)
        {
            int index = Array.IndexOf(DifficultyValues, difficulty);
            return index >= 0 ? index : SaberChartSecrets.IsSecret(difficulty) ? SecretDifficultyIndex : 0;
        }

        private static string DifficultyLabelFor(string difficulty)
        {
            int index = Array.IndexOf(DifficultyValues, difficulty);
            if (index >= 0) return DifficultyLabels[index];
            return SaberChartSecrets.IsSecret(difficulty) ? SaberChartSecrets.Label(difficulty) : DifficultyLabels[0];
        }

        // 裏譜面で使えない操作の理由(ボタンのツールチップと、止めたときの知らせ)。
        private const string SecretNewReason = "裏譜面は生成の道具（gen_*.py）で作ります。エディターでは開いて配置を直すだけです";
        private const string SecretDuplicateReason = "裏譜面は複製に使えません（仕掛けの項目が表の譜面へ写らないように。裏譜面は生成の道具で作ります）";
        private const string SecretSettingsReason = "裏譜面の曲の設定は道具で決めています";

        // 裏譜面の曲の設定(BPM・OFFSET・グリッド原点・座標倍率・拍子・テンポ地図・拍の数え直し・ほかの難易度との写し)は変えない。
        private bool SongSettingsLocked()
        {
            if (!EditingIsSecret) return false;
            SetStatus(SecretSettingsReason + "（ここでは変えません）");
            return true;
        }

        private string SecretOpenedMessage =>
            $"裏譜面 {SaberChartSecrets.Letter(EditingDifficulty)} を開きました。ノーツごとの仕掛け（鏡・風など）はそのまま残します。新しく置いたノーツには付きません";

        private void DrawLeftPanel(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, panelStyle);
            GUILayout.BeginArea(new Rect(rect.x + 10f, rect.y + 10f, rect.width - 20f, rect.height - 20f));
            // スクロール内容の最小幅でパレットが横にはみ出さないよう、縦スクロール用の幅を先に確保する。
            leftScroll.x = 0f;
            leftScroll = GUILayout.BeginScrollView(leftScroll, false, true);
            float contentWidth = rect.width - 40f;
            GUILayout.BeginVertical(GUILayout.Width(contentWidth));

            if (!string.IsNullOrEmpty(persistentWarning)) EditorGUILayout.HelpBox(persistentWarning, MessageType.Warning);
            if (SaberChartFileStore.IsValidSongId(EditingSongId, out _) && EditingDifficulty != "normal" &&
                SaberChartFileStore.MissingNormalChart(EditingSongId))
                EditorGUILayout.HelpBox("Normal がまだありません。共通の chart.json は Normal を保存すると作成されます。", MessageType.Info);
            if (EditingIsSecret)
                EditorGUILayout.HelpBox($"裏譜面 {SaberChartSecrets.Title(EditingDifficulty)}: ノーツごとの仕掛けはそのまま残します。新しく置いたノーツには付きません。",
                    MessageType.Info);
            SectionLabel("次に置くノーツ / パレット");
            GUILayout.Label("選択済みノーツは右の「ノーツ」で編集", smallMutedStyle);
            SectionLabel("編集ツール");
            GUILayout.BeginHorizontal();
            DrawToolToggle(EditTool.Select, "選択 (1)");
            DrawToolToggle(EditTool.Draw, "配置 (2)");
            DrawToolToggle(EditTool.Erase, "消去 (3)");
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
            directionAdvance = EditorGUILayout.ToggleLeft(new GUIContent("方向入力後、次のノーツへ",
                "キーで方向を付けたあと、次のノーツを選びます（ステップでは次の配置済みの拍へ）"), directionAdvance, GUILayout.Width(contentWidth));

            GUILayout.Space(10f);
            SectionLabel("LONG カット回数");
            paletteCount = EditorGUILayout.IntSlider(paletteCount, 2, 12, GUILayout.Width(contentWidth));

            GUILayout.Space(10f);
            SectionLabel("分解能 / SNAP");
            placementSnap = EditorGUILayout.ToggleLeft("配置・移動を格子に吸着", placementSnap);
            snapIndex = EditorGUILayout.Popup(snapIndex, SnapLabels, GUILayout.Width(contentWidth));
            GUILayout.Label($"1ステップ = {SaberChartUtility.SnapStep(CurrentSnap):0.###} 拍", smallMutedStyle);
            GUILayout.Label("吸着OFF: 自由配置 / Shiftで一時吸着", smallMutedStyle);

            GUILayout.Space(10f);
            SectionLabel("履歴");
            GUILayout.BeginHorizontal();
            GUI.enabled = !RecordingBusy && history.CanUndo;
            if (GUILayout.Button("↶ 元に戻す", GUILayout.MinWidth(0f))) Undo();
            GUI.enabled = !RecordingBusy && history.CanRedo;
            if (GUILayout.Button("↷ やり直し", GUILayout.MinWidth(0f))) Redo();
            GUI.enabled = !RecordingBusy;
            GUILayout.EndHorizontal();

            GUILayout.Space(10f);
            EditorGUILayout.HelpBox(
                "中央: 横=X / 縦=時間\n空間パッド: X・Y位置（選択ノーツ欄）\n右クリック: ノーツ削除",
                MessageType.None);

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawRightPanel(Rect rect)
        {
            DrawInspectorWorkspace(rect);
        }

        private void DrawChartSettings()
        {
            // 数値は Enter か欄を離れたときに確定する。1文字ごとに履歴を増やさない。
            EditorGUI.BeginChangeCheck();
            float nextBpm = EditorGUILayout.DelayedFloatField("BPM", document.bpm);
            float nextOffset = EditorGUILayout.DelayedFloatField("全体OFFSET (ms)", document.offsetMs);
            float nextBeatZero = EditorGUILayout.DelayedFloatField(new GUIContent("拍の始まり (ms)", "譜面グリッド原点。全体OFFSETを足した位置が音源上の拍の始まりです。負の値も指定できます。ノーツの時刻は変わりません。"), beatZeroMs);
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
            if (SongSettingsLocked()) return;
            EndNoteDrag();
            string before = CurrentJson();
            float safeBpm = Mathf.Max(1f, nextBpm);
            // 全体OFFSETが正なら、音源上の拍へ合わせる原点は負になることもある。
            float safeBeatZero = nextBeatZero;
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
            if (SongSettingsLocked()) return;
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

            float previewHeight = expandPlaybackPreview && !recordMode ? rect.height : Mathf.Min(rect.width * 9f / 16f + 30f, rect.height * .30f);
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
                    playbackPreview.ShowPerfectCuts = previewPerfectCuts;
                    playbackPreview.ApproachTimeOverride = GameDifficultyRules.ApproachSeconds(
                        EditingDifficulty, GameSession.NoteApproachTime, true);
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
            if (recordMode || !expandPlaybackPreview)
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
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            previewBarLines = GUILayout.Toggle(previewBarLines, new GUIContent("小節線",
                "本編の小節線（ファイルの BPM・原点・OFFSET・拍子）を、ノーツと同じ速さで流します"), EditorStyles.miniButtonMid);
            previewHitRadius = GUILayout.Toggle(previewHitRadius, new GUIContent("当たりの円",
                "判定の前後0.25秒のノーツに、刃が届けば切れる範囲（半径1.2）の円を出します"), EditorStyles.miniButtonRight);
            GUILayout.EndHorizontal();
            previewPerfectCuts = GUILayout.Toggle(previewPerfectCuts, new GUIContent("Perfect で切る",
                "判定時刻ちょうど（Perfect）に、本編と同じ2片・光の輪・ロングのひびと細片で切って見せます。オフにすると判定時刻で消える従来の表示です"),
                EditorStyles.miniButton);
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
            if (SongSettingsLocked()) return;
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
            if (SongSettingsLocked()) return;
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
            float nextTime = EditorGUILayout.DelayedFloatField(new GUIContent("譜面時刻 (ms)", "OFFSETを加える前の保存値。音源上の時刻は、上の概要欄に表示します。"), time);
            float nextX = EditorGUILayout.DelayedFloatField("X", x);
            float nextY = EditorGUILayout.DelayedFloatField("Y", y);
            int nextTypeIndex = EditorGUILayout.Popup("種類", typeIndex, TypeLabels);
            int nextColorIndex = EditorGUILayout.Popup("色 / 手", colorIndex, ColorLabels);
            int nextDirectionIndex = EditorGUILayout.Popup("方向", directionIndex, DirectionLabels);
            // 99回を越えるロング(裏譜面 L の1000回など)は数の欄で出す(つまみの端に丸めない)。
            int nextCount = nextTypeIndex != 2 ? 1
                : count > 99 ? Mathf.Clamp(EditorGUILayout.DelayedIntField("カット回数", count), 2, SaberChartUtility.MaxLongCount)
                : EditorGUILayout.IntSlider("カット回数", count, 2, 99);
            // Long の長さ。自動 = 本編既定の (回数-1)×0.7秒。手動なら拍数で直接指定できる。
            bool autoLength = note.lengthMs <= 0f;
            bool nextAutoLength = autoLength;
            float nextLengthBeats = 0f;
            float shownBeats = 0f;
            if (nextTypeIndex == 2)
            {
                nextAutoLength = EditorGUILayout.Toggle("長さ自動 ((回数−1)×0.7s)", autoLength);
                float effectiveMs = note.lengthMs > 0f
                    ? note.lengthMs
                    : (Mathf.Max(2, nextCount) - 1) * SaberChartUtility.DefaultSecondsPerLongCut * 1000f;
                // テンポ地図があっても、その場所のテンポで拍数を出す。
                shownBeats = GridBeatAt(note.time + effectiveMs) - GridBeatAt(note.time);
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
                string nextType = TypeValues[Mathf.Clamp(nextTypeIndex, 0, TypeValues.Length - 1)];
                string chosenDirection = DirectionValues[Mathf.Clamp(nextDirectionIndex, 0, DirectionValues.Length - 1)];
                // 向きだけを変えたときは、キーで向きを付けたときと同じく種類も合わせる(TAP に向きを付けると矢印が出ないまま
                // 本編では向きを判定してしまうため)。
                if (nextTypeIndex == typeIndex && nextDirectionIndex != directionIndex)
                    nextType = TypeAfterDirectionEdit(nextType, chosenDirection);
                selected.type = nextType;
                // 色の一覧に無い色(読み込んだ譜面の独自の色)は、色を選び直したときだけ変える。
                if (nextColorIndex != colorIndex) selected.color = ColorValues[Mathf.Clamp(nextColorIndex, 0, ColorValues.Length - 1)];
                bool changedAwayFromDirection = nextTypeIndex != typeIndex && nextType != SaberChartUtility.TypeDirection;
                if (nextType == SaberChartUtility.TypeDirection && chosenDirection == SaberChartUtility.DirectionNone)
                    chosenDirection = "up";
                selected.direction = changedAwayFromDirection ? SaberChartUtility.DirectionNone : chosenDirection;
                selected.count = selected.type == SaberChartUtility.TypeLong ? Mathf.Max(2, nextCount) : 1;
                // 長さは、長さの欄か「長さ自動」を触ったとき(と LONG にしたとき)だけ拍から決め直す。
                // ほかの欄だけ変えたときは ms のまま保つ(テンポの変わる所をまたいでも、触っていない長さを変えない)。
                bool lengthEdited = nextAutoLength != autoLength || typeIndex != 2 ||
                    !nextAutoLength && !Mathf.Approximately(nextLengthBeats, shownBeats);
                if (selected.type != SaberChartUtility.TypeLong || nextAutoLength)
                {
                    selected.lengthMs = 0f; // 自動(回数×0.7s)へ戻す
                }
                else if (lengthEdited)
                {
                    float startBeat = GridBeatAt(selected.time);
                    selected.lengthMs = Mathf.Max(60f, GridTimeAt(startBeat + Mathf.Max(0f, nextLengthBeats)) - selected.time);
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
                DrawHoveredNoteInfo(laneRect);
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
            int[] marks = NoteIssueMarks();
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

                // 使1: 本編で取れない(赤)・取りにくい(黄)ノーツの右上に印。F8 で順に回れる。
                int mark = index < marks.Length ? marks[index] : 0;
                if (mark > 0)
                {
                    Rect corner = new Rect(noteRect.xMax - 6f, noteRect.y - 3f, 9f, 9f);
                    EditorGUI.DrawRect(corner, new Color(0f, 0f, 0f, .8f));
                    EditorGUI.DrawRect(new Rect(corner.x + 1f, corner.y + 1f, 7f, 7f),
                        mark >= 2 ? new Color(1f, .32f, .32f) : new Color(1f, .78f, .25f));
                }
            }
        }

        // ---- 使3: マウスを乗せたノーツの中身(高さ・向き・時刻・問題)を、選ばなくても見られるようにする ----

        private int hoveredNoteIndex = -1;
        private GUIStyle hoverInfoStyle;

        private void UpdateHoveredNote(Event current, Rect laneRect)
        {
            int hover = current.type == EventType.MouseMove && !draggingNote && laneRect.Contains(current.mousePosition)
                ? FindNoteAt(current.mousePosition, laneRect)
                : -1;
            if (hover == hoveredNoteIndex) return;
            hoveredNoteIndex = hover;
            Repaint();
        }

        private void DrawHoveredNoteInfo(Rect laneRect)
        {
            if (Event.current.type != EventType.Repaint || draggingNote || hoveredNoteIndex < 0 || hoveredNoteIndex >= document.notes.Count) return;
            SaberChartNote note = document.notes[hoveredNoteIndex];
            Rect noteRect = NoteRect(note, laneRect);
            // 譜面が変わって別のノーツが来ていたら出さない(次のマウスの動きで選び直す)。
            if (!new Rect(noteRect.x - 3f, noteRect.y - 4f, noteRect.width + 6f, noteRect.height + 8f).Contains(Event.current.mousePosition)) return;
            hoverInfoStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(.93f, .95f, .99f) },
                padding = new RectOffset(7, 7, 4, 4),
                wordWrap = false,
            };
            var content = new GUIContent(NoteInfoText(note, hoveredNoteIndex));
            Vector2 size = hoverInfoStyle.CalcSize(content);
            float x = noteRect.xMax + 8f;
            if (x + size.x > laneRect.xMax) x = Mathf.Max(laneRect.x, noteRect.x - 8f - size.x);
            float y = Mathf.Clamp(noteRect.y - 6f, laneRect.y, Mathf.Max(laneRect.y, laneRect.yMax - size.y));
            var box = new Rect(x, y, size.x, size.y);
            EditorGUI.DrawRect(box, new Color(.05f, .06f, .09f, .95f));
            DrawOutline(box, new Color(1f, 1f, 1f, .28f), 1f);
            GUI.Label(box, content, hoverInfoStyle);
        }

        private string NoteInfoText(SaberChartNote note, int index)
        {
            int colorIndex = Array.IndexOf(ColorValues, note.color);
            string color = colorIndex >= 0 ? ColorLabels[colorIndex] : note.color;
            string arrow = note.direction != SaberChartUtility.DirectionNone ? " " + DirectionGlyph(note.direction) : "";
            string kind = note.type == SaberChartUtility.TypeLong
                ? $"LONG ×{note.count}{arrow}（{SaberChartUtility.EffectiveLongLengthMs(note) / 1000f:0.00}秒）"
                : note.type == SaberChartUtility.TypeDirection ? "方向" + arrow : "TAP" + arrow;
            int height = SaberChartUtility.LaneForCoordinate(note.y, LaneCount,
                SaberChartUtility.DefaultYMin, SaberChartUtility.DefaultYMax) + 1;
            string text = $"#{index + 1}  {color} · {kind}\n" +
                          $"{(note.time + document.offsetMs) / 1000f:0.000}秒 · {SaberChartUtility.FormatMusicalPosition(TimelineBeat(note), document, CurrentSnap)}\n" +
                          $"X {note.x:0.##} · Y {note.y:0.##}（高さ {height}/{LaneCount}）";
            List<string> extras = SaberChartUtility.NoteExtraKeys(note);
            if (extras.Count > 0) text += "\n仕掛け: " + string.Join("・", extras);
            SaberChartIssue issue = WorstIssueFor(index);
            return issue == null ? text : text + $"\n{SaberChartLint.Label(issue.level)}: {issue.message}";
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
            if (current.type == EventType.MouseMove || current.type == EventType.MouseLeaveWindow)
            {
                UpdateHoveredNote(current, laneRect);
                return;
            }
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
                    SeekToBeat(PlacementBeat(BeatAtY(current.mousePosition.y, timelineRect), current.shift));
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
                SeekToBeat(PlacementBeat(BeatAtY(mouse.y, timelineRect), Event.current != null && Event.current.shift));
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
        /// 通常はマウスの移動量どおりに相対移動し、元の細かな時刻・位置を保つ。
        /// 吸着ONでは移動量を格子にそろえ、Shiftでは格子と列の中心へ直接吸い付ける。
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

            float beatDelta = (dragStartMouse.y - mouse.y) / Mathf.Max(1f, pixelsPerBeat);
            if (placementSnap)
            {
                float step = SaberChartUtility.SnapStep(CurrentSnap);
                beatDelta = Mathf.Round(beatDelta / step) * step;
            }
            float time = beatDelta == 0f
                ? dragOriginalTime
                : Mathf.Max(0f, GridTimeAt(GridBeatAt(dragOriginalTime) + beatDelta));
            float laneWidth = Mathf.Max(1f, laneRect.width / LaneCount);
            float lanes = (mouse.x - dragStartMouse.x) / laneWidth;
            if (placementSnap) lanes = Mathf.Round(lanes);
            float spacing = (xMax - xMin) / (LaneCount - 1);
            float x = lanes == 0 ? dragOriginalX : SaberChartUtility.ClampMoved(dragOriginalX + lanes * spacing, dragOriginalX, xMin, xMax);
            return (time, x);
        }

        private float PlacementBeat(float beat, bool forceSnap)
        {
            return placementSnap || forceSnap
                ? SaberChartUtility.QuantizeBeat(beat, CurrentSnap, document)
                : Mathf.Max(0f, beat);
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
            bool forceSnap = Event.current != null && Event.current.shift;
            int xLane = LaneAtX(mouse.x, laneRect);
            float x = placementSnap || forceSnap
                ? SaberChartUtility.CoordinateForLane(xLane, LaneCount,
                    SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax)
                : SaberChartUtility.DefaultXMin + Mathf.Clamp01(
                    ((mouse.x - laneRect.x) / Mathf.Max(1f, laneRect.width / LaneCount) - .5f) / (LaneCount - 1)) *
                    (SaberChartUtility.DefaultXMax - SaberChartUtility.DefaultXMin);
            float y = SaberChartUtility.CoordinateForLane(paletteYLane, LaneCount,
                SaberChartUtility.DefaultYMin, SaberChartUtility.DefaultYMax);
            float beat = PlacementBeat(BeatAtY(mouse.y, timelineRect), forceSnap);
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
            // 文字欄に届いたキーを、録音・方向・譜面の操作へ重ねて渡さない。
            if (TextControlHasFocus())
            {
                // 守11: 入力欄にいても Ctrl/Cmd+S は保存する。欄の値は欄を離れたときに確定するので、
                // 先に欄を離れ、値が入ったあとの再描画で保存する(打った数値を落とさない)。
                if (current.type == EventType.KeyDown && (current.control || current.command) && current.keyCode == KeyCode.S)
                {
                    FinishTextEditing();
                    saveAfterTextCommit = true;
                    current.Use();
                    Repaint();
                    return;
                }
                // 押したまま欄へ移動しても、キーを離せば録音ノーツを閉じる。
                if (current.type == EventType.KeyUp) HandleRecordingKeyboard(current);
                return;
            }
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
            else if (!action && !current.alt && current.keyCode == KeyCode.F8)
            {
                // 使1: 次(Shift で前)のエラー・注意のノーツへ。診断の欄を開かずに順に直していける。
                JumpToIssue(current.shift ? -1 : 1);
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
            if (SaberChartSecrets.IsSecret(CurrentDifficulty))
            {
                SetStatus(SecretNewReason);
                return;
            }
            if (!ConfirmAbandonChanges()) return;
            StopPreview(false);
            string targetSong = songId?.Trim();
            document = NewDocumentFor(targetSong, CurrentDifficulty, out string inheritedFrom, out float displayOrigin);
            diskBaseline = null; diskBaselineKey = null; persistentWarning = null;
            // 格子は引き継ぎ元と同じ位置に出す(ファイルの原点の値は引き継ぎ元のファイルと同じ)。
            beatZeroMs = displayOrigin;
            currentBeat = 0f;
            // Easyの新規配置は表拍から始める。既存ノーツの時刻は変えない。
            if (CurrentDifficulty == "easy") snapIndex = 0;
            selectedIndex = -1;
            // 新しい譜面の保存先は、作ったときに選んでいた曲・難易度。既存ファイルがあれば保存時に確認する。
            loadedSongId = SaberChartFileStore.IsValidSongId(targetSong, out _) ? targetSong : null;
            loadedDifficulty = loadedSongId != null ? CurrentDifficulty : null;
            secretSourceKey = null;
            confirmedOverwriteTarget = null;
            ResetDerivedViews();
            history.Clear();
            savedJson = null;
            // 新規作成に進む確認で破棄した曲情報を、次の譜面へ持ち越さない。
            if (songInfo != null) { LoadSongInfo(EditingSongId); CacheSongInfoDraft(); }
            UpdateDirtyState();
            EnsureAudioForSong(false);
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

        /// <summary>
        /// 使いやすさ: 同じ曲の別の難易度を、ヘッダーから1回で開く(未保存なら保存するかを先に聞く)。
        /// 保存の確認でやめたとき・読めなかったときは、「開く・作る」の選択を元に戻す。
        /// </summary>
        private void SwitchEditingDifficulty(int index)
        {
            if (RecordingBusy || index < 0 || index > SecretDifficultyIndex || index == EditingDifficultyIndex) return;
            if (index == SecretDifficultyIndex && SaberChartSecrets.For(EditingSongId) == null) return;
            string previousSong = songId;
            int previousDifficulty = difficultyIndex;
            songId = EditingSongId;
            difficultyIndex = index;
            LoadDocument();
            if (EditingDifficultyIndex != index)
            {
                songId = previousSong;
                difficultyIndex = previousDifficulty;
            }
            difficultyFilesSong = null;
        }

        private void LoadDocumentForSong(string targetSongId)
        {
            EndNoteDrag();
            if (!ConfirmAbandonChanges()) return;
            try
            {
                // 裏譜面の番号は、読む曲の裏譜面(無い曲なら Normal)。
                string difficulty = DifficultyFor(targetSongId, difficultyIndex) ?? DifficultyValues[1];
                // 読込に失敗した場合は、元の譜面・履歴・未保存状態・保存先を残す。
                var nextDiskState = CaptureDiskState(targetSongId, difficulty);
                SaberChartDocument nextDocument = SaberChartFileStore.Load(targetSongId, difficulty, out string loadedPath);
                if (!DiskStateMatches(nextDiskState)) throw new IOException("読み込み中に譜面が変更されました。もう一度読み込んでください。");
                // 守5: Easy・Hard のファイルが無いと Normal の chart.json を読む。それを「変更なし」として開くと、
                // 保存や「本編でテスト」で Normal の中身のまま新しいファイルを黙って作るので、未保存の新しい譜面として開く。
                bool borrowedNormal = loadedPath != null && difficulty != "normal" &&
                    string.Equals(Path.GetFileName(loadedPath), "chart.json", StringComparison.OrdinalIgnoreCase);
                // 裏譜面は Normal を元に作らない(ファイルが無ければ開かない)。
                if (SaberChartSecrets.IsSecret(difficulty) && (loadedPath == null || borrowedNormal))
                    throw new IOException($"{DifficultyLabelFor(difficulty)} の譜面ファイル（chart_{difficulty}.json）がありません。裏譜面は生成の道具で作ります。");
                float nextBeatZeroMs = SaberChartUtility.EstimateBeatZeroMs(nextDocument);
                string nextSavedJson = borrowedNormal ? null : SaberChartUtility.ToJson(nextDocument, false);
                StopPreview(false);
                document = nextDocument;
                songId = targetSongId.Trim();
                beatZeroMs = nextBeatZeroMs;
                currentBeat = 0f;
                selectedIndex = -1;
                loadedSongId = songId.Trim();
                loadedDifficulty = difficulty;
                secretSourceKey = SaberChartSecrets.IsSecret(difficulty) ? TargetKey(loadedSongId, difficulty) : null;
                // 裏譜面の無い曲へ移ったときは、選択も開いた Normal にそろえる。
                difficultyIndex = DifficultyIndexOf(difficulty);
                confirmedOverwriteTarget = null;
                ResetDerivedViews();
                history.Clear();
                savedJson = nextSavedJson;
                RememberDiskState(loadedSongId, loadedDifficulty, nextDiskState);
                persistentWarning = borrowedNormal
                    ? $"{EditingDifficultyLabel} の譜面はまだありません。Normal（chart.json）の内容を、未保存の新しい譜面として開きました。" +
                      $"保存すると chart_{loadedDifficulty}.json を作ります（Normal は変わりません）。"
                    : null;
                // 保存・破棄の確認は済んだので、成功した読込先の曲情報へ一度で切り替える。
                if (songInfo != null) { LoadSongInfo(loadedSongId); CacheSongInfoDraft(); }
                hasUnsavedChanges = borrowedNormal;
                nextAutosaveAt = 0;
                EnsureAudioForSong(false);
                CheckForDraft();
                SetStatus(loadedPath == null ? "空の譜面を開きました"
                    : borrowedNormal ? $"{EditingDifficultyLabel} は未作成。Normal を元に新しい譜面として開きました"
                    : EditingIsSecret ? SecretOpenedMessage
                    : $"読込: {Path.GetFileName(loadedPath)}");
            }
            catch (Exception exception)
            {
                persistentWarning = "譜面を読み込めません: " + exception.Message;
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
            // 裏譜面へは、その曲のその裏譜面を読み込んで直した譜面だけを書く(読み込んでいない選択や、読み込みに失敗したあとの内容で置き換えない)。
            if (SaberChartSecrets.IsSecret(targetDifficulty) && secretSourceKey != TargetKey(targetSong, targetDifficulty))
            {
                SetStatus($"{targetSong} / {DifficultyLabelFor(targetDifficulty)} へは保存しません。裏譜面は、読み込んで直したときだけ保存できます" +
                          "（上の札か「開く・作る」の「読込」で開いてください）");
                return false;
            }
            bool exists = SaberChartFileStore.ChartExists(targetSong, targetDifficulty);
            if (savedJson != null && CurrentJson() == savedJson && (exists || SongInfoDirty))
            {
                if (SongInfoDirty && !SaveSongInfo()) return false;
                SaberChartDrafts.DeleteSessionChart(sessionDraftPaths, targetSong, targetDifficulty);
                UpdateDirtyState();
                SetStatus("保存済み（譜面の内容は変更ありません）");
                return true;
            }
            string targetLabel = DifficultyLabelFor(targetDifficulty);
            if (exists && (savedJson == null || string.IsNullOrEmpty(loadedSongId) || string.IsNullOrEmpty(loadedDifficulty))
                && confirmedOverwriteTarget != TargetKey(targetSong, targetDifficulty))
            {
                // 新規の譜面で、既にある譜面を黙って置き換えない。
                if (!confirmDuplicateOverwrite($"{targetSong} / {targetLabel} は既にあります。",
                        "新しく作った譜面で、このファイルを置き換えますか？（置き換える前のファイルはバックアップされます）"))
                    return false;
            }

            try
            {
                if (!ConfirmDiskOverwrite(targetSong, targetDifficulty)) return false;
                SaberChartUtility.Normalize(document);
                if (SongInfoDirty && !SaveSongInfo()) return false;
                string destination = SaberChartFileStore.Save(document, targetSong, targetDifficulty);
                RememberDiskState(targetSong, targetDifficulty);
                persistentWarning = null;
                loadedSongId = targetSong.Trim();
                loadedDifficulty = targetDifficulty;
                // 表の譜面として保存したら、裏譜面の読み込み元ではなくなる(裏譜面への保存は、読み込み元と同じときだけここまで来る)。
                if (!SaberChartSecrets.IsSecret(targetDifficulty)) secretSourceKey = null;
                confirmedOverwriteTarget = null;
                savedJson = CurrentJson();
                hasUnsavedChanges = false;
                nextAutosaveAt = 0;
                settingsMismatchKey = null;
                // 保存した曲・難易度の、この窓が作った下書きだけを消す。
                SaberChartDrafts.DeleteSessionChart(sessionDraftPaths, targetSong, targetDifficulty);
                string unseenDraft = pendingDraft != null ? "。開いていない下書きは Library/3DSaberChartDrafts に残しています" : "";
                lastDraftSavedAt = lastDraftTarget = null;
                pendingDraft = null;
                if (targetDifficulty != "normal" && SaberChartFileStore.MissingNormalChart(targetSong))
                    SetStatus($"保存: {Path.GetFileName(destination)}。Normal がまだ無いため、本編の Easy / Normal は選べません（chart.json は Normal から作ります）{unseenDraft}");
                else
                    SetStatus($"保存: {Path.GetFileName(destination)}{unseenDraft}");
                return true;
            }
            catch (Exception exception)
            {
                SetStatus("譜面を保存できません: " + exception.Message);
                EditorUtility.DisplayDialog("譜面を保存できません", SaveFailureMessage(exception), "OK");
                return false;
            }
        }

        /// <summary>
        /// 使1: 保存できなかったときの文。例外の文だけでなく、よくある原因と次にすることを書き、編集が消えていないことも伝える。
        /// </summary>
        private static string SaveFailureMessage(Exception exception)
        {
            string hint = exception is UnauthorizedAccessException
                ? "ファイルが読み取り専用か、書き込む権限がありません。読み取り専用を外すか、ほかのアプリが使っていないかを確かめてから、もう一度保存してください。"
                : exception is IOException
                    ? "ほかのアプリ（テキストエディター・同期ソフト・ウイルス対策など）がファイルを開いている可能性があります。閉じてから、もう一度保存してください。"
                    : "もう一度保存してください。続くときは、この文をそのまま共有してください（下の知らせの欄を押すとコピーできます）。";
            return exception.Message + "\n\n" + hint + "\n\n編集中の内容は、この窓に残っています。";
        }

        private bool ConfirmAbandonChanges()
        {
            UpdateDirtyState();
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
            AudioClip found = SaberChartFileStore.LoadAudioClipFor(EditingSongId, EditingDifficulty);
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
                NoticeReplacedAudio();
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("音源を取り込めません", exception.Message, "OK");
            }
        }

        private void HandleAudioClipSelection(AudioClip clip)
        {
            if (clip == null || SaberChartFileStore.IsAudioClipForChart(clip, EditingSongId, EditingDifficulty))
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
                NoticeReplacedAudio();
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("音源を取り込めません", exception.Message, "OK");
            }
        }

        // 守12: 差し替えた音源の行き先と、音量そろえ(loudness.json)を作り直す必要を知らせる。
        // 作り直すまで、この曲だけ補正なし(ゲイン1)で鳴る(SongLoudness は音源のハッシュが合わないと補正を外す)。
        private void NoticeReplacedAudio()
        {
            string backup = SaberChartFileStore.LastReplacedAudioBackup;
            persistentWarning = "音源を差し替えました。" + (backup != null ? $"前の音源は {backup} に残しています。" : "") +
                "音量そろえ（loudness.json・master.ogg）は前の音源用なので、Tools/Audio/SongLoudness の build_loudness.py → build_master.py の順で作り直してください" +
                "（作り直すまで、この曲だけ音量の補正が外れます）。";
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
                // 合1: 止めた位置は音声そのものの位置にする(表示補正のぶん戻すと、止めて再生するたびに補正だけ巻き戻る)。
                UpdatePlaybackPositionCore(withDisplayOffset: false);
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
            if (!countingIn && !isPlaying) CheckDiskForExternalChanges();
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

        private void UpdatePlaybackPosition() => UpdatePlaybackPositionCore(true);

        private void UpdatePlaybackPositionCore(bool withDisplayOffset)
        {
            if (!isPlaying) return;
            if (!SaberChartAudioPreview.TryGetPosition(playingClip ?? audioClip, out float audioSeconds))
            {
                StopPreview(false);
                return;
            }
            if (recorder != null) lastRecordingSeconds = audioSeconds;
            // 個人の表示補正を譜面や波形に焼き込まず、試聴中のカーソルだけへ適用する。
            if (useGameTiming && withDisplayOffset) audioSeconds -= GameSession.JudgmentOffsetMs / 1000f;
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
            // 音源上の時刻へ変換してから制限し、正のOFFSETで相殺される負の原点を保つ。
            return Mathf.Max(0f, (GridTimeAt(Mathf.Max(0f, beat)) + document.offsetMs) / 1000f);
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
            AudioClip packagedClip = SaberChartFileStore.LoadAudioClipFor(testSong, EditingDifficulty);
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

            SaberChartTestPlayBridge.Queue(testSong.Trim(), EditingGameDifficulty);
            EditorSceneManager.OpenScene(gameScenePath);
            EditorApplication.EnterPlaymode();
        }

        private void DrawValidationSummary()
        {
            // ノーツの検査は譜面チェック(SaberChartLint)にまとめ、ここでは件数と音源の注意だけを出す。
            List<string> warnings = ValidationWarnings();
            List<SaberChartIssue> issues = LintIssues(PackagedAudioLengthMs());
            int errors = issues.Count(issue => issue.level == SaberChartIssueLevel.Error);
            int cautions = issues.Count(issue => issue.level == SaberChartIssueLevel.Warning);
            int infos = issues.Count - errors - cautions;
            string counts = $"譜面チェック: エラー {errors} / 注意 {cautions} / 情報 {infos}";
            if (warnings.Count == 0 && errors + cautions == 0)
            {
                EditorGUILayout.HelpBox(counts + "\n本編で取れない配置はありません。", MessageType.Info);
                return;
            }

            var lines = warnings.Select(warning => "• " + warning)
                .Concat(issues.Where(issue => issue.level != SaberChartIssueLevel.Info).Take(4)
                    .Select(issue => "• " + SaberChartLint.Describe(document, issue))).ToList();
            EditorGUILayout.HelpBox(counts + "\n" + string.Join("\n", lines.Take(6)),
                errors > 0 ? MessageType.Error : MessageType.Warning);
        }

        // 音源の注意。ノーツごとの検査は SaberChartLint が行う。
        private List<string> ValidationWarnings()
        {
            var warnings = new List<string>();
            AudioClip packagedClip = SaberChartFileStore.LoadAudioClipFor(EditingSongId, EditingDifficulty);
            if (packagedClip == null)
                warnings.Add("曲フォルダに本編用音源がありません");
            else if (audioClip != null && audioClip != packagedClip)
                warnings.Add("プレビュー音源と本編用音源が異なります");
            return warnings;
        }

        private void DrawFooter(Rect rect)
        {
            EditorGUI.DrawRect(rect, HeaderColor);
            string visibleStatus = EditorApplication.timeSinceStartup <= statusUntil ? statusMessage : "準備完了";
            Rect statusRect = new Rect(rect.x + 10f, rect.y + 4f, rect.width * 0.30f, 20f);
            EditorGUIUtility.AddCursorRect(statusRect, MouseCursor.Link);
            if (GUI.Button(statusRect, new GUIContent(visibleStatus, "押すと最近の知らせを見返せます（選ぶと文をコピー）"), smallMutedStyle))
                ShowStatusHistory();
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
            // 譜面全体を JSON にして、保存した内容と比べる(編集の直後に呼ぶ確実な判定)。
            dirtyJsonDiffers = string.IsNullOrEmpty(savedJson) || CurrentJson() != savedJson;
            dirtyFingerprint = DirtyFingerprint();
            dirtySavedJson = savedJson;
            dirtyCheckedAt = EditorApplication.timeSinceStartup;
            ApplyDirtyState();
        }

        // 性1: 描画のイベントごとに譜面全体を JSON にしない。内容の指紋が変わったときと、0.5秒ごと(指紋に入らない項目の変更も拾う)だけ比べ直す。
        private int dirtyFingerprint;
        private string dirtySavedJson;
        private double dirtyCheckedAt = double.NegativeInfinity;
        private bool dirtyJsonDiffers;

        private void UpdateDirtyStateCheaply()
        {
            if (DirtyFingerprint() != dirtyFingerprint || !ReferenceEquals(savedJson, dirtySavedJson) ||
                EditorApplication.timeSinceStartup >= dirtyCheckedAt + .5)
            {
                UpdateDirtyState();
                return;
            }
            ApplyDirtyState();
        }

        private void ApplyDirtyState()
        {
            hasUnsavedChanges = SongInfoDirty || dirtyJsonDiffers;
            if (!hasUnsavedChanges) nextAutosaveAt = 0;
            else if (nextAutosaveAt <= 0) nextAutosaveAt = EditorApplication.timeSinceStartup + AutosaveIntervalSeconds;
        }

        private int DirtyFingerprint()
        {
            if (document == null) return 0;
            unchecked
            {
                int hash = SaberChartLint.Fingerprint(document);
                hash = hash * 31 + document.bpm.GetHashCode();
                hash = hash * 31 + document.beatZeroMs.GetHashCode();
                hash = hash * 31 + document.displayLevel;
                hash = hash * 31 + (document.timeSignatures?.Count ?? 0);
                hash = hash * 31 + (document.markers?.Count ?? 0);
                hash = hash * 31 + (document.tempoMap?.Count ?? 0);
                hash = hash * 31 + (document.extraFields?.Count ?? 0);
                foreach (SaberChartNote note in document.notes) if (note != null) hash = hash * 31 + note.beat.GetHashCode();
                return hash;
            }
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
                SaberChartDrafts.LatestFor(EditingSongId, EditingDifficulty, out string previousPath);
                string path = SaberChartDrafts.Write(EditingSongId, EditingDifficulty, document, DraftSourceHash());
                // 同じ内容の過去の下書きを再利用しても、この窓が作ったファイルとはみなさない。
                if (path != previousPath && !sessionDraftPaths.Contains(path)) sessionDraftPaths.Add(path);
                lastDraftSavedAt = DateTime.Now.ToString("HH:mm");
                lastDraftTarget = TargetKey(EditingSongId, EditingDifficulty);
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // 下書きの失敗で作業を止めない。保存の操作はいつもどおりできる。
                Debug.LogWarning("譜面の下書きを保存できませんでした: " + exception.Message);
                return false;
            }
        }

        // この窓が書いた下書き(保存したら消す)。前の起動で残った下書きは、案内を見るまで消さない。
        [SerializeField] private List<string> sessionDraftPaths = new List<string>();

        // 編集のもとになった譜面ファイル(読み込んだ時点)のハッシュ。下書きに書き、開くときに外での書き換えを見つける。
        private string DraftSourceHash()
        {
            string chartPath = SaberChartFileStore.ChartPath(EditingSongId, EditingDifficulty);
            return diskBaseline?.FirstOrDefault(file => string.Equals(file.path, chartPath, StringComparison.OrdinalIgnoreCase))?.hash;
        }

        private Func<string, bool> confirmStaleDraft = message => EditorUtility.DisplayDialog("下書きのあとに譜面が変わっています", message, "下書きを開く", "やめる");

        /// <summary>
        /// 開いた譜面より新しい下書きがあれば、復元を案内する(Unity が落ちた後など)。
        /// 編集中に未保存の変更があるときは案内しない(今の作業を優先する)。
        /// </summary>
        private void CheckForDraft()
        {
            pendingDraft = null;
            lastDraftSavedAt = lastDraftTarget = null;
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
            EditorGUILayout.HelpBox($"{pendingDraft.songId} / {pendingDraft.difficulty} の下書き（{local:M/d HH:mm}・{pendingDraft.noteCount}ノーツ）\n" +
                                    "Unity が終了する前の編集かもしれません。", MessageType.Warning);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("下書きを開く")) RestoreDraft();
            if (GUILayout.Button("下書きを捨てる")) DiscardDraft();
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);
        }

        private void DrawDraftHistory()
        {
            using (new EditorGUI.DisabledScope(!CanEditAtCursor))
            {
                if (!GUILayout.Button("保存済みの下書きを選ぶ…")) return;
                var drafts = SaberChartDrafts.ListFor(EditingSongId, EditingDifficulty);
                var menu = new GenericMenu();
                if (drafts.Count == 0) menu.AddDisabledItem(new GUIContent("この譜面の下書きはありません"));
                for (int i = 0; i < drafts.Count; i++)
                {
                    SaberChartDrafts.Draft draft = drafts[i];
                    string label = $"{i + 1}. {draft.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}・{draft.noteCount}ノーツ";
                    menu.AddItem(new GUIContent(label), false, () => { pendingDraft = draft; RestoreDraft(); Repaint(); });
                }
                menu.ShowAsContext();
            }
        }

        private void RestoreDraft()
        {
            if (pendingDraft == null) return;
            // 守4: 下書きのもとになった譜面が、そのあと外で書き換えられていたら、開く前に知らせる
            // (開いて保存すると、その書き換えが消える)。
            string chartPath = SaberChartFileStore.ChartPath(EditingSongId, EditingDifficulty);
            if (!string.IsNullOrEmpty(pendingDraft.sourceHash) && FileHash(chartPath) != pendingDraft.sourceHash &&
                !confirmStaleDraft("この下書きを書いたあとに、譜面ファイルがほかのツール（プレイレビュー・AI・Codex など）で書き換えられています。" +
                                   "下書きを開いて保存すると、その書き換えは消えます。開きますか？")) return;
            EndNoteDrag();
            StopPreview(false);
            SaberChartDocument restored;
            try { restored = SaberChartUtility.FromJson(pendingDraft.document); }
            catch (FormatException exception)
            {
                try
                {
                    SaberChartDrafts.KeepUnreadable(pendingDraft);
                    pendingDraft = SaberChartDrafts.LatestFor(EditingSongId, EditingDifficulty, out _);
                    SetStatus("この下書きは読めません。読めないファイルを控えに残し、ほかの下書きは保持しました。" + exception.Message);
                }
                catch (Exception keepError) when (keepError is IOException || keepError is UnauthorizedAccessException)
                {
                    SetStatus("下書きを開けず、控えへの移動もできません。元のファイルは残しています。" + keepError.Message);
                }
                Repaint();
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
            // 使1: 長い知らせほど長く出す(読み切れるように3〜10秒)。消えたあとも下の欄を押せば見返せる。
            statusUntil = EditorApplication.timeSinceStartup + StatusSeconds(message);
            RememberStatus(message);
            Repaint();
        }

        private const int StatusHistoryLimit = 15;
        private readonly List<(string time, string message)> statusHistory = new List<(string, string)>();

        private static double StatusSeconds(string message) => Mathf.Clamp(3f + (message?.Length ?? 0) * .08f, 3f, 10f);

        private void RememberStatus(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            // ズームの値など、数だけが変わる知らせが続いたら1件にまとめる。
            string kind = System.Text.RegularExpressions.Regex.Replace(message, "[0-9.+−-]+", "#");
            if (statusHistory.Count > 0 &&
                System.Text.RegularExpressions.Regex.Replace(statusHistory[0].message, "[0-9.+−-]+", "#") == kind)
                statusHistory.RemoveAt(0);
            statusHistory.Insert(0, (DateTime.Now.ToString("HH:mm:ss"), message));
            if (statusHistory.Count > StatusHistoryLimit) statusHistory.RemoveAt(statusHistory.Count - 1);
        }

        // 最近の知らせ。選ぶとその文をコピーする(AI やほかの人に、出た文をそのまま渡せるように)。
        private void ShowStatusHistory()
        {
            var menu = new GenericMenu();
            if (statusHistory.Count == 0) menu.AddDisabledItem(new GUIContent("まだ知らせはありません"));
            foreach ((string time, string message) in statusHistory)
            {
                string text = message.Length > 90 ? message.Substring(0, 89) + "…" : message;
                // メニューの「/」は階層になるので全角にする。
                string copied = message;
                menu.AddItem(new GUIContent($"{time}  {text.Replace('/', '／')}"), false, () =>
                {
                    EditorGUIUtility.systemCopyBuffer = copied;
                    SetStatus("知らせの文をコピーしました");
                });
            }
            menu.ShowAsContext();
        }

        private void DrawToolToggle(EditTool tool, string label)
        {
            bool active = editTool == tool;
            Color old = GUI.backgroundColor;
            if (active) GUI.backgroundColor = AccentColor;
            if (GUILayout.Button(label, GUILayout.MinWidth(0f), GUILayout.Height(28f))) editTool = tool;
            GUI.backgroundColor = old;
        }

        private bool DrawChoiceButton(string label, bool active, Color tint, float height = 30f)
        {
            Color old = GUI.backgroundColor;
            GUI.backgroundColor = active ? tint : new Color(0.33f, 0.38f, 0.45f);
            bool clicked = GUILayout.Button(label, GUILayout.MinWidth(0f), GUILayout.Height(height));
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
                wordWrap = true,
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
                wordWrap = true,
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

        // 新しい窓だけ、前回選んだ譜面を読み込む。再コンパイル時の編集中データは上書きしない。
        private void RestoreLastDocument()
        {
            if (documentInitialized) return;
            documentInitialized = true;
            if (!string.IsNullOrEmpty(loadedSongId) || document.notes.Count > 0
                || !SaberChartFileStore.IsValidSongId(songId, out _)) return;
            try
            {
                string path = SaberChartFileStore.ChartPath(songId, CurrentDifficulty);
                if (!File.Exists(path)) return;
                var snapshot = CaptureDiskState(songId, CurrentDifficulty);
                // 自動復元だけは例外を握りつぶす TryLoadExact を使わない。
                var restored = SaberChartUtility.FromJson(File.ReadAllText(path), SaberChartSecrets.IsSecret(CurrentDifficulty));
                if (!DiskStateMatches(snapshot)) throw new IOException("読み込み中に譜面が変更されました。");
                RememberDiskState(songId, CurrentDifficulty, snapshot);
                persistentWarning = null;
                document = restored;
                loadedSongId = songId.Trim(); loadedDifficulty = CurrentDifficulty;
                secretSourceKey = SaberChartSecrets.IsSecret(loadedDifficulty) ? TargetKey(loadedSongId, loadedDifficulty) : null;
                beatZeroMs = SaberChartUtility.EstimateBeatZeroMs(document);
                savedJson = CurrentJson();
            }
            catch (Exception error)
            {
                // 読み込めないファイルを、空の保存済み譜面と誤認させない。
                savedJson = null;
                persistentWarning = "前回の譜面を読み込めません: " + error.Message;
                SetStatus(persistentWarning);
            }
        }

        private void LoadPreferences()
        {
            showPlaybackPreview = EditorPrefs.GetInt(PrefPrefix + "ShowPlaybackPreview", showPlaybackPreview ? 1 : 0) != 0;
            songId = EditorPrefs.GetString(PrefPrefix + "SongId", songId);
            difficultyIndex = EditorPrefs.GetInt(PrefPrefix + "Difficulty", difficultyIndex);
            snapIndex = EditorPrefs.GetInt(PrefPrefix + "Snap", snapIndex);
            pixelsPerBeat = EditorPrefs.GetFloat(PrefPrefix + "Zoom", pixelsPerBeat);
            difficultyIndex = Mathf.Clamp(difficultyIndex, 0, SecretDifficultyIndex);
            // 裏譜面は文字でも覚えておき、その曲の今の裏譜面と違えば(裏譜面が無くなった・曲が変わった)Normal にする。
            if (difficultyIndex == SecretDifficultyIndex && !string.Equals(EditorPrefs.GetString(PrefPrefix + "SecretDifficulty", string.Empty),
                    DifficultyFor(songId, SecretDifficultyIndex), StringComparison.OrdinalIgnoreCase))
                difficultyIndex = 1;
            snapIndex = Mathf.Clamp(snapIndex, 0, SnapDenominators.Length - 1);
            pixelsPerBeat = Mathf.Clamp(pixelsPerBeat, 30f, 240f);
        }

        private void SavePreferences()
        {
            EditorPrefs.SetInt(PrefPrefix + "ShowPlaybackPreview", showPlaybackPreview ? 1 : 0);
            EditorPrefs.SetString(PrefPrefix + "SongId", songId ?? string.Empty);
            EditorPrefs.SetInt(PrefPrefix + "Difficulty", difficultyIndex);
            if (difficultyIndex == SecretDifficultyIndex) EditorPrefs.SetString(PrefPrefix + "SecretDifficulty", CurrentDifficulty);
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
