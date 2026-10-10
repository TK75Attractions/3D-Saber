using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// 曲の表示名・ふりがな・アーティストと、選曲に出すかを、曲フォルダの stage.json から読む。
// 新しい曲のたびにコードへ曲名を決め打ちしない。stage.json に無い項目は、フォルダ名(曲ID)をそのまま使う。
// 保存記録と音源フォルダの ID は変えず、画面に出す名前だけをここで決める。
public static class SongDisplayInfo
{
    [Serializable]
    private sealed class Data
    {
        public string displayName;
        public string displayReading;
        public string artist;
        // 譜面エディターの「選曲に出す」を外すと false。項目が無い曲はこれまでどおり出す。
        public bool listed = true;
    }

    public readonly struct Info
    {
        public readonly string DisplayName;
        public readonly string Reading;
        public readonly string Artist;
        public readonly bool Listed;

        public Info(string displayName, string reading, string artist, bool listed)
        {
            DisplayName = displayName;
            Reading = reading;
            Artist = artist;
            Listed = listed;
        }
    }

    // 選曲画面は毎フレーム名前を引くことがあるので、短い間だけ覚えておく(stage.json の更新は1秒以内に反映)。
    private const double CacheSeconds = 1.0;
    private static readonly Dictionary<string, (DateTime checkedAt, long stamp, Info info)> cache =
        new Dictionary<string, (DateTime, long, Info)>(StringComparer.Ordinal);
    // 曲フォルダの実際の名前(大文字小文字を区別しない引き当て用)。
    private static Dictionary<string, string> folders;
    private static DateTime foldersCheckedAt;

    public static Info Get(string songId)
    {
        var fallback = new Info(songId ?? string.Empty, string.Empty, string.Empty, true);
        if (!ChartLoader.ValidPathSegment(songId)) return fallback;
        DateTime now = DateTime.UtcNow;
        if (cache.TryGetValue(songId, out var cached) && (now - cached.checkedAt).TotalSeconds < CacheSeconds)
            return cached.info;

        string folder = FolderName(songId, now);
        if (folder == null)
        {
            cache[songId] = (now, 0, fallback);
            return fallback;
        }
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", folder, "stage.json");
        long stamp = 0;
        try
        {
            if (File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
        if (cached.stamp == stamp && stamp != 0 && cache.ContainsKey(songId))
        {
            cache[songId] = (now, stamp, cached.info);
            return cached.info;
        }

        Info info = fallback;
        if (stamp != 0)
        {
            try
            {
                var data = JsonUtility.FromJson<Data>(File.ReadAllText(path));
                if (data != null)
                {
                    // 表示名がフォルダ名と同じだけなら新しい情報はないので、渡された ID の綴りのまま出す。
                    string name = data.displayName?.Trim();
                    bool meaningful = !string.IsNullOrEmpty(name) && !string.Equals(name, folder, StringComparison.Ordinal);
                    info = new Info(meaningful ? name : fallback.DisplayName,
                        data.displayReading?.Trim() ?? string.Empty, data.artist?.Trim() ?? string.Empty, data.listed);
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            {
                Debug.LogWarning("曲の表示名を読み込めません。フォルダ名で表示します: " + error.Message);
            }
        }
        cache[songId] = (now, stamp, info);
        return info;
    }

    public static void ClearCache()
    {
        cache.Clear();
        folders = null;
    }

    // 曲フォルダの実際の名前。保存記録の ID と大文字小文字が違っても同じ曲として引く。
    private static string FolderName(string songId, DateTime now)
    {
        if (folders == null || (now - foldersCheckedAt).TotalSeconds >= CacheSeconds)
        {
            folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foldersCheckedAt = now;
            string root = Path.Combine(Application.streamingAssetsPath, "Songs");
            try
            {
                if (Directory.Exists(root))
                    foreach (string directory in Directory.GetDirectories(root))
                    {
                        string name = Path.GetFileName(directory);
                        if (!folders.ContainsKey(name)) folders.Add(name, name);
                    }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
        }
        return folders.TryGetValue(songId, out string folder) ? folder : null;
    }
}
