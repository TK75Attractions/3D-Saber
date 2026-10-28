using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

// 譜面の「ここを直したい」印(2026-10-27)。ユーザー「AI指摘システムで裏譜面の直したいところをすぐに簡単に直せるようにする」に、
// 形は「自分で印 → AI が直す」、一言は「曲のあとに打つ」と答えてもらった。
// プレイ中に F5(ChartMarkRecorder)かスマホで印を付けると、曲・難易度・時刻・拍・小節・前後のノーツ・直前の判定・画面を
// persistentDataPath/ChartMarks に1プレイ1ファイルで書く。結果画面(ChartMarkCommentPanel)かスマホで印ごとに一言を足す。
// AI は dev\tools\ChartMarks\marks.py で読み、その場所を探さずに直す。判定・採点・譜面には触れない。
// format 2(2026-10-28): ファイル名と中身に台名と1プレイの ID、中身に保存先と OS、印に source(key / phone)と取り消し(canceled)、
// 前後のノーツに NoteData の全項目(data)と残る長さ(lingerMs)。小節は拍子の表(ChartMeterMap)で数える。
// 直したかどうかは marks.py が別のファイル <名前>.status.json に書く(ゲームは読むだけ。印のファイルとしては読まない)。
[Serializable]
public sealed class ChartMarkNote
{
    // ゲームが読み込んだあとの並び(時刻順の並べ替え・不正なノーツの除去・音源の長さでの切り詰めのあと)。
    // 元の chart_*.json の何番目かとは限らない(marks.py が data の時刻・色・位置で元の番号を照合する)。
    public int index;
    public float time;          // 譜面の時刻(ms)
    public float beat;
    public float x, y;
    public string color, type, direction;
    public int count;
    public float lengthMs;
    // ロングが残る長さ(ms。lengthMs の指定、無ければ (count-1) × 1回の長さ)。ロングでなければ 0。
    public float lingerMs;
    // NoteData の全項目(mirror・wind・vanish・veil・slide・fall・hidden・hitScale など。JsonUtility がそのまま書く)。
    public NoteData data;
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
    public string id;           // ファイル名 + "-" + 番号(marks.py・スマホが使う)
    // "open" / "canceled"(結果画面の Delete・スマホで取り消した)。直したかは marks.py が <名前>.status.json に書く
    // (ゲームは "fixed" を書かない。format 1 のファイルには marks.py が "fixed" を書いたものがある)。
    public string status = ChartMarks.StatusOpen;
    public string source = ChartMarks.SourceKey;   // "key"(F5) / "phone"(スマホ)
    public string markedAt;
    public double songTime;     // 曲の時計(秒)
    public double chartMs;      // 譜面の時刻(ms)。ノーツの time と同じ物差し
    public double beat;
    public int bar;             // 拍子の表(ChartMeterMap)で数えた小節(1から)。カウントイン中(負の拍)は 0
    public string screenshot = "";   // 画面の JPEG のファイル名。撮らなかった・撮れなかったときは ""
    public string comment = "";      // 打つたびに下書きを保存する
    public List<ChartMarkNote> notes = new List<ChartMarkNote>();
    public List<ChartMarkJudgment> recent = new List<ChartMarkJudgment>();
}

[Serializable]
public sealed class ChartMarkFile
{
    public int format = ChartMarks.Format;
    public string createdAt;
    public string station;      // 台名(PhoneSaberStation。空なら "PC")
    public string runId;        // 1プレイの ID(GameSession.AchievementRunId、無ければ GUID)。ファイル名には先頭8文字
    public string folder;       // 書いたフォルダー(persistentDataPath/ChartMarks の絶対パス)
    public string os;
    public string gameVersion;
    public string songId;
    public string songTitle;
    public string difficulty;
    public string chartFile;    // StreamingAssets から見た譜面(例 Songs/Morning/chart_m.json)
    public string chartSha256;  // 印を付けたときの譜面(生のバイト)。あとで譜面が変わったかを marks.py が見る
    public float bpm;
    public float beatZeroMs;
    public float offsetMs;
    public double totalOffsetSeconds;
    public string settings;
    public List<ChartMarkEntry> marks = new List<ChartMarkEntry>();
}

