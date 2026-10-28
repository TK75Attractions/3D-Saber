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
        // Keep transaction I/O behind replaceable delegates so rollback tests can inject
        // portable failures instead of relying on OS-specific open-file locking behavior.
        // 書いたばかりのファイルは、ウイルス対策などが数 ms 開いていて、置き換え・移動が失敗することがある(Windows で
        // 「置換されるファイルを削除できません」。保存は巻き戻るが、ときどき保存できなかった)。元のファイルが両方とも
        // そのまま残っている(何も変わっていない)ときだけ、少し待ってやり直す。
        private static Action<string, string> moveFileOperation = (source, destination) =>
            RetryWhileUnchanged(() => File.Move(source, destination), () => File.Exists(source) && !File.Exists(destination));
        private static Action<string, string, string> replaceFileOperation = (source, destination, backup) =>
            RetryWhileUnchanged(() => File.Replace(source, destination, backup), () => File.Exists(source) && File.Exists(destination));

        private static void RetryWhileUnchanged(Action operation, Func<bool> unchanged)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { operation(); return; }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < 5 && unchanged())
                {
                    System.Threading.Thread.Sleep(40);
                }
            }
        }

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
            // 裏譜面は Normal(chart.json)で代わりに開かない(保存すると Normal の中身の裏譜面ができるため)。
            if (SaberChartSecrets.IsSecret(difficulty)) return null;

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
            // 裏譜面はノーツごとに元の文字を持たせる(保存で、触っていない項目を元の綴りのまま書くため)。
            return SaberChartUtility.FromJson(File.ReadAllText(loadedPath, Encoding.UTF8), SaberChartSecrets.IsSecret(difficulty));
        }

        public static string Save(SaberChartDocument document, string songId, string difficulty)
        {
            if (!IsValidSongId(songId, out string reason))
                throw new InvalidOperationException(reason);

            string folder = SongFolderPath(songId);
            Directory.CreateDirectory(folder);
            string destination = ChartPath(songId, difficulty);
            string json;
            if (SaberChartSecrets.IsSecret(difficulty))
            {
                // 裏譜面は、生成の道具で作り直すときに手で直したことが分かるよう、保存した日時を書く(ファイルにだけ。編集中の譜面には入れない)。
                // 触っていない上の段の項目は、元のファイルの文字のまま書く(bpm 57.333333333333336 を float に丸めない)。
                json = SaberChartUtility.ToFileJson(SaberChartUtility.WithEditedAt(document, DateTimeOffset.Now));
                if (File.Exists(destination)) json = SaberChartUtility.KeepUntouchedRootTexts(json, File.ReadAllText(destination, Encoding.UTF8));
            }
            else json = SaberChartUtility.ToFileJson(document);
            var targets = new List<(string path, string difficulty)> { (destination, difficulty) };

            // chart.json は Easy / Normal の読み込み先の予備。Normal と同じ内容に保ち、
            // Hard や Easy を先に保存しても予備には書かない(Hard が Easy・Normal の代わりに出ないように)。
            string baseChart = Path.Combine(folder, "chart.json");
            if (NormalizeDifficulty(difficulty) == "normal" && !PathsEqual(destination, baseChart))
                targets.Add((baseChart, "base"));

            SaveTogether(targets, songId, json, folder);
            AssetDatabase.Refresh();
            return destination;
        }

        public static string StagePath(string songId)
        {
            string folder = SongFolderPath(songId);
            return folder == null ? null : Path.Combine(folder, "stage.json");
        }

        /// <summary>その難易度だけの演出 stage_<難易度>.json(ElDorado G など。本編はあればこちらを使う)。無ければ null。</summary>
        public static string StageVariantPath(string songId, string difficulty)
        {
            string folder = SongFolderPath(songId);
            if (folder == null || string.IsNullOrWhiteSpace(difficulty) || !IsValidSongId(difficulty, out _)) return null;
            string path = Path.Combine(folder, "stage_" + NormalizeDifficulty(difficulty) + ".json");
            return File.Exists(path) ? path : null;
        }

        public static string CoverPath(string songId)
        {
            string folder = SongFolderPath(songId);
            return folder == null ? null : Path.Combine(folder, "cover.png");
        }

        /// <summary>本編が鳴らす音源ファイル(ogg → wav → mp3 の順で最初に見つかったもの)。無ければ null。</summary>
        public static string AudioPath(string songId) => AudioPathFor(songId, null);

        /// <summary>その難易度で本編が鳴らす音源。その難易度の音源(ElDorado G の audio_g.mp3 など)があればそちら。</summary>
        public static string AudioPathFor(string songId, string difficulty)
        {
            string folder = SongFolderPath(songId);
            if (folder == null) return null;
            foreach (string audioName in AudioNamesFor(folder, difficulty))
            {
                string path = Path.Combine(folder, audioName);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        // 本編(SongLoudness.VariantSuffix)と同じ決まり: audio_<難易度>.* が1つでもあれば、その難易度はそちらだけを鳴らす。
        private static IEnumerable<string> AudioNamesFor(string folder, string difficulty)
        {
            string suffix = SongLoudness.VariantSuffix(folder, difficulty);
            if (string.IsNullOrEmpty(suffix)) return AudioNames;
            var names = new List<string>();
            foreach (string audioName in AudioNames)
                names.Add(Path.GetFileNameWithoutExtension(audioName) + suffix + Path.GetExtension(audioName));
            return names;
        }

        /// <summary>stage.json を、譜面と同じ手順(一時ファイル→バックアップ→置き換え、失敗したら戻す)で書く。</summary>
        public static void SaveStage(string songId, string text)
        {
            if (!IsValidSongId(songId, out string reason)) throw new InvalidOperationException(reason);
            string folder = SongFolderPath(songId);
            Directory.CreateDirectory(folder);
            SaveTogether(new List<(string path, string difficulty)> { (Path.Combine(folder, "stage.json"), "stage") }, songId, text, folder);
            AssetDatabase.Refresh();
        }

        /// <summary>cover.png を書く。前の画像は Library のバックアップへ写してから置き換える。</summary>
        public static void SaveCover(string songId, byte[] png)
        {
            if (!IsValidSongId(songId, out string reason)) throw new InvalidOperationException(reason);
            if (png == null || png.Length == 0) throw new InvalidOperationException("画像が空です。");
            string folder = SongFolderPath(songId);
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, "cover.png");
            if (File.Exists(destination))
            {
                string backupRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "3DSaberChartBackups"));
                Directory.CreateDirectory(backupRoot);
                File.Copy(destination, Path.Combine(backupRoot, $"{songId.Trim()}_cover_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png"), true);
            }
            string incoming = destination + ".incoming";
            File.WriteAllBytes(incoming, png);
            if (File.Exists(destination)) replaceFileOperation(incoming, destination, null);
            else moveFileOperation(incoming, destination);
            AssetDatabase.Refresh();
        }

        /// <summary>Normal(=chart.json の元)がまだ無い曲か。Easy / Hard だけ保存したときの注意に使う。</summary>
        public static bool MissingNormalChart(string songId)
        {
            string folder = SongFolderPath(songId);
            if (folder == null) return false;
            return !File.Exists(Path.Combine(folder, "chart_normal.json")) &&
                   !File.Exists(Path.Combine(folder, "chart.json"));
        }

        /// <summary>難易度のファイルがあるか(読み込みの予備 chart.json は数えない)。</summary>
        public static bool ChartExists(string songId, string difficulty)
        {
            string path = ChartPath(songId, difficulty);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        /// <summary>
        /// 指定の難易度ファイルだけを読む(chart.json への予備の読み込みはしない)。無ければ null。
        /// 他の難易度との設定の比較や、上の難易度を重ねて見るときに使う。
        /// </summary>
        public static SaberChartDocument TryLoadExact(string songId, string difficulty)
        {
            string path = ChartPath(songId, difficulty);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try { return SaberChartUtility.FromJson(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                                              exception is FormatException) { return null; }
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
                    if (file.existed) replaceFileOperation(file.incoming, file.destination, file.previous);
                    else moveFileOperation(file.incoming, file.destination);
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
                        if (file.existed) replaceFileOperation(file.previous, file.destination, null);
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
            public SongLoudness.Playback loudness;
        }

        private static readonly Dictionary<string, CachedAudio> audioCache =
            new Dictionary<string, CachedAudio>(StringComparer.OrdinalIgnoreCase);

        // StreamingAssets 内のファイルは Unity では「素通しコピー用」(DefaultAsset)扱いで、
        // AudioClip アセットとしては絶対にインポートされない。そのため AssetDatabase ではなく
        // 本編ランタイムと同じ file:// 経由のデコードで読み込む。
        // 毎リペイントの検証(ValidationWarnings)からも呼ばれるため、ファイル更新時刻+サイズで
        // キャッシュし、失敗もキャッシュして再デコードの連発(ビジーカーソル連打)を防ぐ。
        public static AudioClip LoadAudioClip(string songId) => LoadAudioClipFor(songId, null);

        /// <summary>その難易度で本編が鳴らす音源(audio_<難易度>.* があればそちら)。</summary>
        public static AudioClip LoadAudioClipFor(string songId, string difficulty)
        {
            string folder = SongFolderPath(songId);
            if (folder == null) return null;
            foreach (string audioName in AudioNamesFor(folder, difficulty))
            {
                string path = Path.Combine(folder, audioName);
                if (!File.Exists(path)) continue;

                var info = new FileInfo(path);
                var loudness = SongLoudness.Resolve(path);
                if (audioCache.TryGetValue(path, out CachedAudio cached) &&
                    cached.writeTimeUtc == info.LastWriteTimeUtc &&
                    cached.fileLength == info.Length && ReferenceEquals(cached.loudness, loudness))
                {
                    if (cached.failed) continue;         // 前回デコード失敗 → 次の候補へ
                    if (cached.clip) return cached.clip; // 生存していればそのまま使う
                    // クリップが破棄されていたら作り直しに落ちる
                }

                if (cached != null && cached.clip != null)
                {
                    SaberChartAudioPreview.Stop();
                    UnityEngine.Object.DestroyImmediate(cached.clip);
                }
                AudioClip clip = null;
                foreach (var playback in SongLoudness.Candidates(path))
                {
                    clip = DecodeAudioFile(playback.Path);
                    if (clip == null) continue;
                    SongLoudness.ApplyToDecodedClip(clip, playback.Gain);
                    break;
                }
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
                    loudness = loudness,
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

        public static bool IsAudioClipForSong(AudioClip clip, string songId) => IsAudioClipForChart(clip, songId, null);

        /// <summary>その難易度で本編が鳴らす音源のクリップか(audio_<難易度>.* があればそちら)。</summary>
        public static bool IsAudioClipForChart(AudioClip clip, string songId, string difficulty)
        {
            if (clip == null) return false;
            string folder = SongFolderPath(songId);
            if (folder == null) return false;

            // このストアがデコードして返したクリップはキャッシュ照合で判定する
            foreach (string audioName in AudioNamesFor(folder, difficulty))
            {
                string path = Path.Combine(folder, audioName);
                if (audioCache.TryGetValue(path, out CachedAudio cached) && cached.clip == clip) return true;
            }

            // プロジェクト内アセットとして選択されたクリップはパスで判定する
            string assetPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(assetPath)) return false;
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string absolute = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
            foreach (string audioName in AudioNamesFor(folder, difficulty))
            {
                if (PathsEqual(absolute, Path.Combine(folder, audioName))) return true;
            }
            return false;
        }

        // 直前の取り込みで置き換えた音源を残した場所(置き換えがなければ null)。
        public static string LastReplacedAudioBackup { get; private set; }

        private static string KeepReplacedAudio(string songId, List<(string original, string backup)> moved)
        {
            if (moved.Count == 0) return null;
            try
            {
                string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "3DSaberChartBackups",
                    songId.Trim(), "audio-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
                Directory.CreateDirectory(folder);
                foreach (var (original, backup) in moved)
                    if (File.Exists(backup)) File.Move(backup, Path.Combine(folder, Path.GetFileName(original)));
                return folder;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                Debug.LogWarning("置き換えた音源をバックアップへ移せませんでした: " + exception.Message);
                return null;
            }
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
                moveFileOperation(incoming, destination);
                committed = true;
            }
            catch (Exception importError)
            {
                var errors = new List<Exception> { importError };
                for (int index = movedFiles.Count - 1; index >= 0; index--)
                {
                    try { moveFileOperation(movedFiles[index].backup, movedFiles[index].original); }
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
                // 守12: 置き換えた音源は消さずに、譜面のバックアップと同じ場所へ残す(元に戻せるように)。
                LastReplacedAudioBackup = committed ? KeepReplacedAudio(songId, movedFiles) : null;
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
            // 同一サイズ・更新時刻の取り込みでも古い音量情報を再利用しない。
            SongLoudness.Invalidate(destination);
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
            moveFileOperation(path, backup);
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

        [Serializable]
        private sealed class Snapshot
        {
            public string current;
            public List<string> undo = new List<string>();
            public List<string> redo = new List<string>();
        }

        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;
        public int UndoCount => undo.Count;

        /// <summary>
        /// 再コンパイルや Play 開始の前に退避する文字列。current は退避したときの譜面で、
        /// 戻すときに同じ譜面であることを確かめる(別の譜面の履歴を混ぜない)。
        /// </summary>
        public string Export(string currentJson)
        {
            var snapshot = new Snapshot { current = currentJson };
            snapshot.undo.AddRange(undo);
            snapshot.redo.AddRange(redo);
            return JsonUtility.ToJson(snapshot);
        }

        public bool Import(string exported, string currentJson)
        {
            if (string.IsNullOrEmpty(exported)) return false;
            Snapshot snapshot;
            try { snapshot = JsonUtility.FromJson<Snapshot>(exported); }
            catch (ArgumentException) { return false; }
            if (snapshot == null || snapshot.current != currentJson) return false;
            undo.Clear();
            redo.Clear();
            if (snapshot.undo != null) undo.AddRange(snapshot.undo.FindAll(item => !string.IsNullOrEmpty(item)));
            if (snapshot.redo != null) redo.AddRange(snapshot.redo.FindAll(item => !string.IsNullOrEmpty(item)));
            while (undo.Count > Capacity) undo.RemoveAt(0);
            return true;
        }

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
