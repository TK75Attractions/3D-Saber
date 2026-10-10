using System;
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
        private static readonly int[] PositionDivisions = { 0, 4, 8, 16, 32 };
        private static readonly string[] PositionGridLabels = { "自由配置", "4分割", "8分割", "16分割", "32分割" };
        private static readonly string[] PositionKeyLabels = { "1: D", "2: F", "3: G", "4: J", "5: K" };
        private int recordXYButton = -1;
        private int recordXYInput = -1;
        private int recordXYPad = -1;
        private bool recordXYAdjusting;
        [Serializable]
        private sealed class RecordingLayout
        {
            public Vector2[] positions;
            public int grid = 2;
            public int activePad = 1;
            public bool positionOnly;
        }
        private SaberChartRecorder recorder;
        private bool countingIn;
        private double countInEndsAt;
        private AudioClip countInClip;
        private float lastRecordingSeconds;
        private int recordMousePad = -1;
        private readonly double[] recordFlashes = new double[5];
        private readonly bool[] recordKeysDown = new bool[5];
        // 音声時計だけを差し替えられるようにし、入力経路を音声機器なしでも検証する。
        private Func<float?> recordingClockOverride;
        private bool RecordingBusy => countingIn || recorder != null;

        private void DrawRecordingSettings()
        {
            GUILayout.Space(10);
            SectionLabel("演奏して譜面を作る");
            using (new EditorGUI.DisabledScope(RecordingBusy))
                recordMode = EditorGUILayout.ToggleLeft("打ち込みパッドを表示", recordMode);
            if (!recordMode) return;
            EditorGUI.BeginDisabledGroup(RecordingBusy);
            recordCountIn = EditorGUILayout.ToggleLeft("開始前に4拍カウント", recordCountIn);
            recordSnap = EditorGUILayout.ToggleLeft("入力を左のSnapへそろえる", recordSnap);
            recordHold = EditorGUILayout.ToggleLeft("250ms以上の長押しをLONGにする", recordHold);
            recordLongCount = EditorGUILayout.IntSlider("LONGのカット回数", recordLongCount, 2, 12);
            recordInputOffsetMs = EditorGUILayout.FloatField(new GUIContent("入力補正 (ms)",
                "押すのが遅れる場合は正の値を指定します。個人のゲーム表示補正は録音時刻へ適用しません。"), recordInputOffsetMs);
            if (float.IsNaN(recordInputOffsetMs) || float.IsInfinity(recordInputOffsetMs)) recordInputOffsetMs = 0;
            recordInputOffsetMs = Mathf.Clamp(recordInputOffsetMs, -1000, 1000);
            EditorGUI.EndDisabledGroup();

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
            recordPositionOnly = EditorGUILayout.ToggleLeft("XYパッドは位置調整のみ", recordPositionOnly);
            EditorGUILayout.HelpBox("1～5: 調整するキーを選択\n矢印: 位置移動 / Shift+矢印: 0.01刻み\nXYパッド: 左=青 / 右=赤 / Shift+左=金\nAlt+ドラッグ: 選択キーの位置だけ変更\n停止中は配置だけを調整できます。", MessageType.None);
            EditorGUILayout.HelpBox("Rで録音開始、Space / Escで終了。\nD・F / G / J・Kは各マーカーの位置へ入力。\n方向は左の矢印、LONGは長押し。\n録音1回分をまとめてUndoできます。", MessageType.None);
        }

        private Rect DrawRecordingPad(Rect rect)
        {
            if (!recordMode) return rect;
            EnsureRecordingPositions();
            Rect panel = new Rect(rect.x, rect.y, rect.width, Mathf.Min(390, rect.height * .62f));
            EditorGUI.DrawRect(panel, PanelColor);
            string label = countingIn ? $"開始まで {Mathf.Clamp(Mathf.CeilToInt((float)(countInEndsAt - EditorApplication.timeSinceStartup) * document.bpm / 60f), 1, 4)}"
                : recorder != null ? $"● 録音中  +{recorder.AddedCount} NOTES" : "演奏して打ち込み";
            GUI.Label(new Rect(rect.x + 10, rect.y + 6, rect.width - 134, 24), label, sectionStyle);
            using (new EditorGUI.DisabledScope(!RecordingBusy && (audioClip == null || !SaberChartAudioPreview.IsSupported || EditorApplication.isPlaying)))
            {
                if (GUI.Button(new Rect(rect.xMax - 120, rect.y + 5, 110, 26), RecordingBusy ? "■ 録音終了" : "● 録音開始 [R]"))
                {
                    if (RecordingBusy) StopPreview(false);
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
                bool lit = recorder != null && (recorder.IsHeld(i) || recorder.IsHeld(i + 5)) ||
                    EditorApplication.timeSinceStartup < recordFlashes[i];
                EditorGUI.DrawRect(pad, Color.Lerp(PanelColor, color, lit ? .85f : .3f));
                if (i == recordActivePad) DrawOutline(pad, color, 2);
                GUI.Label(new Rect(pad.x, pad.y + 2, pad.width, 24), RecordLabels[i], new GUIStyle(EditorStyles.boldLabel)
                    { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } });
                Vector2 xy = RecordingPosition(i);
                GUI.Label(new Rect(pad.x, pad.y + 27, pad.width, 20), $"{xy.x:0.00}, {xy.y:0.00}", centeredSmallStyle);
                if (input.type == EventType.MouseDown && input.button == 0 && pad.Contains(input.mousePosition) && recordXYButton < 0)
                {
                    SelectRecordingPad(i);
                    if (recorder != null)
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
            DrawRecordingPlane(RecordingPlaneRect(panel));
            GUI.Label(new Rect(panel.x + 10, panel.yMax - 38, panel.width - 20, 18),
                "XY: 左クリック 青 / 右 赤 / Shift+左 金 / Alt 位置調整", smallMutedStyle);
            GUI.Label(new Rect(panel.x + 10, panel.yMax - 20, panel.width - 20, 18),
                $"調整 {RecordKeys[recordActivePad]}  ·  矢印で移動 / Shiftで微調整  ·  Space 終了", smallMutedStyle);
            return new Rect(rect.x, panel.yMax + PanelGap, rect.width, rect.height - panel.height - PanelGap);
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
                for (int i = 0; i < recordPositions.Length; i++) SetRecordingPosition(i, layout.positions[i], false);
            }
            catch (ArgumentException) { /* 壊れた個人設定は既定の配置へ戻す。譜面ファイルには触れない。 */ }
        }

        private void SaveRecordingLayout()
        {
            if (recordPositions == null) return;
            EditorPrefs.SetString(PrefPrefix + "RecordingLayout", JsonUtility.ToJson(new RecordingLayout
            {
                positions = recordPositions, grid = recordPositionGrid, activePad = recordActivePad,
                positionOnly = recordPositionOnly,
            }));
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
            Repaint();
        }

        private void MirrorRecordingPosition()
        {
            EnsureRecordingPositions();
            if (recordActivePad == 2) return;
            Vector2 source = RecordingPosition(recordActivePad);
            SetRecordingPosition(4 - recordActivePad, new Vector2(-source.x, source.y), false);
            FinishTextEditing();
        }

        private bool HandleRecordingPositionKey(Event input)
        {
            int number = (int)input.keyCode - (int)KeyCode.Alpha1;
            if (number >= 0 && number < RecordKeys.Length)
            {
                SelectRecordingPad(number);
                input.Use();
                return true;
            }
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
            float height = Mathf.Min(panel.height - 144, (panel.width - 40) * 3f / 5f);
            return new Rect(panel.center.x - height * 5f / 6f, panel.y + 102, height * 5f / 3f, height);
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
            GUI.Label(new Rect(plane.x, plane.y - 16, plane.width, 16), "+Y  高い   /   右が +X", centeredSmallStyle);

            float time = SaberChartUtility.BeatToTimeMs(currentBeat, document.bpm, beatZeroMs);
            foreach (var note in document.notes)
            {
                if (Mathf.Abs(note.time - time) > 300 || Mathf.Abs(note.x) > 2.5f || Mathf.Abs(note.y) > 1.5f) continue;
                Vector2 p = RecordingPositionToPoint(plane, new Vector2(note.x, note.y));
                DrawOutline(new Rect(p.x - 7, p.y - 7, 14, 14), NoteColor(note.color), 1);
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
                if (i == recordActivePad) DrawOutline(new Rect(p.x - 8, p.y - 8, 16, 16), Color.white, 1);
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
                plane.Contains(input.mousePosition) && recordMousePad < 0 && recordXYButton < 0 && !input.control && !input.command)
            {
                recordXYAdjusting = recorder == null || recordPositionOnly || input.alt;
                int pad = recordActivePad;
                if (recordXYAdjusting)
                {
                    int marker = RecordingMarkerAt(plane, input.mousePosition);
                    if (marker >= 0) pad = marker;
                }
                else
                    pad = input.button == 1 ? (pad >= 3 ? pad : 3) : input.shift ? 2 : (pad <= 1 ? pad : 1);
                SelectRecordingPad(pad);
                SetRecordingPosition(pad, RecordingPointToPosition(plane, input.mousePosition), true);
                recordXYButton = input.button;
                recordXYPad = pad;
                recordXYInput = recordXYAdjusting ? -1 : 10 + pad;
                GUIUtility.hotControl = control;
                if (recordXYInput >= 0) RecordingPress(recordXYInput, pad);
                input.Use();
            }
            if (input.type == EventType.MouseDrag && recordXYButton >= 0)
            {
                if (recordXYAdjusting) SetRecordingPosition(recordXYPad, RecordingPointToPosition(plane, input.mousePosition), true);
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
            if (RecordingBusy || audioClip == null || EditorApplication.isPlaying) return;
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
            document.beatZeroMs = beatZeroMs;
            recorder = new SaberChartRecorder(document, audioClip.length, recordSnap ? CurrentSnap : 0,
                recordInputOffsetMs, recordHold, recordLongCount);
            lastRecordingSeconds = playbackAudioStartSeconds;
            SetStatus("録音中: D・F / J・K / G で入力、Spaceで終了");
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
            if (recorder == null) return;
            if (!TryRecordingPosition(out float seconds)) { StopPreview(false); return; }
            lastRecordingSeconds = seconds;
            string direction = paletteType == SaberChartUtility.TypeDirection ? paletteDirection : SaberChartUtility.DirectionNone;
            Vector2 position = RecordingPosition(pad);
            var note = recorder.Press(input, seconds, position.x, position.y, RecordColors[pad], direction);
            recordFlashes[pad] = EditorApplication.timeSinceStartup + .15;
            if (note != null) MarkChanged();
            Repaint();
        }

        private void RecordingRelease(int input)
        {
            if (recorder == null) return;
            if (TryRecordingPosition(out float seconds)) lastRecordingSeconds = seconds;
            recorder.Release(input, lastRecordingSeconds);
            MarkChanged();
        }

        private void FinishRecording()
        {
            countingIn = false;
            if (recorder != null)
            {
                if (TryRecordingPosition(out float seconds)) lastRecordingSeconds = seconds;
                recorder.Finish(lastRecordingSeconds);
                int count = recorder.AddedCount;
                if (count > 0) history.Record(recorder.BeforeJson);
                recorder = null;
                MarkChanged();
                SetStatus(count > 0 ? $"{count}ノーツを録音しました。Ctrl+Zで録音分を取り消せます" : "入力なしで録音を終了しました");
            }
            if (recordMousePad >= 0 || recordXYButton >= 0) GUIUtility.hotControl = 0;
            recordMousePad = -1;
            recordXYButton = recordXYInput = recordXYPad = -1;
            Array.Clear(recordKeysDown, 0, recordKeysDown.Length);
        }

        private void DisposeCountIn()
        {
            if (countInClip == null) return;
            DestroyImmediate(countInClip);
            countInClip = null;
        }

        private bool HandleRecordingKeyboard(Event input)
        {
            int pad = Array.IndexOf(RecordKeys, input.keyCode);
            // 入力欄や修飾キーの状態が途中で変わっても、離したキーは必ず解除する。
            if (input.type == EventType.KeyUp && pad >= 0 && RecordingBusy)
            {
                recordKeysDown[pad] = false;
                RecordingRelease(pad);
                input.Use();
                return true;
            }
            if (input.type != EventType.KeyDown || EditorGUIUtility.editingTextField) return false;
            bool modified = input.control || input.command || input.alt;
            if (!modified && RecordingBusy && (input.keyCode == KeyCode.Space || input.keyCode == KeyCode.Escape))
            {
                StopPreview(false);
                input.Use();
                return true;
            }
            if (!modified && recordMode && input.keyCode == KeyCode.R)
            {
                StartRecording();
                input.Use();
                return true;
            }
            if (!modified && recordMode && HandleRecordingPositionKey(input)) return true;
            if (!RecordingBusy) return false;
            if (!modified && pad >= 0 && !recordKeysDown[pad])
            {
                recordKeysDown[pad] = true;
                if (recorder != null) RecordingPress(pad, pad);
            }
            // 保存・履歴操作だけは既存のショートカットへ渡し、先に録音を確定する。
            if ((input.control || input.command) && (input.keyCode == KeyCode.S || input.keyCode == KeyCode.Z || input.keyCode == KeyCode.Y)) return false;
            input.Use();
            return true;
        }

        private void OnLostFocus()
        {
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
