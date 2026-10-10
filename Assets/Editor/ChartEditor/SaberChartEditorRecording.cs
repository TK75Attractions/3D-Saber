using System;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    public sealed partial class SaberChartEditorWindow
    {
        private static readonly KeyCode[] RecordKeys = { KeyCode.D, KeyCode.F, KeyCode.G, KeyCode.J, KeyCode.K };
        private static readonly string[] RecordLabels = { "D  青・外", "F  青・内", "G  金", "J  赤・内", "K  赤・外" };
        private static readonly float[] RecordX = { -1.7857143f, -.7142857f, 0f, .7142857f, 1.7857143f };
        private static readonly string[] RecordColors = { "blue", "blue", "gold", "red", "red" };
        [SerializeField] private bool recordMode;
        [SerializeField] private bool recordSnap = true;
        [SerializeField] private bool recordHold = true;
        [SerializeField] private bool recordCountIn = true;
        [SerializeField] private float recordInputOffsetMs;
        [SerializeField] private int recordHeight = 1;
        [SerializeField] private int recordLongCount = 2;
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
            recordMode = EditorGUILayout.ToggleLeft("打ち込みパッドを表示", recordMode);
            if (!recordMode) return;
            recordCountIn = EditorGUILayout.ToggleLeft("開始前に4拍カウント", recordCountIn);
            recordSnap = EditorGUILayout.ToggleLeft("入力を左のSnapへそろえる", recordSnap);
            recordHold = EditorGUILayout.ToggleLeft("250ms以上の長押しをLONGにする", recordHold);
            recordLongCount = EditorGUILayout.IntSlider("LONGのカット回数", recordLongCount, 2, 12);
            recordInputOffsetMs = EditorGUILayout.FloatField(new GUIContent("入力補正 (ms)",
                "押すのが遅れる場合は正の値を指定します。個人のゲーム表示補正は録音時刻へ適用しません。"), recordInputOffsetMs);
            if (float.IsNaN(recordInputOffsetMs) || float.IsInfinity(recordInputOffsetMs)) recordInputOffsetMs = 0;
            recordInputOffsetMs = Mathf.Clamp(recordInputOffsetMs, -1000, 1000);
            recordHeight = GUILayout.Toolbar(recordHeight, new[] { "低", "中", "高" });
            EditorGUILayout.HelpBox("Rで録音開始、Space / Escで終了。\nD・F=青、J・K=赤、G=金。同時押し可。\n方向ノーツは左の「方向」と矢印を選択。\n既存ノーツへ追記し、1回のUndoで録音分を戻せます。", MessageType.None);
        }

        private Rect DrawRecordingPad(Rect rect)
        {
            if (!recordMode) return rect;
            Rect panel = new Rect(rect.x, rect.y, rect.width, 132);
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
                GUI.Label(pad, RecordLabels[i], new GUIStyle(EditorStyles.boldLabel)
                    { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } });
                if (input.type == EventType.MouseDown && input.button == 0 && pad.Contains(input.mousePosition))
                {
                    FinishTextEditing();
                    if (recorder != null)
                    {
                        recordMousePad = i;
                        GUIUtility.hotControl = padControl;
                        RecordingPress(i + 5, i);
                    }
                    else SetStatus("「録音開始」または R を押してから打ち込んでください");
                    input.Use();
                }
            }
            if (input.rawType == EventType.MouseUp && recordMousePad >= 0)
            {
                RecordingRelease(recordMousePad + 5);
                recordMousePad = -1;
                GUIUtility.hotControl = 0;
                input.Use();
            }
            GUI.Label(new Rect(panel.x + 10, panel.y + 94, panel.width - 20, 18),
                (recordHold ? "短押し TAP / 長押し LONG" : "押した瞬間に入力") + "   ·   Space / Esc 終了", smallMutedStyle);
            GUI.Label(new Rect(panel.x + 10, panel.y + 112, panel.width - 20, 18),
                $"{(recordSnap ? SnapLabels[snapIndex] : "自由なタイミング")}  ·  高さ {new[] { "低", "中", "高" }[recordHeight]}  ·  追記録音", smallMutedStyle);
            return new Rect(rect.x, rect.y + 139, rect.width, Mathf.Max(100, rect.height - 139));
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
            var note = recorder.Press(input, seconds, RecordX[pad], (recordHeight - 1) * .8571429f, RecordColors[pad], direction);
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
            if (recordMousePad >= 0) GUIUtility.hotControl = 0;
            recordMousePad = -1;
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
        }
    }
}
