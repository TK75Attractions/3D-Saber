using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// 操作名と許可値を HTTP と UI で共用。任意のスマホコマンド文字列は受け付けない。
public static class PhoneSaberControlPanelActions
{
    static readonly Dictionary<string, string[]> choices = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        { "calibration-start", new[] { "" } }, { "calibration-advance", new[] { "" } },
        { "calibration-cancel", new[] { "" } }, { "calibration-reset", new[] { "" } },
        { "calibration-blue", new[] { "0", "1" } }, { "calibration-enabled", new[] { "0", "1" } },
        { "prediction", new[] { "auto", "0", "20", "40", "60" } },
        { "display-sync", new[] { "0", "1" } }, { "frame-cap", new[] { "0", "1", "2" } },
        { "judge-mode", new[] { "0", "1" } }, { "judge-adjust", new[] { "-10", "10" } }, { "judge-centre", new[] { "" } },
        { "filter", new[] { "0", "1", "2" } }, { "stabilizer", new[] { "0", "1" } },
        { "phone-ignore", new[] { "red", "blue", "both" } }, { "phone-clear-ignore", new[] { "" } },
        { "phone-learn-area", new[] { "" } }, { "phone-clear-area", new[] { "" } },
        { "phone-exposure", new[] { "0", "-1", "-2" } },
        { "phone-track-lock", new[] { "" } }, { "phone-clip", new[] { "" } },
        { "pc-ignore", new[] { "red", "blue" } }, { "pc-clear-ignore", new[] { "" } },
        { "clocks-synchronized", new[] { "0", "1" } }
    };

    public static IEnumerable<string> Names => choices.Keys;
    public static bool IsValid(string name, string value) => name != null && choices.TryGetValue(name, out var values) && Array.IndexOf(values, value) >= 0;
    public static bool Dispatch(string name, string value, IReadOnlyDictionary<string, Action<string>> handlers)
    {
        if (!IsValid(name, value) || !handlers.TryGetValue(name, out var handler)) return false;
        handler(value);
        return true;
    }

    // キーの判断を UI・ブラウザ・Unity 入力から分離する。
    public static bool ShouldOpenBrowser(bool shift, bool serverRunning) => !shift && serverRunning;
}

// main thread で一度組み立てた同じ表示を、IMGUI と JSON の両方で読む。
public sealed class PhoneSaberControlPanelState
{
    public sealed class Item
    {
        public string Text, Name, Value, Grade;
        public bool Enabled, Selected, Warning;
    }
    public sealed class Section
    {
        public string Title;
        public readonly List<Item> Items = new List<Item>();
    }
    public readonly List<Section> Sections = new List<Section>();
    Section current;
    public void BeginSection(string title) { current = new Section { Title = title }; Sections.Add(current); }
    public void Label(string text, bool warning = false) => current.Items.Add(new Item { Text = text ?? "", Warning = warning });
    public void HealthLabel(PhoneSaberControlPanelHealth.Check check) => current.Items.Add(new Item { Text = check.Text, Grade = check.Grade });
    public void Button(string text, string name, string value = "", bool enabled = true, bool selected = false)
    {
        if (!PhoneSaberControlPanelActions.IsValid(name, value)) throw new ArgumentException("Unknown control panel action: " + name);
        current.Items.Add(new Item { Text = text, Name = name, Value = value, Enabled = enabled, Selected = selected });
    }
    public void Toggle(string text, bool selected, string name, bool enabled = true) => Button(text, name, selected ? "0" : "1", enabled, selected);
    public string ToJson()
    {
        var json = new StringBuilder("{\"sections\":[");
        foreach (var section in Sections)
        {
            if (json[json.Length - 1] != '[') json.Append(',');
            json.Append("{\"title\":").Append(Quote(section.Title)).Append(",\"items\":[");
            foreach (var item in section.Items)
            {
                if (json[json.Length - 1] != '[') json.Append(',');
                json.Append("{\"text\":").Append(Quote(item.Text)).Append(",\"name\":").Append(Quote(item.Name))
                    .Append(",\"value\":").Append(Quote(item.Value)).Append(",\"enabled\":").Append(item.Enabled ? "true" : "false")
                    .Append(",\"selected\":").Append(item.Selected ? "true" : "false").Append(",\"warning\":").Append(item.Warning ? "true" : "false")
                    .Append(",\"grade\":").Append(Quote(item.Grade)).Append('}');
            }
            json.Append("]}");
        }
        return json.Append("]}").ToString();
    }
    public static string Quote(string value)
    {
        if (value == null) return "null";
        var result = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (c < 32 || char.IsSurrogate(c) || c == '\u2028' || c == '\u2029') result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else result.Append(c);
                    break;
            }
        }
        return result.Append('"').ToString();
    }
}

