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
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", songId, "chart.json");
        return LoadFromFile(path);
    }
}
