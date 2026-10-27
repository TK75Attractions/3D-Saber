using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

// 譜面の「ここを直したい」印(2026-10-27)。ユーザー「AI指摘システムで裏譜面の直したいところをすぐに簡単に直せるようにする」に、
// 形は「自分で印 → AI が直す」、一言は「曲のあとに打つ」と答えてもらった。
// プレイ中に F5(ChartMarkRecorder)で印を付けると、曲・難易度・時刻・拍・小節・前後のノーツ・直前の判定・画面を
// persistentDataPath/ChartMarks に1プレイ1ファイルで書く。結果画面(ChartMarkCommentPanel)で印ごとに一言を足す。
// AI は dev\tools\ChartMarks\marks.py で読み、その場所を探さずに直す。判定・採点・譜面には触れない。
[Serializable]
public sealed class ChartMarkNote
{
    public int index;           // chart.notes の並び(譜面ファイルの何番目か)
    public float time;          // 譜面の時刻(ms)
    public float beat;
    public float x, y;
    public string color, type, direction;
    public int count;
    public float lengthMs;
}

[Serializable]
public sealed class ChartMarkJudgment
{
    public double chartMs;      // そのノーツの譜面の時刻(ms)
    public string hand;
    public float x, y;
    public string result;       // Perfect / Great / Good / Bad / Miss
}

[Serializable]
public sealed class ChartMarkEntry
{
    public int number;          // このプレイの中の番号(結果画面の「印1」)
    public string id;           // ファイル名 + 番号(marks.py が使う)
    public string status = "open";
    public string markedAt;
    public double songTime;     // 曲の時計(秒)
    public double chartMs;      // 譜面の時刻(ms)。ノーツの time と同じ物差し
    public double beat;
    public int bar;             // 4拍を1小節として数えた小節(1から)
    public string screenshot;
    public string comment = "";
    public List<ChartMarkNote> notes = new List<ChartMarkNote>();
    public List<ChartMarkJudgment> recent = new List<ChartMarkJudgment>();
}

[Serializable]
public sealed class ChartMarkFile
{
    public int format = 1;
    public string createdAt;
    public string gameVersion;
    public string songId;
    public string songTitle;
    public string difficulty;
    public string chartFile;    // StreamingAssets から見た譜面(例 Songs/Morning/chart_m.json)
    public string chartSha256;  // 印を付けたときの譜面。あとで譜面が変わったかを marks.py が見る
    public float bpm;
    public float beatZeroMs;
    public float offsetMs;
    public double totalOffsetSeconds;
    public string settings;
    public List<ChartMarkEntry> marks = new List<ChartMarkEntry>();
}

public static class ChartMarks
{
    public const string FolderName = "ChartMarks";
    // 印の前後に書き出すノーツ(押すのは気づいたあとなので、前を長く)。
    public const double NotesBeforeMs = 4000, NotesAfterMs = 2000;
    public const int RecentJudgments = 16;
    public const int BeatsPerBar = 4;
    public const int CommentLimit = 120;
    // 上限: 1プレイの印の数、画面の幅(JPEG)、置き場所の合計(超えたら画面だけ省く)。
    public const int MaxMarksPerPlay = 30;
    public const int ScreenshotMaxWidth = 1280;
    public const long MaxFolderBytes = 300L * 1024 * 1024;

