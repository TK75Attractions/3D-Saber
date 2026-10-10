using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>
    /// 音に合わせる道具: ヒット音とメトロノーム、打ち込みの遅れの測定と補正(出力機器ごと)、テンポ地図(格子だけ)。
    /// </summary>
    public sealed partial class SaberChartEditorWindow
    {
        private static readonly string[] OutputProfileLabels = { "スピーカー", "イヤホン", "その他" };

        [SerializeField] private bool playHitSounds;
        [SerializeField] private bool playMetronome;
        [SerializeField] private int outputProfile;
        [SerializeField] private string inputOffsetNote = string.Empty;
        [SerializeField] private bool showTempoMap;

        // 再生中のクリップ(曲そのもの、またはクリックを混ぜたもの)。再生位置はこのクリップで読む。
        private AudioClip playingClip;
        private AudioClip clickMixClip;
        private string clickMixKey;
        private bool suppressClicksForNextPlay;

        private bool calibrating;
        private AudioClip calibrationClip;
        private double calibrationEndsAt;
        private readonly List<float> calibrationTaps = new List<float>();
        private readonly HashSet<KeyCode> calibrationKeysHeld = new HashSet<KeyCode>();
        // テストで音声の時計を差し替える。
        private Func<float?> calibrationClockOverride;
        private string calibrationResult;
        private SaberChartLatency.Summary lastTakeSummary;
        private bool hasLastTakeSummary;

        private SaberChartGrid gridCache;
        private float gridBpm = float.NaN;
        private float gridOrigin = float.NaN;
        private List<SaberChartTempoPoint> gridSource;
        private int gridCount = -1;
        private double gridChecksum;

        // ---- 合10: テンポ地図を含む編集用の格子 ----

        private SaberChartGrid Grid
        {
            get
            {
                List<SaberChartTempoPoint> map = document.tempoMap;
                int count = map?.Count ?? 0;
                double checksum = 0;
                for (int i = 0; i < count; i++)
                    if (map[i] != null) checksum += (i + 1) * (map[i].beat * 1.618034 + map[i].timeMs * .001);
                if (gridCache == null || gridBpm != document.bpm || gridOrigin != beatZeroMs || !ReferenceEquals(gridSource, map) ||
                    gridCount != count || gridChecksum != checksum)
                {
                    gridCache = new SaberChartGrid(document.bpm, beatZeroMs, map);
                    gridBpm = document.bpm;
                    gridOrigin = beatZeroMs;
                    gridSource = map;
                    gridCount = count;
                    gridChecksum = checksum;
                }
                return gridCache;
            }
        }

        private void DrawTempoMapPanel()
        {
            int count = document.tempoMap?.Count ?? 0;
            showTempoMap = EditorGUILayout.Foldout(showTempoMap, $"テンポ地図（エディターの格子だけ・{count}点）", true);
            if (!showTempoMap) return;
            EditorGUILayout.HelpBox("テンポが途中で変わる曲用です。拍と時刻の対応点を置くと、点の間を直線で結んだ格子になります。" +
                                    "本編の小節線・カウントインは、上の BPM と原点のまま変わりません。", MessageType.None);
            for (int i = 0; i < count; i++)
            {
                SaberChartTempoPoint point = document.tempoMap[i];
                GUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                GUILayout.Label("拍", GUILayout.Width(14f));
                float beat = EditorGUILayout.DelayedFloatField(point.beat, GUILayout.Width(52f));
                GUILayout.Label("時刻", GUILayout.Width(26f));
                float time = EditorGUILayout.DelayedFloatField(point.timeMs, GUILayout.Width(70f));
                bool changed = EditorGUI.EndChangeCheck();
                string tempo = i + 1 < count ? $"→{Grid.TempoAt(point.beat):0.##}" : string.Empty;
                GUILayout.Label(tempo, smallMutedStyle, GUILayout.Width(52f));
                bool remove = GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22f));
                GUILayout.EndHorizontal();
                if (changed || remove)
                {
                    EditTempoPoint(i, beat, time, remove);
                    break;
                }
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("現在位置に点", "今の位置にいちばん近い拍へ、今の格子の時刻で点を置きます（置いただけでは格子は動きません）")))
                AddTempoPointAtCursor();
            if (GUILayout.Button(new GUIContent("ノーツの拍から作る", "ノーツの拍の値と時刻から地図を作ります（テンポの変わる曲の拍の値を使う）")))
                BuildTempoMapFromNotes();
            using (new EditorGUI.DisabledScope(count == 0))
                if (GUILayout.Button("消す", GUILayout.Width(40f))) SetTempoMap(new List<SaberChartTempoPoint>(), "テンポ地図を消しました");
            GUILayout.EndHorizontal();
            using (new EditorGUI.DisabledScope(count == 0))
                if (GUILayout.Button(new GUIContent("ノーツの拍の値を地図に合わせる", "時刻は変えずに、拍の値(選曲画面の拍の演出が使う)をこの地図で計算し直します")))
                    RecalculateBeatsWithGrid();
        }

        private void EditTempoPoint(int index, float beat, float time, bool remove)
        {
            if (index < 0 || index >= document.tempoMap.Count) return;
            var next = document.tempoMap.Select(point => new SaberChartTempoPoint { beat = point.beat, timeMs = point.timeMs }).ToList();
            if (remove) next.RemoveAt(index);
            else
            {
                if (float.IsNaN(beat) || float.IsInfinity(beat) || float.IsNaN(time) || float.IsInfinity(time)) return;
                next[index] = new SaberChartTempoPoint { beat = Mathf.Max(0f, beat), timeMs = time };
            }
            int before = next.Count;
            List<SaberChartTempoPoint> cleaned = SaberChartGrid.Clean(next);
            SetTempoMap(cleaned, cleaned.Count < before
                ? "前後の点と拍・時刻の順が合わない点は外しました"
                : remove ? "点を消しました" : "テンポ地図を直しました");
        }

        private void AddTempoPointAtCursor()
        {
            float beat = Mathf.Max(0f, Mathf.Round(currentBeat));
            var next = new List<SaberChartTempoPoint>(document.tempoMap ?? new List<SaberChartTempoPoint>())
            {
                new SaberChartTempoPoint { beat = beat, timeMs = TimeAtBeat(beat) },
            };
            SetTempoMap(SaberChartGrid.Clean(next), $"{beat:0}拍目に点を置きました。時刻を直すと、その前後の格子が伸び縮みします");
        }

        private void BuildTempoMapFromNotes()
        {
            List<SaberChartTempoPoint> map = SaberChartGrid.FromNotes(document.notes);
            if (map.Count < 2)
            {
                SetStatus("拍の値を持つノーツが足りないので、地図を作れませんでした");
                return;
            }
            SetTempoMap(map, $"ノーツの拍から {map.Count}点の地図を作りました");
        }

        private void SetTempoMap(List<SaberChartTempoPoint> map, string status)
        {
            EndNoteDrag();
            string before = CurrentJson();
            document.tempoMap = map;
            if (CurrentJson() == before) return;
            history.Record(before);
            MarkChanged();
            RestartPreviewIfPlaying();
            SetStatus(status);
        }

        private void RecalculateBeatsWithGrid()
        {
            EndNoteDrag();
            string before = CurrentJson();
            SaberChartUtility.RecalculateBeatsFromTimes(document, time => Grid.BeatAt(time));
            if (CurrentJson() == before) { SetStatus("拍の値はすでに地図と合っています"); return; }
            history.Record(before);
            MarkChanged();
            SetStatus("拍の値を地図に合わせました（時刻は変えていません）");
        }

        // ---- 合6: ヒット音とメトロノーム ----

        private void DrawClickToggles()
        {
            GUILayout.BeginHorizontal();
            bool hits = GUILayout.Toggle(playHitSounds, new GUIContent("ヒット音", "ノーツの時刻にクリックを鳴らします（曲と同じ経路で鳴らすので、ずれを耳で確かめられます）"),
                EditorStyles.miniButtonLeft);
            bool metronome = GUILayout.Toggle(playMetronome, new GUIContent("メトロノーム", "格子の拍にクリックを鳴らします。小節の頭は高い音です"),
                EditorStyles.miniButtonRight);
            GUILayout.EndHorizontal();
            if (hits == playHitSounds && metronome == playMetronome) return;
            playHitSounds = hits;
            playMetronome = metronome;
            RestartPreviewIfPlaying();
        }

        /// <summary>再生するクリップ。ヒット音・メトロノームがオンなら、曲にクリックを混ぜたものを作る(内容が同じなら使い回す)。</summary>
        private AudioClip PlaybackClipFor(AudioClip song)
        {
            if (song == null || !playHitSounds && !playMetronome || suppressClicksForNextPlay) return song;
            string key = ClickMixKey(song);
            if (clickMixClip != null && key == clickMixKey) return clickMixClip;
            DisposeClickMix();
            float offset = document.offsetMs;
            IEnumerable<double> hits = playHitSounds
                ? document.notes.Select(note => Math.Round(note.time + offset)).Distinct().Select(ms => ms / 1000.0).Where(s => s >= 0)
                : null;
            IEnumerable<(double, bool)> beats = playMetronome ? MetronomeSeconds(song.length) : null;
            clickMixClip = SaberChartClicks.Mix(song, hits, beats);
            if (clickMixClip == null)
            {
                SetStatus("音源の波形を読めないので、ヒット音・メトロノームなしで再生します");
                return song;
            }
            clickMixKey = key;
            return clickMixClip;
        }

        private string ClickMixKey(AudioClip song)
        {
            unchecked
            {
                int hash = 17;
                foreach (SaberChartNote note in document.notes) hash = hash * 31 + Mathf.RoundToInt(note.time);
                return $"{song.GetInstanceID()}|{playHitSounds}|{playMetronome}|{document.offsetMs}|{document.bpm}|{beatZeroMs}|{gridChecksum}|{gridCount}|{hash}|" +
                       JsonUtility.ToJson(new SignatureList { items = document.timeSignatures });
            }
        }

        // 拍子に合わせた拍の時刻(音源の秒)。小節の頭は強い音にする。
        private IEnumerable<(double seconds, bool accent)> MetronomeSeconds(float songLength)
        {
            var meter = new ChartMeterMap(document.timeSignatures);
            float lastBeat = BeatAtTime(songLength * 1000f - document.offsetMs) + 1f;
            foreach (double start in meter.BarStarts(0, lastBeat))
            {
                var bar = meter.At(start);
                for (int pulse = 0; pulse < bar.Numerator; pulse++)
                {
                    double beat = start + pulse * 4.0 / bar.Denominator;
                    if (beat >= bar.BarEnd - ChartMeterMap.Epsilon) break;
                    double seconds = (TimeAtBeat((float)beat) + document.offsetMs) / 1000.0;
                    if (seconds >= 0 && seconds < songLength) yield return (seconds, pulse == 0);
                }
            }
        }

        private void DisposeClickMix()
        {
            if (clickMixClip != null)
            {
                if (playingClip == clickMixClip) SaberChartAudioPreview.Stop();
                DestroyImmediate(clickMixClip);
            }
            clickMixClip = null;
            clickMixKey = null;
        }

        // ---- 合1: 打ち込みの遅れを測って補正する(出力機器ごと) ----

        private string ProfileKey(string name) => PrefPrefix + name + "." + Mathf.Clamp(outputProfile, 0, OutputProfileLabels.Length - 1);

        /// <summary>選んでいる出力機器の入力補正を読み込む。メニューから開いたときと、出力を切り替えたときに使う。</summary>
        private void LoadInputOffsetProfile()
        {
            outputProfile = Mathf.Clamp(EditorPrefs.GetInt(PrefPrefix + "OutputProfile", outputProfile), 0, OutputProfileLabels.Length - 1);
            recordInputOffsetMs = Mathf.Clamp(EditorPrefs.GetFloat(ProfileKey("InputOffset"), 0f), -1000f, 1000f);
            inputOffsetNote = EditorPrefs.GetString(ProfileKey("InputOffsetNote"), string.Empty);
        }

        // 利用者が補正を変えたときだけ保存する(テストや一時的な値で、個人の設定を書き換えない)。
        private void SaveInputOffsetProfile(string note)
        {
            inputOffsetNote = note ?? string.Empty;
            EditorPrefs.SetInt(PrefPrefix + "OutputProfile", outputProfile);
            EditorPrefs.SetFloat(ProfileKey("InputOffset"), recordInputOffsetMs);
            EditorPrefs.SetString(ProfileKey("InputOffsetNote"), inputOffsetNote);
        }

        private void DrawLatencyPanel()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("出力", GUILayout.Width(28f));
            int profile = EditorGUILayout.Popup(outputProfile, OutputProfileLabels, GUILayout.Width(84f));
            if (profile != outputProfile)
            {
                outputProfile = profile;
                EditorPrefs.SetInt(PrefPrefix + "OutputProfile", outputProfile);
                LoadInputOffsetProfile();
            }
            string state = string.IsNullOrEmpty(inputOffsetNote) ? "未測定" : inputOffsetNote;
            GUILayout.Label($"入力補正 {recordInputOffsetMs:+0;-0;0}ms（{state}）", smallMutedStyle);
            GUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(RecordingBusy))
            {
                GUIContent button = calibrating
                    ? new GUIContent("測定をやめる（Esc）")
                    : new GUIContent("遅れを測る（クリック16拍）",
                        "クリックに合わせて D F G J K か Space を叩くと、録音と同じ経路で遅れを測り、入力補正にします");
                if (GUILayout.Button(button))
                {
                    if (calibrating) FinishLatencyCalibration(true);
                    else StartLatencyCalibration();
                }
            }
            if (calibrating)
                EditorGUILayout.HelpBox($"4拍の低い音のあと、高い音16拍に合わせて叩いてください（{calibrationTaps.Count}打）", MessageType.Info);
            else if (!string.IsNullOrEmpty(calibrationResult))
                GUILayout.Label(calibrationResult, EditorStyles.wordWrappedMiniLabel);

            if (!hasLastTakeSummary || lastTakeSummary.Count == 0) return;
            SaberChartLatency.Summary summary = lastTakeSummary;
            GUILayout.Label($"今回のずれ（Snap 前）: 中央値 {summary.Median:+0;-0;0}ms・ばらつき {summary.Spread:0}ms・外れ {summary.Outliers}（{summary.Count}打）",
                EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(!summary.Reliable))
            {
                string label = summary.Reliable
                    ? $"{summary.Median:+0;-0;0}ms を入力補正に足す"
                    : summary.Count < SaberChartLatency.MinHits ? $"{SaberChartLatency.MinHits}打以上で使えます" : "ばらつきが大きいので使いません";
                if (GUILayout.Button(label)) ApplyLastTakeOffset();
            }
        }

        private void ApplyLastTakeOffset()
        {
            if (!hasLastTakeSummary || !lastTakeSummary.Reliable) return;
            float add = Mathf.Round(lastTakeSummary.Median);
            recordInputOffsetMs = Mathf.Clamp(recordInputOffsetMs + add, -1000f, 1000f);
            SaveInputOffsetProfile($"録音から {add:+0;-0;0}ms・{DateTime.Now:M/d HH:mm}");
            hasLastTakeSummary = false;
            SetStatus($"入力補正を {recordInputOffsetMs:+0;-0;0}ms にしました（次の録音から使います。録ったノーツは動かしません）");
        }

        private void StartLatencyCalibration()
        {
            if (RecordingBusy || calibrating || EditorApplication.isPlaying) return;
            StopPreview(false);
            FinishTextEditing();
            calibrationClip = SaberChartClicks.CalibrationClip();
            if (calibrationClockOverride == null && !SaberChartAudioPreview.Play(calibrationClip, 0f))
            {
                SetStatus("クリックを再生できません: " + SaberChartAudioPreview.LastError);
                DestroyImmediate(calibrationClip);
                calibrationClip = null;
                return;
            }
            calibrating = true;
            calibrationTaps.Clear();
            calibrationKeysHeld.Clear();
            calibrationResult = null;
            calibrationEndsAt = EditorApplication.timeSinceStartup + calibrationClip.length + .3;
            Repaint();
        }

        private bool TryCalibrationPosition(out float seconds)
        {
            if (calibrationClockOverride != null)
            {
                float? value = calibrationClockOverride();
                seconds = value ?? 0f;
                return value.HasValue;
            }
            return SaberChartAudioPreview.TryGetPosition(calibrationClip, out seconds);
        }

        // 測定中はキーを全部ここで受け、録音や編集の操作をしない。
        private bool HandleCalibrationKey(Event input)
        {
            if (!calibrating) return false;
            if (input.type == EventType.KeyUp)
            {
                calibrationKeysHeld.Remove(input.keyCode);
                input.Use();
                return true;
            }
            if (input.type != EventType.KeyDown || input.keyCode == KeyCode.None) return false;
            if (input.keyCode == KeyCode.Escape)
            {
                FinishLatencyCalibration(true);
                input.Use();
                return true;
            }
            bool tap = Array.IndexOf(RecordKeys, input.keyCode) >= 0 || Array.IndexOf(RecordChordKeys, input.keyCode) >= 0 ||
                       input.keyCode == KeyCode.Space;
            if (tap && calibrationKeysHeld.Add(input.keyCode) && TryCalibrationPosition(out float seconds))
                calibrationTaps.Add(seconds * 1000f);
            input.Use();
            return true;
        }

        private void TickLatencyCalibration()
        {
            if (!calibrating) return;
            if (EditorApplication.timeSinceStartup >= calibrationEndsAt) FinishLatencyCalibration(false);
            Repaint();
        }

        private void FinishLatencyCalibration(bool cancel)
        {
            if (!calibrating) return;
            calibrating = false;
            SaberChartAudioPreview.Stop();
            if (calibrationClip != null) DestroyImmediate(calibrationClip);
            calibrationClip = null;
            calibrationKeysHeld.Clear();
            if (cancel)
            {
                calibrationResult = "測定をやめました（入力補正は変えていません）";
                Repaint();
                return;
            }
            SaberChartLatency.Summary summary = SaberChartLatency.Measure(calibrationTaps);
            if (summary.Reliable)
            {
                recordInputOffsetMs = Mathf.Clamp(Mathf.Round(summary.Median), -1000f, 1000f);
                SaveInputOffsetProfile($"測定 {DateTime.Now:M/d HH:mm}");
                calibrationResult = $"遅れ {summary.Median:+0;-0;0}ms（{summary.Count}打・ばらつき {summary.Spread:0}ms）を、{OutputProfileLabels[outputProfile]}の入力補正にしました";
            }
            else
            {
                calibrationResult = summary.Count < SaberChartLatency.MinCalibrationHits
                    ? $"使える打鍵が {summary.Count}打 でした（{SaberChartLatency.MinCalibrationHits}打以上必要）。入力補正は変えていません。もう一度測ってください"
                    : $"ばらつきが {summary.Spread:0}ms と大きいので、入力補正は変えていません。もう一度測ってください";
            }
            SetStatus(calibrationResult);
            Repaint();
        }

        private void RememberTakeLatency(SaberChartRecorder take)
        {
            lastTakeSummary = SaberChartLatency.Summarize(take?.Deviations);
            hasLastTakeSummary = lastTakeSummary.Count > 0;
        }
    }
}
