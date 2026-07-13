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
    public sealed class SaberChartEditorWindow : EditorWindow
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
        private static readonly string[] SnapLabels = { "4分", "8分", "12分", "16分", "24分", "32分" };
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
        [SerializeField] private int beatsPerMeasure = 4;
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
        private double playbackEditorStart;
        private float playbackAudioStartSeconds;
        private bool draggingNote;
        private bool dragRecorded;
        private int dragNoteIndex = -1;
        private string dragSnapshot;
        private string statusMessage = "準備完了";
        private double statusUntil;

        private int CurrentSnap => SnapDenominators[Mathf.Clamp(snapIndex, 0, SnapDenominators.Length - 1)];
        private string CurrentDifficulty => DifficultyValues[Mathf.Clamp(difficultyIndex, 0, DifficultyValues.Length - 1)];
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
            EnsureAudioForSong(false);
            EditorApplication.update += EditorTick;
        }

        private void OnDisable()
        {
            EditorApplication.update -= EditorTick;
            StopPreview(false);
            SavePreferences();
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

            DrawHeader(headerRect);
            DrawLeftPanel(leftRect);
            DrawTimeline(timelineRect);
            DrawRightPanel(rightRect);
            DrawFooter(footerRect);
        }

        private void DrawHeader(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, headerStyle);
            GUILayout.BeginArea(new Rect(rect.x + 14f, rect.y + 8f, rect.width - 28f, rect.height - 12f));

            GUILayout.BeginHorizontal();
            GUILayout.Label("3D SABER  /  CHART STUDIO", titleStyle, GUILayout.Width(360f));
            GUILayout.FlexibleSpace();
            if (hasUnsavedChanges)
                GUILayout.Label("● 未保存", new GUIStyle(smallMutedStyle) { normal = { textColor = GoldColor } });
            else
                GUILayout.Label("保存済み", smallMutedStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("保存先", smallMutedStyle, GUILayout.Width(44f));
            string nextSongId = GUILayout.TextField(songId ?? string.Empty, GUILayout.MinWidth(120f), GUILayout.MaxWidth(260f));
            if (nextSongId != songId) songId = nextSongId;
            if (GUILayout.Button("▾", EditorStyles.miniButton, GUILayout.Width(26f))) ShowSongMenu();

            int nextDifficulty = EditorGUILayout.Popup(difficultyIndex, DifficultyLabels, GUILayout.Width(88f));
            if (nextDifficulty != difficultyIndex) difficultyIndex = nextDifficulty;

            GUILayout.Space(8f);
            if (GUILayout.Button("新規", GUILayout.Width(58f))) NewDocument();
            if (GUILayout.Button("読込", GUILayout.Width(58f))) LoadDocument();
            GUI.enabled = SaberChartFileStore.IsValidSongId(songId, out _);
            if (GUILayout.Button("保存", GUILayout.Width(62f))) SaveDocument();
            GUI.enabled = true;
            if (GUILayout.Button("フォルダ", GUILayout.Width(72f))) RevealSongFolder();
            GUILayout.FlexibleSpace();
            if (DestinationChanged())
                GUILayout.Label("別の保存先", new GUIStyle(smallMutedStyle) { normal = { textColor = GoldColor } });
            GUILayout.Label($"{document.notes.Count:N0} NOTES", smallMutedStyle, GUILayout.Width(92f));
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

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
            GUI.enabled = history.CanUndo;
            if (GUILayout.Button("↶ 元に戻す")) Undo();
            GUI.enabled = history.CanRedo;
            if (GUILayout.Button("↷ やり直し")) Redo();
            GUI.enabled = true;
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

            SectionLabel("再生 / シーク");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("|◀", GUILayout.Width(42f))) SeekToBeat(0f);
            if (GUILayout.Button(isPlaying ? "一時停止" : "▶ 再生", GUILayout.Height(28f))) TogglePreview();
            if (GUILayout.Button("■", GUILayout.Width(36f))) StopPreview(true);
            GUILayout.EndHorizontal();

            float maxBeat = MaxBeat();
            EditorGUI.BeginChangeCheck();
            float soughtBeat = EditorGUILayout.Slider(currentBeat, 0f, maxBeat);
            if (EditorGUI.EndChangeCheck()) SeekToBeat(soughtBeat);
            GUILayout.Label(
                $"{SaberChartUtility.FormatMusicalPosition(currentBeat, beatsPerMeasure, CurrentSnap)}   /   {BeatToAudioSeconds(currentBeat):0.000}s",
                centeredSmallStyle);

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
            GUI.enabled = SaberChartFileStore.IsValidSongId(songId, out _);
            if (GUILayout.Button("保存して本編でテスト", GUILayout.Height(32f))) TestInGame();
            GUI.enabled = true;

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawChartSettings()
        {
            EditorGUI.BeginChangeCheck();
            float nextBpm = EditorGUILayout.FloatField("BPM", document.bpm);
            float nextOffset = EditorGUILayout.FloatField("全体OFFSET (ms)", document.offsetMs);
            float nextBeatZero = EditorGUILayout.FloatField("譜面グリッド原点 (ms / 0以上)", beatZeroMs);
            int nextMeasure = EditorGUILayout.IntField("1小節の拍数", beatsPerMeasure);
            float nextScale = EditorGUILayout.FloatField("座標倍率", document.coordScale);
            if (!EditorGUI.EndChangeCheck()) return;

            string before = CurrentJson();
            float safeBeatZero = Mathf.Max(0f, nextBeatZero);
            bool timingChanged = !Mathf.Approximately(nextBpm, document.bpm) ||
                                 !Mathf.Approximately(safeBeatZero, beatZeroMs);
            document.bpm = Mathf.Max(1f, nextBpm);
            document.offsetMs = nextOffset;
            beatZeroMs = safeBeatZero;
            beatsPerMeasure = Mathf.Clamp(nextMeasure, 1, 16);
            document.coordScale = Mathf.Max(0.0001f, nextScale);
            // time が本編の判定時刻。BPM変更でも時刻は動かさず、補助値 beat だけ更新する。
            if (timingChanged) SaberChartUtility.RecalculateBeatsFromTimes(document, beatZeroMs);
            history.Record(before);
            MarkChanged();
            RestartPreviewIfPlaying();
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

            EditorGUI.BeginChangeCheck();
            float nextBeat = EditorGUILayout.FloatField("拍", beat);
            float nextTime = EditorGUILayout.FloatField("時刻 (ms)", time);
            float nextX = EditorGUILayout.FloatField("X", x);
            float nextY = EditorGUILayout.FloatField("Y", y);
            int nextTypeIndex = EditorGUILayout.Popup("種類", typeIndex, TypeLabels);
            int nextColorIndex = EditorGUILayout.Popup("色 / 手", colorIndex, ColorLabels);
            int nextDirectionIndex = EditorGUILayout.Popup("方向", directionIndex, DirectionLabels);
            int nextCount = nextTypeIndex == 2
                ? EditorGUILayout.IntSlider("カット回数", count, 2, 20)
                : 1;
            // Long の長さ。自動 = 本編既定の (回数-1)×0.7秒。手動なら拍数で直接指定できる。
            bool autoLength = note.lengthMs <= 0f;
            bool nextAutoLength = autoLength;
            float nextLengthBeats = 0f;
            if (nextTypeIndex == 2)
            {
                nextAutoLength = EditorGUILayout.Toggle("長さ自動 (回数×0.7s)", autoLength);
                float beatMs = 60000f / Mathf.Max(1f, document.bpm);
                float effectiveMs = note.lengthMs > 0f
                    ? note.lengthMs
                    : (Mathf.Max(2, nextCount) - 1) * SaberChartUtility.DefaultSecondsPerLongCut * 1000f;
                float shownBeats = effectiveMs / beatMs;
                using (new EditorGUI.DisabledScope(nextAutoLength))
                {
                    nextLengthBeats = EditorGUILayout.FloatField("長さ (拍)", shownBeats);
                }
                GUILayout.Label($"実効: {effectiveMs / 1000f:0.00}s", smallMutedStyle);
            }
            if (EditorGUI.EndChangeCheck())
            {
                string before = CurrentJson();
                SaberChartNote selected = note;
                bool beatEdited = !Mathf.Approximately(nextBeat, beat);
                bool timeEdited = !Mathf.Approximately(nextTime, time);
                selected.beat = Mathf.Max(0f, nextBeat);
                selected.time = Mathf.Max(0f, nextTime);
                if (beatEdited && !timeEdited)
                    selected.time = SaberChartUtility.BeatToTimeMs(selected.beat, document.bpm, beatZeroMs);
                else if (timeEdited)
                    selected.beat = SaberChartUtility.TimeMsToBeat(selected.time, document.bpm, beatZeroMs);
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
                    float beatMs = 60000f / Mathf.Max(1f, document.bpm);
                    selected.lengthMs = Mathf.Max(60f, nextLengthBeats * beatMs);
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
                history.Record(before);
                MarkChanged();
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
            EditorGUI.DrawRect(rect, new Color(0.018f, 0.026f, 0.044f));
            Rect laneRect = new Rect(rect.x + TimelineGutter, rect.y, rect.width - TimelineGutter - 12f, rect.height);
            if (laneRect.width < 80f) return;

            DrawLaneBackgrounds(laneRect);
            DrawWaveform(rect, laneRect);
            DrawBeatGrid(rect, laneRect);
            DrawNotes(laneRect);
            DrawPlayhead(rect, laneRect);
            HandleTimelineInput(rect, laneRect, Event.current);
            DrawOutline(rect, new Color(0.12f, 0.2f, 0.28f), 1f);
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

            float first = Mathf.Floor(minBeat / step) * step;
            for (float beat = first; beat <= maxBeat + step * 0.5f; beat += step)
            {
                float y = YForBeat(beat, timelineRect);
                bool wholeBeat = Mathf.Abs(beat - Mathf.Round(beat)) < step * 0.25f;
                bool measure = wholeBeat && Mathf.RoundToInt(beat) % Mathf.Max(1, beatsPerMeasure) == 0;
                Color color = measure
                    ? new Color(0.28f, 0.92f, 1f, 0.72f)
                    : wholeBeat
                        ? new Color(0.8f, 0.9f, 1f, 0.28f)
                        : new Color(0.65f, 0.75f, 0.84f, 0.10f);
                float thickness = measure ? 2f : 1f;
                EditorGUI.DrawRect(new Rect(laneRect.x, y, laneRect.width, thickness), color);

                if (wholeBeat)
                {
                    int measureNumber = Mathf.FloorToInt(beat / Mathf.Max(1, beatsPerMeasure)) + 1;
                    int beatNumber = Mathf.FloorToInt(beat) % Mathf.Max(1, beatsPerMeasure) + 1;
                    GUI.Label(
                        new Rect(timelineRect.x + 2f, y - 9f, TimelineGutter - 6f, 18f),
                        $"{measureNumber}:{beatNumber}",
                        smallMutedStyle);
                }
            }
        }

        private void DrawNotes(Rect laneRect)
        {
            if (document?.notes == null) return;
            for (int index = 0; index < document.notes.Count; index++)
            {
                SaberChartNote note = document.notes[index];
                Rect noteRect = NoteRect(note, laneRect);
                if (noteRect.yMax < laneRect.y - 100f || noteRect.y > laneRect.yMax + 100f) continue;

                Color color = NoteColor(note.color);
                if (note.type == SaberChartUtility.TypeLong)
                {
                    float durationBeats = Mathf.Max(0f,
                        SaberChartUtility.EffectiveLongLengthMs(note) / 1000f * document.bpm / 60f);
                    float timelineBeat = TimelineBeat(note);
                    float endY = YForBeat(timelineBeat + durationBeats, laneRect);
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
                SaberChartUtility.FormatMusicalPosition(currentBeat, beatsPerMeasure, CurrentSnap),
                new GUIStyle(smallMutedStyle) { normal = { textColor = AccentColor } });
        }

        private void HandleTimelineInput(Rect timelineRect, Rect laneRect, Event current)
        {
            if (!timelineRect.Contains(current.mousePosition))
            {
                if (current.type == EventType.MouseUp) EndNoteDrag();
                return;
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
                    SeekToBeat(currentBeat + current.delta.y * SaberChartUtility.SnapStep(CurrentSnap) * 2f);
                }
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
                int hit = FindNoteAt(current.mousePosition, laneRect);
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
                        draggingNote = true;
                        dragNoteIndex = hit;
                        dragRecorded = false;
                        dragSnapshot = CurrentJson();
                    }
                }
                else if (editTool == EditTool.Draw && laneRect.Contains(current.mousePosition))
                {
                    AddNoteAtMouse(current.mousePosition, timelineRect, laneRect);
                }
                else
                {
                    selectedIndex = -1;
                    SeekToBeat(SaberChartUtility.QuantizeBeat(BeatAtY(current.mousePosition.y, timelineRect), CurrentSnap));
                }
                current.Use();
                return;
            }

            if (current.type == EventType.MouseDrag && draggingNote && dragNoteIndex >= 0 &&
                dragNoteIndex < document.notes.Count)
            {
                if (!dragRecorded)
                {
                    history.Record(dragSnapshot);
                    dragRecorded = true;
                }

                SaberChartNote note = document.notes[dragNoteIndex];
                int lane = LaneAtX(current.mousePosition.x, laneRect);
                float beat = SaberChartUtility.QuantizeBeat(BeatAtY(current.mousePosition.y, timelineRect), CurrentSnap);
                note.x = SaberChartUtility.CoordinateForLane(lane, LaneCount,
                    SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax);
                note.beat = beat;
                note.time = SaberChartUtility.BeatToTimeMs(beat, document.bpm, beatZeroMs);
                paletteXLane = lane;
                // ドラッグ中にJSON化すると時刻ソートでindexが変わるため、確定までは並べ替えない。
                hasUnsavedChanges = true;
                Repaint();
                current.Use();
                return;
            }

            if (current.type == EventType.MouseUp && current.button == 0)
            {
                EndNoteDrag();
                current.Use();
            }
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
            float beat = SaberChartUtility.QuantizeBeat(BeatAtY(mouse.y, timelineRect), CurrentSnap);
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
                time = SaberChartUtility.BeatToTimeMs(beat, document.bpm, beatZeroMs),
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
            int displayLane = SaberChartUtility.LaneForCoordinate(
                note.x,
                LaneCount,
                SaberChartUtility.DefaultXMin,
                SaberChartUtility.DefaultXMax);
            float centerX = laneRect.x + (displayLane + 0.5f) * laneRect.width / LaneCount;
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
            else if (current.keyCode == KeyCode.Space)
            {
                TogglePreview();
                current.Use();
            }
            else if (current.keyCode == KeyCode.Delete || current.keyCode == KeyCode.Backspace)
            {
                DeleteSelected();
                current.Use();
            }
            else if (current.keyCode == KeyCode.LeftArrow || current.keyCode == KeyCode.RightArrow)
            {
                float direction = current.keyCode == KeyCode.RightArrow ? 1f : -1f;
                float amount = current.shift
                    ? Mathf.Max(1, beatsPerMeasure)
                    : SaberChartUtility.SnapStep(CurrentSnap);
                SeekToBeat(currentBeat + direction * amount);
                current.Use();
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
            if (!ConfirmAbandonChanges()) return;
            StopPreview(false);
            document = new SaberChartDocument();
            beatZeroMs = 0f;
            currentBeat = 0f;
            selectedIndex = -1;
            loadedSongId = null;
            loadedDifficulty = null;
            history.Clear();
            savedJson = null;
            hasUnsavedChanges = true;
            SetStatus("新しい譜面を作成しました");
        }

        private void LoadDocument()
        {
            if (!ConfirmAbandonChanges()) return;
            try
            {
                StopPreview(false);
                document = SaberChartFileStore.Load(songId, CurrentDifficulty, out string loadedPath);
                beatZeroMs = SaberChartUtility.EstimateBeatZeroMs(document);
                currentBeat = 0f;
                selectedIndex = -1;
                loadedSongId = songId.Trim();
                loadedDifficulty = CurrentDifficulty;
                history.Clear();
                savedJson = CurrentJson();
                hasUnsavedChanges = false;
                EnsureAudioForSong(false);
                SetStatus(loadedPath == null ? "空の譜面を開きました" : $"読込: {Path.GetFileName(loadedPath)}");
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("譜面を読み込めません", exception.Message, "OK");
            }
        }

        private bool SaveDocument()
        {
            try
            {
                string destination = SaberChartFileStore.ChartPath(songId, CurrentDifficulty);
                if (DestinationChanged() && !string.IsNullOrEmpty(destination) && File.Exists(destination))
                {
                    bool overwrite = EditorUtility.DisplayDialog(
                        "別の保存先を上書きしますか？",
                        $"現在開いている譜面とは別の保存先です。\n{destination}\n\nこのファイルを上書きしますか？",
                        "上書きする",
                        "キャンセル");
                    if (!overwrite) return false;
                }

                // 本編で権威値となる time は動かさず、可読用 beat だけ現在のグリッドへ同期する。
                SaberChartUtility.RecalculateBeatsFromTimes(document, beatZeroMs);
                SaberChartUtility.Normalize(document);
                destination = SaberChartFileStore.Save(document, songId, CurrentDifficulty);
                loadedSongId = songId.Trim();
                loadedDifficulty = CurrentDifficulty;
                savedJson = CurrentJson();
                hasUnsavedChanges = false;
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
            int choice = EditorUtility.DisplayDialogComplex(
                "未保存の変更",
                "現在の譜面を保存してから続けますか？",
                "保存",
                "キャンセル",
                "保存しない");
            if (choice == 0) return SaveDocument();
            if (choice != 2) return false;
            hasUnsavedChanges = false;
            return true;
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
                        if (!ConfirmAbandonChanges()) return;
                        songId = captured;
                        LoadDocument();
                    });
                }
            }
            menu.ShowAsContext();
        }

        private void RevealSongFolder()
        {
            string folder = SaberChartFileStore.SongFolderPath(songId);
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
            AudioClip found = SaberChartFileStore.LoadAudioClip(songId);
            SetAudioClip(found);
            if (notify) SetStatus(found != null ? "曲フォルダの音源を読み込みました" : "音源が見つかりません");
        }

        private void ImportAudio()
        {
            if (!SaberChartFileStore.IsValidSongId(songId, out string reason))
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
                SetAudioClip(SaberChartFileStore.ImportAudio(source, songId, true));
                SetStatus("音源を取り込みました");
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("音源を取り込めません", exception.Message, "OK");
            }
        }

        private void HandleAudioClipSelection(AudioClip clip)
        {
            if (clip == null || SaberChartFileStore.IsAudioClipForSong(clip, songId))
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
                SetAudioClip(SaberChartFileStore.ImportAudio(sourcePath, songId, true));
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
            audioClip = clip;
            waveform.Build(clip);
            Repaint();
        }

        private void TogglePreview()
        {
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
            if (!SaberChartAudioPreview.Play(audioClip, seconds))
            {
                EditorUtility.DisplayDialog("音源を再生できません", SaberChartAudioPreview.LastError ?? "不明なエラー", "OK");
                return;
            }
            playbackEditorStart = EditorApplication.timeSinceStartup;
            playbackAudioStartSeconds = seconds;
            isPlaying = true;
            Repaint();
        }

        private void StopPreview(bool resetToStart)
        {
            SaberChartAudioPreview.Stop();
            isPlaying = false;
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
            if (!isPlaying) return;
            UpdatePlaybackPosition();
            Repaint();
        }

        private void UpdatePlaybackPosition()
        {
            if (!isPlaying) return;
            float elapsed = (float)(EditorApplication.timeSinceStartup - playbackEditorStart);
            float audioSeconds = playbackAudioStartSeconds + elapsed;
            if (audioClip == null || audioSeconds >= audioClip.length)
            {
                StopPreview(false);
                return;
            }
            currentBeat = SaberChartUtility.TimeMsToBeat(
                audioSeconds * 1000f - document.offsetMs,
                document.bpm,
                beatZeroMs);
        }

        private void SeekToBeat(float beat)
        {
            currentBeat = Mathf.Clamp(beat, 0f, MaxBeat());
            if (isPlaying) RestartPreviewIfPlaying();
            Repaint();
        }

        private float BeatToAudioSeconds(float beat)
        {
            return (SaberChartUtility.BeatToTimeMs(beat, document.bpm, beatZeroMs) + document.offsetMs) / 1000f;
        }

        private float MaxBeat()
        {
            float notesMax = document?.notes != null && document.notes.Count > 0
                ? document.notes.Max(TimelineBeat) + 8f
                : 32f;
            if (audioClip == null) return Mathf.Max(32f, notesMax);
            float audioBeat = SaberChartUtility.TimeMsToBeat(
                audioClip.length * 1000f - document.offsetMs,
                document.bpm,
                beatZeroMs);
            return Mathf.Max(4f, notesMax, audioBeat);
        }

        private void Undo()
        {
            if (!history.CanUndo) return;
            StopPreview(false);
            document = history.Undo(document);
            beatZeroMs = SaberChartUtility.EstimateBeatZeroMs(document);
            selectedIndex = -1;
            UpdateDirtyState();
            SetStatus("元に戻しました");
        }

        private void Redo()
        {
            if (!history.CanRedo) return;
            StopPreview(false);
            document = history.Redo(document);
            beatZeroMs = SaberChartUtility.EstimateBeatZeroMs(document);
            selectedIndex = -1;
            UpdateDirtyState();
            SetStatus("やり直しました");
        }

        private void DeleteSelected()
        {
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
            copy.time = SaberChartUtility.BeatToTimeMs(copy.beat, document.bpm, beatZeroMs);
            document.notes.Add(copy);
            SaberChartUtility.SortNotes(document);
            selectedIndex = document.notes.IndexOf(copy);
            history.Record(before);
            MarkChanged();
            SetStatus("ノーツを複製しました");
        }

        private void TestInGame()
        {
            if (!SaveDocument()) return;
            AudioClip packagedClip = SaberChartFileStore.LoadAudioClip(songId);
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

            SaberChartTestPlayBridge.Queue(
                songId.Trim(),
                DifficultyLabels[Mathf.Clamp(difficultyIndex, 0, DifficultyLabels.Length - 1)]);
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

            AudioClip packagedClip = SaberChartFileStore.LoadAudioClip(songId);
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
            GUI.Label(new Rect(rect.x + 10f, rect.y + 4f, rect.width * 0.42f, 20f), visibleStatus, smallMutedStyle);
            GUI.Label(
                new Rect(rect.x + rect.width * 0.42f, rect.y + 4f, rect.width * 0.57f - 10f, 20f),
                "Space 再生  /  Ctrl+S 保存  /  Ctrl+Z/Y 履歴  /  Wheel シーク  /  Ctrl+Wheel ズーム",
                new GUIStyle(smallMutedStyle) { alignment = TextAnchor.MiddleRight });
        }

        private void MarkChanged()
        {
            UpdateDirtyState();
            Repaint();
        }

        private void UpdateDirtyState()
        {
            hasUnsavedChanges = string.IsNullOrEmpty(savedJson) || CurrentJson() != savedJson;
        }

        private string CurrentJson()
        {
            return SaberChartUtility.ToJson(document, false);
        }

        private bool DestinationChanged()
        {
            if (string.IsNullOrEmpty(loadedSongId) || string.IsNullOrEmpty(loadedDifficulty)) return true;
            return !string.Equals(loadedSongId, songId?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                   !string.Equals(loadedDifficulty, CurrentDifficulty, StringComparison.OrdinalIgnoreCase);
        }

        private float TimelineBeat(SaberChartNote note)
        {
            return note == null
                ? 0f
                : SaberChartUtility.TimeMsToBeat(note.time, document.bpm, beatZeroMs);
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
            beatsPerMeasure = EditorPrefs.GetInt(PrefPrefix + "Measure", beatsPerMeasure);
            pixelsPerBeat = EditorPrefs.GetFloat(PrefPrefix + "Zoom", pixelsPerBeat);
            difficultyIndex = Mathf.Clamp(difficultyIndex, 0, DifficultyValues.Length - 1);
            snapIndex = Mathf.Clamp(snapIndex, 0, SnapDenominators.Length - 1);
            beatsPerMeasure = Mathf.Clamp(beatsPerMeasure, 1, 16);
            pixelsPerBeat = Mathf.Clamp(pixelsPerBeat, 30f, 240f);
        }

        private void SavePreferences()
        {
            EditorPrefs.SetString(PrefPrefix + "SongId", songId ?? string.Empty);
            EditorPrefs.SetInt(PrefPrefix + "Difficulty", difficultyIndex);
            EditorPrefs.SetInt(PrefPrefix + "Snap", snapIndex);
            EditorPrefs.SetInt(PrefPrefix + "Measure", beatsPerMeasure);
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
            GameSession.SelectedSongTitle = songId;
            GameSession.SelectedDifficulty = difficulty;
            GameSession.IsCalibrationMode = false;
        }
    }
}
