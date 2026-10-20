using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// 受信側はキューへの追加だけ。Flush は main thread から呼ぶ。
public sealed class PhoneSaberEventLog
{
    public const long MaximumFileBytes = 2 * 1024 * 1024;
    public const int FileCount = 5; // 現行1個と世代4個。
    // 本体10 MiB + archive180 MiB = 190 MiB（約199 MB）。直近7日は容量より保全を優先する。
    public const long MaximumArchiveBytes = 180 * 1024 * 1024;
    public const int QueueCapacity = 512;
    const int FlushBatchSize = 128;
    static readonly Encoding Utf8 = new UTF8Encoding(false);
    public static PhoneSaberEventLog Current { get; set; }
    readonly object gate = new object();
    readonly Queue<string> pending = new Queue<string>();
    volatile bool hasPending;
    int dropped;
    // 書込に失敗した行。Flush(main thread)だけが触り、次回の Flush で最初に書き直す(失敗で行を失わない)。
    string retryLine;
    bool retryWritten, retryArchiveStarted;
    string archivePath;
    long archiveBytesSincePrune;
    bool pruneArchives;
    readonly PhoneSaberLogActivity activity = new PhoneSaberLogActivity(
        SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
    public string LogPath { get; }
    public string LastError { get; private set; } = "";

    public PhoneSaberEventLog(string logPath) { LogPath = logPath; }

    public static void Record(string station, string colour, string kind, string detail = "")
    {
        try
        {
            var log = Current;
            if (log == null) return;
            // 定期診断の呼出元は main thread。座標や受信 thread には触れない。
            if (PhoneSaberLogActivity.IsPeriodic(kind)) log.RefreshActivity(station);
            log.Enqueue(DateTimeOffset.UtcNow, station, colour, kind, detail);
        }
        catch { /* 診断の失敗をゲームや受信 thread に伝播させない。 */ }
    }

    public void Enqueue(DateTimeOffset timestamp, string station, string colour, string kind, string detail)
    {
        try
        {
            string line = Format(timestamp, station, colour, kind, detail);
            lock (gate)
            {
                if (activity.IsIdle && PhoneSaberLogActivity.IsPeriodic(kind)) return;
                if (pending.Count >= QueueCapacity) { pending.Dequeue(); dropped++; }
                pending.Enqueue(line);
                hasPending = true;
            }
        }
        catch { /* 記録不能でも入力を継続する。 */ }
    }

    // UTC・固定形式。改行と制御文字を除去し、異常な例外メッセージでも1行に収める。
    public static string Format(DateTimeOffset timestamp, string station, string colour, string kind, string detail)
    {
        return timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) +
            " station=" + Field(string.IsNullOrEmpty(station) ? "none" : station) +
            " colour=" + Field(colour) + " event=" + Field(kind) + " " + Field(detail, 2048) + "\n";
    }

    static string Field(string value, int limit = 512)
    {
        if (value == null) return "";
        var result = new StringBuilder();
        for (int i = 0; i < Math.Min(value.Length, limit); i++)
            result.Append(char.IsControl(value[i]) ? ' ' : value[i]);
        return result.ToString();
    }

    public static bool ShouldRotate(long currentBytes, int incomingBytes, long limit = MaximumFileBytes)
        => currentBytes > 0 && (currentBytes >= limit || incomingBytes > limit - currentBytes);

    public static string GenerationPath(string path, int generation)
        => generation == 0 ? path : path + "." + generation.ToString(CultureInfo.InvariantCulture);