    public static ChartMarkFile Current { get; private set; }
    public static string CurrentPath { get; private set; }
    public static bool Full => Count >= MaxMarksPerPlay;
    // 直近の保存の失敗理由(成功なら空)。結果画面・札で操作する人に見せる。
    public static string LastSaveError { get; private set; } = "";
    // テストでは書き出し先を差し替える。
    public static string FolderOverride { get; set; }
    public static string Folder => string.IsNullOrEmpty(FolderOverride)
        ? Path.Combine(Application.persistentDataPath, FolderName) : FolderOverride;
    public static int Count => Current != null ? Current.marks.Count : 0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Current = null; CurrentPath = null; FolderOverride = null; LastSaveError = ""; }

    // 1プレイ(1曲)ごとに1ファイル。曲が始まるたびに区切る。
    public static void BeginPlay() { Current = null; CurrentPath = null; }

    public static ChartMarkEntry Add(ChartData chart, double songTime, double totalOffsetSeconds,
        IReadOnlyList<ChartMarkJudgment> recent, string songId, string songTitle, string difficulty, string settings, DateTime now)
    {
        if (chart == null || !Finite(songTime) || Full) return null;
        if (Current == null)
        {
            string chartFile = ChartFileFor(songId, difficulty);
            Current = new ChartMarkFile
            {
                createdAt = Stamp(now),
                gameVersion = GameVersion.Number,
                songId = songId ?? "",
                songTitle = songTitle ?? "",
                difficulty = difficulty ?? "",
                chartFile = chartFile,
                chartSha256 = Sha256(Path.Combine(Application.streamingAssetsPath, chartFile)),
                bpm = chart.bpm,
                beatZeroMs = chart.beatZeroMs,
                offsetMs = chart.offsetMs,
                totalOffsetSeconds = totalOffsetSeconds,
                settings = settings ?? ""
            };
            CurrentPath = Path.Combine(Folder, FileStem(now, songId, difficulty) + ".json");
        }
        double chartMs = (songTime - totalOffsetSeconds) * 1000.0;
        double beat = BeatAt(chart, chartMs);
        int number = Current.marks.Count + 1;
        string stem = Path.GetFileNameWithoutExtension(CurrentPath);
        var entry = new ChartMarkEntry
        {
            number = number,
            id = stem + "-" + number,
            markedAt = Stamp(now),
            songTime = Math.Round(songTime, 4),
            chartMs = Math.Round(chartMs, 1),
            beat = Math.Round(beat, 3),
            bar = BarOf(beat),
            screenshot = stem + "_" + number + ".jpg",
            notes = NotesAround(chart, chartMs)
        };
        if (recent != null) entry.recent.AddRange(recent);
        Current.marks.Add(entry);
        Save();
        return entry;
    }

    public static string ScreenshotPath(ChartMarkEntry entry)
        => entry != null && CurrentPath != null ? Path.Combine(Path.GetDirectoryName(CurrentPath), entry.screenshot) : null;

    public static void SetComment(int number, string comment)
    {
        if (Current == null) return;
        foreach (var mark in Current.marks)
            if (mark.number == number) mark.comment = Clean(comment);
    }

    public static string Clean(string comment)
    {
        if (string.IsNullOrEmpty(comment)) return "";
        var text = comment.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > CommentLimit ? text.Substring(0, CommentLimit) : text;
    }

    // 一時ファイルに書き終えてから置き換える(途中で失敗しても、それまでの印を壊さない)。
    // 書けなくても遊びは止めない。失敗は LastSaveError に残し、呼んだ側が操作する人に見せる。
    public static bool Save()
    {
        if (Current == null || CurrentPath == null) return false;
        string temp = CurrentPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CurrentPath));
            File.WriteAllText(temp, JsonUtility.ToJson(Current, true), new UTF8Encoding(false));
            if (File.Exists(CurrentPath)) File.Replace(temp, CurrentPath, null);
            else File.Move(temp, CurrentPath);
            LastSaveError = "";
            return true;
        }
        catch (Exception e)
        {
            LastSaveError = e.Message;
            Debug.LogWarning("[ChartMarks] 印を書けませんでした: " + e.Message);
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            return false;
        }
    }

    // 置き場所の合計(画面を省くかの判断。印のフォルダの直下だけ数える)。
    public static long FolderBytes()
    {
        try
        {
            if (!Directory.Exists(Folder)) return 0;
            long total = 0;
            foreach (var file in new DirectoryInfo(Folder).GetFiles()) total += file.Length;
            return total;
        }
        catch (Exception) { return 0; }
    }

    public static double BeatAt(ChartData chart, double chartMs)
        => chart == null ? 0 : new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap).BeatAt(chartMs);

    public static int BarOf(double beat) => Finite(beat) ? (int)Math.Floor(beat / BeatsPerBar) + 1 : 0;

    public static List<ChartMarkNote> NotesAround(ChartData chart, double chartMs)
    {
        var result = new List<ChartMarkNote>();
        if (chart?.notes == null) return result;
        for (int i = 0; i < chart.notes.Count; i++)
        {
            var n = chart.notes[i];
            if (n == null || n.time < chartMs - NotesBeforeMs || n.time > chartMs + NotesAfterMs) continue;
            result.Add(new ChartMarkNote
            {
                index = i, time = n.time, beat = n.beat, x = n.x, y = n.y,
                color = n.color, type = n.type, direction = n.direction, count = n.count, lengthMs = n.lengthMs
            });
        }
        return result;
    }

    // ChartLoader.LoadFromStreamingAssets と同じ探し方(chart_<難易度>.json → chart.json)。
    public static string ChartFileFor(string songId, string difficulty)
    {
        string song = songId ?? "";
        if (!string.IsNullOrWhiteSpace(difficulty))
        {
            string specific = "Songs/" + song + "/chart_" + difficulty.Trim().ToLowerInvariant() + ".json";
            if (File.Exists(Path.Combine(Application.streamingAssetsPath, specific))) return specific;
        }
        return "Songs/" + song + "/chart.json";
    }

    public static string Sha256(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(File.ReadAllBytes(path));
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }
        catch (Exception) { return ""; }
    }

    public static string FileStem(DateTime now, string songId, string difficulty)
    {
        string song = Safe(songId), diff = Safe(difficulty);
        return now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + song + "_" + diff;
    }

    static string Safe(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var text = new StringBuilder(value.Length);
        foreach (char c in value.Trim())
            text.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == ' ' ? '_' : c);
        return text.ToString();
    }

    static string Stamp(DateTime now) => now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    // 表示用「0:04.6」。
    public static string Clock(double seconds)
    {
        if (!Finite(seconds)) return "-";
        bool negative = seconds < 0;
        seconds = Math.Abs(seconds);
        int minutes = (int)(seconds / 60);
        return (negative ? "-" : "") + minutes + ":" + (seconds - minutes * 60).ToString("00.0", CultureInfo.InvariantCulture);
    }
}
