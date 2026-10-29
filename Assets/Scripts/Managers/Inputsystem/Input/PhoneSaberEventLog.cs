using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;

// 受信側はキューへの追加だけ。main thread の Flush は単一 writer へ通知する。
public sealed class PhoneSaberEventLog
{
    public const long MaximumFileBytes = 2 * 1024 * 1024;
    public const int FileCount = 5; // 現行1個と世代4個。
    // 本体10 MiB + archive180 MiB = 190 MiB（約199 MB）。直近7日は容量より保全を優先する。
    public const long MaximumArchiveBytes = 180 * 1024 * 1024;
    public const int QueueCapacity = 512;
    const int FlushBatchSize = 128;
    static readonly Encoding Utf8 = new UTF8Encoding(false);
    static volatile PhoneSaberEventLog current;
    public static PhoneSaberEventLog Current { get => current; set => current = value; }
    readonly object gate = new object();
    readonly Queue<string> pending = new Queue<string>();
    volatile bool hasPending;
    Thread writer;
    bool requested, writing, stopping;
    bool accepting = true;
    readonly Action beforeSave;
    volatile string lastError = "";
    int dropped;
    // 以下の保存状態は writer だけが触る。失敗行は次の要求で最初に再試行する。
    string retryLine;
    bool retryWritten, retryArchiveStarted;
    string archivePath;
    long archiveBytesSincePrune;
    bool pruneArchives;
    PhoneSaberLogActivity activity = new PhoneSaberLogActivity(
        SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
    // 保存に成功した行の通知。購読側は背景 writer を待たせず、上限つきキューへ渡すだけ。
    public event Action<string> LineWritten;
    public string LogPath { get; }
    public string LastError => lastError;

    // beforeSave は同期ゲート・I/O停滞を再現する試験用。通常は指定しない。
    public PhoneSaberEventLog(string logPath, Action beforeSave = null)
    { LogPath = logPath; this.beforeSave = beforeSave; }

    // Domain Reload 無効時も同じ保存先の所有者を再利用する。停止期限切れでも二重 writer にしない。
    public static void InitializeCurrent(string logPath)
    {
        var log = Current;
        if (log != null && log.LogPath == logPath)
        {
            lock (log.gate)
            {
                log.stopping = false;
                log.accepting = true;
                log.activity = new PhoneSaberLogActivity(
                    SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
            }
        }
        else
        {
            if (log != null && !log.StopAndDrain(1000))
                throw new InvalidOperationException("Previous event log is not drained");
            Current = new PhoneSaberEventLog(logPath);
        }
    }

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
                if (!accepting || (activity.IsIdle && PhoneSaberLogActivity.IsPeriodic(kind))) return;
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
            " colour=" + Field(colour) + " event=" + Field(kind) + " " + Field(detail, kind == "peer-log" ? PhoneSaberPeerLog.MaximumLineBytes + 64 : 2048) + "\n";
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

    // Unity オブジェクトの参照は呼出元に残す。I/O 用のロックや Task キューを作らない。
    public void Flush()
    {
        try
        {
            if (ReferenceEquals(Current, this)) RefreshActivity(null);
            if (!hasPending) return;
            lock (gate)
            {
                if (!accepting) return;
                RequestLocked();
            }
        }
        catch (Exception e) { lastError = e.GetType().Name; }
    }

    void RequestLocked()
    {
        if (!hasPending) return;
        requested = true; // 停滞中の何万回の要求も1ビットに集約する。
        if (writer == null)
        {
            try
            {
                writer = new Thread(WriteLoop) { IsBackground = true, Name = "PhoneSaber event log" };
                writer.Start();
            }
            catch (Exception e)
            {
                writer = null;
                requested = false;
                lastError = e.GetType().Name;
            }
        }
        Monitor.PulseAll(gate);
    }

    // 試験・明示的な保存確認用。通常の Update からは呼ばない。失敗時は次の要求で再試行する。
    public bool FlushAndWait(int timeoutMilliseconds = 1000)
    {
        var elapsed = Stopwatch.StartNew();
        Flush();
        lock (gate) return WaitLocked(timeoutMilliseconds, elapsed);
    }

    // 受付を閉じて終了行まで保存する。期限は呼出元の待機だけに適用し、I/O を強制中断しない。
    // 期限切れでも所有者は残り、background writer は解放後に残りを保存して停止する。
    public bool StopAndDrain(int timeoutMilliseconds = 1000)
    {
        var elapsed = Stopwatch.StartNew();
        lock (gate)
        {
            accepting = false;
            stopping = true;
            RequestLocked();
            Monitor.PulseAll(gate);
            return WaitLocked(timeoutMilliseconds, elapsed);
        }
    }

    bool WaitLocked(int timeoutMilliseconds, Stopwatch elapsed)
    {
        while (writing || requested || (stopping && writer != null))
        {
            long remaining = (long)Math.Max(0, timeoutMilliseconds) - elapsed.ElapsedMilliseconds;
            if (remaining <= 0) return false;
            Monitor.Wait(gate, (int)remaining);
        }
        return !hasPending;
    }

    void WriteLoop()
    {
        while (true)
        {
            lock (gate)
            {
                while (!requested && !stopping) Monitor.Wait(gate);
                if (!requested && stopping) { writer = null; Monitor.PulseAll(gate); return; }
                requested = false;
                writing = true;
            }
            // 保存中は gate を持たず、enqueue と通知がディスクを待たないようにする。
            while (SaveBatch() && hasPending) { }
            lock (gate)
            {
                writing = false;
                Monitor.PulseAll(gate);
            }
        }
    }

    // 1バッチ128行。キュー・再試行・archive 状態の所有者はこの writer だけ。
    bool SaveBatch()
    {
        try
        {
            if (!hasPending) return true;
            beforeSave?.Invoke();
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
                    try { LineWritten?.Invoke(line); }
                    catch { /* 診断共有の失敗でもローカルの記録を続ける。 */ }
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
            lastError = rotationError ?? "";
            return true;
        }
        catch (Exception e) { lastError = e.GetType().Name; return false; }
        finally
        {
            lock (gate) hasPending = pending.Count != 0 || dropped != 0 || retryLine != null || pruneArchives;
        }
    }

    // main thread の Flush と定期診断からのみ呼ぶ。最後の有効座標を保持し、受信停止時の時刻リセットも跨ぐ。
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
        // 移動先がディレクトリなら、既存の世代を移動する前に失敗させる。
        for (int i = 1; i < FileCount; i++)
            if (Directory.Exists(GenerationPath(LogPath, i)))
                throw new IOException("Log generation path is a directory");

        // 最古の世代も退避し、全世代の移動が成功するまで削除しない。
        // 旧実装は最古を先に消して若い世代から順に移動していたので、現行ファイルを別のプロセスが
        // 開いていて（Windows で Delete の共有なし）移動に失敗すると、追記ごとの再試行で
        // 旧世代が1つずつ消えていった（2026-10-30 の点検）。失敗したら済んだ移動を逆順に戻す。
        string retired = LogPath + ".rotation-" + Guid.NewGuid().ToString("N");
        var moved = new List<(string source, string destination)>(FileCount);
        try
        {
            for (int i = FileCount - 1; i >= 0; i--)
            {
                string source = GenerationPath(LogPath, i);
                if (!File.Exists(source)) continue;
                string destination = i == FileCount - 1 ? retired : GenerationPath(LogPath, i + 1);
                File.Move(source, destination);
                moved.Add((source, destination));
            }
            File.Delete(retired);
        }
        catch (Exception rotationError)
        {
            // 復元にも失敗した場合は退避ファイルを残し、両方の失敗を通知する。
            Exception restoreError = null;
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                try { File.Move(moved[i].destination, moved[i].source); }
                catch (Exception e) { restoreError = e; }
            }
            if (restoreError != null) throw new AggregateException(rotationError, restoreError);
            throw;
        }
    }
}

