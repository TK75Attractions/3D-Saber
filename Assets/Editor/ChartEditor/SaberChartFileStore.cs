using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Saber.ChartEditor
{
    internal static class SaberChartFileStore
    {
        private static readonly string[] AudioNames = { "audio.ogg", "audio.wav", "audio.mp3" };

        public static string SongsRootPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "StreamingAssets", "Songs"));

        public static bool IsValidSongId(string songId, out string reason)
        {
            if (string.IsNullOrWhiteSpace(songId))
            {
                reason = "曲フォルダ名を入力してください。";
                return false;
            }

            string trimmed = songId.Trim();
            if (trimmed == "." || trimmed == ".." ||
                trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                trimmed.Contains("/") || trimmed.Contains("\\"))
            {
                reason = "曲フォルダ名に使用できない文字が含まれています。";
                return false;
            }

            reason = null;
            return true;
        }

        public static string SongFolderPath(string songId)
        {
            if (!IsValidSongId(songId, out _)) return null;
            string combined = Path.GetFullPath(Path.Combine(SongsRootPath, songId.Trim()));
            string root = SongsRootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return combined.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? combined : null;
        }

        public static string ChartPath(string songId, string difficulty)
        {
            string folder = SongFolderPath(songId);
            if (folder == null) return null;
            string safeDifficulty = NormalizeDifficulty(difficulty);
            return Path.Combine(folder, $"chart_{safeDifficulty}.json");
        }

        public static string FindChartToLoad(string songId, string difficulty)
        {
            string specific = ChartPath(songId, difficulty);
            if (!string.IsNullOrEmpty(specific) && File.Exists(specific)) return specific;

            string folder = SongFolderPath(songId);
            if (folder == null) return null;
            string legacy = Path.Combine(folder, "chart.json");
            return File.Exists(legacy) ? legacy : null;
        }

        public static SaberChartDocument Load(string songId, string difficulty, out string loadedPath)
        {
            loadedPath = FindChartToLoad(songId, difficulty);
            if (string.IsNullOrEmpty(loadedPath)) return new SaberChartDocument();
            return SaberChartUtility.FromJson(File.ReadAllText(loadedPath, Encoding.UTF8));
        }

        public static string Save(SaberChartDocument document, string songId, string difficulty)
        {
            if (!IsValidSongId(songId, out string reason))
                throw new InvalidOperationException(reason);

            string folder = SongFolderPath(songId);
            Directory.CreateDirectory(folder);
            string destination = ChartPath(songId, difficulty);
            string json = SaberChartUtility.ToJson(document, true);
            BackupExisting(destination, songId, difficulty);
            WriteUtf8(destination, json);

            // 曲一覧の契約上 chart.json は必須。Normal は常に同期し、初回は他難易度でも作る。
            string baseChart = Path.Combine(folder, "chart.json");
            if (NormalizeDifficulty(difficulty) == "normal" || !File.Exists(baseChart))
            {
                if (!PathsEqual(destination, baseChart)) BackupExisting(baseChart, songId, "base");
                WriteUtf8(baseChart, json);
            }

            AssetDatabase.Refresh();
            return destination;
        }

        private sealed class CachedAudio
        {
            public AudioClip clip;
            public bool failed;
            public DateTime writeTimeUtc;
            public long fileLength;
        }

        private static readonly Dictionary<string, CachedAudio> audioCache =
            new Dictionary<string, CachedAudio>(StringComparer.OrdinalIgnoreCase);

        // StreamingAssets 内のファイルは Unity では「素通しコピー用」(DefaultAsset)扱いで、
        // AudioClip アセットとしては絶対にインポートされない。そのため AssetDatabase ではなく
        // 本編ランタイムと同じ file:// 経由のデコードで読み込む。
        // 毎リペイントの検証(ValidationWarnings)からも呼ばれるため、ファイル更新時刻+サイズで
        // キャッシュし、失敗もキャッシュして再デコードの連発(ビジーカーソル連打)を防ぐ。
        public static AudioClip LoadAudioClip(string songId)
        {
            string folder = SongFolderPath(songId);
            if (folder == null) return null;
            foreach (string audioName in AudioNames)
            {
                string path = Path.Combine(folder, audioName);
                if (!File.Exists(path)) continue;

                var info = new FileInfo(path);
                if (audioCache.TryGetValue(path, out CachedAudio cached) &&
                    cached.writeTimeUtc == info.LastWriteTimeUtc &&
                    cached.fileLength == info.Length)
                {
                    if (cached.failed) continue;         // 前回デコード失敗 → 次の候補へ
                    if (cached.clip) return cached.clip; // 生存していればそのまま使う
                    // クリップが破棄されていたら作り直しに落ちる
                }

                AudioClip clip = DecodeAudioFile(path);
                if (clip != null)
                {
                    clip.name = songId + " audio";
                    clip.hideFlags = HideFlags.HideAndDontSave;
                }
                audioCache[path] = new CachedAudio
                {
                    clip = clip,
                    failed = clip == null,
                    writeTimeUtc = info.LastWriteTimeUtc,
                    fileLength = info.Length,
                };
                if (clip != null) return clip;
            }
            return null;
        }

        // file:// 経由の実行時デコード(mp3/ogg/wav)。エディタ専用なのでタイムアウト付きで同期待ちする。
        private static AudioClip DecodeAudioFile(string absolutePath)
        {
            AudioType type;
            switch (Path.GetExtension(absolutePath).ToLowerInvariant())
            {
                case ".mp3": type = AudioType.MPEG; break;
                case ".ogg": type = AudioType.OGGVORBIS; break;
                case ".wav": type = AudioType.WAV; break;
                default: return null;
            }

            string url = new Uri(absolutePath).AbsoluteUri; // 日本語・空白を含むパスをエスケープ
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(url, type))
            {
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                double start = EditorApplication.timeSinceStartup;
                while (!operation.isDone)
                {
                    if (EditorApplication.timeSinceStartup - start > 20.0)
                    {
                        request.Abort();
                        return null;
                    }
                    System.Threading.Thread.Sleep(10);
                }
                if (request.result != UnityWebRequest.Result.Success) return null;
                try
                {
                    return DownloadHandlerAudioClip.GetContent(request);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public static bool IsAudioClipForSong(AudioClip clip, string songId)
        {
            if (clip == null) return false;
            string folder = SongFolderPath(songId);
            if (folder == null) return false;

            // このストアがデコードして返したクリップはキャッシュ照合で判定する
            foreach (string audioName in AudioNames)
            {
                string path = Path.Combine(folder, audioName);
                if (audioCache.TryGetValue(path, out CachedAudio cached) && cached.clip == clip) return true;
            }

            // プロジェクト内アセットとして選択されたクリップはパスで判定する
            string assetPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(assetPath)) return false;
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string absolute = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
            foreach (string audioName in AudioNames)
            {
                if (PathsEqual(absolute, Path.Combine(folder, audioName))) return true;
            }
            return false;
        }

        public static AudioClip ImportAudio(string sourcePath, string songId, bool removeOtherFormats)
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("音源が見つかりません。", sourcePath);
            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (extension != ".ogg" && extension != ".wav" && extension != ".mp3")
                throw new InvalidOperationException("対応音源は .ogg / .wav / .mp3 です。");

            string folder = SongFolderPath(songId);
            if (folder == null) throw new InvalidOperationException("曲フォルダ名が正しくありません。");
            Directory.CreateDirectory(folder);

            if (removeOtherFormats)
            {
                foreach (string audioName in AudioNames)
                {
                    string oldPath = Path.Combine(folder, audioName);
                    if (!PathsEqual(oldPath, sourcePath))
                    {
                        if (File.Exists(oldPath)) File.Delete(oldPath);
                        string meta = oldPath + ".meta";
                        if (File.Exists(meta)) File.Delete(meta);
                    }
                }
            }

            string destination = Path.Combine(folder, "audio" + extension);
            if (!PathsEqual(sourcePath, destination)) File.Copy(sourcePath, destination, true);
            AssetDatabase.Refresh();
            // StreamingAssets 内は AudioClip アセットにならないため、実行時デコード(キャッシュ付き)で返す
            return LoadAudioClip(songId);
        }

        public static List<string> ExistingSongIds()
        {
            var result = new List<string>();
            if (!Directory.Exists(SongsRootPath)) return result;
            foreach (string directory in Directory.GetDirectories(SongsRootPath))
                result.Add(Path.GetFileName(directory));
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static string NormalizeDifficulty(string difficulty)
        {
            return string.IsNullOrWhiteSpace(difficulty) ? "normal" : difficulty.Trim().ToLowerInvariant();
        }

        private static void BackupExisting(string sourcePath, string songId, string difficulty)
        {
            if (!File.Exists(sourcePath)) return;
            string backupRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "3DSaberChartBackups"));
            Directory.CreateDirectory(backupRoot);
            string safeSong = string.IsNullOrWhiteSpace(songId) ? "Song" : songId.Trim();
            string name = $"{safeSong}_{difficulty}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json";
            File.Copy(sourcePath, Path.Combine(backupRoot, name), true);
        }

        private static void WriteUtf8(string path, string content)
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class SaberChartHistory
    {
        private const int Capacity = 100;
        private readonly List<string> undo = new List<string>();
        private readonly List<string> redo = new List<string>();

        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;

        public void Clear()
        {
            undo.Clear();
            redo.Clear();
        }

        public void Record(string jsonBeforeChange)
        {
            if (string.IsNullOrEmpty(jsonBeforeChange)) return;
            if (undo.Count == 0 || undo[undo.Count - 1] != jsonBeforeChange)
                undo.Add(jsonBeforeChange);
            if (undo.Count > Capacity) undo.RemoveAt(0);
            redo.Clear();
        }

        public SaberChartDocument Undo(SaberChartDocument current)
        {
            if (!CanUndo) return current;
            redo.Add(SaberChartUtility.ToJson(current, false));
            string previous = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            return SaberChartUtility.FromJson(previous);
        }

        public SaberChartDocument Redo(SaberChartDocument current)
        {
            if (!CanRedo) return current;
            undo.Add(SaberChartUtility.ToJson(current, false));
            string next = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            return SaberChartUtility.FromJson(next);
        }
    }
}