// good-20261020 の診断基準。認識・判定・送信設定には作用しない純粋な表示計算。
public static class PhoneSaberControlPanelHealth
{
    public sealed class Check
    {
        public string Text, Grade;
    }
    public static string NumericGrade(double? value, double limit, bool minimum = false) =>
        !value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value < 0 ? "WARN" :
        (minimum ? value >= limit : value <= limit) ? "OK" : "WARN";
    static string N(double? value) => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
        ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "未計測";

    public static List<Check> Evaluate(double now, PhoneSaberInputStats red, PhoneSaberInputStats blue,
        PhoneSaberLatencyEstimate redEstimate, PhoneSaberLatencyEstimate blueEstimate,
        double? redRate, double? blueRate, PhoneSaberAcknowledgement[] acknowledgements,
        bool inGame, int bothGaps, int framesOver50, bool wholeSong)
    {
        var result = new List<Check>();
        void Add(string grade, string text) => result.Add(new Check { Grade = grade, Text = grade + " · " + text });
        void Number(string label, double? value, double limit, bool minimum = false) =>
            Add(NumericGrade(value, limit, minimum), label + " " + N(value) + (minimum ? "（基準 ≥" : "（基準 ≤") + limit + "）");
        bool Active(PhoneSaberInputStats stats) => stats.HasPacket && stats.PayloadParsed && stats.SecondsSinceLastPacket >= 0 && stats.SecondsSinceLastPacket <= 1;
        Add(Active(red) && Active(blue) ? "OK" : "BAD",
            "スマホ送信: 赤 " + (Active(red) ? "受信中" : "停止 / 未接続") + " / 青 " + (Active(blue) ? "受信中" : "停止 / 未接続"));
        Number("赤 pkt/s（直近10秒）", redRate, 50, true);
        Number("青 pkt/s（直近10秒）", blueRate, 50, true);
        var estimates = new[] { redEstimate, blueEstimate };
        for (int i = 0; i < estimates.Length; i++)
        {
            var e = estimates[i];
            string colour = i == 0 ? "赤" : "青";
            var phone = e?.Phone;
            bool fresh = phone != null && now - phone.ReceivedAt <= PhoneSaberMetricsStore.FreshSeconds &&
                now - phone.ReceivedAt >= 0 && e.Sender != "127.0.0.1" && e.Sender != "::1";
            Number(colour + " 撮影fps", fresh ? phone.CaptureFps : null, 58, true);
            Number(colour + " c2s中央値ms", fresh ? phone.C2sMedian : null, 40);
            Number(colour + " 合計推定ms", fresh && now - e.At <= 15 && phone.RttAge + now - phone.ReceivedAt <= 10 ? e.TotalMs : null, 65);
            string thermal = fresh ? phone.Thermal : "未計測";
            Add(thermal == "serious" || thermal == "critical" ? "BAD" : thermal == "nominal" ? "OK" : "WARN", colour + " 発熱: " + thermal);
            string route = e?.Sender == "127.0.0.1" || e?.Sender == "::1" ? "P2P / loopback（スマホ計測なし）" : fresh ? phone.Route : "未確認";
            Add(fresh && route == "LAN" ? "OK" : "WARN", colour + " 経路: " + route + " / " + (e?.Sender ?? "未接続"));
            // 最新defaultsの後に別操作のACKが来たら、それも反映する。送信しただけでは確定しない。
            PhoneSaberAcknowledgement defaults = null, track = null, exposure = null;
            foreach (var ack in acknowledgements ?? Array.Empty<PhoneSaberAcknowledgement>())
            {
                if (ack.Sender != e?.Sender) continue;
                if (ack.Result == "defaults" && (defaults == null || ack.Id > defaults.Id)) defaults = ack;
                if (ack.Result == "tracklock" && (track == null || ack.Id > track.Id)) track = ack;
                if (ack.Result == "exposure" && (exposure == null || ack.Id > exposure.Id)) exposure = ack;
            }
            bool? locked = defaults != null ? true : (bool?)null;
            int? ev = defaults != null ? -2 : (int?)null;
            if (track != null && (defaults == null || track.Id > defaults.Id)) locked = track.Detail == "on";
            if (exposure != null && (defaults == null || exposure.Id > defaults.Id))
                ev = exposure.Detail == "ev-2" ? -2 : exposure.Detail == "ev-1" ? -1 : 0;
            Add(defaults != null && locked == true && ev == -2 ? "OK" : "WARN", colour + " 最終スマホACK: track lock " +
                (locked.HasValue ? locked.Value ? "ON" : "OFF" : "未確認") + " / 露出 " + (ev?.ToString() ?? "未確認") +
                " / defaults " + (defaults != null ? "確認済 id=" + defaults.Id : "未確認") + "（現在値の再取得ではありません）");
        }
        Add(bothGaps > 0 ? "BAD" : wholeSong || !inGame ? "OK" : "WARN",
            "曲中の両色同時途切れ >1秒: " + bothGaps + "（基準0）" + (!wholeSong && inGame ? "・監視開始前は未確認" : ""));
        Add(framesOver50 > 1 ? "BAD" : wholeSong || !inGame ? "OK" : "WARN",
            "Game中 PCフレーム >50ms: " + framesOver50 + "（基準1曲≤1）" + (!wholeSong && inGame ? "・監視開始前は未確認" : ""));
        string overall = result.Exists(c => c.Grade == "BAD") ? "BAD" : result.Exists(c => c.Grade == "WARN") ? "WARN" : "OK";
        result.Insert(0, new Check { Grade = overall, Text = overall + " · 状態チェック: " +
            (overall == "OK" ? "基準内" : overall == "BAD" ? "異常あり・下の項目を確認" : "基準外 / 未確認あり・下の項目を確認") });
        return result;
    }