// 最後の状態だけを単一 writer に渡す。開始時の読取り・開始印と終了待ちは通常通知から分離する。
public sealed class PhoneSaberRunJournal
{
    public static PhoneSaberRunJournal Current { get; set; }
    // Domain Reload 無効時の再開始でも、同じ保存先の I/O と .tmp の所有者は1個だけ。
    static readonly Dictionary<string, Storage> stores = new Dictionary<string, Storage>(StringComparer.OrdinalIgnoreCase);
    readonly Storage storage;
    readonly Action beforeSave;
    readonly string run = "run=" + Guid.NewGuid().ToString("N") + " started-utc=" + DateTime.UtcNow.ToString("O");
    bool begun, finished;
    long version, savedVersion = -1;
    volatile string lastError = "";
    public string LastError => lastError;

    sealed class Snapshot
    {
        public PhoneSaberRunJournal journal;
        public long version;
        public string text;
    }

    sealed class Storage
    {
        public readonly string path;
        public readonly object gate = new object(), ioGate = new object(), beginGate = new object(), markerGate = new object();
        public PhoneSaberRunJournal active;
        public Snapshot pending;
        public Thread writer;
        public bool requested, writing;
        public Storage(string path) { this.path = path; }

        public void StartLocked()
        {
            if (writer != null) return;
            try
            {
                writer = new Thread(WriteLoop) { IsBackground = true, Name = "PhoneSaber run journal" };
                writer.Start();
            }
            catch (Exception e) { writer = null; active.lastError = e.GetType().Name; }
        }

