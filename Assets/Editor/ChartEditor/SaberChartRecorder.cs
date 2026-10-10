using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>音声サンプル時刻で1回の追記録音を管理する。既存ノーツは変更しない。</summary>
    public sealed class SaberChartRecorder
    {
        private sealed class Stroke
        {
            public SaberChartNote note;
            public float audioSeconds;
        }

        private readonly SaberChartDocument document;
        private readonly float audioLength;
        private readonly int snap;
        private readonly float inputOffsetMs;
        private readonly bool holdToLong;
        private readonly int longCount;
        private readonly Dictionary<int, Stroke> held = new Dictionary<int, Stroke>();
        public string BeforeJson { get; }
        public int AddedCount { get; private set; }
        public bool IsHeld(int input) => held.ContainsKey(input);

        public SaberChartRecorder(SaberChartDocument document, float audioLength, int snap,
            float inputOffsetMs, bool holdToLong, int longCount)
        {
            this.document = document ?? throw new ArgumentNullException(nameof(document));
            this.audioLength = Mathf.Max(0, audioLength);
            this.snap = snap;
            this.inputOffsetMs = inputOffsetMs;
            this.holdToLong = holdToLong;
            this.longCount = Mathf.Clamp(longCount, 2, 99);
            BeforeJson = SaberChartUtility.ToJson(document, false);
        }

        // 表示カーソルや個人の判定補正を使わず、音声位置からOFFSETを一度だけ引く。
        private float NoteTime(float audioSeconds)
        {
            float time = audioSeconds * 1000f - document.offsetMs - inputOffsetMs;
            if (snap > 0)
            {
                float beat = SaberChartUtility.TimeMsToBeat(time, document.bpm, document.beatZeroMs);
                time = SaberChartUtility.BeatToTimeMs(
                    SaberChartUtility.QuantizeBeat(beat, snap, document), document.bpm, document.beatZeroMs);
            }
            return time;
        }

        public SaberChartNote Press(int input, float audioSeconds, float x, float y,
            string color, string direction = SaberChartUtility.DirectionNone)
        {
            if (held.ContainsKey(input) || !Finite(audioSeconds) || audioSeconds < 0 || audioSeconds >= audioLength)
                return null;
            // OSのキーリピートは、無効時刻や重複で追加しなかったキーにも適用する。
            held.Add(input, new Stroke { audioSeconds = audioSeconds });
            float rawTime = audioSeconds * 1000f - document.offsetMs - inputOffsetMs;
            if (rawTime < 0) return null;
            float time = NoteTime(audioSeconds);
            if (time < 0 || time + document.offsetMs < 0 || time + document.offsetMs >= audioLength * 1000f)
                return null;
            if (document.notes.Any(n => Mathf.Abs(n.time - time) < 1f &&
                Mathf.Abs(n.x - x) < .0001f && Mathf.Abs(n.y - y) < .0001f && n.color == color)) return null;
            var note = new SaberChartNote
            {
                time = time,
                beat = SaberChartUtility.TimeMsToBeat(time, document.bpm, document.beatZeroMs),
                x = x, y = y, color = color, direction = direction,
                type = direction == SaberChartUtility.DirectionNone ? SaberChartUtility.TypeTap : SaberChartUtility.TypeDirection,
            };
            document.notes.Add(note);
            SaberChartUtility.SortNotes(document);
            held[input].note = note;
            AddedCount++;
            return note;
        }

        public void Release(int input, float audioSeconds)
        {
            if (!held.TryGetValue(input, out Stroke stroke)) return;
            held.Remove(input);
            if (stroke.note == null || !holdToLong || !Finite(audioSeconds)) return;
            float endAudio = Mathf.Clamp(audioSeconds, stroke.audioSeconds, audioLength);
            if (endAudio - stroke.audioSeconds < .25f) return;
            float end = Mathf.Min(NoteTime(endAudio), audioLength * 1000f - document.offsetMs);
            if (end <= stroke.note.time) return;
            stroke.note.type = SaberChartUtility.TypeLong;
            stroke.note.count = longCount;
            stroke.note.lengthMs = Mathf.Min(600000f, end - stroke.note.time);
        }

        public void Finish(float audioSeconds)
        {
            foreach (int input in held.Keys.ToArray()) Release(input, audioSeconds);
            SaberChartUtility.SortNotes(document);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
