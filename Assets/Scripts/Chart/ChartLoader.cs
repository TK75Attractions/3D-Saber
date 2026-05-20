using System.IO;
using UnityEngine;

public static class ChartLoader
{
    // JsonUtility は JSON 文字列から直接読む。ファイル I/O と分離してテストできるようにする。
    public static ChartData Parse(string json)
    {
        if (string.IsNullOrEmpty(json)) return new ChartData();
        ChartData data = JsonUtility.FromJson<ChartData>(json);
        if (data == null) return new ChartData();
        if (data.notes == null) data.notes = new System.Collections.Generic.List<NoteData>();
        data.notes.Sort((a, b) => a.time.CompareTo(b.time));
        return data;
    }

    public static ChartData LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogWarning($"Chart not found: {filePath}");
            return new ChartData();
        }
        return Parse(File.ReadAllText(filePath));
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
        string dir = Path.Combine(Application.streamingAssetsPath, "Songs", songId);
        if (!string.IsNullOrEmpty(difficulty))
        {
            string specific = Path.Combine(dir, $"chart_{difficulty.ToLowerInvariant()}.json");
            if (File.Exists(specific)) return LoadFromFile(specific);
        }
        return LoadFromFile(Path.Combine(dir, "chart.json"));
    }
}