        void WriteLoop()
        {
            while (true)
            {
                Snapshot next;
                lock (gate)
                {
                    while (!requested && !active.finished) Monitor.Wait(gate);
                    if (!requested)
                    {
                        writer = null;
                        Monitor.PulseAll(gate);
                        return;
                    }
                    next = pending;
                    pending = null;
                    requested = false;
                    writing = true;
                }
                bool saved = false;
                // 開始印と置換までを直列化する。受付 gate は I/O 中に保持しない。
                lock (ioGate)
                {
                    bool current;
                    lock (gate) current = ReferenceEquals(active, next.journal);
                    if (current) saved = next.journal.Save(next);
                }
                lock (gate)
                {
                    if (ReferenceEquals(active, next.journal))
                    {
                        if (saved) next.journal.savedVersion = next.version;
                        // 失敗は次の通知・明示的要求まで再試行しない。新しい状態を優先する。
                        else if (pending == null) pending = next;
                    }
                    writing = false;
                    Monitor.PulseAll(gate);
                }
            }
        }
    }

    // beforeSave は実保存直前の停滞・障害を再現する試験用。通常は指定しない。
    public PhoneSaberRunJournal(string path, Action beforeSave = null)
    {
        string fullPath = Path.GetFullPath(path);
        lock (stores)
        {
            if (!stores.TryGetValue(fullPath, out storage)) stores.Add(fullPath, storage = new Storage(fullPath));
        }
        this.beforeSave = beforeSave;
    }

    public string Begin(string watchdogExitFile = null, int timeoutMilliseconds = 1000)
    {
        // 開始処理だけを直列化する。通常通知の受付 lock は I/O 中に保持しない。
        lock (storage.beginGate)
        {
            lock (storage.gate) { if (begun) return null; }
            string previous = null;
            lock (storage.markerGate)
            {
                try
                {
                    // 未完了の開始印は旧 writer が後から書いた clean-quit より優先する。
                    string path = File.Exists(storage.path + ".starting") ? storage.path + ".starting" : storage.path;
                    if (File.Exists(path))
                    {
                        string saved = File.ReadAllText(path);
                        if (!saved.StartsWith("clean-quit\n", StringComparison.Ordinal))
                            previous = "reason=no-clean-quit-observed cause=unknown previous=" + saved.Replace('\n', ' ');
                    }
                    if (previous != null && !string.IsNullOrEmpty(watchdogExitFile) && File.Exists(watchdogExitFile))
                        previous += " watchdog-last-observation=" + File.ReadAllText(watchdogExitFile).Trim();
                }
                catch { previous = "reason=previous-run-state-unreadable cause=unknown"; }
                // .tmp を共有せず、新 run の意図を I/O 所有権の待機前に残す。
                WriteStartMarker("journal-start-pending");
            }
            bool ownsIo = Monitor.TryEnter(storage.ioGate, Math.Max(0, timeoutMilliseconds));
            try
            {
                if (!ownsIo)
                {
                    lock (storage.markerGate) WriteStartMarker("journal-start-unavailable previous-writer-timeout");
                    lastError = "PreviousWriterTimeout" + (lastError.Length == 0 ? "" : " marker-error=" + lastError);
                }
                var startup = CreateSnapshot("startup", false);
                lock (storage.gate)
                {
                    if (storage.active != null) storage.active.finished = true;
                    storage.active = this;
                    storage.pending = ownsIo ? null : startup;
                    storage.requested = !ownsIo;
                    // 期限切れ時は開始状態の投入と同時に受付を開き、後続の End を巻き戻さない。
                    begun = !ownsIo;
                }
                // 通常開始は同期保存。旧 I/O の期限切れ後も受付を開き、同じ writer が復帰して保存する。
                bool savedStartup = ownsIo && Save(startup);
                lock (storage.gate)
                {
                    begun = true;
                    if (savedStartup) savedVersion = startup.version;
                    else if (ownsIo) storage.pending = startup;
                    storage.StartLocked();
                    Monitor.PulseAll(storage.gate);
                }
                return ownsIo ? previous : "reason=journal-start-unavailable cause=unknown" +
                    (previous == null ? "" : " " + previous);
            }
            finally { if (ownsIo) Monitor.Exit(storage.ioGate); }
        }
    }

