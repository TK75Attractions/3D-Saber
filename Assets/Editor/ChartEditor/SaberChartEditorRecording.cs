using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    public sealed partial class SaberChartEditorWindow
    {
        private static readonly KeyCode[] RecordKeys = { KeyCode.D, KeyCode.F, KeyCode.G, KeyCode.J, KeyCode.K };
        private static readonly string[] RecordLabels = { "D  青1", "F  青2", "G  金", "J  赤1", "K  赤2" };
        private static readonly float[] RecordX = { -1.7857143f, -.7142857f, 0f, .7142857f, 1.7857143f };
        private static readonly string[] RecordColors = { "blue", "blue", "gold", "red", "red" };
        private static readonly KeyCode[] RecordChordKeys = { KeyCode.H, KeyCode.B };
        private static readonly int[,] RecordChordPads = { { 1, 3 }, { 0, 4 } };
        private static readonly string[] RecordChordLabels = { "H  同時打ち F＋J", "B  同時打ち D＋K" };
        // 単打キー(0～4)、単打ボタン(5～9)、XY入力(10～14)とは別の保持状態を使う。
        private const int RecordChordKeyInput = 20;
        private const int RecordChordMouseInput = 30;
        [SerializeField] private bool recordMode;
        [SerializeField] private bool recordSnap = true;
        [SerializeField] private bool recordHold = true;
        [SerializeField] private bool recordCountIn = true;
        [SerializeField] private float recordInputOffsetMs;
        [SerializeField] private int recordHeight = 1;
        [SerializeField] private int recordLongCount = 2;
        [SerializeField] private Vector2[] recordPositions;
        [SerializeField] private int recordActivePad = 1;
        [SerializeField] private int recordPositionGrid = 2;
        [SerializeField] private bool recordPositionOnly;
        [SerializeField] private bool recordMirrorPositions;
        [SerializeField] private bool recordAdvancedSettings;
        [SerializeField] private bool recordHelpExpanded;
        [SerializeField] private bool recordStepMode;
        [SerializeField] private bool recordStepAdvance = true;
        [SerializeField] private RecordingPositionPreset[] recordPositionPresets;
        private readonly HashSet<int> recordStepHeld = new HashSet<int>();
        private readonly HashSet<KeyCode> recordStepActionKeys = new HashSet<KeyCode>();
        private float recordStepBeat;
        private float recordStepNextBeat;
        private bool recordStepAdded;
        private bool recordRetryKeyDown;
        private string recordTakeBeforeJson;
        private string recordTakeAfterJson;
        private float recordTakeGridOriginMs;
        [Serializable]
        private sealed class RecordingPositionPreset
        {
            public Vector2[] positions;
            public int grid;
            public bool mirrorPositions;
        }
        private static readonly int[] PositionDivisions = { 0, 4, 8, 16, 32 };
        private static readonly string[] PositionGridLabels = { "自由配置", "4分割", "8分割", "16分割", "32分割" };
        private static readonly string[] PositionKeyLabels = { "1: D", "2: F", "3: G", "4: J", "5: K" };
        private int recordXYButton = -1;
        private int recordXYInput = -1;
        private int recordXYPad = -1;
        private bool recordXYAdjusting;
        private Vector2 recordXYDragOffset;
        [Serializable]
        private sealed class RecordingLayout
        {
            public Vector2[] positions;
            public int grid = 2;
            public int activePad = 1;
            public bool positionOnly;
            public bool mirrorPositions;
            public RecordingPositionPreset[] presets;
        }
        private SaberChartRecorder recorder;
        private bool countingIn;
        private double countInEndsAt;
        private AudioClip countInClip;
        private float lastRecordingSeconds;
        private int recordMousePad = -1;
        private int recordMouseChord = -1;
        private readonly double[] recordFlashes = new double[5];
        private readonly bool[] recordKeysDown = new bool[5];
        private readonly bool[] recordChordKeysDown = new bool[2];
        private readonly bool[] recordPresetKeysDown = new bool[3];
        private bool recordUndoKeyDown;
        private bool recordReviewKeyDown;
        private float recordReviewBeat;
        private float recordReviewAudioSeconds;
        private SaberChartDocument recordReviewDocument;
        private AudioClip recordReviewClip;
        // 音声時計だけを差し替えられるようにし、入力経路を音声機器なしでも検証する。
        private Func<float?> recordingClockOverride;
        private bool RecordingBusy => countingIn || recorder != null;
        private bool CanStepInput => recordMode && recordStepMode && !RecordingBusy && !isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode;
        private bool CanReviewRecording => !RecordingBusy && audioClip != null && recordReviewClip == audioClip &&
            recordReviewDocument == document && !EditorApplication.isPlaying;

        private void SetRecordingMode(bool enabled)
        {
            if (RecordingBusy) return;
            EndNoteDrag();
            FinishTextEditing();
            ClearRecordingStepInput();
            recordMode = enabled;
            if (!enabled)
            {
                Array.Clear(recordKeysDown, 0, recordKeysDown.Length);
                Array.Clear(recordChordKeysDown, 0, recordChordKeysDown.Length);
            }
            recordReviewKeyDown = false;
            rightScroll = Vector2.zero;
            SetStatus(!enabled ? "タイムラインで譜面を編集できます" : recordStepMode
                ? "D / F / G / J / Kで現在の拍へ配置。H / Bは同時打ち、Tabで次の拍へ"
                : "マーカーを動かして配置を準備 → Rで録音。H / Bは同時打ちです");
        }

        private void SetRecordingInputMode(int mode)
        {
            if (RecordingBusy) return;
            recordStepMode = mode == 2;
            SetRecordingMode(mode != 0);
        }

        private void DrawRecordingSettings()
        {
            GUILayout.Space(10);
            SectionLabel(recordStepMode && recordMode ? "停止して1ステップずつ入力" : "演奏して譜面を作る");
            if (!recordMode)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("録音で作る", GUILayout.Height(28))) SetRecordingInputMode(1);
                if (GUILayout.Button("ステップで作る", GUILayout.Height(28))) SetRecordingInputMode(2);
                GUILayout.EndHorizontal();
                return;
            }
            if (recordStepMode)
            {
                recordStepAdvance = EditorGUILayout.ToggleLeft("キーを離すと次のSnapへ進む", recordStepAdvance);
                using (new EditorGUI.DisabledScope(!CanStepInput))
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("← 前へ")) MoveRecordingStep(-1);
                    if (GUILayout.Button("次へ / 休符 →")) MoveRecordingStep(1);
                    GUILayout.EndHorizontal();
                }
                GUILayout.Label($"現在 {SaberChartUtility.FormatMusicalPosition(currentBeat, document, CurrentSnap)}", centeredSmallStyle);
                DrawRecordingStepActions();
                EditorGUILayout.HelpBox("音源なしでも入力できます。H / Bで同時打ち。\nTabで次へ、Shift+Tabで前へ。\n種類・方向・LONG回数は左側で選びます。", MessageType.None);
            }
            else
            {
                EditorGUI.BeginDisabledGroup(RecordingBusy);
                recordCountIn = EditorGUILayout.ToggleLeft("開始前に4拍カウント", recordCountIn);
                recordSnap = EditorGUILayout.ToggleLeft("入力を左のSnapへそろえる", recordSnap);
                recordHold = EditorGUILayout.ToggleLeft("250ms以上の長押しをLONGにする", recordHold);
                recordAdvancedSettings = EditorGUILayout.Foldout(recordAdvancedSettings, "詳細設定（LONG回数・入力補正）", true);
                if (recordAdvancedSettings)
                {
                    recordLongCount = EditorGUILayout.IntSlider("LONGのカット回数", recordLongCount, 2, 12);
                    recordInputOffsetMs = EditorGUILayout.FloatField(new GUIContent("入力補正 (ms)",
                        "押すのが遅れる場合は正の値を指定します。個人のゲーム表示補正は録音時刻へ適用しません。"), recordInputOffsetMs);
                    if (float.IsNaN(recordInputOffsetMs) || float.IsInfinity(recordInputOffsetMs)) recordInputOffsetMs = 0;
                    recordInputOffsetMs = Mathf.Clamp(recordInputOffsetMs, -1000, 1000);
                }
                EditorGUI.EndDisabledGroup();
                using (new EditorGUI.DisabledScope(!CanRetryRecording()))
                    if (GUILayout.Button(new GUIContent("↶ 今回を録り直す  Shift+R", "今回の録音分を取り消して、同じ開始位置から録音し直します。Ctrl+Zで元の録音も戻せます。"), GUILayout.Height(26))) RetryRecording();
            }

            GUILayout.Space(8);
            SectionLabel("打ち込み位置 / 録音中も変更可");
            EnsureRecordingPositions();
            int selected = GUILayout.Toolbar(recordActivePad, PositionKeyLabels);
            if (selected != recordActivePad) SelectRecordingPad(selected);
            recordPositionGrid = EditorGUILayout.Popup("位置グリッド", recordPositionGrid, PositionGridLabels);
            Vector2 position = RecordingPosition(recordActivePad);
            EditorGUI.BeginChangeCheck();
            float x = EditorGUILayout.DelayedFloatField("X（横） -2.5 ～ 2.5", position.x);
            float y = EditorGUILayout.DelayedFloatField("Y（高さ） -1.5 ～ 1.5", position.y);
            if (EditorGUI.EndChangeCheck()) SetRecordingPosition(recordActivePad, new Vector2(x, y), false);
            GUILayout.BeginHorizontal();
            GUILayout.Label("高さ", GUILayout.Width(30));
            if (GUILayout.Button("低")) SetRecordingHeight(-.8571429f);
            if (GUILayout.Button("中")) SetRecordingHeight(0);
            if (GUILayout.Button("高")) SetRecordingHeight(.8571429f);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(recordActivePad == 2))
                if (GUILayout.Button("反対色へ左右反転")) MirrorRecordingPosition();
            if (GUILayout.Button("初期配置"))
            {
                recordHeight = 1;
                recordPositions = null;
                EnsureRecordingPositions();
                FinishTextEditing();
            }
            GUILayout.EndHorizontal();
            DrawRecordingPresets();
            recordHelpExpanded = EditorGUILayout.Foldout(recordHelpExpanded, "操作ガイド / ショートカット", true);
            if (recordHelpExpanded)
            {
                EditorGUILayout.HelpBox("マーカーを左ドラッグ: 位置調整\n左右連動: D↔K / F↔Jを対称移動\n1～5: 調整するキーを選択\nAlt+矢印: 移動 / Alt+Shift+矢印: 0.01刻み\n矢印だけはシーク（録音中は無効）\n空白クリック: 左=青 / 右=赤 / Shift+左=金\nAlt+ドラッグや位置調整モードでは入力しません。", MessageType.None);
                EditorGUILayout.HelpBox("R: 録音開始 / Shift+R: 今回を録り直す\nSpace・Esc: 録音終了\nH: F＋J / B: D＋Kの同時打ち\nBackspace: 直前の1打を取り消し（ステップも）\nテンキー: 録音中は次に打つノーツの方向\nEnter: 録音開始位置から聴き直し\nF1～F3: 配置呼出 / Shift付き: 配置保存\nCtrl+Zは録音を止め、1回分を取り消します。", MessageType.None);
            }
        }

        private Rect DrawRecordingPad(Rect rect)
        {
            if (!recordMode) return rect;
            EnsureRecordingPositions();
            Rect panel = new Rect(rect.x, rect.y, rect.width, Mathf.Min(426, rect.height * .68f));
            EditorGUI.DrawRect(panel, PanelColor);
            string label = countingIn ? $"開始まで {Mathf.Clamp(Mathf.CeilToInt((float)(countInEndsAt - EditorApplication.timeSinceStartup) * document.bpm / 60f), 1, 4)}"
                : recorder != null ? $"● 録音中  +{recorder.AddedCount} NOTES"
                : recordStepMode ? $"ステップ {SaberChartUtility.FormatMusicalPosition(currentBeat, document, CurrentSnap)}" : "演奏して打ち込み";
            GUI.Label(new Rect(rect.x + 10, rect.y + 6, rect.width - 224, 24), label, sectionStyle);
            using (new EditorGUI.DisabledScope(!CanReviewRecording))
                if (GUI.Button(new Rect(rect.xMax - 210, rect.y + 5, 84, 26), new GUIContent("聴き直す ↵", "今回の録音開始位置から再生します（Enter）"))) ReviewRecording();
            using (new EditorGUI.DisabledScope(!RecordingBusy && (audioClip == null || !SaberChartAudioPreview.IsSupported || EditorApplication.isPlaying)))
            {
                if (GUI.Button(new Rect(rect.xMax - 120, rect.y + 5, 110, 26), RecordingBusy ? "■ 録音終了"
                    : recordStepMode ? isPlaying ? "■ 再生停止" : "▶ 確認 [Space]" : "● 録音開始 [R]"))
                {
                    if (RecordingBusy) StopPreview(false);
                    else if (recordStepMode) TogglePreview();
                    else StartRecording();
                }
            }
            float width = (panel.width - 24) / 5;
            int padControl = GUIUtility.GetControlID("SaberRecordingPad".GetHashCode(), FocusType.Passive, panel);
            Event input = Event.current;
            for (int i = 0; i < 5; i++)
            {
                Rect pad = new Rect(panel.x + 8 + width * i, panel.y + 38, width - 4, 52);
                Color color = NoteColor(RecordColors[i]);
                bool lit = RecordingPadHeld(i) ||
                    EditorApplication.timeSinceStartup < recordFlashes[i];
                EditorGUI.DrawRect(pad, Color.Lerp(PanelColor, color, lit ? .85f : .3f));
                if (i == recordActivePad) DrawOutline(pad, color, 2);
                GUI.Label(new Rect(pad.x, pad.y + 2, pad.width, 24), RecordLabels[i], new GUIStyle(EditorStyles.boldLabel)
                    { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } });
                Vector2 xy = RecordingPosition(i);
                GUI.Label(new Rect(pad.x, pad.y + 27, pad.width, 20), $"{xy.x:0.00}, {xy.y:0.00}", centeredSmallStyle);
                if (input.type == EventType.MouseDown && input.button == 0 && pad.Contains(input.mousePosition) &&
                    recordMousePad < 0 && recordMouseChord < 0 && recordXYButton < 0)
                {
                    SelectRecordingPad(i);
                    if (recorder != null || CanStepInput)
                    {
                        recordMousePad = i;
                        GUIUtility.hotControl = padControl;
                        RecordingPress(i + 5, i);
                    }
                    else SetStatus($"{RecordKeys[i]}の位置をXYパッド・数値・矢印で調整できます");
                    input.Use();
                }
            }
            if (input.rawType == EventType.MouseUp && input.button == 0 && recordMousePad >= 0)
            {
                RecordingRelease(recordMousePad + 5);
                recordMousePad = -1;
                GUIUtility.hotControl = 0;
                input.Use();
            }
            DrawRecordingChordPads(panel, input);
            DrawRecordingPositionToolbar(panel);
            DrawRecordingPlane(RecordingPlaneRect(panel));
            GUI.Label(new Rect(panel.x + 10, panel.yMax - 22, panel.width - 20, 18),
                recordStepMode ? "D / F / G / J / Kで配置  ·  H / B 同時打ち  ·  Tab 次へ"
                    : recorder == null ? "マーカーをドラッグして配置を準備  ·  上が高さ +Y  ·  Rで録音"
                    : recordPositionOnly ? "位置調整中  ·  キーでは入力できます  ·  Spaceで録音終了"
                    : "空白クリックで入力 / マーカーは移動  ·  ⌫ 1打戻す  ·  Space終了", smallMutedStyle);
            return new Rect(rect.x, panel.yMax + PanelGap, rect.width, rect.height - panel.height - PanelGap);
        }

        private bool RecordingPadHeld(int pad)
        {
            if (RecordingInputHeld(pad) || RecordingInputHeld(pad + 5) || RecordingInputHeld(pad + 10)) return true;
            for (int chord = 0; chord < RecordChordKeys.Length; chord++)
                for (int member = 0; member < 2; member++)
                    if (RecordChordPads[chord, member] == pad &&
                        (RecordingInputHeld(RecordChordKeyInput + chord * 2 + member) ||
                         RecordingInputHeld(RecordChordMouseInput + chord * 2 + member))) return true;
            return false;
        }

        private bool RecordingInputHeld(int input) => recordStepHeld.Contains(input) || recorder != null && recorder.IsHeld(input);

        private Rect RecordingChordRect(Rect panel, int chord)
        {
            float width = (panel.width - 112) / 2;
            return new Rect(panel.x + 8 + (width + 6) * chord, panel.y + 96, width, 30);
        }

        private Rect RecordingUndoRect(Rect panel) => new Rect(panel.xMax - 90, panel.y + 96, 82, 30);

        private void DrawRecordingChordPads(Rect panel, Event input)
        {
            int control = GUIUtility.GetControlID("SaberRecordingChord".GetHashCode(), FocusType.Passive, panel);
            for (int chord = 0; chord < RecordChordKeys.Length; chord++)
            {
                Rect pad = RecordingChordRect(panel, chord);
                bool lit = RecordingInputHeld(RecordChordKeyInput + chord * 2) || RecordingInputHeld(RecordChordMouseInput + chord * 2);
                EditorGUI.DrawRect(new Rect(pad.x, pad.y, pad.width / 2, pad.height),
                    Color.Lerp(PanelColor, NoteColor("blue"), lit ? .85f : .3f));
                EditorGUI.DrawRect(new Rect(pad.center.x, pad.y, pad.width / 2, pad.height),
                    Color.Lerp(PanelColor, NoteColor("red"), lit ? .85f : .3f));
                GUI.Label(pad, new GUIContent(RecordChordLabels[chord], "青と赤を同じ時刻に入力。長押しで両方LONG。各キーのXY位置を使います。"),
                    new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } });
                if (input.type == EventType.MouseDown && input.button == 0 && pad.Contains(input.mousePosition) &&
                    recordMousePad < 0 && recordMouseChord < 0 && recordXYButton < 0)
                {
                    FinishTextEditing();
                    if (recorder != null || CanStepInput)
                    {
                        recordMouseChord = chord;
                        GUIUtility.hotControl = control;
                        RecordingChordPress(RecordChordMouseInput, chord);
                    }
                    else SetStatus("録音開始後に同時打ちできます。左右の位置は各キーのXYで調整します");
                    input.Use();
                }
            }
            if (input.rawType == EventType.MouseUp && input.button == 0 && recordMouseChord >= 0)
            {
                RecordingChordRelease(RecordChordMouseInput, recordMouseChord);
                recordMouseChord = -1;
                GUIUtility.hotControl = 0;
                input.Use();
            }
            using (new EditorGUI.DisabledScope(recordStepMode ? !CanStepInput || !history.CanUndo : recorder == null || !recorder.CanUndoInput))
                if (GUI.Button(RecordingUndoRect(panel), recordStepMode ? new GUIContent("↶ 戻す", "直前の操作を元に戻します（Ctrl+Z）")
                    : new GUIContent("⌫ 1打戻す", "録音を続けながら直前の入力を取り消します。同時打ちは左右まとめて取り消します（Backspace）")))
                {
                    if (recordStepMode) Undo();
                    else UndoRecordingInput();
                }
        }

        private void DrawRecordingPositionToolbar(Rect panel)
        {
            Rect row = new Rect(panel.x + 8, panel.y + 134, panel.width - 16, 22);
            int mode = GUI.Toolbar(new Rect(row.x, row.y, 160, row.height), recordPositionOnly ? 1 : 0,
                new[] { "クリック入力", "位置調整" });
            if (recordPositionOnly != (mode == 1))
            {
                recordPositionOnly = mode == 1;
                FinishTextEditing();
            }
            bool mirror = GUI.Toggle(new Rect(row.x + 170, row.y, 94, row.height), recordMirrorPositions,
                new GUIContent("左右連動", "選択キーと反対色のキーを左右対称に動かします（D↔K / F↔J）"), EditorStyles.miniButton);
            if (mirror != recordMirrorPositions) SetRecordingMirror(mirror);
            recordPositionGrid = EditorGUI.Popup(new Rect(row.x + 274, row.y + 2, row.width - 274, 18), recordPositionGrid, PositionGridLabels);
        }

        private void EnsureRecordingPositions()
        {
            if (recordPositions == null || recordPositions.Length != RecordKeys.Length)
            {
                // 旧版の高さ設定は初回だけ引き継ぐ。以降は各キーが独立したXYを持つ。
                recordPositions = new Vector2[RecordKeys.Length];
                for (int i = 0; i < recordPositions.Length; i++)
                    recordPositions[i] = new Vector2(RecordX[i], (Mathf.Clamp(recordHeight, 0, 2) - 1) * .8571429f);
            }
            recordActivePad = Mathf.Clamp(recordActivePad, 0, RecordKeys.Length - 1);
            recordPositionGrid = Mathf.Clamp(recordPositionGrid, 0, PositionDivisions.Length - 1);
        }

        private void LoadRecordingLayout()
        {
            if (recordPositions != null || !EditorPrefs.HasKey(PrefPrefix + "RecordingLayout")) return;
            try
            {
                var layout = JsonUtility.FromJson<RecordingLayout>(EditorPrefs.GetString(PrefPrefix + "RecordingLayout"));
                if (layout?.positions == null || layout.positions.Length != RecordKeys.Length) return;
                recordPositionGrid = layout.grid;
                recordActivePad = layout.activePad;
                recordPositionOnly = layout.positionOnly;
                EnsureRecordingPositions();
                recordMirrorPositions = false;
                for (int i = 0; i < recordPositions.Length; i++) SetRecordingPosition(i, layout.positions[i], false);
                recordMirrorPositions = layout.mirrorPositions;
                recordPositionPresets = layout.presets;
            }
            catch (ArgumentException) { /* 壊れた個人設定は既定の配置へ戻す。譜面ファイルには触れない。 */ }
        }

        private void SaveRecordingLayout()
        {
            if (recordPositions == null) return;
            EditorPrefs.SetString(PrefPrefix + "RecordingLayout", JsonUtility.ToJson(new RecordingLayout
            {
                positions = recordPositions, grid = recordPositionGrid, activePad = recordActivePad,
                positionOnly = recordPositionOnly, mirrorPositions = recordMirrorPositions,
                presets = recordPositionPresets,
            }));
        }

        private void EnsureRecordingPresets()
        {
            if (recordPositionPresets == null || recordPositionPresets.Length != 3)
                recordPositionPresets = new RecordingPositionPreset[3];
        }

        private bool HasRecordingPreset(int slot)
        {
            EnsureRecordingPresets();
            if (slot < 0 || slot >= recordPositionPresets.Length) return false;
            var preset = recordPositionPresets[slot];
            if (preset?.positions == null || preset.positions.Length != RecordKeys.Length) return false;
            foreach (var p in preset.positions)
                if (float.IsNaN(p.x) || float.IsInfinity(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.y)) return false;
            return true;
        }

        private void SaveRecordingPreset(int slot)
        {
            EnsureRecordingPresets();
            EnsureRecordingPositions();
            if (slot < 0 || slot >= recordPositionPresets.Length) return;
            recordPositionPresets[slot] = new RecordingPositionPreset
            {
                positions = (Vector2[])recordPositions.Clone(), grid = recordPositionGrid, mirrorPositions = recordMirrorPositions,
            };
            SaveRecordingLayout();
            FinishTextEditing();
            SetStatus($"配置{(char)('A' + slot)}を保存しました。F{slot + 1}で呼び出せます");
        }

        private void RecallRecordingPreset(int slot)
        {
            if (!HasRecordingPreset(slot)) { SetStatus("この配置は未登録です。横の保存ボタンかShift+F1～F3で登録できます"); return; }
            var preset = recordPositionPresets[slot];
            recordMirrorPositions = false;
            for (int i = 0; i < RecordKeys.Length; i++) SetRecordingPosition(i, preset.positions[i], false);
            recordMirrorPositions = preset.mirrorPositions;
            recordPositionGrid = Mathf.Clamp(preset.grid, 0, PositionDivisions.Length - 1);
            FinishTextEditing();
            SetStatus($"配置{(char)('A' + slot)}へ切り替えました。次の入力から適用します");
        }

        private void DrawRecordingPresets()
        {
            GUILayout.Space(6);
            SectionLabel("配置メモリー / 録音中も切替可");
            GUILayout.BeginHorizontal();
            for (int slot = 0; slot < 3; slot++)
            {
                string name = ((char)('A' + slot)).ToString();
                using (new EditorGUI.DisabledScope(!HasRecordingPreset(slot)))
                    if (GUILayout.Button(new GUIContent(name, $"配置{name}を呼び出す（F{slot + 1}）"), GUILayout.MinWidth(30))) RecallRecordingPreset(slot);
                if (GUILayout.Button(new GUIContent("保存", $"現在の5キーの配置を{name}に登録（Shift+F{slot + 1}）"), GUILayout.Width(36))) SaveRecordingPreset(slot);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("F1～F3で呼出 / Shift付きで保存", smallMutedStyle);
        }

        private float RecordingStepNeighbor(float beat, int direction)
        {
            if (direction < 0 && beat <= 0) return 0;
            float step = SaberChartUtility.SnapStep(CurrentSnap);
            var bar = new ChartMeterMap(document.timeSignatures).At(direction > 0 ? beat : Math.Max(0, beat - .0001));
            // 分母が8の小節や小節途中の拍子変更でも、小節頭を飛び越さない。
            double cell = (beat - bar.BarStart) / step;
            return direction > 0
                ? (float)Math.Min(bar.BarEnd, bar.BarStart + (Math.Floor(cell + .00001) + 1) * step)
                : (float)Math.Max(bar.BarStart, bar.BarStart + (Math.Ceiling(cell - .00001) - 1) * step);
        }

        private void MoveRecordingStep(int direction)
        {
            if (!CanStepInput) return;
            float next = RecordingStepNeighbor(currentBeat, direction);
            SeekToBeat(next);
            FinishTextEditing();
        }

        private List<SaberChartNote> RecordingStepNotes()
        {
            float time = TimeAtBeat(currentBeat);
            return document.notes.FindAll(note => Mathf.Abs(note.time - time) < 1f);
        }

        private void DrawRecordingStepActions()
        {
            int count = RecordingStepNotes().Count;
            GUILayout.Label($"この拍のノーツ: {count}個 / 同時打ちをまとめて編集", smallMutedStyle);
            using (new EditorGUI.DisabledScope(!CanStepInput || recordStepHeld.Count > 0 || count == 0))
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("次へコピー", "この拍のノーツを次のSnapへコピー（Ctrl+D）。同じ位置・色の既存ノーツは残します。"))) EditRecordingStep(0);
                if (GUILayout.Button(new GUIContent("左右反転", "この拍のX・赤青・カット方向を左右反転（Ctrl+M）"))) EditRecordingStep(1);
                if (GUILayout.Button(new GUIContent("削除", "この拍のノーツをまとめて削除（Shift+Delete）。Ctrl+Zで戻せます。"))) EditRecordingStep(2);
                GUILayout.EndHorizontal();
            }
            using (new EditorGUI.DisabledScope(!CanStepInput || recordStepHeld.Count > 0))
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("‹ 前のノーツ", "PageUp: 配置済みの前の時刻へ"))) JumpRecordingStep(-1);
                if (GUILayout.Button(new GUIContent("次のノーツ ›", "PageDown: 配置済みの次の時刻へ"))) JumpRecordingStep(1);
                GUILayout.EndHorizontal();
            }
        }

        private void JumpRecordingStep(int direction)
        {
            if (!CanStepInput || recordStepHeld.Count > 0) return;
            JumpToNote(direction);
        }

        private void EditRecordingStep(int action)
        {
            if (!CanStepInput || recordStepHeld.Count > 0 || action < 0 || action > 2) return;
            EditBeatNotes(action);
        }

        private void RecordingStepPress(int input, int firstPad, int secondPad = -1)
        {
            if (!CanStepInput || recordStepHeld.Contains(input)) return;
            EndNoteDrag();
            if (recordStepHeld.Count == 0)
            {
                recordStepBeat = SaberChartUtility.QuantizeBeat(currentBeat, CurrentSnap, document);
                recordStepNextBeat = RecordingStepNeighbor(recordStepBeat, 1);
                recordStepAdded = false;
            }
            string before = CurrentJson();
            float time = TimeAtBeat(recordStepBeat);
            int added = 0;
            for (int member = 0; member < (secondPad < 0 ? 1 : 2); member++)
            {
                int pad = member == 0 ? firstPad : secondPad;
                recordStepHeld.Add(input + member);
                Vector2 xy = RecordingPosition(pad);
                if (document.notes.Exists(n => Mathf.Abs(n.time - time) < 1 && Mathf.Abs(n.x - xy.x) < .0001f &&
                    Mathf.Abs(n.y - xy.y) < .0001f && n.color == RecordColors[pad])) continue;
                document.notes.Add(new SaberChartNote
                {
                    beat = recordStepBeat, time = time, x = xy.x, y = xy.y, color = RecordColors[pad], type = paletteType,
                    direction = paletteType == SaberChartUtility.TypeDirection ? paletteDirection : SaberChartUtility.DirectionNone,
                    count = paletteType == SaberChartUtility.TypeLong ? Mathf.Max(2, paletteCount) : 1,
                });
                recordFlashes[pad] = EditorApplication.timeSinceStartup + .15;
                added++;
            }
            if (added == 0) return;
            recordStepAdded = true;
            selectedIndex = -1;
            SaberChartUtility.SortNotes(document);
            history.Record(before);
            // Backspace で「直前の1打」を取り消したときに戻る拍。
            recordStepLastUndoCount = history.UndoCount;
            recordStepLastBeat = recordStepBeat;
            MarkChanged();
            SetStatus($"{recordStepBeat:0.###}拍に{added}ノーツ配置。Ctrl+Zでこの入力を取り消せます");
        }

        private void RecordingStepRelease(int input, int count = 1)
        {
            bool released = false;
            for (int member = 0; member < count; member++) released |= recordStepHeld.Remove(input + member);
            if (!released || recordStepHeld.Count > 0) return;
            if (CanStepInput && recordStepAdvance && recordStepAdded) currentBeat = Mathf.Min(recordStepNextBeat, MaxBeat());
            recordStepAdded = false;
            Repaint();
        }

        private void ClearRecordingStepInput()
        {
            if (recordStepHeld.Count > 0 && (recordMousePad >= 0 || recordMouseChord >= 0 || recordXYButton >= 0))
            {
                GUIUtility.hotControl = 0;
                recordMousePad = recordMouseChord = -1;
                recordXYButton = recordXYInput = recordXYPad = -1;
            }
            recordStepHeld.Clear();
            recordStepAdded = false;
        }

        private bool CanRetryRecording()
        {
            return !recordStepMode && !countingIn && audioClip != null && recordReviewClip == audioClip &&
                recordReviewDocument == document && !EditorApplication.isPlayingOrWillChangePlaymode &&
                (recorder != null || !string.IsNullOrEmpty(recordTakeBeforeJson));
        }

        private void RetryRecording()
        {
            if (!CanRetryRecording()) return;
            if (recorder == null && CurrentJson() != recordTakeAfterJson)
            {
                SetStatus("録音後に譜面を編集したため録り直しを止めました。編集をUndoするか、Rで追記録音してください");
                return;
            }
            StopPreview(false);
            if (recordTakeBeforeJson != recordTakeAfterJson)
            {
                history.Record(recordTakeAfterJson);
                document = SaberChartUtility.FromJson(recordTakeBeforeJson);
                recordReviewDocument = document;
                selectedIndex = -1;
                MarkChanged();
            }
            // 録音開始時に覚えた原点を使う。先頭ノーツから再推定すると再開位置がずれる。
            beatZeroMs = recordTakeGridOriginMs;
            currentBeat = recordReviewBeat;
            StartRecording();
        }

        private Vector2 RecordingPosition(int pad)
        {
            EnsureRecordingPositions();
            return recordPositions[pad];
        }

        private void SelectRecordingPad(int pad)
        {
            recordActivePad = Mathf.Clamp(pad, 0, RecordKeys.Length - 1);
            FinishTextEditing();
            Repaint();
        }

        private void SetRecordingPosition(int pad, Vector2 position, bool quantize)
        {
            EnsureRecordingPositions();
            if (pad < 0 || pad >= recordPositions.Length ||
                float.IsNaN(position.x) || float.IsInfinity(position.x) ||
                float.IsNaN(position.y) || float.IsInfinity(position.y)) return;
            position.x = Mathf.Clamp(position.x, SaberChartUtility.DefaultXMin, SaberChartUtility.DefaultXMax);
            position.y = Mathf.Clamp(position.y, SaberChartUtility.DefaultYMin, SaberChartUtility.DefaultYMax);
            int divisions = PositionDivisions[recordPositionGrid];
            if (quantize && divisions > 0)
            {
                position.x = Mathf.Round((position.x + 2.5f) / 5f * divisions) * 5f / divisions - 2.5f;
                position.y = Mathf.Round((position.y + 1.5f) / 3f * divisions) * 3f / divisions - 1.5f;
            }
            recordPositions[pad] = position;
            if (recordMirrorPositions && pad != 2)
                recordPositions[4 - pad] = new Vector2(-position.x, position.y);
            Repaint();
        }

        private void SetRecordingMirror(bool enabled)
        {
            recordMirrorPositions = enabled;
            if (enabled) SetRecordingPosition(recordActivePad, RecordingPosition(recordActivePad), false);
            FinishTextEditing();
            Repaint();
        }

        private void SetRecordingHeight(float height)
        {
            Vector2 position = RecordingPosition(recordActivePad);
            SetRecordingPosition(recordActivePad, new Vector2(position.x, height), false);
            FinishTextEditing();
        }

        private void MirrorRecordingPosition()
        {
            EnsureRecordingPositions();
            if (recordActivePad == 2) return;
            Vector2 source = RecordingPosition(recordActivePad);
            SetRecordingPosition(4 - recordActivePad, new Vector2(-source.x, source.y), false);
            FinishTextEditing();
        }

        // 1〜5 で調整するキーを選び、Alt+矢印で位置を動かす(矢印だけはどのモードでもシーク)。
        private bool HandleRecordingPositionKey(Event input)
        {
            bool plain = !input.control && !input.command && !input.alt;
            int number = (int)input.keyCode - (int)KeyCode.Alpha1;
            if (plain && number >= 0 && number < RecordKeys.Length)
            {
                SelectRecordingPad(number);
                input.Use();
                return true;
            }
            if (!input.alt || input.control || input.command) return false;
            Vector2 direction;
            switch (input.keyCode)
            {
                case KeyCode.LeftArrow: direction = Vector2.left; break;
                case KeyCode.RightArrow: direction = Vector2.right; break;
                case KeyCode.UpArrow: direction = Vector2.up; break;
                case KeyCode.DownArrow: direction = Vector2.down; break;
                default: return false;
            }
            EnsureRecordingPositions();
            int divisions = PositionDivisions[recordPositionGrid];
            Vector2 step = input.shift ? Vector2.one * .01f : divisions == 0 ? Vector2.one * .05f : new Vector2(5f, 3f) / divisions;
            // 明示入力した座標は微調整で丸めない。通常の矢印だけグリッドへ合わせる。
            if (!input.shift && divisions > 0) SetRecordingPosition(recordActivePad, RecordingPosition(recordActivePad), true);
            SetRecordingPosition(recordActivePad, RecordingPosition(recordActivePad) + Vector2.Scale(direction, step), false);
            input.Use();
            return true;
        }

        private Rect RecordingPlaneRect(Rect panel)
        {
            float height = Mathf.Min(panel.height - 190, (panel.width - 40) * 3f / 5f);
            return new Rect(panel.center.x - height * 5f / 6f, panel.y + 164, height * 5f / 3f, height);
        }

        private Vector2 RecordingPointToPosition(Rect plane, Vector2 point)
        {
            return new Vector2(
                Mathf.Lerp(-2.5f, 2.5f, Mathf.InverseLerp(plane.xMin, plane.xMax, point.x)),
                Mathf.Lerp(1.5f, -1.5f, Mathf.InverseLerp(plane.yMin, plane.yMax, point.y)));
        }

        private Vector2 RecordingPositionToPoint(Rect plane, Vector2 position)
        {
            return new Vector2(Mathf.Lerp(plane.xMin, plane.xMax, (position.x + 2.5f) / 5f),
                Mathf.Lerp(plane.yMax, plane.yMin, (position.y + 1.5f) / 3f));
        }

        private int RecordingMarkerAt(Rect plane, Vector2 point)
        {
            EnsureRecordingPositions();
            if (Vector2.Distance(RecordingPositionToPoint(plane, RecordingPosition(recordActivePad)), point) <= 12)
                return recordActivePad;
            int found = -1;
            float distance = 12;
            for (int i = 0; i < RecordKeys.Length; i++)
            {
                float next = Vector2.Distance(RecordingPositionToPoint(plane, RecordingPosition(i)), point);
                if (next <= distance) { distance = next; found = i; }
            }
            return found;
        }

        private void DrawRecordingPlane(Rect plane)
        {
            EditorGUI.DrawRect(plane, BackgroundColor);
            int divisions = PositionDivisions[recordPositionGrid];
            for (int i = 1; i < divisions; i++)
            {
                float part = i / (float)divisions;
                Color grid = new Color(.17f, .23f, .3f, .7f);
                EditorGUI.DrawRect(new Rect(plane.x + plane.width * part, plane.y, 1, plane.height), grid);
                EditorGUI.DrawRect(new Rect(plane.x, plane.y + plane.height * part, plane.width, 1), grid);
            }
            EditorGUI.DrawRect(new Rect(plane.center.x, plane.y, 1, plane.height), MutedTextColor);
            EditorGUI.DrawRect(new Rect(plane.x, plane.center.y, plane.width, 1), MutedTextColor);
            DrawOutline(plane, MutedTextColor, 1);

            float time = TimeAtBeat(currentBeat);
            foreach (var note in document.notes)
            {
                if (Mathf.Abs(note.time - time) > 300 || Mathf.Abs(note.x) > 2.5f || Mathf.Abs(note.y) > 1.5f) continue;
                Vector2 p = RecordingPositionToPoint(plane, new Vector2(note.x, note.y));
                bool atStep = recordStepMode && Mathf.Abs(note.time - time) < 1f;
                Color color = NoteColor(note.color);
                if (recordStepMode && !atStep) color.a = .3f;
                float size = atStep ? 11 : 7;
                DrawOutline(new Rect(p.x - size, p.y - size, size * 2, size * 2), color, atStep ? 2 : 1);
            }
            // 選択中のマーカーを最後に描き、位置が重なっても調整対象を見失わないようにする。
            for (int draw = 0; draw <= RecordKeys.Length; draw++)
            {
                int i = draw == RecordKeys.Length ? recordActivePad : draw;
                if (draw < RecordKeys.Length && i == recordActivePad) continue;
                Vector2 p = RecordingPositionToPoint(plane, RecordingPosition(i));
                Color color = NoteColor(RecordColors[i]);
                Rect mark = new Rect(p.x - 5, p.y - 5, 10, 10);
                EditorGUI.DrawRect(mark, color);
                if (i == recordActivePad || recordMirrorPositions && i == 4 - recordActivePad)
                    DrawOutline(new Rect(p.x - 8, p.y - 8, 16, 16), Color.white, i == recordActivePad ? 2 : 1);
                GUI.Label(new Rect(Mathf.Clamp(p.x - 10, plane.x, plane.xMax - 22),
                    Mathf.Clamp(p.y - 24, plane.y, plane.yMax - 18), 22, 18), RecordKeys[i].ToString(),
                    new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = color } });
            }
            HandleRecordingPlaneInput(plane, Event.current);
        }

        private void HandleRecordingPlaneInput(Rect plane, Event input)
        {
            int control = GUIUtility.GetControlID("SaberRecordingXY".GetHashCode(), FocusType.Passive, plane);
            if (input.type == EventType.MouseDown && (input.button == 0 || input.button == 1) &&
                plane.Contains(input.mousePosition) && recordMousePad < 0 && recordMouseChord < 0 &&
                recordXYButton < 0 && !input.control && !input.command)
            {
                int marker = RecordingMarkerAt(plane, input.mousePosition);
                recordXYAdjusting = recorder == null && !CanStepInput || recordPositionOnly || input.alt ||
                    marker >= 0 && input.button == 0 && !input.shift;
                int pad = recordActivePad;
                if (recordXYAdjusting)
                {
                    if (marker >= 0) pad = marker;
                }
                else
                    pad = input.button == 1 ? (pad >= 3 ? pad : 3) : input.shift ? 2 : (pad <= 1 ? pad : 1);
                SelectRecordingPad(pad);
                bool grabbing = recordXYAdjusting && marker >= 0;
                recordXYDragOffset = grabbing ? RecordingPositionToPoint(plane, RecordingPosition(pad)) - input.mousePosition : Vector2.zero;
                if (!grabbing) SetRecordingPosition(pad, RecordingPointToPosition(plane, input.mousePosition), true);
                recordXYButton = input.button;
                recordXYPad = pad;
                recordXYInput = recordXYAdjusting ? -1 : 10 + pad;
                GUIUtility.hotControl = control;
                if (recordXYInput >= 0) RecordingPress(recordXYInput, pad);
                input.Use();
            }
            if (input.type == EventType.MouseDrag && recordXYButton >= 0)
            {
                if (recordXYAdjusting) SetRecordingPosition(recordXYPad, RecordingPointToPosition(plane, input.mousePosition + recordXYDragOffset), true);
                input.Use();
            }
            if (input.rawType == EventType.MouseUp && recordXYButton == input.button)
            {
                if (recordXYInput >= 0) RecordingRelease(recordXYInput);
                recordXYButton = recordXYInput = recordXYPad = -1;
                GUIUtility.hotControl = 0;
                input.Use();
            }
        }

        private void StartRecording()
        {
            if (recordStepMode || RecordingBusy || audioClip == null || EditorApplication.isPlaying) return;
            EndNoteDrag();
            FinishTextEditing();
            if (isPlaying) UpdatePlaybackPosition();
            StopPreview(false);
            if (BeatToAudioSeconds(currentBeat) >= audioClip.length - .01f)
            {
                SetStatus("音源の終端です。録音を始めたい位置へ戻してください");
                return;
            }
            recordMode = true;
            selectedIndex = -1;
            if (!recordCountIn) { BeginRecordingSong(); return; }
            float beatSeconds = 60f / Mathf.Max(1, document.bpm);
            const int rate = 22050;
            // プレビュー音声に4拍の短いクリックを流し、終了後に選択位置から曲を始める。
            float[] samples = new float[Mathf.CeilToInt(beatSeconds * 4 * rate)];
            for (int beat = 0; beat < 4; beat++)
            {
                int start = Mathf.RoundToInt(beat * beatSeconds * rate);
                for (int n = 0; n < rate * .035f && start + n < samples.Length; n++)
                    samples[start + n] = .25f * Mathf.Sin(n * 2f * Mathf.PI * (beat == 0 ? 1200 : 900) / rate) * (1 - n / (rate * .035f));
            }
            countInClip = AudioClip.Create("譜面録音カウント", samples.Length, 1, rate, false);
            countInClip.hideFlags = HideFlags.HideAndDontSave;
            countInClip.SetData(samples, 0);
            if (!SaberChartAudioPreview.Play(countInClip, 0))
            {
                StopPreview(false);
                SetStatus("カウントを再生できません: " + SaberChartAudioPreview.LastError);
                return;
            }
            countingIn = true;
            countInEndsAt = EditorApplication.timeSinceStartup + beatSeconds * 4;
            Repaint();
        }

        private void BeginRecordingSong()
        {
            countingIn = false;
            SaberChartAudioPreview.Stop();
            DisposeCountIn();
            TogglePreview();
            if (!isPlaying) return;
            // 格子の原点は表示中の値を渡す。ファイルの原点(本編の小節線)は録音では書き換えない。
            recordTakeGridOriginMs = beatZeroMs;
            recorder = new SaberChartRecorder(document, audioClip.length, recordSnap ? CurrentSnap : 0,
                recordInputOffsetMs, recordHold, recordLongCount, beatZeroMs);
            lastRecordingSeconds = playbackAudioStartSeconds;
            recordReviewBeat = currentBeat;
            recordReviewAudioSeconds = playbackAudioStartSeconds;
            recordReviewDocument = document;
            recordReviewClip = audioClip;
            recordTakeBeforeJson = recordTakeAfterJson = null;
            SetStatus("録音中: D・F / J・K / G で単打、H=F＋J / B=D＋Kで同時打ち、Spaceで終了");
        }

        private void ReviewRecording()
        {
            if (!CanReviewRecording) return;
            EndNoteDrag();
            FinishTextEditing();
            StopPreview(false);
            SeekToBeat(BeatAtTime(recordReviewAudioSeconds * 1000 - document.offsetMs));
            TogglePreview();
            if (isPlaying) SetStatus("今回の録音開始位置から再生しています。Spaceで一時停止できます");
        }

        private void UndoRecordingInput()
        {
            if (recorder == null) return;
            int removed = recorder.UndoLastInput();
            if (removed == 0) return;
            FinishTextEditing();
            MarkChanged();
            SetStatus($"直前の1打（{removed}ノーツ）を取り消しました。録音は継続中です");
        }

        private bool TryRecordingPosition(out float seconds)
        {
            if (recordingClockOverride != null)
            {
                float? value = recordingClockOverride();
                seconds = value ?? lastRecordingSeconds;
                return value.HasValue;
            }
            return SaberChartAudioPreview.TryGetPosition(audioClip, out seconds);
        }

        private void RecordingPress(int input, int pad)
        {
            if (CanStepInput) { RecordingStepPress(input, pad); return; }
            if (recorder == null) return;
            if (!TryRecordingPosition(out float seconds)) { StopPreview(false); return; }
            lastRecordingSeconds = seconds;
            RecordingPressAt(input, pad, seconds);
        }

        private void RecordingPressAt(int input, int pad, float seconds, int inputGroup = 0)
        {
            string direction = paletteType == SaberChartUtility.TypeDirection ? paletteDirection : SaberChartUtility.DirectionNone;
            Vector2 position = RecordingPosition(pad);
            var note = recorder.Press(input, seconds, position.x, position.y, RecordColors[pad], direction, inputGroup);
            recordFlashes[pad] = EditorApplication.timeSinceStartup + .15;
            if (note != null) MarkChanged();
            Repaint();
        }

        private void RecordingChordPress(int inputBase, int chord)
        {
            if (CanStepInput)
            {
                RecordingStepPress(inputBase + chord * 2, RecordChordPads[chord, 0], RecordChordPads[chord, 1]);
                return;
            }
            if (recorder == null) return;
            if (!TryRecordingPosition(out float seconds)) { StopPreview(false); return; }
            lastRecordingSeconds = seconds;
            // 音声時計を一度だけ読み、Snapなしやグリッド境界でも左右の始点を一致させる。
            int group = recorder.BeginInputGroup();
            for (int member = 0; member < 2; member++)
                RecordingPressAt(inputBase + chord * 2 + member, RecordChordPads[chord, member], seconds, group);
        }

        private void RecordingChordRelease(int inputBase, int chord)
        {
            if (recordStepHeld.Contains(inputBase + chord * 2)) { RecordingStepRelease(inputBase + chord * 2, 2); return; }
            if (recorder == null) return;
            if (TryRecordingPosition(out float seconds)) lastRecordingSeconds = seconds;
            for (int member = 0; member < 2; member++)
                recorder.Release(inputBase + chord * 2 + member, lastRecordingSeconds);
            MarkChanged();
            Repaint();
        }

        private void RecordingRelease(int input)
        {
            if (recordStepHeld.Contains(input)) { RecordingStepRelease(input); return; }
            if (recorder == null) return;
            if (TryRecordingPosition(out float seconds)) lastRecordingSeconds = seconds;
            recorder.Release(input, lastRecordingSeconds);
            MarkChanged();
        }

        private void FinishRecording()
        {
            bool preserveStepKeys = recordStepMode && recorder == null;
            ClearRecordingStepInput();
            countingIn = false;
            if (recorder != null)
            {
                if (TryRecordingPosition(out float seconds)) lastRecordingSeconds = seconds;
                recorder.Finish(lastRecordingSeconds);
                int count = recorder.AddedCount;
                recordTakeBeforeJson = recorder.BeforeJson;
                recordTakeAfterJson = CurrentJson();
                if (count > 0) history.Record(recorder.BeforeJson);
                recorder = null;
                MarkChanged();
                SetStatus(count > 0 ? $"{count}ノーツを録音しました。Ctrl+Zで録音分を取り消せます" : "入力なしで録音を終了しました");
            }
            if (recordMousePad >= 0 || recordMouseChord >= 0 || recordXYButton >= 0) GUIUtility.hotControl = 0;
            recordMousePad = recordMouseChord = -1;
            recordXYButton = recordXYInput = recordXYPad = -1;
            if (!preserveStepKeys)
            {
                Array.Clear(recordKeysDown, 0, recordKeysDown.Length);
                Array.Clear(recordChordKeysDown, 0, recordChordKeysDown.Length);
            }
            recordUndoKeyDown = false;
        }

        private void DisposeCountIn()
        {
            if (countInClip == null) return;
            DestroyImmediate(countInClip);
            countInClip = null;
        }

        private bool HandleRecordingKeyboard(Event input)
        {
            if (input.type == EventType.KeyUp) recordStepActionKeys.Remove(input.keyCode);
            int pad = Array.IndexOf(RecordKeys, input.keyCode);
            int chord = Array.IndexOf(RecordChordKeys, input.keyCode);
            int preset = (int)input.keyCode - (int)KeyCode.F1;
            if (input.type == EventType.KeyUp && preset >= 0 && preset < 3)
            {
                recordPresetKeysDown[preset] = false;
                if (recordMode) { input.Use(); return true; }
            }
            if (input.type == EventType.KeyUp && input.keyCode == KeyCode.R) recordRetryKeyDown = false;
            if (input.type == EventType.KeyUp && recordMode &&
                (input.keyCode == KeyCode.Return || input.keyCode == KeyCode.KeypadEnter))
            {
                recordReviewKeyDown = false;
                input.Use();
                return true;
            }
            if (input.type == EventType.KeyUp && input.keyCode == KeyCode.Backspace && (RecordingBusy || recordMode && recordStepMode))
            {
                recordUndoKeyDown = false;
                input.Use();
                return true;
            }
            // 入力欄や修飾キーの状態が途中で変わっても、離したキーは必ず解除する。
            if (input.type == EventType.KeyUp && (pad >= 0 || chord >= 0) && (recordMode || RecordingBusy))
            {
                if (pad >= 0)
                {
                    recordKeysDown[pad] = false;
                    RecordingRelease(pad);
                }
                else
                {
                    recordChordKeysDown[chord] = false;
                    RecordingChordRelease(RecordChordKeyInput, chord);
                }
                input.Use();
                return true;
            }
            if (input.type != EventType.KeyDown || EditorGUIUtility.editingTextField) return false;
            bool modified = input.control || input.command || input.alt;
            bool stepCopy = (input.control || input.command) && !input.shift && !input.alt && input.keyCode == KeyCode.D;
            bool stepMirror = (input.control || input.command) && !input.shift && !input.alt && input.keyCode == KeyCode.M;
            bool stepDelete = !modified && input.shift && input.keyCode == KeyCode.Delete;
            bool stepJump = !modified && !input.shift && (input.keyCode == KeyCode.PageUp || input.keyCode == KeyCode.PageDown);
            if (recordMode && recordStepMode && (stepCopy || stepMirror || stepDelete || stepJump))
            {
                if (recordStepActionKeys.Add(input.keyCode))
                {
                    if (stepJump) JumpRecordingStep(input.keyCode == KeyCode.PageUp ? -1 : 1);
                    else EditRecordingStep(stepCopy ? 0 : stepMirror ? 1 : 2);
                }
                input.Use();
                return true;
            }
            if (!modified && recordMode && preset >= 0 && preset < 3)
            {
                if (!recordPresetKeysDown[preset])
                {
                    if (input.shift) SaveRecordingPreset(preset);
                    else RecallRecordingPreset(preset);
                    recordPresetKeysDown[preset] = true;
                }
                input.Use();
                return true;
            }
            if (!modified && CanStepInput && input.keyCode == KeyCode.Tab)
            {
                MoveRecordingStep(input.shift ? -1 : 1);
                input.Use();
                return true;
            }
            if (!modified && RecordingBusy && input.keyCode == KeyCode.Backspace)
            {
                if (!recordUndoKeyDown) UndoRecordingInput();
                recordUndoKeyDown = true;
                input.Use();
                return true;
            }
            // ステップでも Backspace は「直前の1打を取り消す」。取り消した拍へ戻る。
            if (!modified && CanStepInput && input.keyCode == KeyCode.Backspace)
            {
                if (!recordUndoKeyDown) UndoLastStepInput();
                recordUndoKeyDown = true;
                input.Use();
                return true;
            }
            if (!modified && recordMode && !RecordingBusy &&
                (input.keyCode == KeyCode.Return || input.keyCode == KeyCode.KeypadEnter))
            {
                if (!recordReviewKeyDown) ReviewRecording();
                recordReviewKeyDown = true;
                input.Use();
                return true;
            }
            if (!modified && RecordingBusy && (input.keyCode == KeyCode.Space || input.keyCode == KeyCode.Escape))
            {
                StopPreview(false);
                input.Use();
                return true;
            }
            if (!modified && recordMode && !recordStepMode && input.keyCode == KeyCode.R)
            {
                if (!recordRetryKeyDown)
                {
                    if (input.shift) RetryRecording();
                    else StartRecording();
                    recordRetryKeyDown = true;
                }
                input.Use();
                return true;
            }
            if (recordMode && HandleRecordingPositionKey(input)) return true;
            if (!RecordingBusy && !CanStepInput) return false;
            if (!modified && pad >= 0 && !recordKeysDown[pad])
            {
                recordKeysDown[pad] = true;
                RecordingPress(pad, pad);
            }
            if (!modified && chord >= 0 && !recordChordKeysDown[chord])
            {
                recordChordKeysDown[chord] = true;
                RecordingChordPress(RecordChordKeyInput, chord);
            }
            if (!RecordingBusy && (modified || pad < 0 && chord < 0)) return false;
            // 保存・履歴操作だけは既存のショートカットへ渡し、先に録音を確定する。
            if ((input.control || input.command) && (input.keyCode == KeyCode.S || input.keyCode == KeyCode.Z || input.keyCode == KeyCode.Y)) return false;
            input.Use();
            return true;
        }

        private void OnLostFocus()
        {
            recordStepActionKeys.Clear();
            recordReviewKeyDown = false;
            recordRetryKeyDown = false;
            Array.Clear(recordPresetKeysDown, 0, recordPresetKeysDown.Length);
            ClearRecordingStepInput();
            if (recordStepMode)
            {
                Array.Clear(recordKeysDown, 0, recordKeysDown.Length);
                Array.Clear(recordChordKeysDown, 0, recordChordKeysDown.Length);
            }
            if (RecordingBusy) StopPreview(false);
            else if (recordXYButton >= 0)
            {
                // 停止中の位置ドラッグも解除し、別ウィンドウで離したボタンを引きずらない。
                GUIUtility.hotControl = 0;
                recordXYButton = recordXYInput = recordXYPad = -1;
            }
        }
    }
}