    public struct GameWindow
    {
        public bool Observed, WholeSong, GapActive;
        public int Scene, Frame, BothGaps, FramesOver50;
        public double StartedAt;
    }

    // 監視の中断・シーン切替を含め、時刻と統計値だけで更新する。
    public static GameWindow ObserveGame(GameWindow previous, double now, int frame, int scene, bool inGame, double frameMs)
    {
        bool continuous = previous.Observed && previous.Frame == frame - 1;
        bool sameScene = previous.Observed && previous.Scene == scene;
        var next = previous;
        if (!sameScene || !continuous)
            next = new GameWindow { WholeSong = continuous && !sameScene, StartedAt = now };
        next.Observed = true; next.Scene = scene; next.Frame = frame;
        if (!inGame) return next;
        if (continuous && sameScene && frameMs > 50) next.FramesOver50++;
        return next;
    }

    public static GameWindow ObserveGaps(GameWindow next, double now, double redAge, double blueAge)
    {
        if (!next.Observed) return next;
        bool gap = now - next.StartedAt > 1 && redAge > 1 && blueAge > 1;
        if (gap && !next.GapActive) next.BothGaps++;
        next.GapActive = gap;
        return next;
    }
}

// パネルを監視している間だけ使う。累積受信数の差を実時間で割り、1秒値の平均にはしない。
public sealed class PhoneSaberControlPanelRates
{
    readonly List<(double at, int red, int blue)> samples = new List<(double, int, int)>();
    string source;
    public double? Red { get; private set; }
    public double? Blue { get; private set; }
    public void Sample(double now, int red, int blue, string identity)
    {
        if (source != identity || samples.Count > 0 && (now - lastAt > 1 || red < lastRed || blue < lastBlue)) samples.Clear();
        source = identity; lastAt = now; lastRed = red; lastBlue = blue;
        samples.Add((now, red, blue));
        // 直近10秒の境界を標本間で補間するため、直前を1標本だけ残す。
        while (samples.Count > 1 && samples[1].at <= now - 10) samples.RemoveAt(0);
        var first = samples[0];
        double elapsed = now - first.at;
        if (elapsed < 10) { Red = Blue = null; return; }
        var second = samples[1];
        double fraction = (now - 10 - first.at) / (second.at - first.at);
        Red = (red - first.red - (second.red - first.red) * fraction) / 10;
        Blue = (blue - first.blue - (second.blue - first.blue) * fraction) / 10;
    }
    double lastAt;
    int lastRed, lastBlue;
}