// 印の一覧の1行(ChartMarks.Recent。スマホの画面などが使う)。
[Serializable]
public sealed class ChartMarkSummary
{
    public string id; public string song; public string title; public string difficulty; public string station;
    public int number; public double songSeconds; public int bar; public string comment; public string status;
    public string source; public bool current; public string savedAt;   // savedAt は印を付けた時刻(markedAt)
}

// AI の状態 <名前>.status.json(marks.py が書く。ゲームは "fixed" の id を数えるためにだけ読む)。
[Serializable]
public sealed class ChartMarkStatusFile
{
    public int format;
    public List<ChartMarkStatusEntry> marks = new List<ChartMarkStatusEntry>();
}

[Serializable]
public sealed class ChartMarkStatusEntry
{
    public string id;
    public string status;
    public string fixedAt, fixedCommit, fixedNote;
}

// スマホの操作盤のアドレス(スマホの部品が起動時に入れる)。null・空なら結果画面の一言の欄に出さない。
public static class ChartMarkPhoneLink
{
    public static string Url;
}

public static class ChartMarks
{
    public const string FolderName = "ChartMarks";
    public const int Format = 2;
    public const string StatusOpen = "open", StatusCanceled = "canceled", StatusFixed = "fixed";
    public const string SourceKey = "key", SourcePhone = "phone";
    // 台名が無い PC(PhoneSaberStation が空)。
    public const string StationFallback = "PC";
    // marks.py が書く AI の状態。印のファイルとしては読まない。
    public const string StatusFileSuffix = ".status.json";
    // 書き込みの一時ファイル(<名前>.json.tmp。marks.py は別の名前を使う)。
    public const string TempSuffix = ".tmp";
    // 印の前後に書き出すノーツ(押すのは気づいたあとなので、前を長く)。窓より前に始まって窓にかかるロングも入れる。
    public const double NotesBeforeMs = 4000, NotesAfterMs = 2000;
    public const int RecentJudgments = 16;
    public const int BeatsPerBar = 4;
    public const int CommentLimit = 120;
    // 上限: 1プレイの印の数、画面の幅(JPEG)、置き場所の合計(超えたら画面だけ省く)。
    public const int MaxMarksPerPlay = 30;
    public const int ScreenshotMaxWidth = 1280;
    public const long MaxFolderBytes = 300L * 1024 * 1024;

    // 書けなかった回は次の曲でも捨てずに持ち、RetryUnsaved で書き直す(古いものから、この数まで)。
    public const int MaxUnsaved = 10;

    public static ChartMarkFile Current { get; private set; }
    public static string CurrentPath { get; private set; }
    public static bool Full => Count >= MaxMarksPerPlay;
    // 直近の保存の失敗理由(成功なら空)。結果画面・札で操作する人に見せる。
    public static string LastSaveError { get; private set; } = "";
    // この回の最後の保存が失敗したか。
    public static bool CurrentUnsaved { get; private set; }
    public static int UnsavedCount => Unsaved.Count + (CurrentUnsaved ? 1 : 0);
    static readonly List<KeyValuePair<ChartMarkFile, string>> Unsaved = new List<KeyValuePair<ChartMarkFile, string>>();
    // テストでは書き出し先を差し替える。
    public static string FolderOverride { get; set; }
    public static string Folder => string.IsNullOrEmpty(FolderOverride)
        ? Path.Combine(Application.persistentDataPath, FolderName) : FolderOverride;
    public static int Count => Current != null ? Current.marks.Count : 0;

