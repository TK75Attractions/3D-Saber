using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class ChartLoader
{
    // JsonUtility は JSON 文字列から直接読む。ファイル I/O と分離してテストできるようにする。
    public static ChartData Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ChartData();
        try
        {
            json = json.Trim().TrimStart('\uFEFF');
            ChartData data = JsonUtility.FromJson<ChartData>(json);
            if (data == null) return new ChartData();
            if (data.notes == null) data.notes = new System.Collections.Generic.List<NoteData>();
            var explicitNullNotes = FindNullNoteIndices(json);
            // 同時刻の並びは作者の順番を維持する。壊れた1ノーツで曲全体を失わない。
            data.notes = data.notes.Where((n, index) => !explicitNullNotes.Contains(index)
                    && n != null && Finite(n.time) && Finite(n.x) && Finite(n.y))
                .OrderBy(n => n.time).ToList();
            foreach (var note in data.notes)
            {
                note.type = NormalizeToken(note.type, "tap");
                note.color = NormalizeToken(note.color, "default");
                note.direction = NormalizeToken(note.direction, "none");
                note.count = Math.Max(1, note.count);
                if (!Finite(note.lengthMs) || note.lengthMs < 0) note.lengthMs = 0;
                if (!Finite(note.beat)) note.beat = 0;
            }
            // 拍ガイドと描画を成立させる最低限の復旧。ノーツ時刻・配置は補正しない。
            if (!Finite(data.bpm) || data.bpm <= 0) data.bpm = 120;
            if (!Finite(data.coordScale) || data.coordScale <= 0) data.coordScale = 1;
            if (!Finite(data.offsetMs)) data.offsetMs = 0;
            if (!Finite(data.beatZeroMs)) data.beatZeroMs = 0;
            data.timeSignatures = ChartMeterMap.Normalize(data.timeSignatures);
            return data;
        }
        catch (ArgumentException exception)
        {
            Debug.LogWarning("ChartLoader: 譜面を読み込めません。" + exception.Message);
            return new ChartData();
        }
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static string NormalizeToken(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    // JsonUtilityは配列内のnullを既定値オブジェクトに変換する。
    // 0秒・中央の正規ノーツと区別するため、検証済みJSONの明示null位置だけ読む。
    static HashSet<int> FindNullNoteIndices(string json)
    {
        var result = new HashSet<int>();
        if (json.IndexOf("null", StringComparison.Ordinal) < 0) return result;
        int depth = 0;
        for (int i = 0; i < json.Length; i++)
        {
            char token = json[i];
            if (token == '"')
            {
                int end = StringEnd(json, i);
                if (depth == 1 && end - i == 6 && string.CompareOrdinal(json, i + 1, "notes", 0, 5) == 0)
                {
                    int value = SkipWhiteSpace(json, end + 1);
                    if (value < json.Length && json[value] == ':')
                    {
                        value = SkipWhiteSpace(json, value + 1);
                        result.Clear();
                        if (value < json.Length && json[value] == '[') ReadNullElements(json, value, result);
                    }
                }
                i = end;
            }
            else if (token == '{' || token == '[') depth++;
            else if (token == '}' || token == ']') depth--;
        }
        return result;
    }

    static void ReadNullElements(string json, int start, HashSet<int> result)
    {
        int depth = 0, index = 0;
        bool atElementStart = true;
        for (int i = start + 1; i < json.Length; i++)
        {
            char token = json[i];
            if (char.IsWhiteSpace(token)) continue;
            if (depth == 0 && token == ']') return;
            if (depth == 0 && token == ',') { index++; atElementStart = true; continue; }
            if (depth == 0 && atElementStart)
            {
                if (token == 'n' && i + 4 <= json.Length && string.CompareOrdinal(json, i, "null", 0, 4) == 0)
                    result.Add(index);
                atElementStart = false;
            }
            if (token == '"') i = StringEnd(json, i);
            else if (token == '{' || token == '[') depth++;
            else if (token == '}' || token == ']') depth--;
        }
    }

    static int StringEnd(string json, int start)
    {
        for (int i = start + 1; i < json.Length; i++)
        {
            if (json[i] == '\\') { i++; continue; }
            if (json[i] == '"') return i;
        }
        return json.Length - 1;
    }

    static int SkipWhiteSpace(string json, int start)
    {
        while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
        return start;
    }

    public static ChartData LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogWarning($"Chart not found: {filePath}");
            return new ChartData();
        }
        try { return Parse(File.ReadAllText(filePath)); }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning($"ChartLoader: 譜面ファイルを開けません: {filePath} ({exception.GetType().Name})");
            return new ChartData();
        }
    }

    // StreamingAssets/Songs/<songId>/chart.json
    public static ChartData LoadFromStreamingAssets(string songId)
    {
        return LoadFromStreamingAssets(songId, null);
    }

    // 難易度別ロード：chart_<difficulty>.json を優先、無ければ legacy chart.json にフォールバック。
    // difficulty 例：Easy / Normal / Hard（大文字小文字無視）
    public static ChartData LoadFromStreamingAssets(string songId, string difficulty)
    {
        if (!ValidPathSegment(songId)) return new ChartData();
        string dir = Path.Combine(Application.streamingAssetsPath, "Songs", songId);
        if (!string.IsNullOrWhiteSpace(difficulty) && ValidPathSegment(difficulty.Trim()))
        {
            string specific = Path.Combine(dir, $"chart_{difficulty.Trim().ToLowerInvariant()}.json");
            if (File.Exists(specific)) return LoadFromFile(specific);
        }
        return LoadFromFile(Path.Combine(dir, "chart.json"));
    }

    public static bool ValidPathSegment(string value) => !string.IsNullOrWhiteSpace(value)
        && value != "." && value != ".." && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && value.IndexOf('/') < 0 && value.IndexOf('\\') < 0;
}
