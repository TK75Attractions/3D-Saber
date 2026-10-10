using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>
    /// 範囲の編集の純粋な処理。対象の決め方はスマホ版(Saber Tap Studio)と同じで、
    /// 「開始以上・終了未満の時刻に始まるノーツ」。貼り付け先に同じノーツがあれば、元のノーツを残す。
    /// </summary>
    public static class SaberChartRangeOps
    {
        public static List<SaberChartNote> NotesIn(SaberChartDocument document, float startMs, float endMs)
        {
            var result = new List<SaberChartNote>();
            if (document?.notes == null || !(endMs > startMs)) return result;
            foreach (SaberChartNote note in document.notes)
                if (note != null && note.time >= startMs && note.time < endMs) result.Add(note);
            return result;
        }

        /// <summary>左右反転: 横位置を反転し、赤青を入れ替え、矢印の左右も反転する(Beat Saber 公式のミラーと同じ)。</summary>
        public static void Mirror(IEnumerable<SaberChartNote> notes)
        {
            foreach (SaberChartNote note in notes)
            {
                if (note == null) continue;
                note.x = -note.x;
                note.color = SwapColor(note.color);
                note.direction = MirrorDirection(note.direction);
            }
        }

        /// <summary>色だけ入れ替える(赤⇔青)。位置と方向はそのまま。</summary>
        public static void SwapColors(IEnumerable<SaberChartNote> notes)
        {
            foreach (SaberChartNote note in notes)
                if (note != null) note.color = SwapColor(note.color);
        }

        public static string SwapColor(string color)
        {
            if (color == SaberChartUtility.ColorRed) return SaberChartUtility.ColorBlue;
            if (color == SaberChartUtility.ColorBlue) return SaberChartUtility.ColorRed;
            return color;
        }

        public static string MirrorDirection(string direction)
        {
            switch (direction)
            {
                case "left": return "right";
                case "right": return "left";
                case "upleft": return "upright";
                case "upright": return "upleft";
                case "downleft": return "downright";
                case "downright": return "downleft";
                default: return direction;
            }
        }

        /// <summary>
        /// 写しを足す。newTime は元のノーツから貼り付け先の時刻を返す。同じ時刻・位置・色のノーツが
        /// 既にあれば足さない(貼り付け先のノーツを残す)。足したノーツを返す。
        /// </summary>
        public static List<SaberChartNote> Paste(SaberChartDocument document, IEnumerable<SaberChartNote> clip,
            Func<SaberChartNote, float> newTime, Func<float, float> beatAtTime)
        {
            var added = new List<SaberChartNote>();
            if (document?.notes == null || clip == null) return added;
            foreach (SaberChartNote source in clip)
            {
                if (source == null) continue;
                SaberChartNote copy = source.Clone();
                copy.time = Mathf.Max(0f, newTime(source));
                copy.beat = beatAtTime(copy.time);
                if (document.notes.Any(note => SameSlot(note, copy)) || added.Any(note => SameSlot(note, copy))) continue;
                added.Add(copy);
            }
            document.notes.AddRange(added);
            SaberChartUtility.SortNotes(document);
            return added;
        }

        public static bool SameSlot(SaberChartNote a, SaberChartNote b) =>
            a != null && b != null && Mathf.Abs(a.time - b.time) < 1f &&
            Mathf.Abs(a.x - b.x) < .0001f && Mathf.Abs(a.y - b.y) < .0001f && a.color == b.color;

        /// <summary>種類をまとめて変える。TAP は方向と回数を消し、LONG は回数を、方向は方向を補う。</summary>
        public static void SetType(IEnumerable<SaberChartNote> notes, string type, string fallbackDirection, int longCount)
        {
            foreach (SaberChartNote note in notes)
            {
                if (note == null) continue;
                if (type == SaberChartUtility.TypeTap)
                {
                    note.type = SaberChartUtility.TypeTap;
                    note.direction = SaberChartUtility.DirectionNone;
                    note.count = 1;
                    note.lengthMs = 0f;
                }
                else if (type == SaberChartUtility.TypeDirection)
                {
                    note.type = SaberChartUtility.TypeDirection;
                    if (note.direction == SaberChartUtility.DirectionNone)
                        note.direction = fallbackDirection == SaberChartUtility.DirectionNone ? "down" : fallbackDirection;
                    note.count = 1;
                    note.lengthMs = 0f;
                }
                else if (type == SaberChartUtility.TypeLong)
                {
                    note.type = SaberChartUtility.TypeLong;
                    note.count = Mathf.Max(2, note.count > 1 ? note.count : longCount);
                }
            }
        }
    }
}
