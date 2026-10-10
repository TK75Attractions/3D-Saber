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
        // 本編の譜面ごとの表示難易度。エディターで保存しても失わないよう保持する。
        public int displayLevel;
        public float beatZeroMs;
        public List<ChartTimeSignature> timeSignatures = new List<ChartTimeSignature>();
        public List<SaberChartNote> notes = new List<SaberChartNote>();
        // 本編が読まない項目(制作メモ _comment など)。読み込んだ綴りのまま保存し直す。
        public List<SaberChartExtraField> extraFields = new List<SaberChartExtraField>();
        // 名前付きの目印(サビ・Aメロなど)。ファイルでは本編が読まない "editorMarkers" に書く。
        public List<SaberChartMarker> markers = new List<SaberChartMarker>();
    }

    [Serializable]
    public sealed class SaberChartMarker
    {
        public float timeMs;
        public string name = string.Empty;
    }

    [Serializable]
    public sealed class SaberChartExtraField
    {
        public string key;
        // 値の JSON 表記(文字列なら引用符つき)。
        public string json;
    }

    /// <summary>
    /// 譜面ファイルへ書く形。エディター内の作業用の項目(extraFields)は持たず、
    /// 未知の項目は先頭へそのまま書き戻す。
    /// </summary>
    [Serializable]
    public sealed class SaberChartFileData
    {
        public float bpm = 120f;
        public float coordScale = 1f;
        public float offsetMs;
        public int displayLevel;
        public float beatZeroMs;
        public List<ChartTimeSignature> timeSignatures = new List<ChartTimeSignature>();
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
            CollectExtraFields(json, document);
            Normalize(document);
            return document;
        }

        // 譜面の項目として扱う名前。これ以外は extraFields に残して保存し直す。
        private const string MarkersFileKey = "editorMarkers";

        private static readonly HashSet<string> KnownChartKeys = new HashSet<string>
        {
            "bpm", "coordScale", "offsetMs", "displayLevel", "beatZeroMs", "timeSignatures", "notes", "extraFields",
            "markers", MarkersFileKey,
        };

        private static void CollectExtraFields(string json, SaberChartDocument document)
        {
            document.extraFields ??= new List<SaberChartExtraField>();
            document.markers ??= new List<SaberChartMarker>();
            if (!SaberJson.TryParseObject(json, out SaberJsonObject root)) return;
            if (document.markers.Count == 0 && root.Get(MarkersFileKey) is SaberJsonArray markerArray)
            {
                foreach (SaberJsonNode item in markerArray.Items)
                {
                    if (!(item is SaberJsonObject marker)) continue;
                    if (!SaberJson.TryGetNumber(marker.Get("timeMs"), out double timeMs)) continue;
                    SaberJson.TryGetString(marker.Get("name"), out string name);
                    document.markers.Add(new SaberChartMarker { timeMs = (float)timeMs, name = name ?? string.Empty });
                }
            }
            foreach (var member in root.Members)
            {
                if (KnownChartKeys.Contains(member.Key)) continue;
                document.extraFields.RemoveAll(field => field != null && field.key == member.Key);
                document.extraFields.Add(new SaberChartExtraField
                {
                    key = member.Key,
                    json = SaberJson.WriteCompact(member.Value),
                });
            }
        }

        /// <summary>エディター内の比較・履歴用の JSON。作業用の項目も含む。</summary>
        public static string ToJson(SaberChartDocument document, bool prettyPrint = true)
        {
            SaberChartDocument output = CopyRaw(document);
            Normalize(output);
            return JsonUtility.ToJson(output, prettyPrint);
        }

        /// <summary>
        /// 譜面ファイルに書く JSON。本編と同じ項目に加え、読み込んだときの未知の項目
        /// (制作メモなど)を先頭へそのまま戻す。
        /// </summary>
        public static string ToFileJson(SaberChartDocument document)
        {
            SaberChartDocument output = CopyRaw(document);
            Normalize(output);
            var file = new SaberChartFileData
            {
                bpm = output.bpm,
                coordScale = output.coordScale,
                offsetMs = output.offsetMs,
                displayLevel = output.displayLevel,
                beatZeroMs = output.beatZeroMs,
                timeSignatures = output.timeSignatures,
                notes = output.notes,
            };
            string json = JsonUtility.ToJson(file, true);
            if (output.extraFields.Count == 0 && output.markers.Count == 0) return json;

            int open = json.IndexOf('{');
            if (open < 0) return json;
            var extra = new System.Text.StringBuilder();
            foreach (SaberChartExtraField field in output.extraFields)
                extra.Append("\n    ").Append(SaberJson.Quote(field.key)).Append(": ").Append(field.json).Append(',');
            if (output.markers.Count > 0)
            {
                var markers = new SaberJsonArray();
                foreach (SaberChartMarker marker in output.markers)
                {
                    var item = new SaberJsonObject();
                    item.Set("timeMs", SaberJson.Number(Math.Round(marker.timeMs, 3)));
                    item.Set("name", SaberJson.String(marker.name));
                    markers.Items.Add(item);
                }
                extra.Append("\n    ").Append(SaberJson.Quote(MarkersFileKey)).Append(": ")
                    .Append(SaberJson.WriteCompact(markers)).Append(',');
            }
            return json.Insert(open + 1, extra.ToString());
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
            if (!IsFinite(document.beatZeroMs)) document.beatZeroMs = 0f;
            document.timeSignatures = ChartMeterMap.Normalize(document.timeSignatures);
            if (document.displayLevel < 0 || document.displayLevel > 10) document.displayLevel = 0;
            document.notes ??= new List<SaberChartNote>();
            document.notes.RemoveAll(note => note == null);
            document.extraFields ??= new List<SaberChartExtraField>();
            document.extraFields.RemoveAll(field => field == null || string.IsNullOrEmpty(field.key) ||
                KnownChartKeys.Contains(field.key) || !IsJsonValue(field.json));
            document.markers ??= new List<SaberChartMarker>();
            document.markers.RemoveAll(marker => marker == null || !IsFinite(marker.timeMs));
            foreach (SaberChartMarker marker in document.markers)
            {
                marker.timeMs = Mathf.Max(0f, marker.timeMs);
                marker.name ??= string.Empty;
            }
            document.markers.Sort((a, b) => a.timeMs.CompareTo(b.timeMs));

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
            if (document != null && IsFinite(document.beatZeroMs) && document.beatZeroMs != 0f)
                return document.beatZeroMs;
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

        /// <summary>
        /// 曲の設定(BPM・OFFSET・グリッド原点・拍子・座標倍率)を写す。ノーツ・表示レベル・制作メモは
        /// 難易度ごとの値なので写さない。
        /// </summary>
        public static void CopySongSettings(SaberChartDocument from, SaberChartDocument to)
        {
            if (from == null || to == null) return;
            to.bpm = from.bpm;
            to.offsetMs = from.offsetMs;
            to.beatZeroMs = from.beatZeroMs;
            to.coordScale = from.coordScale;
            to.timeSignatures = ChartMeterMap.Normalize(from.timeSignatures);
            // 目印は曲の構成(サビなど)なので、同じ曲の他の難易度でも使う。
            to.markers = CopyMarkers(from.markers);
        }

        /// <summary>曲の設定(BPM・OFFSET・グリッド原点・拍子・座標倍率)の食い違いを、読める文で返す。</summary>
        public static List<string> SongSettingDifferences(SaberChartDocument mine, SaberChartDocument other)
        {
            var differences = new List<string>();
            if (mine == null || other == null) return differences;
            if (Mathf.Abs(mine.bpm - other.bpm) > .0005f)
                differences.Add($"BPM {mine.bpm:0.###} / {other.bpm:0.###}");
            if (Mathf.Abs(mine.offsetMs - other.offsetMs) > .5f)
                differences.Add($"OFFSET {mine.offsetMs:0.#} / {other.offsetMs:0.#}ms");
            if (Mathf.Abs(mine.beatZeroMs - other.beatZeroMs) > .5f)
                differences.Add($"原点 {mine.beatZeroMs:0.#} / {other.beatZeroMs:0.#}ms");
            if (!SameTimeSignatures(mine.timeSignatures, other.timeSignatures))
                differences.Add($"拍子 {mine.timeSignatures?.Count ?? 0}件 / {other.timeSignatures?.Count ?? 0}件");
            if (Mathf.Abs(mine.coordScale - other.coordScale) > .0001f)
                differences.Add($"座標倍率 {mine.coordScale:0.###} / {other.coordScale:0.###}");
            return differences;
        }

        private static bool SameTimeSignatures(List<ChartTimeSignature> a, List<ChartTimeSignature> b)
        {
            List<ChartTimeSignature> left = ChartMeterMap.Normalize(a), right = ChartMeterMap.Normalize(b);
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (Mathf.Abs(left[i].beat - right[i].beat) > .0001f || left[i].numerator != right[i].numerator ||
                    left[i].denominator != right[i].denominator) return false;
            return true;
        }

        private static List<SaberChartMarker> CopyMarkers(List<SaberChartMarker> source)
        {
            var copy = new List<SaberChartMarker>();
            if (source == null) return copy;
            foreach (SaberChartMarker marker in source)
                if (marker != null) copy.Add(new SaberChartMarker { timeMs = marker.timeMs, name = marker.name });
            return copy;
        }

        /// <summary>
        /// すべてのノーツの拍の値が、1つの BPM と原点の格子に乗っているか(差のばらつきが50ms以内)。
        /// 乗っていない譜面は、拍の値がテンポの変化などの情報を持つので、格子の変更で計算し直さない。
        /// </summary>
        public static bool BeatsFollowSingleGrid(SaberChartDocument document)
        {
            if (document?.notes == null || document.notes.Count == 0) return true;
            float bpm = SafeBpm(document.bpm);
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            foreach (SaberChartNote note in document.notes)
            {
                if (note == null || !IsFinite(note.beat) || !IsFinite(note.time)) continue;
                float difference = note.time - BeatToTimeMs(note.beat, bpm, 0f);
                min = Mathf.Min(min, difference);
                max = Mathf.Max(max, difference);
            }
            return float.IsInfinity(min) || max - min <= 50f;
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
            if (document != null) document.beatZeroMs = beatZeroMs;
            if (document?.notes == null) return;
            foreach (SaberChartNote note in document.notes)
            {
                if (note == null) continue;
                note.time = BeatToTimeMs(note.beat, document.bpm, beatZeroMs);
            }
            SortNotes(document);
        }

        /// <summary>
        /// 本編で権威値となる time を保ったまま、補助値 beat だけを現在のグリッドへ合わせる。
        /// ファイルのグリッド原点(beatZeroMs)は書き換えない。原点を変えるのは利用者が明示したときだけ。
        /// </summary>
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

        public static float QuantizeBeat(float beat, int noteDenominator, SaberChartDocument document)
        {
            var position = new ChartMeterMap(document.timeSignatures).At(beat);
            float step = SnapStep(noteDenominator);
            double snapped = position.BarStart + Math.Round((beat - position.BarStart) / step,
                MidpointRounding.AwayFromZero) * step;
            return (float)Math.Max(position.BarStart, Math.Min(position.BarEnd, snapped));
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

        public static string FormatMusicalPosition(float beat, SaberChartDocument document, int noteDenominator)
        {
            var position = new ChartMeterMap(document.timeSignatures).At(beat);
            int subdivision = (int)Math.Floor(position.Fraction * 4.0 / position.Denominator /
                SnapStep(noteDenominator) + ChartMeterMap.Epsilon);
            return $"{position.Measure:D3} : {position.Beat:D2} : {subdivision:D2}";
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

        private static bool IsJsonValue(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                SaberJson.Parse(json);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static SaberChartDocument CopyRaw(SaberChartDocument source)
        {
            if (source == null) return new SaberChartDocument();
            var copy = new SaberChartDocument
            {
                bpm = source.bpm,
                coordScale = source.coordScale,
                offsetMs = source.offsetMs,
                displayLevel = source.displayLevel,
                beatZeroMs = source.beatZeroMs,
                timeSignatures = ChartMeterMap.Normalize(source.timeSignatures),
                notes = new List<SaberChartNote>(),
                extraFields = new List<SaberChartExtraField>(),
                markers = CopyMarkers(source.markers),
            };
            if (source.extraFields != null)
                foreach (SaberChartExtraField field in source.extraFields)
                    if (field != null) copy.extraFields.Add(new SaberChartExtraField { key = field.key, json = field.json });
            if (source.notes == null) return copy;
            foreach (SaberChartNote note in source.notes)
                copy.notes.Add(note?.Clone());
            return copy;
        }
    }
}
