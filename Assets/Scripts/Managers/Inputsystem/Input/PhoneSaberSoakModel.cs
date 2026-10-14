using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// Unity に依存しない引数・送信波形・増加判定。通常の認識・採点には使わない。
public static class PhoneSaberSoakModel
{
    public const string Argument = "-phonesaberSoak";
    public const string GameSecondsArgument = "-phonesaberSoakGameSeconds";
    public const int WarmupLoops = 3;
    public const int MinimumSamples = 8;

    public static double Minutes(string[] args)
    {
        // smoke と同時指定された場合は既存 smoke だけを動かす。
        foreach (string arg in args) if (arg == PhoneSaberPlayerSmoke.Argument) return 0;
        return Number(args, Argument, 60, .1, 1440, 0);
    }

    public static double GameSeconds(string[] args) => Number(args, GameSecondsArgument, 40, 8, 600, 40);

    static double Number(string[] args, string name, double fallback, double min, double max, double absent)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] != name) continue;
            if (i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value) && value >= min && value <= max) return value;
            return fallback;
        }
        return absent;
    }

    // 20秒周期(短い指定では圧縮)。停止中も同じ座標を送信し、途絶は実際に送信を止める。
    public static string Payload(double elapsed, double gameSeconds, bool blue)
    {
        double period = Math.Min(20, gameSeconds);
        double phase = elapsed % period / period;
        if (phase >= .65 && phase < .80 || blue && phase >= .45 && phase < .60) return null;
        double swingTime = phase >= .25 && phase < .35 ? period * .25 : elapsed;
        double angle = Math.Sin(swingTime * Math.PI * 1.5 + (blue ? Math.PI : 0)) * .95;
        double x = 960 + Math.Sin(angle) * 630;
        double y = 540 + Math.Cos(swingTime * Math.PI) * 120;
        double dx = Math.Sin(angle) * 300, dy = Math.Cos(angle) * 300;
        return string.Format(CultureInfo.InvariantCulture, "{0:F0},{1:F0},{2:F0},{3:F0}", x - dx, y - dy, x + dx, y + dy);
    }

    // 遅れた周期はまとめて送らず、次の1packetまで必ず間隔を空ける。
    public static double NextPacket(double previousDeadline, double now)
    {
        double next = previousDeadline + 1.0 / 60;
        return next > now ? next : now + 1.0 / 60;
    }

    // 最初の3周を除き8点以上。回帰の増分がnoiseと基準の20%を超え、R²>=.9、
    // 80%以上の隣接点が大きく減らない場合のみ明確な継続増加とする。単発のcache増加は除外。
    public static string Growth(IList<double> values, double noiseFloor)
    {
        int n = values.Count - WarmupLoops;
        if (n < MinimumSamples) return "insufficient";
        double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
        int nondecreasing = 0;
        for (int i = 0; i < n; i++)
        {
            double y = values[i + WarmupLoops];
            if (double.IsNaN(y) || double.IsInfinity(y) || y < 0) return "invalid";
            sx += i; sy += y; sxx += i * (double)i; sxy += i * y; syy += y * y;
            if (i > 0 && y >= values[i + WarmupLoops - 1] - noiseFloor / 4) nondecreasing++;
        }
        double xx = sxx - sx * sx / n, yy = syy - sy * sy / n, xy = sxy - sx * sy / n;
        double delta = xy / xx * (n - 1);
        double r2 = yy > 0 ? xy * xy / (xx * yy) : 0;
        return delta > Math.Max(noiseFloor, values[WarmupLoops] * .20) && r2 >= .9 &&
            nondecreasing >= Math.Ceiling((n - 1) * .8) ? "growth" : "stable";
    }
}

// thread側の例外も集計。周回ごとのメッセージ保持量を固定し、診断自身の継続増加を避ける。
public sealed class PhoneSaberSoakErrors
{
    readonly object gate = new object();
    readonly HashSet<string> messages = new HashSet<string>();
    int exceptions, errors, totalExceptions, totalErrors, omitted;

    public void Add(string message, bool exception)
    {
        lock (gate)
        {
            if (exception) { exceptions++; totalExceptions++; }
            else { errors++; totalErrors++; }
            string first = (message ?? "").Split('\n')[0].Replace('\r', ' ').Replace('"', '\'');
            if (first.Length > 120) first = first.Substring(0, 120);
            if (messages.Count < 8) messages.Add(first);
            else if (!messages.Contains(first)) omitted++;
        }
    }

    public string Drain()
    {
        lock (gate)
        {
            var text = new StringBuilder("exceptions=" + exceptions + " errors=" + errors + " messages=\"");
            foreach (string message in messages) text.Append(message).Append(" | ");
            text.Append('"').Append(" messages-omitted=").Append(omitted);
            messages.Clear(); exceptions = errors = omitted = 0;
            return text.ToString();
        }
    }

    public int Exceptions { get { lock (gate) return totalExceptions; } }
    public int Errors { get { lock (gate) return totalErrors; } }
}
