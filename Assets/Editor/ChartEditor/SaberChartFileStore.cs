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
            if (!IsValidSongId(songId, out string reason))
                throw new InvalidOperationException(reason);
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
            var targets = new List<(string path, string difficulty)> { (destination, difficulty) };

            // 曲一覧の契約上 chart.json は必須。Normal は常に同期し、初回は他難易度でも作る。
            string baseChart = Path.Combine(folder, "chart.json");
            if (NormalizeDifficulty(difficulty) == "normal" || !File.Exists(baseChart))
            {
                if (!PathsEqual(destination, baseChart)) targets.Add((baseChart, "base"));
            }

            SaveTogether(targets, songId, json, folder);
            AssetDatabase.Refresh();
            return destination;
        }

        private static void SaveTogether(List<(string path, string difficulty)> targets,
            string songId, string json, string folder)
        {
            // File.Replace と同じボリュームに準備する。先頭が . の作業フォルダはインポート対象外。
            string temporary = Path.Combine(folder, ".chart-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            var files = new List<(string destination, string incoming, string previous, bool existed)>();
            int completed = 0;
            bool keepRecoveryFiles = false;
            try
            {
                // 書き出しと永続バックアップが全部成功するまで、元の譜面には触れない。
                for (int index = 0; index < targets.Count; index++)
                {
                    var target = targets[index];
                    string incoming = Path.Combine(temporary, index + ".incoming");
                    WriteUtf8(incoming, json);
                    BackupExisting(target.path, songId, target.difficulty);
                    files.Add((target.path, incoming, Path.Combine(temporary, index + ".previous"),
                        File.Exists(target.path)));
                }

                foreach (var file in files)
                {
                    // .meta は差し替えず、既存アセットの GUID を維持する。
                    if (file.existed) File.Replace(file.incoming, file.destination, file.previous);
                    else File.Move(file.incoming, file.destination);
                    completed++;
                }
            }
            catch (Exception saveError)
            {
                var errors = new List<Exception> { saveError };
                for (int index = completed - 1; index >= 0; index--)
                {
                    var file = files[index];
                    try
                    {
                        if (file.existed) File.Replace(file.previous, file.destination, null);
                        else File.Delete(file.destination);
                    }
                    catch (Exception restoreError) { errors.Add(restoreError); }
                }
                if (errors.Count > 1)
                {
                    // 復元も阻まれた場合は退避データを残し、復旧場所をエラーに含める。
                    keepRecoveryFiles = true;
                    throw new IOException("譜面を保存できず、一部を元に戻せませんでした。復旧用データ: " + temporary,
                        new AggregateException(errors));
                }
                throw;
            }
            finally
            {
                if (!keepRecoveryFiles)
                {
                    try { Directory.Delete(temporary, true); }
                    catch (IOException cleanupError)
                    {
                        Debug.LogWarning("譜面保存の一時データを削除できませんでした: " + temporary + "\n" + cleanupError.Message);
                    }
                    catch (UnauthorizedAccessException cleanupError)
                    {
                        Debug.LogWarning("譜面保存の一時データを削除できませんでした: " + temporary + "\n" + cleanupError.Message);
                    }
                }
            }
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

            string destination = Path.Combine(folder, "audio" + extension);
            string temporary = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library",
                "3DSaberAudioImports", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(temporary);
            string incoming = Path.Combine(temporary, "incoming" + extension);
            var movedFiles = new List<(string original, string backup)>();
            AudioClip importedClip = null;
            bool committed = false;
            bool keepRecoveryFiles = false;
            DateTime writeTimeUtc;
            long fileLength;
            try
            {
                // コピーとデコードが成功するまで既存音源に触れない。同じファイルの再選択も検証する。
                File.Copy(sourcePath, incoming);
                importedClip = DecodeAudioFile(incoming);
                if (importedClip == null)
                    throw new InvalidOperationException("選択した音源を読み込めませんでした。元の音源は変更していません。");
                var info = new FileInfo(incoming);
                writeTimeUtc = info.LastWriteTimeUtc;
                fileLength = info.Length;

                // 削除の代わりに退避する。途中でロック等に遭遇したら、移動済みのファイルを戻す。
                foreach (string audioName in AudioNames)
                {
                    string oldPath = Path.Combine(folder, audioName);
                    bool isDestination = PathsEqual(oldPath, destination);
                    if (isDestination || removeOtherFormats)
                    {
                        MoveAudioAside(oldPath, temporary, movedFiles);
                        // 同形式の差し替えではGUIDを保持する。他形式のメタデータだけ整理する。
                        if (!isDestination) MoveAudioAside(oldPath + ".meta", temporary, movedFiles);
                    }
                }
                File.Move(incoming, destination);
                committed = true;
            }
            catch (Exception importError)
            {
                var errors = new List<Exception> { importError };
                for (int index = movedFiles.Count - 1; index >= 0; index--)
                {
                    try { File.Move(movedFiles[index].backup, movedFiles[index].original); }
                    catch (Exception restoreError) { errors.Add(restoreError); }
                }
                if (errors.Count > 1)
                {
                    // 復元先まで使用中になった場合、退避データを消さず場所を知らせる。
                    keepRecoveryFiles = true;
                    throw new IOException("音源を取り込めず、一部のファイルを元に戻せませんでした。復旧用データ: " + temporary,
                        new AggregateException(errors));
                }
                throw;
            }
            finally
            {
                if (!committed && importedClip != null) UnityEngine.Object.DestroyImmediate(importedClip);
                if (!keepRecoveryFiles)
                {
                    try { Directory.Delete(temporary, true); }
                    catch (Exception cleanupError)
                    {
                        Debug.LogWarning("音源取り込みの一時データを削除できませんでした: " + temporary + "\n" + cleanupError.Message);
                    }
                }
            }

            // サイズ・更新時刻が同じ差し替えでも、実際に検証した新しい音をプレビューへ渡す。
            // 成功した場合だけ試聴を止め、置き換えたキャッシュの音声データを解放する。
            SaberChartAudioPreview.Stop();
            foreach (string audioName in AudioNames)
            {
                string path = Path.Combine(folder, audioName);
                if (!removeOtherFormats && !PathsEqual(path, destination)) continue;
                if (audioCache.TryGetValue(path, out CachedAudio cached) && cached.clip != null)
                    UnityEngine.Object.DestroyImmediate(cached.clip);
                audioCache.Remove(path);
            }
            importedClip.name = songId + " audio";
            importedClip.hideFlags = HideFlags.HideAndDontSave;
            audioCache[destination] = new CachedAudio
            {
                clip = importedClip,
                writeTimeUtc = writeTimeUtc,
                fileLength = fileLength,
            };

            AssetDatabase.Refresh();
            // 他形式を保持する呼び出しでは、本編と同じ優先順で音源を選ぶ。
            return LoadAudioClip(songId);
        }

        private static void MoveAudioAside(string path, string temporary,
            List<(string original, string backup)> movedFiles)
        {
            if (!File.Exists(path)) return;
            string backup = Path.Combine(temporary, Path.GetFileName(path));
            File.Move(path, backup);
            movedFiles.Add((path, backup));
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