    // 起動時だけの補助印。writer の .tmp と分離し、途中失敗の空ファイルも異常扱いにする。
    void WriteStartMarker(string state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storage.path));
            File.WriteAllText(storage.path + ".starting", CreateSnapshot(state, false).text);
            lastError = "";
        }
        catch (Exception e) { lastError = e.GetType().Name; }
    }

    Snapshot CreateSnapshot(string state, bool clean) => new Snapshot {
        journal = this, version = version,
        text = (clean ? "clean-quit\n" : "unclean-or-running\n") + run +
            " state-utc=" + DateTime.UtcNow.ToString("O") + " state=" + state
    };

    public void Note(string state, bool clean = false)
    {
        lock (storage.gate)
        {
            if (!begun || finished || !ReferenceEquals(storage.active, this)) return;
            version++;
            storage.pending = CreateSnapshot(state, clean);
            finished = clean;
            storage.requested = true;
            Monitor.PulseAll(storage.gate);
        }
    }

    bool Save(Snapshot snapshot)
    {
        try
        {
            beforeSave?.Invoke();
            Directory.CreateDirectory(Path.GetDirectoryName(storage.path));
            File.WriteAllText(storage.path + ".tmp", snapshot.text);
            if (File.Exists(storage.path)) File.Replace(storage.path + ".tmp", storage.path, null);
            else File.Move(storage.path + ".tmp", storage.path);
            lock (storage.markerGate)
            {
                string marker = storage.path + ".starting";
                // 旧 run の保存完了で新 run の開始印を消さない。
                if (File.Exists(marker) && File.ReadAllText(marker).StartsWith("unclean-or-running\n" + run + " state-utc=", StringComparison.Ordinal))
                    File.Delete(marker);
            }
            lastError = "";
            return true;
        }
        catch (Exception e) { lastError = e.GetType().Name; return false; }
    }

    // 試験・明示的な保存確認用。通常の Note / Update から呼ばない。
    public bool FlushAndWait(int timeoutMilliseconds = 1000)
    {
        var elapsed = Stopwatch.StartNew();
        lock (storage.gate)
        {
            if (!begun || !ReferenceEquals(storage.active, this)) return false;
            if (storage.pending != null) storage.requested = true;
            storage.StartLocked();
            Monitor.PulseAll(storage.gate);
            while (storage.writing || storage.requested || (finished && storage.writer != null))
            {
                long remaining = (long)Math.Max(0, timeoutMilliseconds) - elapsed.ElapsedMilliseconds;
                if (remaining <= 0) return false;
                Monitor.Wait(storage.gate, (int)Math.Min(int.MaxValue, remaining));
            }
            return savedVersion == version;
        }
    }

    public bool End(int exitCode, int timeoutMilliseconds = 1000)
    {
        lock (storage.gate)
        {
            if (!begun || !ReferenceEquals(storage.active, this)) return false;
            if (!finished)
            {
                version++;
                storage.pending = CreateSnapshot("quit-callback exit-code=" + exitCode, exitCode == 0);
                finished = true;
                storage.requested = true;
                Monitor.PulseAll(storage.gate);
            }
        }
        return FlushAndWait(timeoutMilliseconds);
    }
}

// datagram の到着と main thread の監視を同期し、短い途絶も復帰時に残す。
// 座標や payload 解析には触れず、不正な payload も「到着」として扱う。
public sealed class PhoneSaberConnectionEvents
{
    static volatile int activeGameScene;
#if UNITY_5_3_OR_NEWER
    // scene の通知は main thread。受信 thread はキャッシュだけを読む。
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void TrackGameScene()
    {
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= ActiveSceneChanged;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += ActiveSceneChanged;
        ActiveSceneChanged(default, UnityEngine.SceneManagement.SceneManager.GetActiveScene());
    }

    static void ActiveSceneChanged(UnityEngine.SceneManagement.Scene previous, UnityEngine.SceneManagement.Scene current)
        => activeGameScene = current.name == "Game" ? (int)current.handle : 0;
#endif

    readonly object gate = new object();
    readonly Action<string, string> record;
    readonly Func<int> gameScene;
    readonly bool mac;
    double lastPacketAt;
    int lastPacketGameScene;
    bool silent;
    string sender = "";
    // 2台のスマホが同じ port へ送ると packet ごとに送信元が入れ替わる。記録は1秒に1行までにして
    // events.log を埋めない（抑制した回数は次の行に残す）。
    double lastSenderChangeAt = double.NegativeInfinity;
    int suppressedSenderChanges;
    string suppressedFrom = "";

    public PhoneSaberConnectionEvents(double startedAt, bool mac, Action<string, string> record, Func<int> gameScene = null)
    {
        lastPacketAt = startedAt;
        this.mac = mac;
        this.record = record;
        this.gameScene = gameScene ?? (() => activeGameScene);
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
            CheckSilence(now, resumed: true);
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
            lastPacketGameScene = gameScene();
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

    void CheckSilence(double now, bool resumed = false)
    {
        // 復帰時に実際の間隔を確定する。起動直後・曲を跨ぐ間隔・1秒超は短い途絶に数えない。
        double gap = now - lastPacketAt;
        if (resumed && !silent && gap >= 0.25 && gap <= 1.0 &&
            lastPacketGameScene != 0 && lastPacketGameScene == gameScene())
            record("input-gap", "gap-seconds=" + gap.ToString("F3", CultureInfo.InvariantCulture));
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