    // 1回の仕事量も制限。容量上限は UTF-8 バイト数で判断する。
    public void Flush()
    {
        try
        {
            if (ReferenceEquals(Current, this)) RefreshActivity(null);
            // 空の定期 Flush はロック不要。フラグの更新はキューと同じ lock 内で行う。
            if (!hasPending && retryLine == null && !pruneArchives) return;
            lock (gate)
            {
                if (pending.Count == 0 && dropped == 0 && retryLine == null && !pruneArchives) { hasPending = false; return; }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            string rotationError = null;
            for (int i = 0; i < FlushBatchSize; i++)
            {
                string line = retryLine;
                if (line == null)
                {
                    lock (gate)
                    {
                        if (dropped > 0)
                        {
                            line = Format(DateTimeOffset.UtcNow, "", "SYSTEM", "queue-overflow", "dropped=" + dropped);
                            dropped = 0;
                        }
                        else if (pending.Count > 0) line = pending.Dequeue();
                        else break;
                    }
                }
                // 書込が成功するまで保持し、例外で抜けても次回に同じ行から再開する。
                retryLine = line;
                byte[] bytes = Utf8.GetBytes(line);
                if (!retryWritten)
                {
                    long length = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
                    if (ShouldRotate(length, bytes.Length))
                    {
                        // Windows で他のプロセスが古い世代を開いていると移動できない。
                        // 世代交代に失敗しても記録は止めず、現行ファイルへ追記を続ける(上限超過は許容)。
                        try { Rotate(); }
                        catch (Exception e) { rotationError = "rotate " + e.GetType().Name; }
                    }
                    using (var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                        stream.Write(bytes, 0, bytes.Length);
                    retryWritten = true;
                }
                WriteArchive(line, bytes);
                retryLine = null;
                retryWritten = retryArchiveStarted = false;
            }
            if (pruneArchives)
            {
                PruneArchives(DateTimeOffset.UtcNow);
                pruneArchives = false;
                archiveBytesSincePrune = 0;
            }
            LastError = rotationError ?? "";
            lock (gate) hasPending = pending.Count != 0 || dropped != 0;
        }
        catch (Exception e) { LastError = e.GetType().Name; }
    }

    // Flush と定期診断からのみ呼ぶ。最後の有効座標を保持し、受信停止時の時刻リセットも跨ぐ。
    void RefreshActivity(string station)
    {
        var input = InputPoint.Instance;
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        ObserveCoordinates(now, input == null ? double.NegativeInfinity : input.LastReceivedMonotonicTime,
            input == null ? double.NegativeInfinity : input.LastReceivedMonotonicTime2, DateTimeOffset.UtcNow, station);
    }

    public void ObserveCoordinates(double now, double redAt, double blueAt, DateTimeOffset timestamp, string station)
    {
        lock (gate)
            if (activity.Update(now, redAt, blueAt))
                Enqueue(timestamp, station ?? PhoneSaberStation.Read(), "SYSTEM", "idle", "no-coordinates-seconds=60");
    }

    public static string ArchivePath(string logPath, DateTimeOffset startedAt)
        => Path.Combine(Path.GetDirectoryName(logPath), "archive", "events-" +
            startedAt.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");

    public static bool ShouldPruneArchive(string path, DateTimeOffset now)
    {
        return TryArchiveStart(path, out var startedAt) && startedAt < now.AddDays(-14);
    }

    static bool TryArchiveStart(string path, out DateTimeOffset startedAt)
    {
        startedAt = default;
        string name = Path.GetFileName(path);
        return name.Length == 26 && name.StartsWith("events-", StringComparison.Ordinal) &&
            name.EndsWith(".log", StringComparison.Ordinal) &&
            DateTimeOffset.TryParseExact(name.Substring(7, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out startedAt);
    }

    // 純粋関数。mtime（最後の複写時刻）で直近7日を保護し、古い終了セッションから削除する。
    public static List<string> ArchivesToPrune(
        IEnumerable<(string path, long bytes, DateTimeOffset lastWrite)> archives, DateTimeOffset now, string activePath)
    {
        var files = new List<(string path, long bytes, DateTimeOffset lastWrite)>();
        long total = 0;
        foreach (var file in archives)
            if (TryArchiveStart(file.path, out _)) { files.Add(file); total += file.bytes; }
        files.Sort((a, b) => a.lastWrite != b.lastWrite ? a.lastWrite.CompareTo(b.lastWrite) :
            string.CompareOrdinal(a.path, b.path));
        var result = new List<string>();
        foreach (var file in files)
        {
            if (file.path == activePath || file.lastWrite >= now.AddDays(-7)) continue;
            if (ShouldPruneArchive(file.path, now) || total > MaximumArchiveBytes)
            {
                result.Add(file.path);
                total -= file.bytes;
            }
        }
        return result;
    }

    static string EventKind(string line)
    {
        int start = line.IndexOf(" event=", StringComparison.Ordinal) + 7;
        int end = line.IndexOf(' ', start);
        return line.Substring(start, end - start);
    }

    void WriteArchive(string line, byte[] bytes)
    {
        string kind = EventKind(line);
        if (kind == "session-start")
        {
            var startedAt = DateTimeOffset.ParseExact(line.Substring(0, 24),
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            if (!retryArchiveStarted)
            {
                string candidate = ArchivePath(LogPath, startedAt);
                Directory.CreateDirectory(Path.GetDirectoryName(candidate));
                // 同じ秒の再起動でも過去の証拠を上書きしない。
                while (File.Exists(candidate)) candidate = ArchivePath(LogPath, startedAt = startedAt.AddSeconds(1));
                using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                archivePath = candidate;
                retryArchiveStarted = true;
                pruneArchives = true;
            }
        }
        // 開始から逐次複写するので、異常終了でも既に書けた行は残る。世代交代にも依存しない。
        if (archivePath != null)
        {
            using (var stream = new FileStream(archivePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                stream.Write(bytes, 0, bytes.Length);
            archiveBytesSincePrune += bytes.Length;
            if (archiveBytesSincePrune >= 1024 * 1024) pruneArchives = true;
        }
        if (kind == "session-quit") { archivePath = null; pruneArchives = true; }
    }

    void PruneArchives(DateTimeOffset now)
    {
        string directory = Path.Combine(Path.GetDirectoryName(LogPath), "archive");
        if (!Directory.Exists(directory)) return;
        var files = new List<(string path, long bytes, DateTimeOffset lastWrite)>();
        foreach (string path in Directory.GetFiles(directory))
        {
            var info = new FileInfo(path);
            files.Add((path, info.Length, info.LastWriteTimeUtc));
        }
        foreach (string path in ArchivesToPrune(files, now, archivePath)) File.Delete(path);
    }

    void Rotate()
    {
        // 移動先がディレクトリなら、既存の世代を削除・移動する前に失敗させる。
        // 毎回同じ障害で Flush が再試行しても、過去のログを順に失わない。
        for (int i = 1; i < FileCount; i++)
            if (Directory.Exists(GenerationPath(LogPath, i)))
                throw new IOException("Log generation path is a directory");
        File.Delete(GenerationPath(LogPath, FileCount - 1));
        for (int i = FileCount - 2; i >= 0; i--)
        {
            string source = GenerationPath(LogPath, i);
            if (File.Exists(source)) File.Move(source, GenerationPath(LogPath, i + 1));
        }
    }
}

// datagram の到着と main thread の監視を同期し、短い途絶も復帰時に残す。
// 座標や payload 解析には触れず、不正な payload も「到着」として扱う。
public sealed class PhoneSaberConnectionEvents
{
    readonly object gate = new object();
    readonly Action<string, string> record;
    readonly bool mac;
    double lastPacketAt;
    bool silent;
    string sender = "";
    // 2台のスマホが同じ port へ送ると packet ごとに送信元が入れ替わる。記録は1秒に1行までにして
    // events.log を埋めない（抑制した回数は次の行に残す）。
    double lastSenderChangeAt = double.NegativeInfinity;
    int suppressedSenderChanges;
    string suppressedFrom = "";

    public PhoneSaberConnectionEvents(double startedAt, bool mac, Action<string, string> record)
    {
        lastPacketAt = startedAt;
        this.mac = mac;
        this.record = record;
    }

    public void Poll(double now)
    {
        lock (gate)
        {
            CheckSilence(now);
            FlushSenderChange(now);
        }
    }

    public void Packet(double now, string ip)
    {
        lock (gate)
        {
            CheckSilence(now);
            FlushSenderChange(now);
            if (sender != ip)
            {
                if (now - lastSenderChangeAt >= 1.0)
                {
                    record("sender-change", "from=" + sender + " to=" + ip +
                        " route-from=" + PhoneSaberStatsDisplay.ClassifyRoute(sender, mac) +
                        " route-to=" + PhoneSaberStatsDisplay.ClassifyRoute(ip, mac) +
                        (suppressedSenderChanges > 0 ? " suppressed=" + suppressedSenderChanges : ""));
                    lastSenderChangeAt = now;
                    suppressedSenderChanges = 0;
                }
                else
                {
                    if (suppressedSenderChanges == 0) suppressedFrom = sender;
                    suppressedSenderChanges++;
                }
                sender = ip;
            }
            if (silent) record("input-back", "sender=" + ip);
            silent = false;
            lastPacketAt = now;
        }
    }

    // 最後の切替の後に同じ送信元が続いても、抑制した出来事を失わない。
    void FlushSenderChange(double now)
    {
        if (suppressedSenderChanges == 0 || now - lastSenderChangeAt < 1.0) return;
        record("sender-change", "from=" + suppressedFrom + " to=" + sender +
            " route-from=" + PhoneSaberStatsDisplay.ClassifyRoute(suppressedFrom, mac) +
            " route-to=" + PhoneSaberStatsDisplay.ClassifyRoute(sender, mac) +
            " suppressed=" + suppressedSenderChanges);
        lastSenderChangeAt = now;
        suppressedSenderChanges = 0;
    }

    void CheckSilence(double now)
    {
        if (silent || now - lastPacketAt <= 1.0) return;
        silent = true;
        record("input-silent", "gap-seconds=" + (now - lastPacketAt).ToString("F3", CultureInfo.InvariantCulture));
    }
}

// Unity・ファイル・実時計に依存しない、定期診断の停止／復帰判定。
public sealed class PhoneSaberLogActivity
{
    public const double IdleSeconds = 60;
    double lastInputAt;
    public bool IsIdle { get; private set; }

    public PhoneSaberLogActivity(double startedAt) { lastInputAt = startedAt; }
    public static bool IsPeriodic(string kind) => kind == "perf" || kind == "latency-estimate";

    // 戻り値は idle に入った1回だけ true。赤・青のどちらかの座標で直ちに復帰する。
    public bool Update(double now, double redAt, double blueAt)
    {
        lastInputAt = Math.Max(lastInputAt, Math.Max(redAt, blueAt));
        bool idle = now - lastInputAt >= IdleSeconds;
        bool entered = idle && !IsIdle;
        IsIdle = idle;
        return entered;
    }
}
