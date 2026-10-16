using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    public sealed partial class SaberPlaybackReviewWindow
    {
        [SerializeField] List<float> pendingNoteSeconds = new List<float>();
        // 再生中に叩いた打点から差し引く入力の遅れ(ms)。譜面エディターの録音で測った値を、メニューから開いたときだけ読む
        // (テストの窓では個人設定を使わない)。
        [SerializeField] float timingInputOffsetMs;
        [SerializeField] string timingInputProfile = "";
        // これより近い打点は、ループで叩き直した同じ拍とみなして1打にまとめる。
        const float MergeTapSeconds = .04f;
        bool timingCapture;

        void LoadTimingInputOffset() => timingInputOffsetMs = SaberChartEditorWindow.SavedInputOffsetMs(out timingInputProfile);
        readonly HashSet<KeyCode> timingHeldKeys = new HashSet<KeyCode>();
        Rect timingTapRect, reviewFrameRect;

        void OnLostFocus() => timingHeldKeys.Clear();

        void ResetTimingCapture()
        {
            timingCapture = false; timingHeldKeys.Clear(); pendingNoteSeconds.Clear();
        }

        void BeginTimingCapture()
        {
            if (document == null || candidate != null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            timingCapture = true; timingHeldKeys.Clear();
            GUI.FocusControl(null);
            if (!playing) Play();
            status = "映像のクリック / Tキーで、ノーツが来てほしい時刻を記録します。Spaceで一時停止、Backspaceで1打戻す。" + TimingOffsetNote;
        }

        string TimingOffsetNote => timingInputOffsetMs != 0f
            ? $" 押す遅れ {timingInputOffsetMs:+0;-0}ms（{timingInputProfile}）を差し引きます。"
            : " 押す遅れは0msで記録します（譜面エディターの録音で測ると、ここでも使います）。";

        void CaptureTiming()
        {
            float time = seconds;
            if (playing)
            {
                // 描画フレームの時刻ではなく、押した瞬間に音声時計を読む。
                if (clip != null)
                {
                    if (!SaberChartAudioPreview.TryGetPosition(clip, out time))
                    { status = "音声の再生位置を取得できません。停止して位置を確認してください。"; return; }
                }
                else time += (float)(EditorApplication.timeSinceStartup - lastTick);
                // 聞いてから押すまでの遅れ(譜面エディターの録音と同じ値)を差し引く。止めて置く打点は位置そのまま。
                time -= timingInputOffsetMs / 1000f;
            }
            RecordTimingAt(time);
        }

        void RecordTimingAt(float raw)
        {
            if (!timingCapture || document == null || candidate != null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (float.IsNaN(raw) || float.IsInfinity(raw) || raw < 0 || raw > Duration) return;
            float time = PointerTime(raw);
            if (time * 1000 - document.offsetMs < 0)
            { status = "この時刻は譜面のOFFSETより前です。OFFSET以降で記録してください。"; return; }
            int near = pendingNoteSeconds.FindIndex(t => Mathf.Abs(t - time) < MergeTapSeconds);
            if (near >= 0)
            {
                // ループで同じ拍を叩き直したときは2打にせず、平均して1打にまとめる。
                pendingNoteSeconds[near] = (pendingNoteSeconds[near] + time) / 2f;
                status = $"{pendingNoteSeconds[near]:0.000}秒の近く（{MergeTapSeconds * 1000:0}ms以内）は1打にまとめました。"; Repaint(); return;
            }
            pendingNoteSeconds.Add(time);
            status = $"● {pendingNoteSeconds.Count}打を記録 · 最後 {time:0.000}秒 · 色や位置はAIにお任せ";
            Repaint();
        }

        void UndoTiming()
        {
            if (candidate != null || pendingNoteSeconds.Count == 0) return;
            pendingNoteSeconds.RemoveAt(pendingNoteSeconds.Count - 1);
            status = $"直前の1打を戻しました。記録 {pendingNoteSeconds.Count}打。"; Repaint();
        }

        void FinishTimingCapture()
        {
            if (candidate != null) return;
            Stop(); timingCapture = false; timingHeldKeys.Clear();
            if (pendingNoteSeconds.Count > 0)
            {
                var comment = SaberPlaybackReviewData.TimingComment(document, pendingNoteSeconds, Duration);
                comment.inputOffsetMs = timingInputOffsetMs;
                comments.Add(comment); pendingNoteSeconds.Clear();
                status = $"{comment.requestedNoteSeconds.Length}打のタイミングを指摘に追加しました。AIへの依頼を書き出してください。";
            }
            else status = "タイミングの記録を終了しました。";
            SaveDraft(); Repaint();
        }

        bool HandleTimingKeys(Event e)
        {
            if (e.type == EventType.KeyUp) { timingHeldKeys.Remove(e.keyCode); return false; }
            if (e.type != EventType.KeyDown || !timingCapture || candidate != null || EditorGUIUtility.editingTextField
                || e.control || e.command || e.alt || EditorApplication.isPlayingOrWillChangePlaymode) return false;
            if (e.keyCode != KeyCode.T && e.keyCode != KeyCode.Backspace && e.keyCode != KeyCode.Escape) return false;
            if (timingHeldKeys.Add(e.keyCode))
            {
                if (e.keyCode == KeyCode.T) CaptureTiming();
                else if (e.keyCode == KeyCode.Backspace) UndoTiming();
                else FinishTimingCapture();
            }
            e.Use(); return true;
        }

        void DrawTimingCaptureControls()
        {
            using (new EditorGUI.DisabledScope(candidate != null))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(timingCapture ? "記録を終了・指摘に追加" : "▶ タイミング記録", GUILayout.Width(155)))
                { if (timingCapture) FinishTimingCapture(); else BeginTimingCapture(); }
                timingTapRect = GUILayoutUtility.GetRect(175, 25, GUILayout.Width(175));
                // ButtonのMouseUpではなく、押した瞬間を記録する。
                GUI.Box(timingTapRect, "ここにノーツがほしい [T]", EditorStyles.miniButton);
                var e = Event.current;
                if (GUI.enabled && timingCapture && e.type == EventType.MouseDown && e.button == 0 && timingTapRect.Contains(e.mousePosition))
                { GUI.FocusControl(null); CaptureTiming(); e.Use(); }
                GUILayout.Label(new GUIContent($"{pendingNoteSeconds.Count}打", "押す遅れ " + (timingInputOffsetMs == 0f ? "0ms（未測定）" :
                    $"{timingInputOffsetMs:+0;-0}ms（{timingInputProfile}）") + " を差し引いて記録します。"), GUILayout.Width(43));
                using (new EditorGUI.DisabledScope(pendingNoteSeconds.Count == 0))
                {
                    if (GUILayout.Button("1打戻す", GUILayout.Width(64))) UndoTiming();
                    if (!timingCapture && GUILayout.Button("指摘に追加", GUILayout.Width(82))) FinishTimingCapture();
                }
                GUILayout.FlexibleSpace();
            }
        }

        void DrawTimingMarkers(Rect rect, float start, float span)
        {
            foreach (float time in pendingNoteSeconds.Concat(comments.SelectMany(c => c.requestedNoteSeconds ?? Array.Empty<float>())))
            {
                if (time < start || time > start + span) continue;
                float x = rect.x + (time - start) / span * rect.width;
                EditorGUI.DrawRect(new Rect(Mathf.Min(x, rect.xMax - 3), rect.y + 57, 3, 6), new Color(.4f, 1f, .6f));
            }
        }
    }
}