    // この回に PC のキー(F5)で付けた印があるか。スマホだけの回は、結果画面で欄を自動では開かない(来場者の画面を覆わない)。
    public static bool HasKeyMarks
    {
        get
        {
            if (Current == null) return false;
            foreach (var mark in Current.marks)
                if (mark != null && mark.source != SourcePhone) return true;
            return false;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Current = null; CurrentPath = null; FolderOverride = null; LastSaveError = "";
        CurrentUnsaved = false; Unsaved.Clear();
        Files.Clear(); Statuses.Clear();
    }

    // 1プレイ(1曲)ごとに1ファイル。曲が始まるたびに区切る。書けなかった回は捨てずに Unsaved へ移す。
    public static void BeginPlay()
    {
        if (Current != null && CurrentPath != null && CurrentUnsaved)
        {
            Unsaved.Add(new KeyValuePair<ChartMarkFile, string>(Current, CurrentPath));
            if (Unsaved.Count > MaxUnsaved)
            {
                LogUnsaved(Unsaved[0].Key);
                Unsaved.RemoveAt(0);
            }
        }
        Current = null; CurrentPath = null; CurrentUnsaved = false;
    }

    // 書けなかった回を書き直す(結果画面・選曲などで数秒ごとと、終わるとき)。まだ書けていない回の数を返す。
    public static int RetryUnsaved()
    {
        if (CurrentUnsaved) Save();
        for (int i = Unsaved.Count - 1; i >= 0; i--)
        {
            try
            {
                Write(Unsaved[i].Key, Unsaved[i].Value);
                Unsaved.RemoveAt(i);
            }
            catch (Exception e) { LastSaveError = e.Message; }
        }
        return UnsavedCount;
    }

    // 終わるときに書けなかった回は、Player.log に中身を残す(一言を手で拾えるように)。
    public static void LogAllUnsaved()
    {
        if (CurrentUnsaved && Current != null) LogUnsaved(Current);
        foreach (var pair in Unsaved) LogUnsaved(pair.Key);
    }

    static void LogUnsaved(ChartMarkFile file)
        => Debug.LogWarning("[ChartMarks] 書けなかった印(" + LastSaveError + "): " + JsonUtility.ToJson(file));

    // テスト用: 書けなかった回を忘れる。
    public static void ForgetUnsaved() { Unsaved.Clear(); CurrentUnsaved = false; }

    // source は "key"(F5) / "phone"(スマホ)。withScreenshot が false なら画面の名前を入れない("")。
    // station・runId はテスト用(null なら PhoneSaberStation・GameSession.AchievementRunId)。
    public static ChartMarkEntry Add(ChartData chart, double songTime, double totalOffsetSeconds,
        IReadOnlyList<ChartMarkJudgment> recent, string songId, string songTitle, string difficulty, string settings, DateTime now,
        string source = SourceKey, bool withScreenshot = true, float secondsPerLongCut = NoteSpawner.DefaultSecondsPerLongCut,
        string station = null, string runId = null)
    {
        if (chart == null || !Finite(songTime) || Full) return null;
        if (Current == null)
        {
            string chartFile = ChartFileFor(songId, difficulty);
            string stationName = StationName(station ?? PhoneSaberStation.Read());
            string run = (runId ?? GameSession.AchievementRunId ?? "").Trim();
            if (run.Length == 0) run = Guid.NewGuid().ToString("N");
            CurrentPath = Path.Combine(Folder, FileStem(now, stationName, songId, difficulty, run) + ".json");
            Current = new ChartMarkFile
            {
                format = Format,
                createdAt = Stamp(now),
                station = stationName,
                runId = run,
                folder = FullPath(Path.GetDirectoryName(CurrentPath)),
                os = OsName(),
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
        }
        double chartMs = (songTime - totalOffsetSeconds) * 1000.0;
        double beat = Math.Round(BeatAt(chart, chartMs), 3);
        int number = Current.marks.Count + 1;
        string stem = Path.GetFileNameWithoutExtension(CurrentPath);
        var entry = new ChartMarkEntry
        {
            number = number,
            id = stem + "-" + number,
            status = StatusOpen,
            source = source == SourcePhone ? SourcePhone : SourceKey,
            markedAt = Stamp(now),
            songTime = Math.Round(songTime, 4),
            chartMs = Math.Round(chartMs, 1),
            beat = beat,
            bar = BarOf(chart, beat),
            screenshot = withScreenshot ? stem + "_" + number + ".jpg" : "",
            notes = NotesAround(chart, chartMs, secondsPerLongCut)
        };
        if (recent != null) entry.recent.AddRange(recent);
        Current.marks.Add(entry);
        Save();
        return entry;
    }

    // 画面の JPEG の置き場所。撮らない印(screenshot が "")は null。
    public static string ScreenshotPath(ChartMarkEntry entry)
        => entry != null && CurrentPath != null && !string.IsNullOrEmpty(entry.screenshot)
            ? Path.Combine(Path.GetDirectoryName(CurrentPath), entry.screenshot) : null;

    // この回の印の一言(メモリだけ。保存は呼んだ側が Save で)。結果画面の欄が使う。
    public static void SetComment(int number, string comment)
    {
        if (Current == null) return;
        foreach (var mark in Current.marks)
            if (mark.number == number) mark.comment = Clean(comment);
    }

    // 印の一言(この回はメモリ、前の回はファイル)。保存まで行う。メインスレッドで。
    public static bool SetComment(string markId, string text, out string error)
        => Change(markId, mark => { mark.comment = Clean(text); return null; }, out error);

    // 取り消し / 戻す。保存まで行う。メインスレッドで。
    public static bool SetCanceled(string markId, bool canceled, out string error)
        => Change(markId, mark =>
        {
            if (mark.status == StatusFixed) return "直した印です";
            mark.status = canceled ? StatusCanceled : StatusOpen;
            return null;
        }, out error);

    // 画面を書けなかった印は screenshot を "" にする(marks.py が「画像なし」と出す)。
    public static bool ForgetScreenshot(string markId)
        => Change(markId, mark => { mark.screenshot = ""; return null; }, out _);

    // change は直せないときに理由を返す(直したら null)。
    static bool Change(string markId, Func<ChartMarkEntry, string> change, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(markId)) { error = "印の ID がありません"; return false; }
        var mark = Current != null ? Find(Current, markId) : null;
        if (mark != null)
        {
            error = change(mark);
            if (error != null) return false;
            error = "";
            if (Save()) return true;
            error = LastSaveError;
            return false;
        }
        // 書けなかった前の回(メモリにある方が新しい)。書けなくても変えた中身はメモリに残り、あとで書き直す。
        for (int i = 0; i < Unsaved.Count; i++)
        {
            mark = Find(Unsaved[i].Key, markId);
            if (mark == null) continue;
            error = change(mark);
            if (error != null) return false;
            error = "";
            try
            {
                Write(Unsaved[i].Key, Unsaved[i].Value);
                Unsaved.RemoveAt(i);
                return true;
            }
            catch (Exception e)
            {
                LastSaveError = error = e.Message;
                return false;
            }
        }
        string path = PathForId(markId);
        if (path == null || !File.Exists(path)) { error = "印が見つかりません"; return false; }
        try
        {
            var file = JsonUtility.FromJson<ChartMarkFile>(File.ReadAllText(path, Encoding.UTF8));
            mark = file != null ? Find(file, markId) : null;
            if (mark == null) { error = "印が見つかりません"; return false; }
            error = change(mark);
            if (error != null) return false;
            error = "";
            Write(file, path);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Debug.LogWarning("[ChartMarks] 印を書き換えられませんでした: " + e.Message);
            return false;
        }
    }

    static ChartMarkEntry Find(ChartMarkFile file, string markId)
    {
        if (file?.marks == null) return null;
        foreach (var mark in file.marks)
            if (mark != null && mark.id == markId) return mark;
        return null;
    }

    // 印の ID(<名前>-<番号>)から印のファイル。スマホから来た ID でフォルダーの外や状態のファイルを指さない。
    static string PathForId(string markId)
    {
        int dash = markId.LastIndexOf('-');
        if (dash <= 0 || dash == markId.Length - 1) return null;
        if (!int.TryParse(markId.Substring(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _)) return null;
        string stem = markId.Substring(0, dash);
        if (stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || stem.IndexOf('/') >= 0 || stem.IndexOf('\\') >= 0
            || stem.StartsWith(".", StringComparison.Ordinal)
            || stem.EndsWith(".status", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(Folder, stem + ".json");
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
        try
        {
            Write(Current, CurrentPath);
            LastSaveError = "";
            CurrentUnsaved = false;
            return true;
        }
        catch (Exception e)
        {
            LastSaveError = e.Message;
            CurrentUnsaved = true;
            Debug.LogWarning("[ChartMarks] 印を書けませんでした: " + e.Message);
            return false;
        }
    }

    static void Write(ChartMarkFile file, string path)
    {
        string temp = path + TempSuffix;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(temp, JsonUtility.ToJson(file, true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        catch (Exception)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            throw;
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

    // 結果画面の欄に出す保存先(フォルダー名の末尾)。
    public static string FolderTail(int parts = 3)
    {
        string full = FullPath(Folder).TrimEnd('\\', '/');
        var pieces = full.Split('\\', '/');
        if (pieces.Length <= parts) return full;
        return "…" + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar.ToString(), pieces, pieces.Length - parts, parts);
    }

    public static double BeatAt(ChartData chart, double chartMs)
        => chart == null ? 0 : new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap).BeatAt(chartMs);

    // 拍子の表(ChartMeterMap、小節線と同じ)で数えた小節(1から)。表が無い譜面は4拍で1小節。負の拍(カウントイン)は 0。
    public static int BarOf(ChartData chart, double beat)
    {
        if (!Finite(beat) || beat < 0) return 0;
        return new ChartMeterMap(chart?.timeSignatures, BeatsPerBar).At(beat).Measure;
    }

    public static int BarOf(double beat) => BarOf(null, beat);

    public static List<ChartMarkNote> NotesAround(ChartData chart, double chartMs,
        float secondsPerLongCut = NoteSpawner.DefaultSecondsPerLongCut)
    {
        var result = new List<ChartMarkNote>();
        if (chart?.notes == null) return result;
        double from = chartMs - NotesBeforeMs, to = chartMs + NotesAfterMs;
        for (int i = 0; i < chart.notes.Count; i++)
        {
            var n = chart.notes[i];
            if (n == null || !Finite(n.time) || n.time > to) continue;
            double linger = n.LingerSeconds(secondsPerLongCut) * 1000.0;
            if (!Finite(linger) || linger < 0) linger = 0;
            // 窓より前に始まっても、残っている間に窓にかかるロングは入れる。
            if (n.time + linger < from) continue;
            result.Add(new ChartMarkNote
            {
                index = i, time = n.time, beat = n.beat, x = n.x, y = n.y,
                color = n.color, type = n.type, direction = n.direction, count = n.count, lengthMs = n.lengthMs,
                lingerMs = (float)Math.Round(linger, 1), data = CopyOf(n)
            });
        }
        return result;
    }

    // 印を付けたときのノーツ(あとでゲームがノーツを書き換えても、印の中身は変えない)。
    static NoteData CopyOf(NoteData note)
    {
        try { return JsonUtility.FromJson<NoteData>(JsonUtility.ToJson(note)); }
        catch (Exception) { return note; }
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

    // <日時>_<台>_<曲>_<難易度>_<ID の先頭8文字>。同じ秒の2台・同じ台の2プレイでも重ならない。
    public static string FileStem(DateTime now, string station, string songId, string difficulty, string runId)
    {
        string run = Safe(runId);
        if (run.Length > 8) run = run.Substring(0, 8);
        return now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + Safe(StationName(station)) + "_"
            + Safe(songId) + "_" + Safe(difficulty) + "_" + run;
    }

    public static string StationName(string station)
    {
        string name = PhoneSaberStation.Normalize(station);
        return name.Length == 0 ? StationFallback : name;
    }

    static string OsName()
    {
        try { return Application.platform + " / " + SystemInfo.operatingSystem; }
        catch (Exception) { return Application.platform.ToString(); }
    }

    static string FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) { return path ?? ""; }
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

    // ---- 一覧と数(スマホ・結果画面の欄)。どれもメインスレッドで ----

    // 新しい順。この回の印(メモリ)+書けなかった前の回(メモリ)+フォルダーの最近のファイルの印。取り消しは status で返す。
    // 直した印(fixed)は返さない。書けなかった回は、フォルダーに残っている古い中身ではなくメモリの方を出す。
    public static List<ChartMarkSummary> Recent(int max)
    {
        var result = new List<ChartMarkSummary>();
        if (max <= 0) return result;
        var scanned = Scan(out var currentFixed, out var unsavedFixed);
        if (Current != null)
            for (int i = Current.marks.Count - 1; i >= 0 && result.Count < max; i--)
            {
                var mark = Current.marks[i];
                if (mark == null || IsFixed(mark.status, mark.id, currentFixed)) continue;
                result.Add(Summary(Current, mark, true));
            }
        for (int u = Unsaved.Count - 1; u >= 0 && result.Count < max; u--)
        {
            var file = Unsaved[u].Key;
            for (int i = file.marks.Count - 1; i >= 0 && result.Count < max; i--)
            {
                var mark = file.marks[i];
                if (mark == null || IsFixed(mark.status, mark.id, unsavedFixed[u])) continue;
                result.Add(Summary(file, mark, false));
            }
        }
        foreach (var file in scanned)
        {
            if (result.Count >= max) break;
            for (int i = file.marks.Count - 1; i >= 0 && result.Count < max; i--)
            {
                var row = file.marks[i];
                if (IsFixed(row.status, row.id, file.fixedIds)) continue;
                result.Add(Copy(row));
            }
        }
        return result;
    }

    // この PC(このフォルダー)の直していない印の数。取り消しと、status.json で fixed のものを除く。
    public static int OpenCountOnThisPc()
    {
        var scanned = Scan(out var currentFixed, out var unsavedFixed);
        int count = 0;
        if (Current != null)
            foreach (var mark in Current.marks)
                if (mark != null && IsOpen(mark.status) && !currentFixed.Contains(mark.id ?? "")) count++;
        for (int u = 0; u < Unsaved.Count; u++)
            foreach (var mark in Unsaved[u].Key.marks)
                if (mark != null && IsOpen(mark.status) && !unsavedFixed[u].Contains(mark.id ?? "")) count++;
        foreach (var file in scanned)
            foreach (var row in file.marks)
                if (IsOpen(row.status) && !file.fixedIds.Contains(row.id ?? "")) count++;
        return count;
    }

    // この回の直していない印(取り消していないもの)の数。
    public static int OpenCountThisPlay()
    {
        int count = 0;
        if (Current != null)
            foreach (var mark in Current.marks)
                if (mark != null && IsOpen(mark.status)) count++;
        return count;
    }

    static bool IsOpen(string status) => string.IsNullOrEmpty(status) || status == StatusOpen;

    static bool IsFixed(string status, string id, HashSet<string> fixedIds)
        => status == StatusFixed || (fixedIds != null && id != null && fixedIds.Contains(id));

    static ChartMarkSummary Summary(ChartMarkFile file, ChartMarkEntry mark, bool current) => new ChartMarkSummary
    {
        id = mark.id ?? "", song = file.songId ?? "", title = file.songTitle ?? "", difficulty = file.difficulty ?? "",
        station = string.IsNullOrEmpty(file.station) ? StationFallback : file.station,
        number = mark.number, songSeconds = mark.songTime, bar = mark.bar, comment = mark.comment ?? "",
        status = string.IsNullOrEmpty(mark.status) ? StatusOpen : mark.status,
        source = string.IsNullOrEmpty(mark.source) ? SourceKey : mark.source,
        current = current, savedAt = mark.markedAt ?? ""
    };

    static ChartMarkSummary Copy(ChartMarkSummary row) => new ChartMarkSummary
    {
        id = row.id, song = row.song, title = row.title, difficulty = row.difficulty, station = row.station,
        number = row.number, songSeconds = row.songSeconds, bar = row.bar, comment = row.comment, status = row.status,
        source = row.source, current = row.current, savedAt = row.savedAt
    };

    // フォルダーの印のファイル(読んだ中身は時刻と大きさが変わるまで覚えておく。スマホが毎秒読んでも読み直さない)。
    sealed class CachedFile
    {
        public string name;
        public DateTime writeUtc;
        public long length;
        public readonly List<ChartMarkSummary> marks = new List<ChartMarkSummary>();
        public HashSet<string> fixedIds = NoIds;
    }

    sealed class CachedStatus
    {
        public DateTime writeUtc;
        public long length;
        public HashSet<string> fixedIds;
    }

    static readonly HashSet<string> NoIds = new HashSet<string>();
    static readonly Dictionary<string, CachedFile> Files = new Dictionary<string, CachedFile>(StringComparer.Ordinal);
    static readonly Dictionary<string, CachedStatus> Statuses = new Dictionary<string, CachedStatus>(StringComparer.Ordinal);

    // 新しい順(ファイル名の頭は日時)。この回と書けなかった回のファイルは入れない(メモリの方が新しい)。
    // *.status.json は印のファイルとして読まない。unsavedFixed は Unsaved と同じ並びの、直した印の ID。
    static List<CachedFile> Scan(out HashSet<string> currentFixed, out HashSet<string>[] unsavedFixed)
    {
        currentFixed = NoIds;
        unsavedFixed = new HashSet<string>[Unsaved.Count];
        for (int u = 0; u < unsavedFixed.Length; u++) unsavedFixed[u] = NoIds;
        var list = new List<CachedFile>();
        FileInfo[] infos;
        try
        {
            string folder = Folder;
            if (!Directory.Exists(folder)) { Files.Clear(); Statuses.Clear(); return list; }
            infos = new DirectoryInfo(folder).GetFiles();
        }
        catch (Exception) { return list; }

        var statusFiles = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in infos)
            if (info.Name.EndsWith(StatusFileSuffix, StringComparison.OrdinalIgnoreCase))
                statusFiles[info.Name.Substring(0, info.Name.Length - StatusFileSuffix.Length)] = info;
        string currentStem = CurrentPath != null ? Path.GetFileNameWithoutExtension(CurrentPath) : null;
        var inMemory = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (CurrentPath != null) inMemory.Add(FullPath(CurrentPath));
        foreach (var pair in Unsaved) inMemory.Add(FullPath(pair.Value));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in infos)
        {
            string name = info.Name;
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(StatusFileSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (inMemory.Contains(info.FullName)) continue;
            string stem = name.Substring(0, name.Length - ".json".Length);
            seen.Add(info.FullName);
            var cached = Load(info);
            if (statusFiles.TryGetValue(stem, out var statusInfo))
            {
                seen.Add(statusInfo.FullName);
                cached.fixedIds = FixedIds(statusInfo);
            }
            else cached.fixedIds = NoIds;
            list.Add(cached);
        }
        // この回の状態のファイル(この回のファイルがまだ書けていなくても読む)。
        if (currentStem != null && statusFiles.TryGetValue(currentStem, out var currentStatus))
        {
            seen.Add(currentStatus.FullName);
            currentFixed = FixedIds(currentStatus);
        }
        for (int u = 0; u < Unsaved.Count; u++)
            if (statusFiles.TryGetValue(Path.GetFileNameWithoutExtension(Unsaved[u].Value), out var unsavedStatus))
            {
                seen.Add(unsavedStatus.FullName);
                unsavedFixed[u] = FixedIds(unsavedStatus);
            }
        Forget(Files, seen);
        Forget(Statuses, seen);
        list.Sort((a, b) => string.CompareOrdinal(b.name, a.name));
        return list;
    }

    static void Forget<T>(Dictionary<string, T> cache, HashSet<string> keep)
    {
        List<string> drop = null;
        foreach (var key in cache.Keys)
            if (!keep.Contains(key)) (drop ?? (drop = new List<string>())).Add(key);
        if (drop != null) foreach (var key in drop) cache.Remove(key);
    }

    static CachedFile Load(FileInfo info)
    {
        if (Files.TryGetValue(info.FullName, out var cached) && cached.writeUtc == info.LastWriteTimeUtc && cached.length == info.Length)
            return cached;
        cached = new CachedFile { name = info.Name, writeUtc = info.LastWriteTimeUtc, length = info.Length };
        try
        {
            var data = JsonUtility.FromJson<ChartMarkFile>(File.ReadAllText(info.FullName, Encoding.UTF8));
            if (data?.marks != null)
                foreach (var mark in data.marks)
                    if (mark != null && !string.IsNullOrEmpty(mark.id)) cached.marks.Add(Summary(data, mark, false));
        }
        catch (Exception) { } // 壊れたファイル・印でない JSON は一覧に出さない(書き換わるまで読み直さない)
        Files[info.FullName] = cached;
        return cached;
    }

    static HashSet<string> FixedIds(FileInfo info)
    {
        if (Statuses.TryGetValue(info.FullName, out var cached) && cached.writeUtc == info.LastWriteTimeUtc && cached.length == info.Length)
            return cached.fixedIds;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var state = JsonUtility.FromJson<ChartMarkStatusFile>(File.ReadAllText(info.FullName, Encoding.UTF8));
            if (state?.marks != null)
                foreach (var mark in state.marks)
                    if (mark != null && !string.IsNullOrEmpty(mark.id) && mark.status == StatusFixed) ids.Add(mark.id);
        }
        catch (Exception) { } // 壊れた状態のファイルは「直した印なし」とみなす(marks.py がよけて作り直す)
        Statuses[info.FullName] = new CachedStatus { writeUtc = info.LastWriteTimeUtc, length = info.Length, fixedIds = ids };
        return ids;
    }
}
