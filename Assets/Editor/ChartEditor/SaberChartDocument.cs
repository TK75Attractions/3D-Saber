using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>
    /// 本編の chart.json と同じフィールド名を持つ、譜面エディター専用データ。
    /// 旧 Note-Recorder の同名クラスとは名前空間と Assembly を分けている。
    /// </summary>
    [Serializable]
    public sealed class SaberChartDocument
    {
        public float bpm = 120f;
        public float coordScale = 1f;
        public float offsetMs;
        public List<SaberChartNote> notes = new List<SaberChartNote>();
    }

    [Serializable]
    public sealed class SaberChartNote
    {
        public float beat;
        public float time;
        public float x;
        public float y;
        public string type = SaberChartUtility.TypeTap;
        public string color = SaberChartUtility.ColorRed;
        public string direction = SaberChartUtility.DirectionNone;
        public int count = 1;
        // Long の実長さ(ミリ秒)。0 なら本編既定の (count-1)×0.7s で自動決定。
        public float lengthMs;

        public SaberChartNote Clone()
        {
            return new SaberChartNote
            {
                beat = beat,
                time = time,
                x = x,
                y = y,
                type = type,
                color = color,
                direction = direction,
                count = count,
                lengthMs = lengthMs,
            };
        }
    }

    /// <summary>
    /// JSON変換と譜面編集の純粋ロジック。EditorWindowから分離し、単体テスト可能にする。
    /// </summary>
    public static class SaberChartUtility
    {
        public const string TypeTap = "tap";
        public const string TypeDirection = "direction";
        public const string TypeLong = "long";

        public const string ColorDefault = "default";
        public const string ColorRed = "red";
        public const string ColorBlue = "blue";
        public const string ColorGold = "gold";

        public const string DirectionNone = "none";

        public const int DefaultLaneCount = 8;
        public const float DefaultXMin = -2.5f;
        public const float DefaultXMax = 2.5f;
        public const float DefaultYMin = -1.5f;
        public const float DefaultYMax = 1.5f;

        private static readonly HashSet<string> ValidTypes = new HashSet<string>
        {
            TypeTap,
            TypeDirection,
            TypeLong,
        };

        private static readonly HashSet<string> ValidDirections = new HashSet<string>
        {
            DirectionNone,
            "right",
            "upright",
            "up",
            "upleft",
            "left",
            "downleft",
            "down",
            "downright",
        };

        public static SaberChartDocument FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new SaberChartDocument();

            SaberChartDocument document;
            try
            {
                document = JsonUtility.FromJson<SaberChartDocument>(json);
            }
            catch (ArgumentException exception)
            {
                throw new FormatException("chart.json のJSON形式が壊れています。", exception);
            }

            if (document == null)
                throw new FormatException("chart.json のルートオブジェクトを読み取れません。 ");
            Normalize(document);
            return document;
        }

        public static string ToJson(SaberChartDocument document, bool prettyPrint = true)
        {
            SaberChartDocument output = CopyRaw(document);
            Normalize(output);
            return JsonUtility.ToJson(output, prettyPrint);
        }

        public static SaberChartDocument Clone(SaberChartDocument source)
        {
            return FromJson(ToJson(source, false));
        }

        public static void Normalize(SaberChartDocument document)
        {
            if (document == null) return;

            if (!IsFinite(document.bpm) || document.bpm <= 0f) document.bpm = 120f;
            if (!IsFinite(document.coordScale) || document.coordScale <= 0f) document.coordScale = 1f;
            if (!IsFinite(document.offsetMs)) document.offsetMs = 0f;
            document.notes ??= new List<SaberChartNote>();
            document.notes.RemoveAll(note => note == null);

            foreach (SaberChartNote note in document.notes)
            {
                if (!IsFinite(note.beat)) note.beat = 0f;
                if (!IsFinite(note.time)) note.time = 0f;
                if (!IsFinite(note.x)) note.x = 0f;
                if (!IsFinite(note.y)) note.y = 0f;

                note.beat = Mathf.Max(0f, note.beat);
                note.time = Mathf.Max(0f, note.time);

                string direction = LowerOrDefault(note.direction, DirectionNone);
                note.direction = ValidDirections.Contains(direction) ? direction : DirectionNone;
                note.color = LowerOrDefault(note.color, ColorDefault);

                string type = LowerOrDefault(note.type, TypeTap);
                note.type = ValidTypes.Contains(type) ? type : TypeTap;
                int count = Mathf.Clamp(note.count, 1, 99);

                // 本編は type より count / direction を実際の判定に使う。
                // 旧・手書き譜面の不整合を読み込んでもゲーム上の意味を失わないよう昇格する。
                if (count > 1)
                    note.type = TypeLong;
                else if (note.direction != DirectionNone && note.type == TypeTap)
                    note.type = TypeDirection;

                note.count = note.type == TypeLong ? Mathf.Max(2, count) : 1;

                // 長さ指定は Long 専用。不正値や Long 以外では 0(自動)へ戻す。
                if (!IsFinite(note.lengthMs) || note.lengthMs < 0f) note.lengthMs = 0f;
                if (note.type != TypeLong) note.lengthMs = 0f;
                note.lengthMs = Mathf.Min(note.lengthMs, 600000f);
            }

            SortNotes(document);
        }

        public static void SortNotes(SaberChartDocument document)
        {
            if (document?.notes == null) return;
            document.notes.Sort((a, b) =>
            {
                int time = a.time.CompareTo(b.time);
                if (time != 0) return time;
                int x = a.x.CompareTo(b.x);
                return x != 0 ? x : a.y.CompareTo(b.y);
            });
        }

        /// <summary>
        /// time = beat * 60000 / BPM + 原点 の原点を推定する。
        /// 既存譜面には time 側に曲頭の空白を含むものがあるため、中央値で保持する。
        /// </summary>
        public static float EstimateBeatZeroMs(SaberChartDocument document)
        {
            if (document?.notes == null || document.notes.Count == 0) return 0f;

            float bpm = SafeBpm(document.bpm);
            var candidates = new List<float>(document.notes.Count);
            foreach (SaberChartNote note in document.notes)
            {
                if (note == null || !IsFinite(note.beat) || !IsFinite(note.time)) continue;
                candidates.Add(note.time - BeatToTimeMs(note.beat, bpm, 0f));
            }

            if (candidates.Count == 0) return 0f;
            candidates.Sort();
            // 差が一定でない譜面は beat が参考値に留まり、time が実タイミングを持つ。
            // 無理に中央値を原点にすると表示までずれるため、この場合は曲頭を原点とする。
            if (candidates[candidates.Count - 1] - candidates[0] > 50f) return 0f;
            int middle = candidates.Count / 2;
            return candidates.Count % 2 == 1
                ? candidates[middle]
                : (candidates[middle - 1] + candidates[middle]) * 0.5f;
        }

        public static float BeatToTimeMs(float beat, float bpm, float beatZeroMs)
        {
            return Mathf.Max(0f, beat) * 60000f / SafeBpm(bpm) + beatZeroMs;
        }

        public static float TimeMsToBeat(float timeMs, float bpm, float beatZeroMs)
        {
            return Mathf.Max(0f, (timeMs - beatZeroMs) * SafeBpm(bpm) / 60000f);
        }

        public static void RecalculateTimesFromBeats(SaberChartDocument document, float beatZeroMs)
        {
            if (document?.notes == null) return;
            foreach (SaberChartNote note in document.notes)
            {
                if (note == null) continue;
                note.time = BeatToTimeMs(note.beat, document.bpm, beatZeroMs);
            }
            SortNotes(document);
        }

        /// <summary>本編で権威値となる time を保ったまま、補助値 beat だけを現在のグリッドへ合わせる。</summary>
        public static void RecalculateBeatsFromTimes(SaberChartDocument document, float beatZeroMs)
        {
            if (document?.notes == null) return;
            foreach (SaberChartNote note in document.notes)
            {
                if (note == null) continue;
                note.beat = TimeMsToBeat(note.time, document.bpm, beatZeroMs);
            }
            SortNotes(document);
        }

        public static float QuantizeBeat(float beat, int noteDenominator)
        {
            float step = SnapStep(noteDenominator);
            return Mathf.Max(0f, Mathf.Round(beat / step) * step);
        }

        public static float SnapStep(int noteDenominator)
        {
            return 4f / Mathf.Max(1, noteDenominator);
        }

        public static float CoordinateForLane(int lane, int laneCount, float min, float max)
        {
            int count = Mathf.Max(1, laneCount);
            if (count == 1) return (min + max) * 0.5f;
            int safeLane = Mathf.Clamp(lane, 0, count - 1);
            return Mathf.Lerp(min, max, safeLane / (float)(count - 1));
        }

        public static int LaneForCoordinate(float coordinate, int laneCount, float min, float max)
        {
            int count = Mathf.Max(1, laneCount);
            if (count == 1 || Mathf.Approximately(min, max)) return 0;
            float t = Mathf.InverseLerp(min, max, coordinate);
            return Mathf.Clamp(Mathf.RoundToInt(t * (count - 1)), 0, count - 1);
        }

        public static bool HasNoteAt(
            SaberChartDocument document,
            float beat,
            float x,
            float y,
            SaberChartNote ignored = null,
            float beatTolerance = 0.0001f,
            float positionTolerance = 0.0001f)
        {
            if (document?.notes == null) return false;
            return document.notes.Any(note =>
                note != null && note != ignored &&
                Mathf.Abs(note.beat - beat) <= beatTolerance &&
                Mathf.Abs(note.x - x) <= positionTolerance &&
                Mathf.Abs(note.y - y) <= positionTolerance);
        }

        public static string FormatMusicalPosition(float beat, int beatsPerMeasure, int noteDenominator)
        {
            int beats = Mathf.Max(1, beatsPerMeasure);
            float step = SnapStep(noteDenominator);
            int totalSteps = Mathf.Max(0, Mathf.RoundToInt(beat / step));
            int stepsPerBeat = Mathf.Max(1, Mathf.RoundToInt(1f / step));
            int stepsPerMeasure = beats * stepsPerBeat;
            int measure = totalSteps / stepsPerMeasure + 1;
            int inside = totalSteps % stepsPerMeasure;
            int beatInMeasure = inside / stepsPerBeat + 1;
            int subdivision = inside % stepsPerBeat;
            return $"{measure:D3} : {beatInMeasure:D2} : {subdivision:D2}";
        }

        // 本編 NoteSpawner.secondsPerLongCut の既定値と同じ(長さ自動時の1カットあたり秒数)。
        public const float DefaultSecondsPerLongCut = 0.7f;

        /// <summary>
        /// Long の実効長さ(ミリ秒)。lengthMs 指定があればそれを、無ければ本編既定の
        /// (count-1) × 0.7s を返す。Long 以外は 0。
        /// </summary>
        public static float EffectiveLongLengthMs(SaberChartNote note)
        {
            if (note == null || note.count <= 1) return 0f;
            return note.lengthMs > 0f
                ? note.lengthMs
                : (note.count - 1) * DefaultSecondsPerLongCut * 1000f;
        }

        /// <summary>
        /// 絶対パスを "Assets/..." のアセットパスへ変換する。プロジェクト外なら null。
        /// FileUtil.GetProjectRelativePath は Windows の「\」区切りを空文字にしてしまうため自前で行う。
        /// </summary>
        public static string ProjectRelativeAssetPath(string absolutePath)
        {
            return ProjectRelativeAssetPath(absolutePath, Application.dataPath);
        }

        public static string ProjectRelativeAssetPath(string absolutePath, string dataPath)
        {
            if (string.IsNullOrEmpty(absolutePath) || string.IsNullOrEmpty(dataPath)) return null;
            string normalized = System.IO.Path.GetFullPath(absolutePath).Replace('\\', '/');
            string data = System.IO.Path.GetFullPath(dataPath).Replace('\\', '/').TrimEnd('/');
            if (string.Equals(normalized, data, StringComparison.OrdinalIgnoreCase)) return "Assets";
            if (!normalized.StartsWith(data + "/", StringComparison.OrdinalIgnoreCase)) return null;
            return "Assets" + normalized.Substring(data.Length);
        }

        private static float SafeBpm(float bpm)
        {
            return IsFinite(bpm) && bpm > 0f ? bpm : 120f;
        }

        private static string LowerOrDefault(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static SaberChartDocument CopyRaw(SaberChartDocument source)
        {
            if (source == null) return new SaberChartDocument();
            var copy = new SaberChartDocument
            {
                bpm = source.bpm,
                coordScale = source.coordScale,
                offsetMs = source.offsetMs,
                notes = new List<SaberChartNote>(),
            };
            if (source.notes == null) return copy;
            foreach (SaberChartNote note in source.notes)
                copy.notes.Add(note?.Clone());
            return copy;
        }
    }
}
