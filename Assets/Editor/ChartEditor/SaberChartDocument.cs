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
        // テンポの変わる曲のための、拍と時刻の対応(テンポ地図)。ファイルでは "editorTempoMap"。
        // エディターの格子のほか、本編の小節線(BarLineSpawner)と選曲画面の拍(SongSelectBeatTimeline)も使う。
        public List<SaberChartTempoPoint> tempoMap = new List<SaberChartTempoPoint>();
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
    /// 譜面ファイルへ書く形。エディター内の作業用の項目(extraFields・ノーツの extraJson)は持たず、
    /// 未知の項目は先頭へ、ノーツの未知の項目は各ノーツの中へ、そのまま書き戻す。
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
        public List<SaberChartFileNote> notes = new List<SaberChartFileNote>();
    }

    /// <summary>ファイルへ書くノーツ。項目と並びは SaberChartNote と同じ(書き出す文字を今までと変えない)。</summary>
    [Serializable]
    public sealed class SaberChartFileNote
    {
        public float beat;
        public float time;
        public float x;
        public float y;
        public string type;
        public string color;
        public string direction;
        public int count;
        public float lengthMs;
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
        // エディターが知らないノーツの項目(裏譜面の mirror・windX など)。読み込んだ綴りのままの JSON オブジェクト
        // ({"mirror": true} の形)で、無ければ空。ファイルへは各ノーツの中に元の名前で書き戻す。新しく置いたノーツは空。
        // ToJson の空の省略が「後ろの項目」を前提にするので、項目を足すときもこの2つを最後に置く。
        public string extraJson = string.Empty;
        // 裏譜面のファイルから読んだときだけの、このノーツの元の文字({"raw": 元のノーツ, "loaded": 読み込んだ直後の値})。
        // 書き出しで、値を変えていない項目は元の綴りのまま書く(触っていないノーツはノーツごと元のまま)。表の譜面・新しいノーツは空。
        public string sourceJson = string.Empty;

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
                extraJson = extraJson,
                sourceJson = sourceJson,
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
        // ロングのカット回数の上限。裏譜面には L の1000回・G の100回がある(本編は上限を設けていない)。
        public const int MaxLongCount = 10000;
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

        /// <summary>
        /// 譜面の JSON(ファイルか、エディター内の比較・履歴用)を読む。keepNoteSource は裏譜面のファイルを読むときだけ true にし、
        /// ノーツごとに元の文字を持たせる(書き出しで、触っていない項目を元の綴りのまま書くため)。
        /// </summary>
        public static SaberChartDocument FromJson(string json, bool keepNoteSource = false)
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
            List<SaberChartNote> sourced = CollectExtraFields(json, document, keepNoteSource);
            Normalize(document);
            // 元の文字に、読み込んだ直後(正規化のあと)の値を添える。書き出しでは、この値から変わった項目だけを書き直す。
            foreach (SaberChartNote note in sourced)
            {
                var source = new SaberJsonObject();
                source.Set("raw", SaberJson.Parse(note.sourceJson));
                var loaded = new SaberJsonArray();
                foreach (string value in NoteValues(note)) loaded.Items.Add(SaberJson.String(value));
                source.Set("loaded", loaded);
                note.sourceJson = SaberJson.WriteCompact(source);
            }
            return document;
        }

        // 譜面の項目として扱う名前。これ以外は extraFields に残して保存し直す。
        private const string MarkersFileKey = "editorMarkers";
        private const string TempoMapFileKey = "editorTempoMap";

        private static readonly HashSet<string> KnownChartKeys = new HashSet<string>
        {
            "bpm", "coordScale", "offsetMs", "displayLevel", "beatZeroMs", "timeSignatures", "notes", "extraFields",
            "markers", MarkersFileKey, "tempoMap", TempoMapFileKey,
        };

        // ノーツの項目として扱う名前。これ以外(裏譜面の mirror・windX など)は、ノーツごとに extraJson へ残す。
        // extraJson・sourceJson はエディター内の JSON だけの入れ物。
        private const string NoteExtraKey = "extraJson";
        private const string NoteSourceKey = "sourceJson";

        private static readonly HashSet<string> KnownNoteKeys = new HashSet<string>
        {
            "beat", "time", "x", "y", "type", "color", "direction", "count", "lengthMs", NoteExtraKey, NoteSourceKey,
        };

        // ファイルに書くノーツの項目(SaberChartFileNote と同じ並び)。sourceJson の loaded もこの並び。
        private static readonly string[] NoteFields = { "beat", "time", "x", "y", "type", "color", "direction", "count", "lengthMs" };

        // ノーツの知っている項目の値(比べるための文字)。並びは NoteFields。
        private static string[] NoteValues(SaberChartNote note) => new[]
        {
            Exact(note.beat), Exact(note.time), Exact(note.x), Exact(note.y), note.type ?? string.Empty, note.color ?? string.Empty,
            note.direction ?? string.Empty, note.count.ToString(System.Globalization.CultureInfo.InvariantCulture), Exact(note.lengthMs),
        };

        private static string Exact(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        // 読み取った JSON のうち、元の文字を持たせたノーツ(keepNoteSource のときだけ。ほかは空の一覧)。
        private static List<SaberChartNote> CollectExtraFields(string json, SaberChartDocument document, bool keepNoteSource)
        {
            document.extraFields ??= new List<SaberChartExtraField>();
            document.markers ??= new List<SaberChartMarker>();
            // ここで読めないと、知らない項目(制作メモ・裏譜面の仕掛け)が保存で消える。知っている項目だけで開かずに知らせる。
            if (!SaberJson.TryParseObject(json, out SaberJsonObject root))
                throw new FormatException("譜面の JSON の一部を読み取れないため、知らない項目（制作メモ・裏譜面の仕掛けなど）を残して開けません。");
            List<SaberChartNote> sourced = AlignNotesWithJson(root, document, keepNoteSource);
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
            document.tempoMap ??= new List<SaberChartTempoPoint>();
            if (document.tempoMap.Count == 0 && root.Get(TempoMapFileKey) is SaberJsonArray tempoArray)
            {
                foreach (SaberJsonNode item in tempoArray.Items)
                {
                    if (!(item is SaberJsonObject point)) continue;
                    if (!SaberJson.TryGetNumber(point.Get("beat"), out double beat) ||
                        !SaberJson.TryGetNumber(point.Get("timeMs"), out double timeMs)) continue;
                    document.tempoMap.Add(new SaberChartTempoPoint { beat = (float)beat, timeMs = (float)timeMs });
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
            return sourced;
        }

        // JsonUtility は notes 配列の null を既定値のノーツ(0秒・中央)にするので、そのまま保存すると本編で実ノーツになる。
        // 本編 ChartLoader と同じく、元の JSON で null と書かれた位置だけ除く(値では見分けず、0秒・中央の正規ノーツは残す)。
        // 同じ位置の対応で、ノーツの知らない項目を extraJson へ、keepNoteSource なら元の文字を sourceJson へ移す
        // (null を除く前に移すので、何番目のノーツかがずれない)。
        // 位置で合わせるので Normalize の並べ替えより前に呼び、root の notes が1つで要素数も合うときだけ行う。
        private static List<SaberChartNote> AlignNotesWithJson(SaberJsonObject root, SaberChartDocument document, bool keepNoteSource)
        {
            var sourced = new List<SaberChartNote>();
            if (document.notes == null) return sourced;
            SaberJsonArray array = root.Members.Count(member => member.Key == "notes") == 1 ? root.Get("notes") as SaberJsonArray : null;
            if (array == null || array.Items.Count != document.notes.Count)
            {
                // 対応が分からないまま開くと、ノーツの知らない項目(裏譜面の仕掛け)が保存で消える。開かずに知らせる。
                if (root.Members.Any(member => member.Key == "notes" && member.Value is SaberJsonArray items &&
                                               items.Items.Any(HasUnknownNoteKeys)))
                    throw new FormatException("ノーツの数が読み取りと合わないため、ノーツごとの項目（裏譜面の仕掛けなど）を残して開けません。");
                return sourced;
            }
            for (int i = 0; i < array.Items.Count; i++)
            {
                if (!(array.Items[i] is SaberJsonObject item) || document.notes[i] == null) continue;
                document.notes[i].extraJson = MergeNoteExtras(document.notes[i].extraJson, item);
                if (!keepNoteSource) continue;
                // エディターの入れ物の項目は元の文字に入れない(ファイルのノーツには無い)。
                var raw = new SaberJsonObject();
                foreach (var member in item.Members)
                    if (member.Key != NoteExtraKey && member.Key != NoteSourceKey) raw.Members.Add(member);
                document.notes[i].sourceJson = SaberJson.WriteCompact(raw);
                sourced.Add(document.notes[i]);
            }
            for (int i = array.Items.Count - 1; i >= 0; i--)
                if (array.Items[i] is SaberJsonValue value && value.Raw == "null") document.notes.RemoveAt(i);
            return sourced;
        }

        private static bool HasUnknownNoteKeys(SaberJsonNode node) =>
            node is SaberJsonObject note && note.Members.Any(member => !KnownNoteKeys.Contains(member.Key));

        // ファイルのノーツにある知らない項目を、元の綴りのまま extraJson に重ねる。無ければ今の値(エディターの JSON の extraJson)のまま。
        private static string MergeNoteExtras(string current, SaberJsonObject note)
        {
            SaberJsonObject extra = null;
            foreach (var member in note.Members)
            {
                if (KnownNoteKeys.Contains(member.Key)) continue;
                extra ??= ParseNoteExtras(current) ?? new SaberJsonObject();
                extra.Set(member.Key, member.Value);
            }
            return extra == null ? current ?? string.Empty : SaberJson.WriteCompact(extra);
        }

        // extraJson を読む。空・壊れている・オブジェクトでなければ null。
        private static SaberJsonObject ParseNoteExtras(string extraJson) =>
            !string.IsNullOrWhiteSpace(extraJson) && SaberJson.TryParseObject(extraJson, out SaberJsonObject extra) ? extra : null;

        // 空の extraJson・sourceJson を省く(表の譜面では、比較・履歴・下書きの JSON を今までと同じ文字にする)。
        // 文字列の中の引用符は \" になるので、本当の空の項目だけに合う。
        private static readonly System.Text.RegularExpressions.Regex EmptyNoteExtra = new System.Text.RegularExpressions.Regex(
            ",\\s*\"(?:" + NoteExtraKey + "|" + NoteSourceKey + ")\"\\s*:\\s*\"\"(?=\\s*[,}])",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>エディター内の比較・履歴用の JSON。作業用の項目も含む。</summary>
        public static string ToJson(SaberChartDocument document, bool prettyPrint = true)
        {
            SaberChartDocument output = CopyRaw(document);
            Normalize(output);
            return EmptyNoteExtra.Replace(JsonUtility.ToJson(output, prettyPrint), string.Empty);
        }

        /// <summary>
        /// 譜面ファイルに書く JSON。本編と同じ項目に加え、読み込んだときの未知の項目
        /// (制作メモなど)を先頭へ、ノーツの未知の項目(裏譜面の仕掛けなど)を各ノーツの中へそのまま戻す。
        /// 裏譜面のファイルから読んだノーツ(sourceJson がある)は、値を変えていない項目を元の綴りのまま書く。
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
                notes = output.notes.Select(note => new SaberChartFileNote
                {
                    beat = note.beat, time = note.time, x = note.x, y = note.y, type = note.type, color = note.color,
                    direction = note.direction, count = note.count, lengthMs = note.lengthMs,
                }).ToList(),
            };
            string json = WriteNoteTexts(JsonUtility.ToJson(file, true), output.notes);
            if (output.extraFields.Count == 0 && output.markers.Count == 0 && output.tempoMap.Count == 0) return json;

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
            if (output.tempoMap.Count > 0)
            {
                var points = new SaberJsonArray();
                foreach (SaberChartTempoPoint point in output.tempoMap)
                {
                    var item = new SaberJsonObject();
                    item.Set("beat", SaberJson.Number(Math.Round(point.beat, 4)));
                    item.Set("timeMs", SaberJson.Number(Math.Round(point.timeMs, 3)));
                    points.Items.Add(item);
                }
                extra.Append("\n    ").Append(SaberJson.Quote(TempoMapFileKey)).Append(": ")
                    .Append(SaberJson.WriteCompact(points)).Append(',');
            }
            return json.Insert(open + 1, extra.ToString());
        }

        // JsonUtility が書いたノーツを、ノーツ自身が持つ元の文字で直す(改行と字下げはそのノーツの書き方に合わせる)。
        // ・元の文字(sourceJson)があり、値をどれも変えていないノーツは、元の項目の並びと綴りのまま書く。
        // ・値を変えたノーツは、変えていない項目だけ元の綴りに戻す。知らない項目は末尾へ元の名前と綴りで足す。
        // 元の文字も知らない項目も無ければ、文字を変えない(表の譜面は今までどおり)。
        private static string WriteNoteTexts(string json, List<SaberChartNote> notes)
        {
            if (notes.All(note => string.IsNullOrEmpty(note.extraJson) && string.IsNullOrEmpty(note.sourceJson))) return json;
            List<(string key, int start, int end)> members = SaberJson.RootMemberSpans(json);
            List<(int start, int end)> spans = members == null || members.Count(member => member.key == "notes") != 1 ? null
                : SaberJson.ArrayItemSpans(json, members.First(member => member.key == "notes").start);
            if (spans == null || spans.Count != notes.Count)
                throw new InvalidOperationException("ノーツごとの項目（裏譜面の仕掛けなど）を書き戻す位置が見つかりません。");
            var builder = new System.Text.StringBuilder(json.Length + notes.Count * 32);
            int copied = 0;
            for (int i = 0; i < notes.Count; i++)
            {
                SaberJsonObject extra = ParseNoteExtras(notes[i].extraJson);
                SaberJsonObject raw = ParseNoteSource(notes[i].sourceJson, out string[] loaded);
                if (extra == null && raw == null) continue;
                int open = spans[i].start, close = spans[i].end;
                if (json[open] != '{' || json[close] != '}') continue;
                int first = open + 1;
                while (first < close && char.IsWhiteSpace(json[first])) first++;
                string separator = json.Substring(open + 1, first - open - 1);
                int end = close;
                while (end > open + 1 && char.IsWhiteSpace(json[end - 1])) end--;
                string[] now = raw != null ? NoteValues(notes[i]) : null;
                if (raw != null && now.SequenceEqual(loaded) &&
                    SaberJson.WriteCompact(UnknownMembers(raw)) == SaberJson.WriteCompact(extra ?? new SaberJsonObject()))
                {
                    builder.Append(json, copied, open - copied).Append('{');
                    for (int m = 0; m < raw.Members.Count; m++)
                    {
                        if (m > 0) builder.Append(',');
                        builder.Append(separator).Append(SaberJson.Quote(raw.Members[m].Key)).Append(": ")
                            .Append(SaberJson.WriteCompact(raw.Members[m].Value));
                    }
                    builder.Append(json, end, close - end).Append('}');
                    copied = close + 1;
                    continue;
                }
                if (raw != null)
                {
                    List<(string key, int start, int end)> fields = SaberJson.ObjectMemberSpans(json, open);
                    if (fields != null)
                        foreach ((string key, int start, int stop) in fields)
                        {
                            int field = Array.IndexOf(NoteFields, key);
                            if (field < 0 || now[field] != loaded[field] || !(LastMember(raw, key) is SaberJsonValue value)) continue;
                            builder.Append(json, copied, start - copied).Append(value.Raw);
                            copied = stop + 1;
                        }
                }
                if (extra == null) continue;
                builder.Append(json, copied, end - copied);
                bool hasMember = end > open + 1;
                foreach (var member in extra.Members)
                {
                    // 名前の重なる項目は書かない(同じ名前が2つあると、本編は後ろの値を読む)。
                    if (KnownNoteKeys.Contains(member.Key)) continue;
                    if (hasMember) builder.Append(',');
                    builder.Append(separator).Append(SaberJson.Quote(member.Key)).Append(": ").Append(SaberJson.WriteCompact(member.Value));
                    hasMember = true;
                }
                copied = end;
            }
            builder.Append(json, copied, json.Length - copied);
            return builder.ToString();
        }

        // sourceJson の元のノーツと、読み込んだ直後の値(NoteFields の並び)。無い・壊れているときは null。
        private static SaberJsonObject ParseNoteSource(string sourceJson, out string[] loaded)
        {
            loaded = null;
            if (string.IsNullOrWhiteSpace(sourceJson) || !SaberJson.TryParseObject(sourceJson, out SaberJsonObject source) ||
                !(source.Get("raw") is SaberJsonObject raw) || !(source.Get("loaded") is SaberJsonArray values) ||
                values.Items.Count != NoteFields.Length) return null;
            var texts = new string[NoteFields.Length];
            for (int i = 0; i < texts.Length; i++)
                if (!SaberJson.TryGetString(values.Items[i], out texts[i])) return null;
            loaded = texts;
            return raw;
        }

        // 元のノーツの知らない項目(読み込みで extraJson にしたものと同じ形)。
        private static SaberJsonObject UnknownMembers(SaberJsonObject note)
        {
            var unknown = new SaberJsonObject();
            foreach (var member in note.Members)
                if (!KnownNoteKeys.Contains(member.Key)) unknown.Set(member.Key, member.Value);
            return unknown;
        }

        // 同じ名前が2つあれば、本編(JsonUtility)と同じく後ろの値。
        private static SaberJsonNode LastMember(SaberJsonObject note, string key)
        {
            SaberJsonNode found = null;
            foreach (var member in note.Members)
                if (member.Key == key) found = member.Value;
            return found;
        }

        /// <summary>
        /// これから書く譜面の JSON の上の段(notes 以外)のうち、元のファイルから変わっていない項目を、元のファイルの文字のまま書く
        /// (float にした bpm・丸めたテンポ地図・1行に詰めた知らない項目を、触っていなければ元の綴りに戻す)。
        /// 「変わっていない」は、元のファイルをこのエディターで読んでそのまま書いたときと同じ文字になること。
        /// 元のファイルにあってエディターが書かない項目(空の editorMarkers など)も、そのままなら元の文字で足す。
        /// </summary>
        public static string KeepUntouchedRootTexts(string json, string original)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrWhiteSpace(original)) return json;
            string unchanged;
            try { unchanged = ToFileJson(FromJson(original)); }
            catch (Exception exception) when (exception is FormatException || exception is InvalidOperationException ||
                                              exception is ArgumentException) { return json; }
            List<(string key, int start, int end)> mine = SaberJson.RootMemberSpans(json);
            List<(string key, int start, int end)> baseline = SaberJson.RootMemberSpans(unchanged);
            List<(string key, int start, int end)> source = SaberJson.RootMemberSpans(original);
            if (mine == null || baseline == null || source == null || mine.Count == 0) return json;
            string newline = json.Contains("\r\n") ? "\r\n" : "\n";
            string RawText((string key, int start, int end) member)
            {
                // 元の文字の改行は、書く JSON の改行にそろえる(値の文字列の中に生の改行は無いので、項目の間の改行だけが変わる)。
                string text = original.Substring(member.start, member.end - member.start + 1).Replace("\r\n", "\n");
                return newline != "\n" ? text.Replace("\n", newline) : text;
            }
            var builder = new System.Text.StringBuilder(json.Length);
            int copied = 0;
            foreach ((string key, int start, int end) in mine)
            {
                // 名前が重なる項目は、どれが対応するか分からないので書き直したままにする。
                if (key == "notes" || mine.Count(member => member.key == key) != 1 ||
                    baseline.Count(member => member.key == key) != 1 || source.Count(member => member.key == key) != 1) continue;
                var before = baseline.First(member => member.key == key);
                int length = end - start + 1;
                if (before.end - before.start + 1 != length || string.CompareOrdinal(json, start, unchanged, before.start, length) != 0) continue;
                builder.Append(json, copied, start - copied).Append(RawText(source.First(member => member.key == key)));
                copied = end + 1;
            }
            // 書いたものにも、元のファイルをそのまま書いたものにも無い項目は、エディターが書かないだけなので元の文字で足す。
            var missing = source.Where(member => member.key != "notes" && source.Count(other => other.key == member.key) == 1 &&
                                                 mine.All(other => other.key != member.key) &&
                                                 baseline.All(other => other.key != member.key)).ToList();
            if (missing.Count > 0)
            {
                int rootOpen = json.IndexOf('{'), first = rootOpen + 1;
                while (first < json.Length && char.IsWhiteSpace(json[first])) first++;
                string separator = json.Substring(rootOpen + 1, first - rootOpen - 1);
                int insertAt = mine[mine.Count - 1].end + 1;
                builder.Append(json, copied, insertAt - copied);
                copied = insertAt;
                foreach (var member in missing)
                    builder.Append(',').Append(separator).Append(SaberJson.Quote(member.key)).Append(": ").Append(RawText(member));
            }
            builder.Append(json, copied, json.Length - copied);
            return builder.ToString();
        }

        // 裏譜面をエディターで保存した日時(上の段の項目。本編は読まない)。生成の道具で作り直すときに、手で直したことが分かるように。
        public const string EditedAtKey = "_editorEditedAt";

        /// <summary>上の段の _editorEditedAt を time(ISO 8601)にした写し。元の譜面は変えない。2回目以降は同じ項目を上書きする。</summary>
        public static SaberChartDocument WithEditedAt(SaberChartDocument document, DateTimeOffset time)
        {
            SaberChartDocument copy = CopyRaw(document);
            string value = SaberJson.Quote(time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture));
            SaberChartExtraField field = copy.extraFields.Find(item => item != null && item.key == EditedAtKey);
            if (field != null) field.json = value;
            else copy.extraFields.Add(new SaberChartExtraField { key = EditedAtKey, json = value });
            return copy;
        }

        /// <summary>ノーツの知らない項目の名前(読み込んだ順)。</summary>
        public static List<string> NoteExtraKeys(SaberChartNote note)
        {
            var keys = new List<string>();
            SaberJsonObject extra = ParseNoteExtras(note?.extraJson);
            if (extra != null) keys.AddRange(extra.Members.Select(member => member.Key));
            return keys;
        }

        /// <summary>ノーツの知らない項目 key が true か(R の mirror など)。</summary>
        public static bool NoteExtraFlag(SaberChartNote note, string key)
        {
            if (string.IsNullOrEmpty(note?.extraJson) || note.extraJson.IndexOf(key, StringComparison.Ordinal) < 0) return false;
            SaberJsonObject extra = ParseNoteExtras(note.extraJson);
            return extra != null && SaberJson.TryGetBool(extra.Get(key), out bool value) && value;
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
            document.tempoMap = SaberChartGrid.Clean(document.tempoMap);

            foreach (SaberChartNote note in document.notes)
            {
                if (!IsFinite(note.beat)) note.beat = 0f;
                if (!IsFinite(note.time)) note.time = 0f;
                if (!IsFinite(note.x)) note.x = 0f;
                if (!IsFinite(note.y)) note.y = 0f;
                // 0 未満の time・beat は直さない。OFFSET が正なら本編は time + offsetMs で曲の中に届かせる(ChartLoader も直さない)ので、
                // 別の項目を編集して保存しても判定時刻を動かさない。時刻を指定する編集と拍の数え直しは、従来どおり 0 未満へ置かない。

                string direction = LowerOrDefault(note.direction, DirectionNone);
                note.direction = ValidDirections.Contains(direction) ? direction : DirectionNone;
                note.color = LowerOrDefault(note.color, ColorDefault);

                string type = LowerOrDefault(note.type, TypeTap);
                note.type = ValidTypes.Contains(type) ? type : TypeTap;
                int count = Mathf.Clamp(note.count, 1, MaxLongCount);

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
                // 知らない項目と元の文字の中身は変えない(読み込んだ綴りのまま書き戻す)。
                note.extraJson ??= string.Empty;
                note.sourceJson ??= string.Empty;
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
            // 目印とテンポ地図は曲の構成なので、同じ曲の他の難易度でも使う。
            to.markers = CopyMarkers(from.markers);
            to.tempoMap = SaberChartGrid.Clean(from.tempoMap);
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
            if (!SameTempoMaps(mine.tempoMap, other.tempoMap))
                differences.Add($"テンポ地図 {mine.tempoMap?.Count ?? 0}点 / {other.tempoMap?.Count ?? 0}点");
            return differences;
        }

        // 曲の設定の食い違いの警告と、設定を写すときに拍を保つかの判断で、同じ許容差を使う。
        internal static bool SameTempoMaps(List<SaberChartTempoPoint> a, List<SaberChartTempoPoint> b)
        {
            List<SaberChartTempoPoint> left = SaberChartGrid.Clean(a), right = SaberChartGrid.Clean(b);
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (Mathf.Abs(left[i].beat - right[i].beat) > .0001f || Mathf.Abs(left[i].timeMs - right[i].timeMs) > .5f) return false;
            return true;
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
        /// <summary>格子(テンポ地図を含む)に合わせて拍の値を計算し直す。時刻は変えない。</summary>
        public static void RecalculateBeatsFromTimes(SaberChartDocument document, Func<float, float> beatAt)
        {
            if (document?.notes == null || beatAt == null) return;
            foreach (SaberChartNote note in document.notes)
            {
                if (note == null) continue;
                note.beat = Mathf.Max(0f, beatAt(note.time));
            }
            SortNotes(document);
        }

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

        /// <summary>
        /// 動かした座標を範囲に収める。元から範囲の外にある値(裏譜面 L の x 2.7 など)は、その値までを範囲とみなす
        /// (少し動かしただけで範囲の端へ飛ばさない)。動かしていない軸には使わない。
        /// </summary>
        public static float ClampMoved(float value, float original, float min, float max) =>
            Mathf.Clamp(value, Mathf.Min(min, original), Mathf.Max(max, original));

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
                tempoMap = SaberChartGrid.Clean(source.tempoMap),
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
