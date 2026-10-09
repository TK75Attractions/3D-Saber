using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// 受信側はキューへの追加だけ。Flush は main thread から呼ぶ。
public sealed class PhoneSaberEventLog
{
    public const long MaximumFileBytes = 1024 * 1024;
    public const int FileCount = 5; // 現行1個と世代4個。
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
    public string LogPath { get; }
    public string LastError { get; private set; } = "";

    public PhoneSaberEventLog(string logPath) { LogPath = logPath; }

    public static void Record(string station, string colour, string kind, string detail = "")
    {
        try { Current?.Enqueue(DateTimeOffset.UtcNow, station, colour, kind, detail); }
        catch { /* 診断の失敗をゲームや受信 thread に伝播させない。 */ }
    }

    public void Enqueue(DateTimeOffset timestamp, string station, string colour, string kind, string detail)
    {
        try
        {
            string line = Format(timestamp, station, colour, kind, detail);
            lock (gate)
            {
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
            " colour=" + Field(colour) + " event=" + Field(kind) + " " + Field(detail) + "\n";
    }

    static string Field(string value)
    {
        if (value == null) return "";
        var result = new StringBuilder();
        for (int i = 0; i < Math.Min(value.Length, 512); i++)
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
            // 空の定期 Flush はロック不要。フラグの更新はキューと同じ lock 内で行う。
            if (!hasPending && retryLine == null) return;
            lock (gate)
            {
                if (pending.Count == 0 && dropped == 0 && retryLine == null) { hasPending = false; return; }
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
                retryLine = null;
            }
            LastError = rotationError ?? "";
            lock (gate) hasPending = pending.Count != 0 || dropped != 0;
        }
        catch (Exception e) { LastError = e.GetType().Name; }
    }

    void Rotate()
    {
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

    public PhoneSaberConnectionEvents(double startedAt, bool mac, Action<string, string> record)
    {
        lastPacketAt = startedAt;
        this.mac = mac;
        this.record = record;
    }

    public void Poll(double now)
    {
        lock (gate) CheckSilence(now);
    }

    public void Packet(double now, string ip)
    {
        lock (gate)
        {
            CheckSilence(now);
            if (sender != ip)
            {
                record("sender-change", "from=" + sender + " to=" + ip +
                    " route-from=" + PhoneSaberStatsDisplay.ClassifyRoute(sender, mac) +
                    " route-to=" + PhoneSaberStatsDisplay.ClassifyRoute(ip, mac));
                sender = ip;
            }
            if (silent) record("input-back", "sender=" + ip);
            silent = false;
            lastPacketAt = now;
        }
    }

    void CheckSilence(double now)
    {
        if (silent || now - lastPacketAt <= 1.0) return;
        silent = true;
        record("input-silent", "gap-seconds=" + (now - lastPacketAt).ToString("F3", CultureInfo.InvariantCulture));
    }
}
