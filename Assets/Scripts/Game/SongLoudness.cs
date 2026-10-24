using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

// 曲全体の知覚音量を測定した結果を、本編・選曲・譜面エディタで共用する。
// 原音が差し替わった場合は古い補正を適用せず、通常の音源へ戻す。
public static class SongLoudness
{
    [Serializable]
    public sealed class Metadata
    {
        public int version;
        public string sourceFile, sourceSha256;
        public float gainDb;
        public string playbackFile, playbackSha256;
    }

    public sealed class Playback
    {
        public string Path { get; }
        public float Gain { get; }
        public Playback(string path, float gain) { Path = path; Gain = gain; }
    }

    sealed class Cached
    {
        public string stamp, derivedPath;
        public Playback playback;
    }

    static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
    static readonly string[] audioExtensions = { ".ogg", ".wav", ".mp3" };

    // 難易度ごとの音源(2026-10-24、ElDorado の隠し譜面 G だけが長い版 audio_g.mp3 を使う)。
    // audio_<難易度>.ogg・.wav・.mp3 のどれかがあればその難易度はそちらを鳴らし、無ければ今までどおり audio.*。
    // 音量そろえも音源ごと(audio_g.mp3 → loudness_g.json)。譜面(chart_<難易度>.json)・演出(stage_<難易度>.json)と同じ決まり。
    public static string VariantSuffix(string folder, string difficulty)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrWhiteSpace(difficulty) || !ChartLoader.ValidPathSegment(difficulty.Trim())) return "";
        string suffix = "_" + difficulty.Trim().ToLowerInvariant();
        foreach (string extension in audioExtensions)
            if (File.Exists(System.IO.Path.Combine(folder, "audio" + suffix + extension))) return suffix;
        return "";
    }

    public static IEnumerable<Playback> CandidatesInFolder(string folder, string difficulty = null)
    {
        string suffix = VariantSuffix(folder, difficulty);
        foreach (string extension in audioExtensions)
        {
            string path = System.IO.Path.Combine(folder, "audio" + suffix + extension);
            if (File.Exists(path))
                foreach (var playback in Candidates(path)) yield return playback;
        }
    }

    // 音源の名前から、その音量そろえの名前を作る(audio.mp3 → loudness.json、audio_g.mp3 → loudness_g.json)。
    public static string MetadataFileFor(string audioFileName)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(audioFileName ?? "");
        return name.StartsWith("audio_", StringComparison.OrdinalIgnoreCase) && name.Length > 6
            ? "loudness" + name.Substring(5).ToLowerInvariant() + ".json" : "loudness.json";
    }

    public static IEnumerable<Playback> Candidates(string sourcePath)
    {
        var resolved = Resolve(sourcePath);
        yield return resolved;
        // 加工済みファイルがデコードできなくても、原音で再生を続けられる。
        if (!string.Equals(resolved.Path, sourcePath, StringComparison.OrdinalIgnoreCase))
            yield return new Playback(sourcePath, 1);
    }

    public static void Invalidate(string sourcePath) { cache.Remove(sourcePath); }

    // 曲フォルダの元の音源(audio.ogg・audio.wav・audio.mp3 の順で最初に見つかったもの。難易度ごとの音源があればそちら)が、
    // 記録された SHA-256 と一致するか。
    // 曲ごとの鳴らす範囲(stage.json)を、音源を差し替えたあとに使わないための確認(仕様面の点検 7)。
    // 同じ大きさ・更新日時のあいだは結果を覚える(本編に入るたびに読み直さない)。
    static readonly Dictionary<string, (string stamp, string expected, bool matches)> sourceChecks =
        new Dictionary<string, (string, string, bool)>(StringComparer.OrdinalIgnoreCase);
    public static bool SourceMatches(string folder, string expectedSha256, string difficulty = null)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(expectedSha256)) return false;
        string source = null, suffix = VariantSuffix(folder, difficulty);
        foreach (string extension in audioExtensions)
        {
            string path = System.IO.Path.Combine(folder, "audio" + suffix + extension);
            if (File.Exists(path)) { source = path; break; }
        }
        if (source == null) return false;
        string stamp = Stamp(source);
        if (sourceChecks.TryGetValue(source, out var cached) && cached.stamp == stamp && cached.expected == expectedSha256) return cached.matches;
        bool matches;
        try { matches = MatchesHash(source, expectedSha256); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { matches = false; }
        sourceChecks[source] = (stamp, expectedSha256, matches);
        return matches;
    }

    public static Playback Resolve(string sourcePath)
    {
        string metadataPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourcePath), MetadataFileFor(System.IO.Path.GetFileName(sourcePath)));
        cache.TryGetValue(sourcePath, out var cached);
        string stamp = Stamp(sourcePath) + "/" + Stamp(metadataPath) + "/" + Stamp(cached?.derivedPath);
        if (cached != null && stamp == cached.stamp) return cached.playback;

        var result = new Playback(sourcePath, 1);
        string derivedPath = null;
        try
        {
            if (File.Exists(metadataPath))
            {
                var data = JsonUtility.FromJson<Metadata>(File.ReadAllText(metadataPath));
                if (data != null && data.version == 1 && data.sourceFile == System.IO.Path.GetFileName(sourcePath)
                    && !float.IsNaN(data.gainDb) && !float.IsInfinity(data.gainDb)
                    && data.gainDb >= -60 && data.gainDb <= 0 && MatchesHash(sourcePath, data.sourceSha256))
                {
                    if (string.IsNullOrEmpty(data.playbackFile))
                        result = new Playback(sourcePath, Mathf.Pow(10, data.gainDb / 20));
                    else if (IsAudioFilename(data.playbackFile))
                    {
                        derivedPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourcePath), data.playbackFile);
                        if (MatchesHash(derivedPath, data.playbackSha256))
                            result = new Playback(derivedPath, Mathf.Pow(10, data.gainDb / 20));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
        {
            // 読み込み途中・未対応のメタデータは原音へフォールバックする。
        }
        cache[sourcePath] = new Cached
        {
            stamp = Stamp(sourcePath) + "/" + Stamp(metadataPath) + "/" + Stamp(derivedPath),
            derivedPath = derivedPath, playback = result
        };
        return result;
    }

    static bool IsAudioFilename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return false;
        string extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return extension == ".wav" || extension == ".mp3" || extension == ".ogg";
    }

    static string Stamp(string path)
    {
        if (string.IsNullOrEmpty(path)) return "-";
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "-";
        }
        catch (IOException) { return "-"; }
        catch (UnauthorizedAccessException) { return "-"; }
    }

    static bool MatchesHash(string path, string expected)
    {
        if (expected == null || expected.Length != 64 || !File.Exists(path)) return false;
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
        {
            string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    // AudioUtilには音量指定がないため、エディタ所有のデコード済みPCMだけを減衰する。
    // サンプル数・周波数・原音ファイルは変えず、同じクリップへ二重適用しないこと。
    public static void ApplyToDecodedClip(AudioClip clip, float gain)
    {
        if (clip == null || gain == 1) return;
        gain = Mathf.Clamp01(gain);
        const int blockFrames = 32768;
        float[] data = new float[Math.Min(blockFrames, clip.samples) * clip.channels];
        for (int offset = 0; offset < clip.samples; offset += blockFrames)
        {
            int length = Math.Min(blockFrames, clip.samples - offset) * clip.channels;
            if (data.Length != length) data = new float[length];
            if (!clip.GetData(data, offset)) throw new InvalidOperationException("音量補正用のPCMを読み込めません。");
            for (int i = 0; i < data.Length; i++) data[i] *= gain;
            if (!clip.SetData(data, offset)) throw new InvalidOperationException("音量補正用のPCMを更新できません。");
        }
    }
}
